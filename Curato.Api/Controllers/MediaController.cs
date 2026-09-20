using Curato.Media;
using Microsoft.AspNetCore.Mvc;

namespace Curato.Api.Controllers;

/// <summary>
/// Serves thumbnails for the web UI's grid (§11). NOT YET behind auth (§15) —
/// same caveat as the other controllers here.
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
    [HttpGet("thumbnail")]
    public IActionResult GetThumbnail(
        [FromQuery] string libraryPath, [FromQuery] string relativePath, [FromQuery] int size = 320)
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
            var jpegBytes = ThumbnailGenerator.GenerateJpeg(fullPath, size);
            return File(jpegBytes, "image/jpeg");
        }
        catch (Exception) when (!System.Diagnostics.Debugger.IsAttached)
        {
            // Formats ImageSharp can't decode yet (RAW/HEIC/video — §1) or a
            // corrupt file both land here; no thumbnail rather than a 500.
            return NotFound();
        }
    }
}
