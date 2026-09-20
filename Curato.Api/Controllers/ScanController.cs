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
/// Protected by the fallback auth policy (§15) — requires a logged-in session.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class ScanController : ControllerBase
{
    public sealed record ScanRequest(string Path);

    public sealed record ScanResponse(
        int MediaItemCount,
        int AutoOrganizedEventCount,
        int AdoptedEventCount,
        int TripGroupCount,
        IReadOnlyList<EventSummary> Events);

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
        var looseItems = byFolder[FolderGrouper.LooseItemsKey].ToList();

        var autoEvents = new EventClusterer().Cluster(looseItems);
        var adoptedEvents = AdoptedFolderPlanner.PlanEvents(byFolder);

        var allEvents = autoEvents.Concat(adoptedEvents).ToList();
        repository.SaveEvents(allEvents); // assigns real Ids, needed before trip-grouping

        var autoTrips = new TripClusterer().Cluster(autoEvents);
        var adoptedTrips = AdoptedFolderPlanner.PlanTrips(adoptedEvents);
        var allTrips = autoTrips.Concat(adoptedTrips).ToList();
        repository.SaveTripGroups(allTrips);

        var response = new ScanResponse(
            MediaItemCount: savedItems.Count,
            AutoOrganizedEventCount: autoEvents.Count,
            AdoptedEventCount: adoptedEvents.Count,
            TripGroupCount: allTrips.Count,
            Events: allEvents
                .OrderBy(e => e.StartAt)
                .Select(e => new EventSummary(
                    e.Id, e.Title, e.NamingMode.ToString(), e.FolderPath,
                    e.StartAt, e.EndAt, e.MediaItemIds.Count))
                .ToList());

        return Ok(response);
    }
}
