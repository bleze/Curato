using Curato.Data;
using Curato.Media;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace Curato.Api.Controllers;

/// <summary>
/// Serves rendered images for the web UI's grid and fullscreen viewer (§11),
/// applying each MediaItem's non-destructive Edit pipeline first (§10) so
/// imported Picasa crops/rotations show up correctly, and caching the
/// result to disk (ThumbnailCache) so repeated requests for the same
/// rendered output skip the decode/edit/resize/encode cost. Protected by
/// the fallback auth policy (§15) — an `&lt;img&gt;` tag still sends the
/// session cookie automatically since this is a same-origin request.
///
/// Path-traversal note (§15): <paramref name="relativePath"/> is resolved
/// against <paramref name="libraryPath"/> and the result is checked to still
/// be inside it before the file is touched, exactly the kind of check §15
/// calls out as needed on every filesystem-touching endpoint.
/// </summary>
[ApiController]
[Route("api/media")]
public sealed class MediaController : ControllerBase
{
    // Big enough to fill most screens without serving a multi-megapixel RAW
    // derivative in full for every viewer open; a real "download original"
    // action (§10) is a separate, unscaled path, not built yet.
    private const int ViewerMaxDimension = 2400;

    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    /// <summary>
    /// The unmodified file, for video playback (no Edit pipeline applies to
    /// video yet, §10) and range requests so the browser can seek/scrub.
    /// </summary>
    [HttpGet("raw")]
    public IActionResult GetRawFile([FromQuery] string libraryPath, [FromQuery] string relativePath)
    {
        var root = Path.GetFullPath(libraryPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));

        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("relativePath escapes the library root.");
        }

        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound();
        }

        if (!ContentTypeProvider.TryGetContentType(fullPath, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        return PhysicalFile(fullPath, contentType, enableRangeProcessing: true);
    }

    [HttpGet("thumbnail")]
    public IActionResult GetThumbnail(
        [FromQuery] string libraryPath, [FromQuery] string relativePath, [FromQuery] int size = 320) =>
        RenderImage(libraryPath, relativePath, size);

    [HttpGet("full")]
    public IActionResult GetFullImage([FromQuery] string libraryPath, [FromQuery] string relativePath) =>
        RenderImage(libraryPath, relativePath, ViewerMaxDimension);

    private IActionResult RenderImage(string libraryPath, string relativePath, int maxDimension)
    {
        var root = Path.GetFullPath(libraryPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));

        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("relativePath escapes the library root.");
        }

        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound();
        }

        try
        {
            var database = new LibraryDatabase(libraryPath);
            database.EnsureSchema();
            var edits = new LibraryQueryService(database).GetEditsForRelativePath(relativePath);

            var jpegBytes = ThumbnailCache.GetOrGenerate(
                root, relativePath, new FileInfo(fullPath), maxDimension, edits,
                () => ThumbnailGenerator.GenerateJpeg(fullPath, maxDimension, edits));
            return File(jpegBytes, "image/jpeg");
        }
        catch (Exception) when (!System.Diagnostics.Debugger.IsAttached)
        {
            // Formats Skia can't decode yet (RAW/HEIC/video — §1) or a
            // corrupt file both land here; no image rather than a 500.
            return NotFound();
        }
    }
}
