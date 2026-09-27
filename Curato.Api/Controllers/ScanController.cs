using Curato.Data;
using Curato.Organizer;
using Microsoft.AspNetCore.Mvc;

namespace Curato.Api.Controllers;

/// <summary>
/// First testable vertical slice of PLAN.md §3's engine: scan a folder and
/// index/cluster whatever's found. The orchestration itself lives in
/// <see cref="ScanService"/> so the upload endpoint (§4a) can trigger the
/// exact same pipeline after writing new files to disk.
///
/// Every scan also runs Picasa import (§8) automatically — no separate
/// button/action for the user to remember, since it's idempotent and a
/// no-op when no `.picasa.ini` files are found. A future settings dialog is
/// the natural place to make this opt-out, not before one exists.
///
/// Protected by the fallback auth policy (§15) — requires a logged-in session.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class ScanController(LibraryOperationGate gate, ServerDatabase serverDatabase, ScanProgressTracker progressTracker) : ControllerBase
{
    public sealed record ScanRequest(string Path);
    public sealed record ScanResponse(ScanResult Scan, PicasaImportResult PicasaImport);
    public sealed record ScanProgressResponse(int FilesFoundSoFar);

    [HttpPost]
    public async Task<ActionResult<ScanResponse>> Scan([FromBody] ScanRequest request, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(request.Path))
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        var homeLabel = new HomeLocationRepository(serverDatabase).Get();

        using var release = await gate.AcquireAsync(request.Path, cancellationToken);
        var database = new LibraryDatabase(request.Path);
        database.EnsureSchema();
        var query = new LibraryQueryService(database);

        try
        {
            // cancellationToken is auto-bound to HttpContext.RequestAborted —
            // if the browser's own Stop button aborts the fetch (or the
            // connection just drops), this actually stops the walk, not just
            // the client's wait for a response (see ScanService.Scan).
            var scanResult = new ScanService(
                new LibraryRepository(database), query, database, homeLabel: homeLabel, progressTracker: progressTracker)
                .Scan(request.Path, cancellationToken);
            var picasaResult = new PicasaImportService(database, query, new PicasaImportRepository(database)).Import();

            return Ok(new ScanResponse(scanResult, picasaResult));
        }
        finally
        {
            // So a later poll doesn't keep reporting this scan's final count
            // forever, as if it were still running.
            progressTracker.Clear(request.Path);
        }
    }

    /// <summary>
    /// Polled by the frontend (every second or so) while the request above
    /// is still in flight — its own single blocking request has no way to
    /// report partway through, so this reads the shared counter
    /// ScanService.Scan ticks as it walks instead. 0 both before a scan has
    /// started ticking yet and after one's already finished (Clear above) —
    /// the frontend already knows separately whether it's mid-scan, so it
    /// doesn't need this endpoint to distinguish those itself.
    /// </summary>
    [HttpGet("progress")]
    public ActionResult<ScanProgressResponse> GetProgress([FromQuery] string path) =>
        Ok(new ScanProgressResponse(progressTracker.Get(path)));
}
