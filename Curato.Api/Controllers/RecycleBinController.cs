using Curato.Data;
using Curato.Media;
using Curato.Organizer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

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
    public sealed record DeleteEventsRequest(string Path, IReadOnlyList<long> EventIds, string Source = "Manual");
    public sealed record DeleteItemsRequest(string Path, IReadOnlyList<long> MediaItemIds, string Source = "Manual");
    public sealed record ItemIdRequest(string Path, long DeletedItemId);

    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

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

    /// <summary>
    /// Deletes every MediaItem belonging to one or more whole Events —
    /// confirmed live as a real gap: selecting an Event only ever offered
    /// Merge/Group-into-Trip/Fix dates, never a way to remove the Event
    /// entirely short of deleting each of its photos one at a time from the
    /// grid. See RecycleBinService.DeleteMediaItems for the actual bulk
    /// delete/cleanup logic — this just resolves the selected Events down to
    /// their member MediaItemIds first.
    /// </summary>
    [HttpPost("delete-events")]
    public ActionResult<IReadOnlyList<DeletedItemDto>> DeleteEvents([FromBody] DeleteEventsRequest request)
    {
        if (OpenLibrary(request.Path) is not { } opened)
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        var query = new LibraryQueryService(opened.Database);
        var eventIds = new HashSet<long>(request.EventIds);
        var memberIds = query.GetAllEvents()
            .Where(e => eventIds.Contains(e.Id))
            .SelectMany(e => e.MediaItemIds)
            .ToList();

        var deleted = opened.Service.DeleteMediaItems(memberIds, request.Source);
        return Ok(deleted.Select(ToDto).ToList());
    }

    /// <summary>
    /// Deletes a batch of individually-selected MediaItems at once — the
    /// grid's own photo-selection bar's "Move to Recycle Bin" (as opposed to
    /// <see cref="DeleteEvents"/>, which deletes whole Events). Same
    /// RecycleBinService.DeleteMediaItems batch path, just with ids supplied
    /// directly rather than resolved from a set of Events first.
    /// </summary>
    [HttpPost("delete-items")]
    public ActionResult<IReadOnlyList<DeletedItemDto>> DeleteItems([FromBody] DeleteItemsRequest request)
    {
        if (OpenLibrary(request.Path) is not { } opened)
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        var deleted = opened.Service.DeleteMediaItems(request.MediaItemIds, request.Source);
        return Ok(deleted.Select(ToDto).ToList());
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
            var isVideo = MediaFileScanner.IsVideoExtension(Path.GetExtension(trashedFullPath));
            var jpegBytes = isVideo
                ? VideoThumbnailGenerator.GenerateJpeg(trashedFullPath, size)
                : ThumbnailGenerator.GenerateJpeg(trashedFullPath, size, opened.Service.GetEditsForDeletedItem(deletedItemId));
            return File(jpegBytes, "image/jpeg");
        }
        catch (Exception) when (!System.Diagnostics.Debugger.IsAttached)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// The unmodified trashed file — backs the Recycle Bin's fullscreen
    /// viewer's video playback (no Edit pipeline for video, same as
    /// MediaController). Photos go through /thumbnail instead (at a larger
    /// size), not this endpoint, since Edit rows (e.g. rotation) still need
    /// applying and a raw HEIC/RAW file can't just be &lt;img&gt;-ed directly.
    /// Mirrors MediaController's own /raw endpoint, just resolved against
    /// the trash location instead of the library root + relativePath.
    /// </summary>
    [HttpGet("raw")]
    public IActionResult GetRawFile([FromQuery] string path, [FromQuery] long deletedItemId)
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

        if (!ContentTypeProvider.TryGetContentType(trashedFullPath, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        return PhysicalFile(trashedFullPath, contentType, enableRangeProcessing: true);
    }
}
