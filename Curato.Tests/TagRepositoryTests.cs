using Curato.Core;
using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

/// <summary>Covers TagRepository's "Favorite Categories" CRUD/assignment — the
/// user-created, at-most-one-per-photo labels behind the star button on each
/// grid tile, built on the same Tag/MediaItemTag tables PicasaImportService's
/// own star-rating tag already uses (IsFavoriteShortcut=1).</summary>
public class TagRepositoryTests
{
    private static MediaItem Item(string relativePath, DateTime takenAt) => new()
    {
        RelativePath = relativePath,
        QuickHash = "irrelevant",
        TakenAt = takenAt
    };

    [Fact]
    public void Creating_categories_assigns_increasing_sort_order()
    {
        WithTempLibrary((tags, _, query, _) =>
        {
            var (resultA, a) = tags.CreateFavoriteCategory("Favorites");
            var (resultB, b) = tags.CreateFavoriteCategory("Kids");

            Assert.Equal(CreateFavoriteCategoryResult.Success, resultA);
            Assert.Equal(CreateFavoriteCategoryResult.Success, resultB);
            Assert.True(a!.IsFavoriteShortcut);
            Assert.True(b!.SortOrder > a.SortOrder);

            var listed = query.GetFavoriteCategories();
            Assert.Equal(["Favorites", "Kids"], listed.Select(t => t.Name).ToList());
        });
    }

    [Fact]
    public void Creating_a_category_with_an_empty_name_is_refused()
    {
        WithTempLibrary((tags, _, _, _) =>
        {
            var (result, category) = tags.CreateFavoriteCategory("   ");

            Assert.Equal(CreateFavoriteCategoryResult.EmptyName, result);
            Assert.Null(category);
        });
    }

    [Fact]
    public void Creating_a_category_with_a_name_already_taken_is_refused()
    {
        WithTempLibrary((tags, _, _, _) =>
        {
            tags.CreateFavoriteCategory("Favorites");
            var (result, category) = tags.CreateFavoriteCategory("Favorites");

            Assert.Equal(CreateFavoriteCategoryResult.NameTaken, result);
            Assert.Null(category);
        });
    }

    [Fact]
    public void Deleting_a_category_removes_it_and_clears_its_assignments()
    {
        WithTempLibrary((tags, repository, query, database) =>
        {
            RunScanPass(repository, query, database, [Item("a.jpg", new DateTime(2026, 1, 1, 9, 0, 0))]);
            var itemId = query.GetAllMediaItems()[0].Id;
            var (_, category) = tags.CreateFavoriteCategory("Favorites");
            tags.SetMediaItemFavoriteCategory(itemId, category!.Id);

            Assert.True(tags.DeleteFavoriteCategory(category.Id));

            Assert.Empty(query.GetFavoriteCategories());
            Assert.Empty(query.GetFavoriteCategoryAssignments());
        });
    }

    [Fact]
    public void Deleting_an_unknown_category_returns_false()
    {
        WithTempLibrary((tags, _, _, _) => Assert.False(tags.DeleteFavoriteCategory(999)));
    }

    [Fact]
    public void Deleting_refuses_a_plain_non_favorite_tag()
    {
        WithTempLibrary((tags, _, query, database) =>
        {
            var plainTagId = new PicasaImportRepository(database).GetOrCreateTag("Vacation 2019");

            Assert.False(tags.DeleteFavoriteCategory(plainTagId));
            Assert.Contains(query.GetAllTags(), t => t.Id == plainTagId);
        });
    }

    [Fact]
    public void Setting_a_favorite_category_can_later_be_cleared()
    {
        WithTempLibrary((tags, repository, query, database) =>
        {
            RunScanPass(repository, query, database, [Item("a.jpg", new DateTime(2026, 1, 1, 9, 0, 0))]);
            var itemId = query.GetAllMediaItems()[0].Id;
            var (_, category) = tags.CreateFavoriteCategory("Favorites");

            Assert.Equal(SetFavoriteCategoryResult.Success, tags.SetMediaItemFavoriteCategory(itemId, category!.Id));
            Assert.Equal(category.Id, query.GetFavoriteCategoryAssignments()[itemId]);

            Assert.Equal(SetFavoriteCategoryResult.Success, tags.SetMediaItemFavoriteCategory(itemId, null));
            Assert.False(query.GetFavoriteCategoryAssignments().ContainsKey(itemId));
        });
    }

