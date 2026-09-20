using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

public class RecycleBinServiceTests
{
    [Fact]
    public void Deleting_a_media_item_moves_its_file_to_trash_and_drops_the_row()
    {
        WithTempLibrary((root, database, repository, service) =>
        {
            var mediaItemId = SeedFile(root, database, "photo.jpg");

            var deleted = service.DeleteMediaItem(mediaItemId, "Manual");

            Assert.NotNull(deleted);
            Assert.False(File.Exists(Path.Combine(root, "photo.jpg")));
            Assert.Null(new LibraryQueryService(database).GetAllMediaItems().SingleOrDefault(m => m.Id == mediaItemId));
            Assert.Single(service.ListPendingDeletion());
        });
    }

    [Fact]
    public void Restoring_puts_the_file_back_at_its_original_path_and_clears_the_bin_entry()
    {
        WithTempLibrary((root, database, repository, service) =>
        {
            var mediaItemId = SeedFile(root, database, "sub/photo.jpg");
            var deleted = service.DeleteMediaItem(mediaItemId, "Manual")!;

            var restored = service.Restore(deleted.Id);

            Assert.True(restored);
            Assert.True(File.Exists(Path.Combine(root, "sub/photo.jpg")));
            Assert.Empty(service.ListPendingDeletion());
        });
    }

    [Fact]
    public void Deleting_permanently_removes_the_trashed_file_and_the_bin_entry()
    {
        WithTempLibrary((root, database, repository, service) =>
        {
            var mediaItemId = SeedFile(root, database, "photo.jpg");
            var deleted = service.DeleteMediaItem(mediaItemId, "Manual")!;
            var trashedPath = service.GetTrashedFileFullPath(deleted.Id);

            var purged = service.DeletePermanently(deleted.Id);

            Assert.True(purged);
            Assert.False(File.Exists(trashedPath));
            Assert.Empty(service.ListPendingDeletion());
        });
    }

    [Fact]
    public void Expired_items_are_purged_automatically_when_the_bin_is_listed()
    {
        WithTempLibrary((root, database, repository, service) =>
        {
            var mediaItemId = SeedFile(root, database, "photo.jpg");
            var deleted = service.DeleteMediaItem(mediaItemId, "Manual")!;
            var trashedPath = service.GetTrashedFileFullPath(deleted.Id)!;

            // Simulate the retention window having already elapsed.
            using (var connection = database.OpenConnection())
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE DeletedItem SET PurgeAt = @PurgeAt WHERE Id = @Id";
                command.Parameters.AddWithValue("@PurgeAt", DateTime.UtcNow.AddDays(-1).ToString("O"));
                command.Parameters.AddWithValue("@Id", deleted.Id);
                command.ExecuteNonQuery();
            }

            var pending = service.ListPendingDeletion();

            Assert.Empty(pending);
            Assert.False(File.Exists(trashedPath));
        });
    }

    private static long SeedFile(string root, LibraryDatabase database, string relativePath)
    {
        var fullPath = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, [1, 2, 3]);

        var item = new Curato.Core.MediaItem
        {
            RelativePath = relativePath,
            QuickHash = "irrelevant",
            TakenAt = new DateTime(2026, 1, 1),
        };
        var saved = new LibraryRepository(database).UpsertMediaItems([item]);
        return saved[0].Id;
    }

    private static void WithTempLibrary(
        Action<string, LibraryDatabase, RecycleBinRepository, RecycleBinService> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-recyclebin-test-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            var repository = new RecycleBinRepository(database);
            var service = new RecycleBinService(database, repository);
            test(tempDir.FullName, database, repository, service);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
