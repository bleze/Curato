using Curato.Core;
using Curato.Data;
using Microsoft.AspNetCore.Mvc;

namespace Curato.Api.Controllers;

/// <summary>
/// PLAN.md §9 — Tags/Albums browsing. A Tag *is* an Album: freeform
/// user-created tags and Picasa-imported star ratings/albums (§8) share the
/// same mechanism and the same browsing UI here.
///
/// Protected by the fallback auth policy (§15) — requires a logged-in session.
/// </summary>
[ApiController]
[Route("api/tags")]
public sealed class TagController : ControllerBase
{
    public sealed record MediaItemDto(long Id, string RelativePath, string Kind, DateTime TakenAt, string TakenAtSource, string? LiveVideoRelativePath);
    public sealed record TagDto(long Id, string Name, bool IsFavoriteShortcut, int ItemCount, string? CoverRelativePath);

    [HttpGet]
    public ActionResult<IReadOnlyList<TagDto>> List([FromQuery] string path)
    {
        if (!Directory.Exists(path))
        {
            return NotFound($"Folder not found: {path}");
        }

        var query = OpenQuery(path);
        var mediaById = query.GetAllMediaItems().ToDictionary(m => m.Id);

        var dtos = query.GetAllTags()
            .Select(tag =>
            {
                var members = query.GetMediaItemIdsForTag(tag.Id)
                    .Where(mediaById.ContainsKey)
                    .Select(id => mediaById[id])
                    .ToList();

                var cover = members
                    .Where(m => m.Kind == MediaKind.Photo)
                    .OrderByDescending(m => m.TakenAt)
                    .FirstOrDefault();

                return new TagDto(tag.Id, tag.Name, tag.IsFavoriteShortcut, members.Count, cover?.RelativePath);
            })
            .Where(t => t.ItemCount > 0)
            .OrderByDescending(t => t.IsFavoriteShortcut)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(dtos);
    }

    [HttpGet("{tagId}/items")]
    public ActionResult<IReadOnlyList<MediaItemDto>> Items(long tagId, [FromQuery] string path)
    {
        if (!Directory.Exists(path))
        {
            return NotFound($"Folder not found: {path}");
        }

        var query = OpenQuery(path);
        var mediaById = query.GetAllMediaItems().ToDictionary(m => m.Id);

        // Apple Live Photos (§3): a paired video is represented by its
        // photo's tile, same rule the main library view (§11) follows.
        var pairedVideoIds = mediaById.Values
            .Where(m => m.LivePhotoVideoItemId is not null)
            .Select(m => m.LivePhotoVideoItemId!.Value)
            .ToHashSet();

        var dtos = query.GetMediaItemIdsForTag(tagId)
            .Where(id => mediaById.ContainsKey(id) && !pairedVideoIds.Contains(id))
            .Select(id => mediaById[id])
            .OrderByDescending(m => m.TakenAt)
            .Select(m => new MediaItemDto(
                m.Id, m.RelativePath, m.Kind.ToString(), m.TakenAt, m.TakenAtSource.ToString(),
                m.LivePhotoVideoItemId is { } videoId && mediaById.TryGetValue(videoId, out var video)
                    ? video.RelativePath
                    : null))
            .ToList();

        return Ok(dtos);
    }

    private static LibraryQueryService OpenQuery(string path)
    {
        var database = new LibraryDatabase(path);
        database.EnsureSchema();
        return new LibraryQueryService(database);
    }
}
