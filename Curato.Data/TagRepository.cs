using Curato.Core;
using Dapper;

namespace Curato.Data;

public enum CreateFavoriteCategoryResult { Success, EmptyName, NameTaken }

public enum SetFavoriteCategoryResult { Success, MediaItemNotFound, CategoryNotFound }

/// <summary>
/// User-facing CRUD for "Favorite Categories" (Settings) — named,
/// at-most-one-per-photo labels (e.g. "Favorites", "Kids") assigned via the
/// star button on each grid tile. Built on the same Tag/MediaItemTag tables
/// PLAN.md §9's freeform tags/albums already use, distinguished only by
/// IsFavoriteShortcut=1 — the exact flag PicasaImportService already sets on
/// its own star-rating tag, so a Picasa-starred photo and a user-picked
/// category share one slot per MediaItem. Read-only access lives on
/// LibraryQueryService (GetFavoriteCategories/GetFavoriteCategoryAssignments)
/// alongside every other Tag query; this only ever writes.
/// </summary>
public sealed class TagRepository(LibraryDatabase database)
{
    public (CreateFavoriteCategoryResult Result, Tag? Category) CreateFavoriteCategory(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return (CreateFavoriteCategoryResult.EmptyName, null);
        }

        using var connection = database.OpenConnection();
        var existing = connection.ExecuteScalar<long?>("SELECT Id FROM Tag WHERE Name = @Name", new { Name = trimmed });
        if (existing is not null)
        {
            return (CreateFavoriteCategoryResult.NameTaken, null);
        }

        var nextSortOrder = connection.ExecuteScalar<int>("SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM Tag");
        var id = connection.ExecuteScalar<long>("""
            INSERT INTO Tag (Name, IsFavoriteShortcut, SortOrder)
            VALUES (@Name, 1, @SortOrder)
            RETURNING Id
            """, new { Name = trimmed, SortOrder = nextSortOrder });

        return (CreateFavoriteCategoryResult.Success, new Tag
        {
            Id = id,
            Name = trimmed,
            IsFavoriteShortcut = true,
            SortOrder = nextSortOrder,
        });
    }

    /// <summary>Refuses to delete a plain (non-favorite) Tag — this is only ever the Settings "Favorite Categories" list, not a general tag/album delete.</summary>
    public bool DeleteFavoriteCategory(long tagId)
    {
        using var connection = database.OpenConnection();
        var rows = connection.Execute("DELETE FROM Tag WHERE Id = @Id AND IsFavoriteShortcut = 1", new { Id = tagId });
        return rows > 0;
    }

    /// <summary>
    /// At most one favorite category per photo, matching the single
    /// filled/unfilled star in the UI — clears any previous pick (a
    /// Picasa-imported "Favorite" star included) before applying a new one.
    /// Pass null to just clear the current pick.
    /// </summary>
    public SetFavoriteCategoryResult SetMediaItemFavoriteCategory(long mediaItemId, long? tagId)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var mediaItemExists = connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM MediaItem WHERE Id = @Id", new { Id = mediaItemId }, transaction) > 0;
        if (!mediaItemExists)
        {
            transaction.Rollback();
            return SetFavoriteCategoryResult.MediaItemNotFound;
        }

        if (tagId is { } id)
        {
            var categoryExists = connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM Tag WHERE Id = @Id AND IsFavoriteShortcut = 1", new { Id = id }, transaction) > 0;
            if (!categoryExists)
            {
                transaction.Rollback();
                return SetFavoriteCategoryResult.CategoryNotFound;
            }
        }

        connection.Execute("""
            DELETE FROM MediaItemTag
            WHERE MediaItemId = @MediaItemId
              AND TagId IN (SELECT Id FROM Tag WHERE IsFavoriteShortcut = 1)
            """, new { MediaItemId = mediaItemId }, transaction);

        if (tagId is { } newTagId)
        {
            connection.Execute("""
                INSERT INTO MediaItemTag (TagId, MediaItemId, AddedAt, ManualSortOrder)
                VALUES (@TagId, @MediaItemId, @AddedAt, NULL)
                """, new { TagId = newTagId, MediaItemId = mediaItemId, AddedAt = DateTime.UtcNow.ToString("O") }, transaction);
        }

        transaction.Commit();
        return SetFavoriteCategoryResult.Success;
    }
}
