using Curato.Core;
using Curato.Data;

namespace Curato.Tests;

/// <summary>
/// PLAN.md §10 — the Lightbox toolbar's Rotate Left/Right buttons. Purely a
/// database write; RotateMediaItem never touches the original file.
/// </summary>
public class RotateMediaItemTests
{
    [Fact]
    public void Rotating_right_from_fresh_records_a_90_degree_edit()
    {
        WithTempLibrary((database, repository, query) =>
        {
            var mediaItemId = SeedItem(repository);

            repository.RotateMediaItem(mediaItemId, 90);

            var edit = Assert.Single(query.GetEditsForRelativePath("photo.jpg"));
            Assert.Equal(EditOperationType.Rotate, edit.OperationType);
            Assert.Contains("90", edit.Parameters);
        });
    }

    [Fact]
    public void Rotating_right_four_times_lands_back_at_zero_and_removes_the_edit()
    {
        WithTempLibrary((database, repository, query) =>
        {
            var mediaItemId = SeedItem(repository);

            repository.RotateMediaItem(mediaItemId, 90);
            repository.RotateMediaItem(mediaItemId, 90);
            repository.RotateMediaItem(mediaItemId, 90);
            repository.RotateMediaItem(mediaItemId, 90);

            Assert.Empty(query.GetEditsForRelativePath("photo.jpg"));
        });
    }

    [Fact]
    public void Rotating_left_from_fresh_wraps_around_to_270()
    {
        WithTempLibrary((database, repository, query) =>
        {
            var mediaItemId = SeedItem(repository);

            repository.RotateMediaItem(mediaItemId, -90);

            var edit = Assert.Single(query.GetEditsForRelativePath("photo.jpg"));
            Assert.Contains("270", edit.Parameters);
        });
    }

    [Fact]
    public void Rotating_twice_replaces_the_previous_edit_rather_than_accumulating_rows()
    {
        WithTempLibrary((database, repository, query) =>
        {
            var mediaItemId = SeedItem(repository);

            repository.RotateMediaItem(mediaItemId, 90);
            repository.RotateMediaItem(mediaItemId, 90);

            var edit = Assert.Single(query.GetEditsForRelativePath("photo.jpg"));
            Assert.Contains("180", edit.Parameters);
        });
    }

    private static long SeedItem(LibraryRepository repository)
    {
        var item = new MediaItem { RelativePath = "photo.jpg", QuickHash = "irrelevant", TakenAt = new DateTime(2026, 1, 1) };
        return repository.UpsertMediaItems([item])[0].Id;
    }

    private static void WithTempLibrary(Action<LibraryDatabase, LibraryRepository, LibraryQueryService> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-rotate-test-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            test(database, new LibraryRepository(database), new LibraryQueryService(database));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
