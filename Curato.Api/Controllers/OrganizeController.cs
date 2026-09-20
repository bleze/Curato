using Curato.Data;
using Curato.Organizer;
using Microsoft.AspNetCore.Mvc;

namespace Curato.Api.Controllers;

/// <summary>
/// PLAN.md §4 — physically arranges whatever the last scan found into the
/// folder-naming template's structure. Deliberately a separate action from
/// /api/scan: scanning only ever indexes/clusters, so a user can review
/// what's proposed (via /api/library) before anything on disk changes.
///
/// Protected by the fallback auth policy (§15) — requires a logged-in session.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class OrganizeController : ControllerBase
{
    public sealed record OrganizeRequest(string Path);

    [HttpPost]
    public ActionResult<OrganizeResult> Organize([FromBody] OrganizeRequest request)
    {
        if (!Directory.Exists(request.Path))
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        var database = new LibraryDatabase(request.Path);
        database.EnsureSchema();

        var service = new OrganizeService(
            database,
            new LibraryQueryService(database),
            new OrganizeRepository(database));

        return Ok(service.Organize());
    }
}
