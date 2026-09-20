using Curato.Data;
using Curato.Organizer;
using Microsoft.AspNetCore.Mvc;

namespace Curato.Api.Controllers;

/// <summary>
/// PLAN.md §8 — opt-in, one-time import of legacy Picasa data (`.picasa.ini`,
/// `contacts.xml`, `Picasa2Albums.ini`) found under an already-scanned
/// library. Never triggered automatically by /api/scan; the user asks for it
/// explicitly.
///
/// Protected by the fallback auth policy (§15) — requires a logged-in session.
/// </summary>
[ApiController]
[Route("api/picasa-import")]
public sealed class PicasaImportController : ControllerBase
{
    public sealed record PicasaImportRequest(string Path);

    [HttpPost]
    public ActionResult<PicasaImportResult> Import([FromBody] PicasaImportRequest request)
    {
        if (!Directory.Exists(request.Path))
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        var database = new LibraryDatabase(request.Path);
        database.EnsureSchema();

        var service = new PicasaImportService(
            database,
            new LibraryQueryService(database),
            new PicasaImportRepository(database));

        return Ok(service.Import());
    }
}
