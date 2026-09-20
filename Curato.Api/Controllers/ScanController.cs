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
/// Protected by the fallback auth policy (§15) — requires a logged-in session.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class ScanController : ControllerBase
{
    public sealed record ScanRequest(string Path);

    [HttpPost]
    public ActionResult<ScanResult> Scan([FromBody] ScanRequest request)
    {
        if (!Directory.Exists(request.Path))
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        var database = new LibraryDatabase(request.Path);
        database.EnsureSchema();
        var service = new ScanService(new LibraryRepository(database), new LibraryQueryService(database));

        return Ok(service.Scan(request.Path));
    }
}
