using Curato.Core;
using Curato.Data;
using Curato.Media;

namespace Curato.Organizer;

public sealed record EventSummary(
    long Id, string Title, string NamingMode, string? FolderPath,
    DateTime StartAt, DateTime EndAt, int ItemCount);

public sealed record ScanResult(
    int MediaItemCount,
    int NewAutoOrganizedEventCount,
    int NewAdoptedEventCount,
    int NewTripGroupCount,
    int ItemsAddedToExistingEvents,
    int RemovedBareYearTripGroupCount,
    int RemovedEmptyEventCount,
    int RemovedMissingFileCount,
    IReadOnlyList<EventSummary> NewEvents);

/// <summary>
/// PLAN.md §3's engine: split a scan into loose vs. already-organized content
/// (Step 0), cluster the loose part (Steps 1-2), adopt the rest as-is,
/// persist everything. Used both by <c>ScanController</c> (pointing at a
/// folder already on the server) and the upload endpoint (§4a) - once new
/// files land on disk, getting them indexed is the same pipeline either way.
///
/// Re-scanning is idempotent: content already assigned to an Event is never
/// re-clustered into a second, duplicate Event. New items in an
/// already-adopted folder join that folder's existing Event instead of
/// spawning a new one. (Known scope limit: if a trip's sibling day-folders
/// get adopted across *separate* scans rather than the same one, the
/// TripGroup isn't retroactively formed once the second folder shows up —
/// real but narrow enough to leave as a follow-up rather than block on.)
/// </summary>
public sealed class ScanService(
    LibraryRepository repository, LibraryQueryService query, LibraryDatabase database,
    INominatimClient? nominatimClient = null, string? homeLabel = null)
{
    // Self-constructed (not required in the constructor) so the ~25 existing
    // call sites across controllers/tests don't need updating — matches how
    // this method already self-constructs a fresh MediaFileScanner below.
    // Only actually invoked (see FormGpsAwareTrips/EnrichAutoTitlesWithLocation)
    // when an Event/Trip has a real GPS location to name itself after, which
    // no existing test's fixtures ever trigger, so `nominatimClient` only
    // matters to tests that specifically exercise GPS-based formation.
    private readonly ReverseGeocoder geocoder = new(database, nominatimClient ?? new NominatimClient());

    /// <summary>
    /// Walks <paramref name="libraryRoot"/> from scratch, indexes whatever's
    /// found, and — confirmed live as a real gap, not just a hypothetical —
    /// cleans up anything previously indexed that's no longer there. Before
    /// this, a file deleted outside Curato (Explorer, a manual `rm`, etc.)
    /// stuck around forever as a "ghost" MediaItem showing "No preview"
    /// rather than the delete actually being reflected in the library.
    /// </summary>
    public ScanResult Scan(string libraryRoot)
    {
        var scannedItems = new MediaFileScanner().Scan(libraryRoot).ToList();
        var removedMissingFileCount = RemoveMissingFiles(scannedItems);
        return ScanItems(scannedItems) with { RemovedMissingFileCount = removedMissingFileCount };
    }

    /// <summary>
    /// Routes anything previously indexed but not found in this scan's walk
    /// through the same Recycle Bin path a manual delete uses
    /// (<c>RecycleBinService.DeleteMediaItem</c>, source "MissingOnDisk") —
    /// there's no file left to move into the trash folder, but that method
    /// already tolerates that (it only moves the file if `File.Exists`), so
    /// this still gets the exact same MediaItem/Event/Tag/Edit/etc. cleanup
    /// a real delete gets, for free, plus a visible DeletedItem record of
    /// what happened rather than a silent purge.
    ///
    /// Guarded: a scan where *most* of the library suddenly appears
    /// missing is far more likely a transient/broken mount than a genuine
    /// bulk delete — wiping real metadata (Event clustering, Tags, Edits)
    /// over that isn't something a 30-day Recycle Bin window meaningfully
    /// protects against if nobody notices in time, so cleanup is skipped
    /// entirely (not partially — an all-or-nothing decision per scan)
    /// rather than risk it.
    /// </summary>
    private int RemoveMissingFiles(IReadOnlyCollection<MediaItem> currentlyOnDisk)
    {
        var allIndexed = query.GetAllMediaItems();
        if (allIndexed.Count == 0)
        {
            return 0;
        }

        var currentRelativePaths = new HashSet<string>(
            currentlyOnDisk.Select(i => i.RelativePath), StringComparer.OrdinalIgnoreCase);
        var missing = allIndexed.Where(i => !currentRelativePaths.Contains(i.RelativePath)).ToList();
        if (missing.Count == 0)
        {
            return 0;
        }

        var safeLimit = Math.Max(20, allIndexed.Count / 2);
        if (missing.Count > safeLimit)
        {
            return 0;
        }

        var recycleBin = new RecycleBinService(database, new RecycleBinRepository(database));
        foreach (var item in missing)
        {
            // A Live Photo pairing means deleting the still-frame photo
            // already recursively deletes its paired video too (existing
            // RecycleBinService behavior) — a second call for that same
            // video, also in `missing`, is a harmless no-op (it just
            // returns null, already gone).
            recycleBin.DeleteMediaItem(item.Id, "MissingOnDisk");
        }

        return missing.Count;
    }

    public ScanResult ScanItems(IEnumerable<MediaItem> scannedItems)
    {
        var savedItems = repository.UpsertMediaItems(scannedItems);

        // PLAN.md §3 Step 0: split by whether an existing folder already
        // bounds these items, or they're loose at the library root.
        var byFolder = FolderGrouper.GroupByContainingFolder(savedItems);

        // Never re-cluster content that's already in an Event — that's what
        // turned every re-scan into a pile of duplicates.
        var alreadyClustered = repository.GetMediaItemIdsAlreadyInAnyEvent();
        var newLooseItems = byFolder[FolderGrouper.LooseItemsKey]
            .Where(item => !alreadyClustered.Contains(item.Id))
            .ToList();

        var existingEventsByFolder = repository.GetEventsByFolderPath();
        var itemsAddedToExisting = 0;
        var newFolderGroups = new List<IGrouping<string, MediaItem>>();

        foreach (var group in byFolder)
        {
            if (group.Key == FolderGrouper.LooseItemsKey)
            {
                continue;
            }

            if (existingEventsByFolder.TryGetValue(group.Key, out var existing))
            {
                var newItemsInFolder = group.Where(item => !existing.MediaItemIds.Contains(item.Id)).ToList();
                repository.AddMediaItemsToEvent(existing.Id, newItemsInFolder);
                itemsAddedToExisting += newItemsInFolder.Count;
            }
            else
            {
                newFolderGroups.Add(group);
            }
        }

        var newAutoEvents = new EventClusterer().Cluster(newLooseItems);
        var newAdoptedEvents = AdoptedFolderPlanner.PlanEvents(newFolderGroups);

        // Shared by both the title enrichment below and GPS-aware Trip
        // formation after saving — computed once from the library's full
        // current MediaItem state (already committed by UpsertMediaItems
        // above, regardless of Event-saving order), not just this scan's items.
        var allMediaItemsById = query.GetAllMediaItems().ToDictionary(i => i.Id);
        var homeLocation = GpsTripGate.DetectHomeLocation(allMediaItemsById.Values);
        EnrichAutoTitlesWithLocation(newAutoEvents, savedItems.ToDictionary(i => i.Id), homeLocation);

        var allNewEvents = newAutoEvents.Concat(newAdoptedEvents).ToList();
        repository.SaveEvents(allNewEvents); // assigns real Ids, needed before trip-grouping

        var adoptedTrips = AdoptedFolderPlanner.PlanTrips(newAdoptedEvents);
        var gpsTrips = FormGpsAwareTrips(homeLocation, allMediaItemsById);
        var allNewTrips = adoptedTrips.Concat(gpsTrips).ToList();
        repository.SaveTripGroups(allNewTrips);

        // Self-heals a library scanned before PlanTrips started excluding
        // bare-year parent folders: a "2019" Trip wrapping every unrelated
        // event that happened to live under a top-level `2019/` folder isn't
        // a real trip, just how the library's filed — ungrouped back to
        // standalone Adopted Events, same as the CuratoManaged self-heal
        // above.
        var removedBareYearTripGroupCount = repository.RemoveBareYearAdoptedTripGroups();

        // Self-heals any Event/TripGroup a library's database already had
        // sitting at zero members — confirmed live as a photo-less
        // "Afternoon" event still showing in the library view even after
        // RecycleBinRepository.RemoveMediaItem started preventing new ones;
        // that fix only guards its own codepath, it never swept up rows a
        // database already had from before it existed.
        var emptiedEvents = repository.RemoveEmptyEvents();
        foreach (var emptiedEvent in emptiedEvents)
        {
            FolderCleanup.DeleteIfEmpty(database.LibraryRoot, emptiedEvent.FolderPath);
        }

        DetectAndPersistLivePhotoPairs();

        return new ScanResult(
            MediaItemCount: savedItems.Count,
            NewAutoOrganizedEventCount: newAutoEvents.Count,
            NewAdoptedEventCount: newAdoptedEvents.Count,
            NewTripGroupCount: allNewTrips.Count,
            ItemsAddedToExistingEvents: itemsAddedToExisting,
            RemovedBareYearTripGroupCount: removedBareYearTripGroupCount,
            RemovedEmptyEventCount: emptiedEvents.Count,
            RemovedMissingFileCount: 0, // only Scan(libraryRoot) can know this; merged in there via `with`
            NewEvents: allNewEvents
                .OrderBy(e => e.StartAt)
                .Select(e => new EventSummary(
                    e.Id, e.Title, e.NamingMode.ToString(), e.FolderPath,
                    e.StartAt, e.EndAt, e.MediaItemIds.Count))
                .ToList());
    }

    /// <summary>
    /// GPS-aware auto-Trip formation (PLAN.md §3 Step 2/§12): TripClusterer's
    /// pure time-gap grouping can't tell a real trip away from home apart
    /// from an ordinary run of days with photos on them on its own — that's
    /// exactly why this was disabled before GPS extraction existed. Gated on
    /// at least one member Event's representative location actually being far
    /// (<see cref="GpsTripGate.AwayFromHomeThresholdKm"/>) from wherever this
    /// library's photos are usually taken; a qualifying Trip is titled after
    /// that away-from-home place (e.g. "Mallorca") when reverse-geocoding
    /// succeeds, falling back to the plain date-range title otherwise. A
    /// library with no GPS data anywhere never has a home location, so the
    /// gate never passes and behavior is unchanged (zero auto-formed Trips).
    ///
    /// Candidates are every not-yet-grouped CuratoManaged Event in the whole
    /// library, not just ones this scan pass just created — confirmed live as
    /// wanted: a library that already existed before GPS extraction landed
    /// still has years of standalone Events sitting in it, and re-scanning
    /// alone can't re-cluster their MediaItems (already-clustered content is
    /// never touched — see GetMediaItemIdsAlreadyInAnyEvent), so this is the
    /// only path that ever forms a Trip for them. Naturally idempotent: once
    /// an Event is grouped, its TripGroupId excludes it from being considered
    /// again on the next scan. Adopted Events are excluded — their grouping
    /// is folder-driven (AdoptedFolderPlanner.PlanTrips), never GPS-guessed.
    /// Already-Organized Events are still eligible (CreateTripGroup's own
    /// doc comment: grouping is pure metadata, never a file move).
    /// </summary>
    private IReadOnlyList<TripGroup> FormGpsAwareTrips(
        (double Lat, double Lon)? homeLocation, IReadOnlyDictionary<long, MediaItem> itemsById)
    {
        if (homeLocation is null)
        {
            return [];
        }

        var candidateEvents = query.GetAllEvents()
            .Where(e => e.NamingMode == NamingMode.CuratoManaged && e.TripGroupId is null)
            .ToList();
        var candidateEventsById = candidateEvents.ToDictionary(e => e.Id);

        var eventLocations = candidateEvents.ToDictionary(
            e => e.Id, e => GpsTripGate.RepresentativeLocation(e, itemsById));

        var clusterer = new TripClusterer();
        var qualifyingTrips = new List<TripGroup>();

        foreach (var candidate in clusterer.Cluster(candidateEvents))
        {
            // TripClusterer's own time-gap chaining has no bound on how long
            // a run can grow, and a candidate qualifying because *something*
            // in it is away from home doesn't mean *every* member is — trim
            // back to just the away-anchored core (see TrimToAwayCore) before
            // accepting it, rather than sweeping in whatever home-Denmark
            // padding happened to chain onto the real trip on either side.
            var trimmedIds = GpsTripGate.TrimToAwayCore(candidate.EventIds, eventLocations, homeLocation);
            if (trimmedIds.Count == 0)
            {
                continue;
            }

            var members = trimmedIds.Select(id => candidateEventsById[id]).OrderBy(e => e.StartAt).ToList();
            var startDate = DateOnly.FromDateTime(members[0].StartAt);
            var endDate = DateOnly.FromDateTime(members[^1].EndAt);
            if (endDate.DayNumber - startDate.DayNumber + 1 < clusterer.MinSpanDays)
            {
                continue; // trimming collapsed it below a real multi-day trip
            }

            qualifyingTrips.Add(new TripGroup
            {
                StartDate = startDate,
                EndDate = endDate,
                Title = $"{startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}",
                TitleIsAuto = true,
                NamingMode = NamingMode.CuratoManaged,
                EventIds = members.Select(e => e.Id).ToList()
            });
        }

        foreach (var trip in qualifyingTrips)
        {
            var awayLocation = trip.EventIds
                .Select(id => eventLocations[id])
                .FirstOrDefault(location => GpsTripGate.IsAwayFromHome(homeLocation, location));

            if (awayLocation is { } location && geocoder.Resolve(location.Lat, location.Lon) is { } placeName)
            {
                trip.Title = placeName;
            }
        }

        return qualifyingTrips;
    }

    /// <summary>
    /// Appends a resolved place name to each newly-clustered loose Event's
    /// plain time-of-day auto-title (e.g. "Afternoon" becomes
    /// "Afternoon · Lejre Kommune") whenever it has at least one GPS-tagged
    /// member — confirmed live as wanted once location display existed
    /// elsewhere (Trip titles, the library view's location text) but the
    /// default Event name itself still didn't say where. Prefers the user's
    /// own Settings-configured "Home" label (<see cref="GpsTripGate.HomeOverrideLabel"/>)
    /// over a real reverse-geocode when the Event is near home — confirmed
    /// live that Nominatim's own result for a real home address can be as
    /// coarse as a whole county, not useful as a title. Never touches an
    /// Adopted Event (its title always comes from a real folder name, not
    /// this clusterer) or an Event with no GPS-tagged members at all.
    /// </summary>
    private void EnrichAutoTitlesWithLocation(
        IReadOnlyList<Event> autoEvents, IReadOnlyDictionary<long, MediaItem> itemsById, (double Lat, double Lon)? homeLocation)
    {
        foreach (var @event in autoEvents)
        {
            var location = GpsTripGate.RepresentativeLocation(@event, itemsById);
            if (location is not { } loc)
            {
                continue;
            }

            var placeName = GpsTripGate.HomeOverrideLabel(homeLocation, homeLabel, loc) ?? geocoder.Resolve(loc.Lat, loc.Lon);
            if (placeName is not null)
            {
                @event.Title = $"{@event.Title} · {placeName}";
            }
        }
    }

    /// <summary>
    /// Re-queries the library's true current state (not <c>savedItems</c>,
    /// which for already-existing rows are just this pass's freshly-scanned
    /// objects and don't reflect what's actually stored) so a photo scanned
    /// in an earlier pass still pairs correctly once its .MOV sidecar shows
    /// up in a later one, or vice versa.
    /// </summary>
    private void DetectAndPersistLivePhotoPairs()
    {
        foreach (var pair in LivePhotoPairer.FindPairs(query.GetAllMediaItems()))
        {
            repository.SetLivePhotoPair(pair.PhotoMediaItemId, pair.VideoMediaItemId);
        }
    }
}