    [Fact]
    public void Setting_a_new_favorite_category_replaces_the_previous_one()
    {
        WithTempLibrary((tags, repository, query, database) =>
        {
            RunScanPass(repository, query, database, [Item("a.jpg", new DateTime(2026, 1, 1, 9, 0, 0))]);
            var itemId = query.GetAllMediaItems()[0].Id;
            var (_, favorites) = tags.CreateFavoriteCategory("Favorites");
            var (_, kids) = tags.CreateFavoriteCategory("Kids");

            tags.SetMediaItemFavoriteCategory(itemId, favorites!.Id);
            tags.SetMediaItemFavoriteCategory(itemId, kids!.Id);

            Assert.Equal(kids.Id, query.GetFavoriteCategoryAssignments()[itemId]);
        });
    }

    [Fact]
    public void Setting_a_favorite_category_on_an_unknown_media_item_returns_not_found()
    {
        WithTempLibrary((tags, _, _, _) =>
        {
            var (_, category) = tags.CreateFavoriteCategory("Favorites");

            Assert.Equal(SetFavoriteCategoryResult.MediaItemNotFound, tags.SetMediaItemFavoriteCategory(999, category!.Id));
        });
    }

    [Fact]
    public void Setting_an_unknown_category_returns_not_found()
    {
        WithTempLibrary((tags, repository, query, database) =>
        {
            RunScanPass(repository, query, database, [Item("a.jpg", new DateTime(2026, 1, 1, 9, 0, 0))]);
            var itemId = query.GetAllMediaItems()[0].Id;

            Assert.Equal(SetFavoriteCategoryResult.CategoryNotFound, tags.SetMediaItemFavoriteCategory(itemId, 999));
        });
    }

    [Fact]
    public void GetFavoriteCategories_excludes_plain_tags()
    {
        WithTempLibrary((tags, _, query, database) =>
        {
            new PicasaImportRepository(database).GetOrCreateTag("Vacation 2019");
            tags.CreateFavoriteCategory("Favorites");

            Assert.Equal(["Favorites"], query.GetFavoriteCategories().Select(t => t.Name).ToList());
        });
    }

    /// <summary>
    /// PicasaImportService creates its own star-rating tag the exact same way
    /// (Tag.IsFavoriteShortcut=1, via GetOrCreateTag) — this just confirms
    /// that tag surfaces automatically as a favorite category, with no
    /// separate import step needed for it to show up in Settings/the star
    /// picker alongside user-created ones.
    /// </summary>
    [Fact]
    public void A_Picasa_imported_star_tag_appears_as_a_favorite_category()
    {
        WithTempLibrary((tags, repository, query, database) =>
        {
            RunScanPass(repository, query, database, [Item("a.jpg", new DateTime(2026, 1, 1, 9, 0, 0))]);
            var itemId = query.GetAllMediaItems()[0].Id;
            var picasaRepository = new PicasaImportRepository(database);

            var favoriteTagId = picasaRepository.GetOrCreateTag("Favorite", isFavoriteShortcut: true);
            picasaRepository.AddMediaItemToTag(favoriteTagId, itemId);

            var categories = query.GetFavoriteCategories();
            Assert.Equal(["Favorite"], categories.Select(t => t.Name).ToList());
            Assert.Equal(favoriteTagId, query.GetFavoriteCategoryAssignments()[itemId]);

            // The star button's "at most one category" rule applies to a
            // Picasa-starred photo exactly the same as a user-picked one.
            var (_, kids) = tags.CreateFavoriteCategory("Kids");
            tags.SetMediaItemFavoriteCategory(itemId, kids!.Id);
            Assert.Equal(kids.Id, query.GetFavoriteCategoryAssignments()[itemId]);
        });
    }

    private static void RunScanPass(
        LibraryRepository repository, LibraryQueryService query, LibraryDatabase database, MediaItem[] rawItems) =>
        new ScanService(repository, query, database).ScanItems(rawItems);

    private static void WithTempLibrary(Action<TagRepository, LibraryRepository, LibraryQueryService, LibraryDatabase> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-tag-repo-test-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            test(new TagRepository(database), new LibraryRepository(database), new LibraryQueryService(database), database);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
