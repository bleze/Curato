using Curato.Core;
using Curato.Data;
using Curato.Media;
using Curato.Organizer;
using Microsoft.AspNetCore.Mvc;

namespace Curato.Api.Controllers;

/// <summary>
/// First testable vertical slice of PLAN.md §3's engine: scan a folder,
/// split it into loose vs. already-organized content (Step 0), cluster the
/// loose part (Steps 1-2), adopt the rest as-is, persist everything.
///
/// Re-scanning is idempotent: content already assigned to an Event is never
/// re-clustered into a second, duplicate Event. New items in an
/// already-adopted folder join that folder's existing Event instead of
/// spawning a new one. (Known scope limit: if a trip's sibling day-folders
/// get adopted across *separate* scans rather than the same one, the
/// TripGroup isn't retroactively formed once the second folder shows up —
/// real but narrow enough to leave as a follow-up rather than block on.)
///
/// Protected by the fallback auth policy (§15) — requires a logged-in session.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class ScanController : ControllerBase
{
    public sealed record ScanRequest(string Path);

    public sealed record ScanResponse(
        int MediaItemCount,
        int NewAutoOrganizedEventCount,
        int NewAdoptedEventCount,
        int NewTripGroupCount,
        int ItemsAddedToExistingEvents,
        IReadOnlyList<EventSummary> NewEvents);

    public sealed record EventSummary(
        long Id, string Title, string NamingMode, string? FolderPath,
        DateTime StartAt, DateTime EndAt, int ItemCount);

    [HttpPost]
    public ActionResult<ScanResponse> Scan([FromBody] ScanRequest request)
    {
        if (!Directory.Exists(request.Path))
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        var database = new LibraryDatabase(request.Path);
        database.EnsureSchema();
        var repository = new LibraryRepository(database);

        var scanner = new MediaFileScanner();
        var scannedItems = scanner.Scan(request.Path).ToList();
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

        var existingAdoptedByFolder = repository.GetAdoptedEventsByFolderPath();
        var itemsAddedToExisting = 0;
        var newFolderGroups = new List<IGrouping<string, MediaItem>>();

        foreach (var group in byFolder)
        {
            if (group.Key == FolderGrouper.LooseItemsKey)
            {
                continue;
            }

            if (existingAdoptedByFolder.TryGetValue(group.Key, out var existing))
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

        var newAutoTrips = new TripClusterer().Cluster(newAutoEvents);
        var newAdoptedTrips = AdoptedFolderPlanner.PlanTrips(newAdoptedEvents);
        var allNewTrips = newAutoTrips.Concat(newAdoptedTrips).ToList();
        repository.SaveTripGroups(allNewTrips);

        var response = new ScanResponse(
            MediaItemCount: savedItems.Count,
            NewAutoOrganizedEventCount: newAutoEvents.Count,
            NewAdoptedEventCount: newAdoptedEvents.Count,
            NewTripGroupCount: allNewTrips.Count,
            ItemsAddedToExistingEvents: itemsAddedToExisting,
            NewEvents: allNewEvents
                .OrderBy(e => e.StartAt)
                .Select(e => new EventSummary(
                    e.Id, e.Title, e.NamingMode.ToString(), e.FolderPath,
                    e.StartAt, e.EndAt, e.MediaItemIds.Count))
                .ToList());

        return Ok(response);
    }
}
