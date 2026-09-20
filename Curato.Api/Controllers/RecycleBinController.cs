using Curato.Data;
using Curato.Organizer;
using Microsoft.AspNetCore.Mvc;

namespace Curato.Api.Controllers;

/// <summary>
/// PLAN.md §5 — the one safe path for removing a MediaItem from a library.
/// Nothing in Curato calls File.Delete directly; everything routes through
/// <see cref="RecycleBinService"/> so a mis-click is recoverable for
/// <see cref="RecycleBinService.RetentionPeriod"/>.
///
/// Protected by the fallback auth policy (§15) — requires a logged-in session.
/// </summary>
[ApiController]
[Route("api/recyclebin")]
public sealed class RecycleBinController : ControllerBase
{
    public sealed record DeleteRequest(string Path, long MediaItemId, string Source = "Manual");
    public sealed record ItemIdRequest(string Path, long DeletedItemId);

    public sealed record DeletedItemDto(
        long Id, string OriginalRelativePath, string Kind, DateTime DeletedAt, DateTime PurgeAt, string Source);

    private static (LibraryDatabase Database, RecycleBinService Service)? OpenLibrary(string path)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        var database = new LibraryDatabase(path);
        database.EnsureSchema();
        return (database, new RecycleBinService(database, new RecycleBinRepository(database)));
    }

    private static DeletedItemDto ToDto(Curato.Organizer.DeletedItemDto d) =>
        new(d.Id, d.OriginalRelativePath, d.Kind, d.DeletedAt, d.PurgeAt, d.Source);

    [HttpGet]
    public ActionResult<IReadOnlyList<DeletedItemDto>> List([FromQuery] string path)
    {
        if (OpenLibrary(path) is not { } opened)
        {
            return NotFound($"Folder not found: {path}");
        }

        return Ok(opened.Service.ListPendingDeletion().Select(ToDto).ToList());
    }

    [HttpPost("delete")]
    public ActionResult<DeletedItemDto> Delete([FromBody] DeleteRequest request)
    {
        if (OpenLibrary(request.Path) is not { } opened)
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        var result = opened.Service.DeleteMediaItem(request.MediaItemId, request.Source);
        return result is null ? NotFound($"No MediaItem with id {request.MediaItemId}") : Ok(ToDto(result));
    }

    [HttpPost("restore")]
    public IActionResult Restore([FromBody] ItemIdRequest request)
    {
        if (OpenLibrary(request.Path) is not { } opened)
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        return opened.Service.Restore(request.DeletedItemId)
            ? NoContent()
            : NotFound($"No deleted item with id {request.DeletedItemId}");
    }

    [HttpPost("purge")]
    public IActionResult Purge([FromBody] ItemIdRequest request)
    {
        if (OpenLibrary(request.Path) is not { } opened)
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        return opened.Service.DeletePermanently(request.DeletedItemId)
            ? NoContent()
            : NotFound($"No deleted item with id {request.DeletedItemId}");
    }

    [HttpGet("thumbnail")]
    public IActionResult GetThumbnail([FromQuery] string path, [FromQuery] long deletedItemId, [FromQuery] int size = 320)
    {
        if (OpenLibrary(path) is not { } opened)
        {
            return NotFound($"Folder not found: {path}");
        }

        var trashedFullPath = opened.Service.GetTrashedFileFullPath(deletedItemId);
        if (trashedFullPath is null || !System.IO.File.Exists(trashedFullPath))
        {
            return NotFound();
        }

        try
        {
            var jpegBytes = Curato.Media.ThumbnailGenerator.GenerateJpeg(trashedFullPath, size);
            return File(jpegBytes, "image/jpeg");
        }
        catch (Exception) when (!System.Diagnostics.Debugger.IsAttached)
        {
            return NotFound();
        }
    }
}
