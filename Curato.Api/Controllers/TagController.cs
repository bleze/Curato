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
    public sealed record MediaItemDto(long Id, string RelativePath, string Kind, DateTime TakenAt, string TakenAtSource, string? LiveVideoRelativePath, long? FavoriteCategoryId);
    public sealed record TagDto(long Id, string Name, bool IsFavoriteShortcut, int ItemCount, string? CoverRelativePath);
    public sealed record FavoriteCategoryDto(long Id, string Name);
    public sealed record CreateFavoriteCategoryRequest(string Path, string Name);
    public sealed record SetMediaItemFavoriteCategoryRequest(string Path, long MediaItemId, long? TagId);

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
        var favoriteCategoryAssignments = query.GetFavoriteCategoryAssignments();

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
                    : null,
                favoriteCategoryAssignments.TryGetValue(m.Id, out var favoriteId) ? favoriteId : null))
            .ToList();

        return Ok(dtos);
    }

    /// <summary>All favorite categories, including empty ones (unlike <see cref="List"/>) — for Settings and the star picker.</summary>
    [HttpGet("favorites")]
    public ActionResult<IReadOnlyList<FavoriteCategoryDto>> ListFavoriteCategories([FromQuery] string path)
    {
        if (!Directory.Exists(path))
        {
            return NotFound($"Folder not found: {path}");
        }

        var dtos = OpenQuery(path).GetFavoriteCategories()
            .Select(t => new FavoriteCategoryDto(t.Id, t.Name))
            .ToList();

        return Ok(dtos);
    }

    [HttpPost("favorites")]
    public IActionResult CreateFavoriteCategory([FromBody] CreateFavoriteCategoryRequest request)
    {
        if (!Directory.Exists(request.Path))
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        var database = new LibraryDatabase(request.Path);
        database.EnsureSchema();
        var (result, category) = new TagRepository(database).CreateFavoriteCategory(request.Name);

        return result switch
        {
            CreateFavoriteCategoryResult.Success => Ok(new FavoriteCategoryDto(category!.Id, category.Name)),
            CreateFavoriteCategoryResult.EmptyName => BadRequest("Name cannot be empty."),
            CreateFavoriteCategoryResult.NameTaken => Conflict($"A tag/category named \"{request.Name}\" already exists."),
            _ => StatusCode(500),
        };
    }

    [HttpDelete("favorites/{tagId:long}")]
    public IActionResult DeleteFavoriteCategory(long tagId, [FromQuery] string path)
    {
        if (!Directory.Exists(path))
        {
            return NotFound($"Folder not found: {path}");
        }

        var database = new LibraryDatabase(path);
        database.EnsureSchema();

        return new TagRepository(database).DeleteFavoriteCategory(tagId)
            ? NoContent()
            : NotFound($"No favorite category with id {tagId}");
    }

    /// <summary>The star button on a grid tile — at most one favorite category per photo, see TagRepository.SetMediaItemFavoriteCategory.</summary>
    [HttpPost("favorite-category")]
    public IActionResult SetMediaItemFavoriteCategory([FromBody] SetMediaItemFavoriteCategoryRequest request)
    {
        if (!Directory.Exists(request.Path))
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        var database = new LibraryDatabase(request.Path);
        database.EnsureSchema();
        var result = new TagRepository(database).SetMediaItemFavoriteCategory(request.MediaItemId, request.TagId);

        return result switch
        {
            SetFavoriteCategoryResult.Success => NoContent(),
            SetFavoriteCategoryResult.MediaItemNotFound => NotFound($"No MediaItem with id {request.MediaItemId}"),
            SetFavoriteCategoryResult.CategoryNotFound => NotFound($"No favorite category with id {request.TagId}"),
            _ => StatusCode(500),
        };
    }

    private static LibraryQueryService OpenQuery(string path)
    {
        var database = new LibraryDatabase(path);
        database.EnsureSchema();
        return new LibraryQueryService(database);
    }
}
