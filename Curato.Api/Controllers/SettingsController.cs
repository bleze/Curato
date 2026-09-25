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

    public sealed record HomeLocationResponse(string? Label);
    public sealed record SetHomeLocationRequest(string? Label);

    /// <summary>
    /// A custom label for wherever "home" actually is (e.g. "Kirke Hyllinge"),
    /// shown instead of Nominatim's own reverse-geocoded name for anything
    /// GpsTripGate detects as nearby — confirmed live that Nominatim's result
    /// for a real home address can be as coarse as a whole county. Optional;
    /// clearing it (empty/whitespace) reverts to plain reverse-geocoding
    /// everywhere. See GpsTripGate.HomeOverrideLabel for how it's applied.
    /// </summary>
    [HttpGet("home-location")]
    public ActionResult<HomeLocationResponse> GetHomeLocation() =>
        Ok(new HomeLocationResponse(new HomeLocationRepository(database).Get()));

    [HttpPut("home-location")]
    public IActionResult SetHomeLocation([FromBody] SetHomeLocationRequest request)
    {
        var repository = new HomeLocationRepository(database);
        if (string.IsNullOrWhiteSpace(request.Label))
        {
            repository.Clear();
        }
        else
        {
            repository.Set(request.Label.Trim());
        }

        return Ok();
    }
}
