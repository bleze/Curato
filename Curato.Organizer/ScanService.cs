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
    int RemovedAutoTripGroupCount,
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
public sealed class ScanService(LibraryRepository repository, LibraryQueryService query, LibraryDatabase database)
{
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

        var allNewEvents = newAutoEvents.Concat(newAdoptedEvents).ToList();
        repository.SaveEvents(allNewEvents); // assigns real Ids, needed before trip-grouping

        // Auto-Trip formation (TripClusterer, purely time-gap-based) is
        // deliberately not wired in here: with no location signal at all, it
        // has no way to tell "a real trip away from home" apart from just an
        // ordinary run of days with photos on them, and for anyone who takes
        // photos close to daily, that's most of their library — TripClusterer
        // itself stays (correct for what it does, and a reasonable building
        // block once GPS-based detection, PLAN.md §12, actually lands), just
        // not used to auto-create Trips a user never asked for. Adopted
        // Trips are unaffected: those come from folder structure the user
        // already made themselves, never a time-based guess.
        var allNewTrips = AdoptedFolderPlanner.PlanTrips(newAdoptedEvents);
        repository.SaveTripGroups(allNewTrips);

        // Self-heals a library scanned before auto-Trip formation was
        // disabled: any leftover CuratoManaged TripGroup from the old
        // time-gap heuristic is ungrouped back into standalone Events.
        var removedAutoTripGroupCount = repository.RemoveAutoFormedTripGroups();

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
            RemovedAutoTripGroupCount: removedAutoTripGroupCount,
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
