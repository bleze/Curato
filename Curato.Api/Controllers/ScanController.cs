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
public sealed class ScanController(LibraryOperationGate gate, ServerDatabase serverDatabase) : ControllerBase
{
    public sealed record ScanRequest(string Path);
    public sealed record ScanResponse(ScanResult Scan, PicasaImportResult PicasaImport);

    [HttpPost]
    public async Task<ActionResult<ScanResponse>> Scan([FromBody] ScanRequest request)
    {
        if (!Directory.Exists(request.Path))
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        var homeLabel = new HomeLocationRepository(serverDatabase).Get();

        using var release = await gate.AcquireAsync(request.Path);
        var database = new LibraryDatabase(request.Path);
        database.EnsureSchema();
        var query = new LibraryQueryService(database);

        var scanResult = new ScanService(new LibraryRepository(database), query, database, homeLabel: homeLabel).Scan(request.Path);
        var picasaResult = new PicasaImportService(database, query, new PicasaImportRepository(database)).Import();

        return Ok(new ScanResponse(scanResult, picasaResult));
    }
}
