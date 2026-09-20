using Curato.Core;
using Curato.Data;
using Curato.Media;
using Microsoft.AspNetCore.Mvc;

namespace Curato.Api.Controllers;

/// <summary>
/// First testable vertical slice of PLAN.md §3's engine: scan a folder,
/// cluster it into Events/Trips (Step 1-2), persist the result.
///
/// NOT YET behind auth (§15) — there's no admin-account/login flow built yet
/// (that's the very next piece of work), so this is deliberately open only
/// for local scaffold testing. Do not expose this port before that lands.
/// Also doesn't yet implement §3 Step 0's "existing folders are the
/// boundary" rule — everything scanned is treated as loose/unstructured
/// input for now, so point it at a flat test folder, not a real archive.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class ScanController : ControllerBase
{
    public sealed record ScanRequest(string Path);

    public sealed record ScanResponse(
        int MediaItemCount,
        int EventCount,
        int TripGroupCount,
        IReadOnlyList<EventSummary> Events);

    public sealed record EventSummary(long Id, string Title, DateTime StartAt, DateTime EndAt, int ItemCount);

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

        var events = new EventClusterer().Cluster(savedItems);
        repository.SaveEvents(events);

        var trips = new TripClusterer().Cluster(events);
        repository.SaveTripGroups(trips);

        var response = new ScanResponse(
            MediaItemCount: savedItems.Count,
            EventCount: events.Count,
            TripGroupCount: trips.Count,
            Events: events
                .OrderBy(e => e.StartAt)
                .Select(e => new EventSummary(e.Id, e.Title, e.StartAt, e.EndAt, e.MediaItemIds.Count))
                .ToList());

        return Ok(response);
    }
}
