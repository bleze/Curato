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
    // Videos indexed before the QuickTimeEpoch fix existed, self-healed this
    // scan — see LibraryRepository.RepairWrongEpochVideoTakenAt.
    int RepairedVideoTakenAtCount,
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
    INominatimClient? nominatimClient = null, string? homeLabel = null, ScanProgressTracker? progressTracker = null)
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
    ///
    /// Incremental under the hood (confirmed live as the dominant cost of
    /// re-scanning a large, mostly-static library): MediaFileScanner.Scan
    /// skips re-reading Exif/hashing every file in a folder whose own
    /// modified-time still matches what the *previous* full scan recorded,
    /// reusing each one's already-known MediaItem instead. Every folder and
    /// file is still walked and still yielded either way, so this is purely
    /// a speedup, never a change in what gets found — deletion detection
    /// (RemoveMissingFiles below) sees the exact same complete picture it
    /// always did. The new folder times are only persisted once this
    /// reaches the end without being cancelled — see the catch block in the
    /// overload below, which a cancelled run never gets past.
    /// </summary>
    public ScanResult Scan(string libraryRoot, CancellationToken cancellationToken = default)
    {
        // Confirmed live as needed here too, not just before an iCloud
        // sync (where this already ran) — an iCloud-sync placeholder stub
        // left behind by an earlier interrupted run doesn't wait for the
        // *next sync* to get cleaned up if an ordinary Scan reaches it
        // first; left alone, that Scan would index it as a real (all-zero)
        // MediaItem instead. Must run before the walk below, not after —
        // once indexed, it's already a false "newly synced" item.
        OrphanedStubCleanup.CleanupOrphanedStubs(libraryRoot, query);

        // Rows indexed from a NAS system folder (Synology's @eaDir thumbnails,
        // before the scanner skipped them) are dropped from the database
        // directly. Left to RemoveMissingFiles instead, they'd count as
        // "missing" — and the Recycle Bin would *move* those still-present
        // files out of the NAS's own folders.
        repository.ForgetMediaItems(query.GetAllMediaItems()
            .Where(item => MediaFileScanner.IsInSystemFolder(item.RelativePath))
            .Select(item => item.Id)
            .ToList());

        var previousFolderModifiedTimes = query.GetFolderModifiedTimes();
        var previouslyIndexedByRelativePath = query.GetAllMediaItems()
            .ToDictionary(item => item.RelativePath, item => item, StringComparer.OrdinalIgnoreCase);
        var currentFolderModifiedTimes = new Dictionary<string, DateTime>();

        var scannedFiles = new MediaFileScanner().Scan(
            libraryRoot, previousFolderModifiedTimes, previouslyIndexedByRelativePath, currentFolderModifiedTimes);

        var result = Scan(libraryRoot, scannedFiles, cancellationToken, currentFolderModifiedTimes);

        // Only reached once the walk finished without being cancelled (see
        // the catch block below, which rethrows) — a folder not in this
        // scan's set either no longer exists or was never visited, so any
        // stale row for it is safe to drop.
        repository.PruneFolderModifiedTimes(currentFolderModifiedTimes.Keys);

        return result;
    }

    /// <summary>
    /// The testable core of <see cref="Scan(string, CancellationToken)"/> —
    /// takes the item source as a plain <see cref="IEnumerable{T}"/> instead
    /// of always walking real files, so a test can supply its own
    /// slow/interceptable sequence (e.g. one that cancels
    /// <paramref name="cancellationToken"/>'s source partway through) without
    /// needing real files on disk or real timing.
    /// </summary>
    // Chosen as "frequent enough that an unexpected interruption loses at
    // most a few seconds of EXIF/hashing work" vs. "infrequent enough that
    // the extra connection/transaction overhead doesn't meaningfully slow
    // down a normal scan" — and, confirmed live, infrequent enough not to
    // trip SQLite's network-filesystem locking fragility either (see
    // LibraryRepository.FlushScanProgress's doc comment for the incident
    // this was tuned against).
    private const int BatchFlushSize = 200;

    public ScanResult Scan(
        string libraryRoot,
        IEnumerable<MediaItem> scannedFiles,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, DateTime>? folderModifiedTimes = null)
    {
        progressTracker?.Reset(libraryRoot);
        // Not a plain .ToList() — MediaFileScanner.Scan is a lazy `yield
        // return` walk, so each file's actual work (EXIF read, hashing)
        // happens right here as it's enumerated. Ticking the counter per
        // item, right where that work happens, is what lets a concurrent
        // poll (ScanController's GET /progress) see real-time movement
        // instead of a number that jumps straight to its final value — and,
        // now, is also where a Stop request actually takes effect.
        var scannedItems = new List<MediaItem>();
        // Confirmed live as a real way to lose an entire long scan's worth
        // of work: the *hosting process* getting killed outright (not a
        // graceful Stop click) can't be caught by the try/catch below at
        // all — nothing runs after that. Flushing what's been found so far
        // every BatchFlushSize items means a hard kill only ever costs
        // whatever's accumulated since the last flush. Each flush also
        // carries along the modified-time of every folder that happened to
        // finish *within* that batch (exploiting MediaFileScanner's
        // depth-first walk, which always yields one folder's files fully
        // before moving to the next, so "folder changed" reliably means
        // "previous folder is done") — in one single connection/transaction
        // together with the items (see FlushScanProgress), not one
        // connection per folder: this repository's database can live on a
        // network share, where a connection per folder was confirmed live
        // to trip SQLite's own network-locking fragility on a library with
        // many small folders. Safe to flush ahead of RemoveMissingFiles/
        // ScanItems below: UpsertMediaItems is idempotent on RelativePath,
        // so re-upserting the same items again as part of the normal
        // end-of-scan pipeline is a harmless no-op, not a duplicate.
        var pendingItems = new List<MediaItem>();
        var pendingCompletedFolders = new List<(string RelativePath, DateTime ModifiedUtc)>();
        string? currentFolder = null;
        try
        {
            foreach (var item in scannedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var folder = GetContainingFolder(item.RelativePath);
                if (currentFolder is not null && folder != currentFolder)
                {
                    MarkFolderComplete(currentFolder, pendingCompletedFolders, folderModifiedTimes);
                }

                currentFolder = folder;
                scannedItems.Add(item);
                pendingItems.Add(item);
                progressTracker?.Increment(libraryRoot);

                if (pendingItems.Count >= BatchFlushSize)
                {
                    repository.FlushScanProgress(pendingItems, pendingCompletedFolders);
                    pendingItems.Clear();
                    pendingCompletedFolders.Clear();
                }
            }

            if (currentFolder is not null)
            {
                MarkFolderComplete(currentFolder, pendingCompletedFolders, folderModifiedTimes);
            }

            if (pendingItems.Count > 0 || pendingCompletedFolders.Count > 0)
            {
                repository.FlushScanProgress(pendingItems, pendingCompletedFolders);
                pendingItems.Clear();
                pendingCompletedFolders.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // Persist whatever was actually found as plain MediaItem rows
            // (idempotent on RelativePath) so nothing already scanned is
            // lost — but deliberately skip RemoveMissingFiles/ScanItems
            // (clustering) entirely: Step 1/2's gap-based clustering needs
            // the *complete* loose-item picture to bucket correctly, and
            // clustering a partial cut risks an Event that gets awkwardly
            // split once the next full scan finishes the job. The next Scan
            // just picks these rows back up and clusters them normally.
            // Any folder completed since the last actual flush is
            // deliberately left unrecorded in FolderScanState — its items
            // are safe (upserted below), but the next scan will revisit it
            // to confirm that, rather than trusting an unflushed promise.
            repository.UpsertMediaItems(scannedItems);
            throw;
        }

        var removedMissingFileCount = RemoveMissingFiles(scannedItems);
        return ScanItems(scannedItems) with { RemovedMissingFileCount = removedMissingFileCount };
    }

    private static void MarkFolderComplete(
        string folder,
        List<(string RelativePath, DateTime ModifiedUtc)> pendingCompletedFolders,
        IReadOnlyDictionary<string, DateTime>? folderModifiedTimes)
    {
        if (folderModifiedTimes is not null && folderModifiedTimes.TryGetValue(folder, out var modifiedUtc))
        {
            pendingCompletedFolders.Add((folder, modifiedUtc));
        }
    }

    private static string GetContainingFolder(string relativePath)
    {
        var lastSlash = relativePath.LastIndexOf('/');
        return lastSlash < 0 ? "" : relativePath[..lastSlash];
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
        // Never "recycle" a file that's actually still there: the Recycle Bin
        // moves it, so anything the walk merely didn't report (a folder it
        // skips, a path spelled differently) would be pulled out of place.
        var missing = allIndexed
            .Where(i => !currentRelativePaths.Contains(i.RelativePath))
            .Where(i => !File.Exists(Path.Combine(database.LibraryRoot, i.RelativePath)))
            .ToList();
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

        // Self-heals any video indexed before the QuickTimeEpoch fix existed
        // — a re-scan alone can never fix an already-indexed item's TakenAt
        // (UpsertMediaItems' ON CONFLICT DO NOTHING never touches an
        // existing row), same reasoning as every other self-heal above.
        var repairedVideoTakenAtCount = repository.RepairWrongEpochVideoTakenAt();

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
            RepairedVideoTakenAtCount: repairedVideoTakenAtCount,
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

        // Confirmed live as a real gap: content re-scanned later for an
        // already-formed Trip's own dates (e.g. imported after the fact into
        // .Import, or any other content that lands outside an
        // already-adopted/already-organized folder) only ever considers
        // *other ungrouped* Events as merge candidates — an Event already
        // belonging to an existing Trip is excluded from `candidateEvents`
        // above, so it can never pull a same-period newcomer in with it.
        // Without this, the newcomer's own run of days — now standing alone
        // because its real neighbors are already spoken for — can still
        // independently clear MinSpanDays and silently form a *second*,
        // duplicate Trip over the same dates instead of joining the first.
        var existingAutoTrips = query.GetAllTripGroups()
            .Where(t => t.NamingMode == NamingMode.CuratoManaged)
            .ToList();

        var clusterer = new TripClusterer();
        var qualifyingTrips = new List<TripGroup>();
        var mergeEventIdsByExistingTripId = new Dictionary<long, List<long>>();

        foreach (var candidate in clusterer.GroupConsecutive(candidateEvents))
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
            var memberIds = members.Select(e => e.Id).ToList();

            // Checked BEFORE the MinSpanDays filter below, deliberately —
            // confirmed live as a real bug, not just an ordering nit: a
            // single new day's worth of away-from-home photos scanned in
            // for a trip already in progress (its earlier days already
            // grouped, hence excluded from `candidateEvents` and invisible
            // to TripClusterer here) is, on its own, far too short a span
            // to independently qualify as a new trip — so the MinSpanDays
            // check below was silently discarding it before ever reaching
            // this existing-trip match, stranding it as its own standalone
            // Event instead of extending the trip it's obviously part of.
            // Same adjacency rule TripClusterer itself uses to decide two
            // Events belong to the same Trip in the first place — applied
            // here against an already-saved Trip's own date range, so a
            // same-period newcomer joins it regardless of how short its own
            // slice is in isolation.
            var existingMatch = existingAutoTrips.FirstOrDefault(
                t => IsWithinGap(t, startDate, endDate, clusterer.GapThreshold));

            if (existingMatch is not null)
            {
                if (!mergeEventIdsByExistingTripId.TryGetValue(existingMatch.Id, out var pending))
                {
                    mergeEventIdsByExistingTripId[existingMatch.Id] = pending = [];
                }

                pending.AddRange(memberIds);
                continue;
            }

            if (endDate.DayNumber - startDate.DayNumber + 1 < clusterer.MinSpanDays)
            {
                continue; // trimming collapsed it below a real multi-day trip, and no existing trip to extend either
            }

            qualifyingTrips.Add(new TripGroup
            {
                StartDate = startDate,
                EndDate = endDate,
                Title = $"{startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}",
                TitleIsAuto = true,
                NamingMode = NamingMode.CuratoManaged,
                EventIds = memberIds
            });
        }

        foreach (var (existingTripId, eventIds) in mergeEventIdsByExistingTripId)
        {
            repository.AddEventsToTripGroup(existingTripId, eventIds);
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

    private static bool IsWithinGap(TripGroup existingTrip, DateOnly startDate, DateOnly endDate, TimeSpan gapThreshold)
    {
        var bufferDays = (int)Math.Ceiling(gapThreshold.TotalDays);
        var existingStart = existingTrip.StartDate.AddDays(-bufferDays);
        var existingEnd = existingTrip.EndDate.AddDays(bufferDays);
        return startDate <= existingEnd && existingStart <= endDate;
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
    public void EnrichAutoTitlesWithLocation(
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
