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

    public sealed record BrowseFolderEntry(string Name, string Path);
    public sealed record BrowseFoldersResponse(string? Path, string? ParentPath, IReadOnlyList<BrowseFolderEntry> Folders);

    /// <summary>
    /// Backs the Library Folder field's "Browse…" button — lists the
    /// server's own filesystem (not the browser's; Curato is self-hosted,
    /// often on a different machine than the browser viewing it, so a
    /// native OS folder picker in the browser couldn't give a path the
    /// server itself could actually open). No path (or an empty one) lists
    /// available roots (drive letters on Windows, "/" elsewhere) rather
    /// than refusing — that's the natural top of the tree to start from.
    /// Same trust boundary as the plain text field already had: a folder
    /// path here is only ever chosen by the one authenticated admin this
    /// app already assumes, same as always being free to type any absolute
    /// path directly.
    /// </summary>
    [HttpGet("browse-folders")]
    public ActionResult<BrowseFoldersResponse> BrowseFolders([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var roots = OperatingSystem.IsWindows() ? Directory.GetLogicalDrives() : ["/"];
            var rootEntries = roots.Select(r => new BrowseFolderEntry(r, r)).ToList();
            return Ok(new BrowseFoldersResponse(null, null, rootEntries));
        }

        if (!Directory.Exists(path))
        {
            return NotFound($"Folder not found: {path}");
        }

        var fullPath = Path.GetFullPath(path);
        string? parentPath;
        try
        {
            parentPath = Directory.GetParent(fullPath)?.FullName;
        }
        catch
        {
            parentPath = null;
        }

        var entries = new List<BrowseFolderEntry>();
        try
        {
            foreach (var dir in Directory.GetDirectories(fullPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.') || IsHidden(dir))
                {
                    continue;
                }

                entries.Add(new BrowseFolderEntry(name, dir));
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Some subfolders can't be listed (permissions) — show what we can rather than refusing the whole request.
        }

        return Ok(new BrowseFoldersResponse(fullPath, parentPath, entries));
    }

    // Dot-prefixed names are the cross-platform convention (also catches
    // .curato itself, see LibraryDatabase); the attribute check additionally
    // hides Windows folders marked Hidden some other way (e.g. "System Volume
    // Information"). Swallow failures reading attributes rather than letting
    // one unreadable entry take down the whole listing.
    private static bool IsHidden(string dir)
    {
        try
        {
            return (System.IO.File.GetAttributes(dir) & FileAttributes.Hidden) != 0;
        }
        catch
        {
            return false;
        }
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
