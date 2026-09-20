using Curato.Data;
using Microsoft.AspNetCore.Mvc;

namespace Curato.Api.Controllers;

/// <summary>
/// PLAN.md §4 — the first real driver of the Settings surface the plan has
/// gestured at since §4a/§5 (Picasa-auto-import opt-out, recycle-bin
/// retention). Kept intentionally minimal: just the one setting actually
/// needed right now (the shared "library folder" destination for iCloud
/// sync and any future external-source puller), not a generic key-value
/// settings framework built ahead of a second concrete need.
///
/// Protected by the fallback auth policy (§15) — requires a logged-in session.
/// </summary>
[ApiController]
[Route("api/settings")]
public sealed class SettingsController(ServerDatabase database) : ControllerBase
{
    public sealed record LibraryFolderResponse(string? Path);
    public sealed record SetLibraryFolderRequest(string Path);

    [HttpGet("library-folder")]
    public ActionResult<LibraryFolderResponse> GetLibraryFolder() =>
        Ok(new LibraryFolderResponse(new LibraryFolderRepository(database).Get()));

    [HttpPut("library-folder")]
    public IActionResult SetLibraryFolder([FromBody] SetLibraryFolderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return BadRequest("Path is required.");
        }

        if (!Directory.Exists(request.Path))
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        new LibraryFolderRepository(database).Set(Path.GetFullPath(request.Path));
        return Ok();
    }
}
