using Curato.Core;
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
    public void Deleting_a_live_photos_still_frame_also_recycles_its_paired_video()
    {
        WithTempLibrary((root, database, repository, service) =>
        {
            var photoId = SeedFile(root, database, "IMG_0001.JPG");
            var videoId = SeedFile(root, database, "IMG_0001.MOV");
            new LibraryRepository(database).SetLivePhotoPair(photoId, videoId);

            var deleted = service.DeleteMediaItem(photoId, "Manual");

            Assert.NotNull(deleted);
            Assert.False(File.Exists(Path.Combine(root, "IMG_0001.JPG")));
            Assert.False(File.Exists(Path.Combine(root, "IMG_0001.MOV")));
            Assert.Equal(2, service.ListPendingDeletion().Count);
        });
    }

    [Fact]
    public void Deleting_an_item_referenced_by_every_other_table_still_succeeds()
    {
        // Regression: RemoveMediaItem only ever cleaned up EventMediaItem —
        // confirmed live that deleting any item with a Tag, Edit, Face, or
        // Picasa-import marker (or an OrganizeMove log entry) failed
        // outright with a raw SQLite "FOREIGN KEY constraint failed".
        WithTempLibrary((root, database, repository, service) =>
        {
            var mediaItemId = SeedFile(root, database, "photo.jpg");

            using (var connection = database.OpenConnection())
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO Tag (Id, Name, IsFavoriteShortcut, SortOrder) VALUES (1, 'Favorites', 1, 0);
                    INSERT INTO MediaItemTag (TagId, MediaItemId, AddedAt) VALUES (1, @Id, @Now);
                    INSERT INTO Edit (MediaItemId, OperationType, Parameters, PipelineOrder, Source)
                        VALUES (@Id, 'Rotate', '{}', 0, 'Manual');
                    INSERT INTO Face (MediaItemId, BoundingBoxX, BoundingBoxY, BoundingBoxWidth, BoundingBoxHeight, Source, Ignored)
                        VALUES (@Id, 0, 0, 10, 10, 'Manual', 0);
                    INSERT INTO PicasaImportedMediaItem (MediaItemId) VALUES (@Id);
                    INSERT INTO OrganizeBatch (Id, StartedAt) VALUES (1, @Now);
                    INSERT INTO OrganizeMove (BatchId, MediaItemId, OldRelativePath, NewRelativePath)
                        VALUES (1, @Id, 'a.jpg', 'b.jpg');
                    """;
                command.Parameters.AddWithValue("@Id", mediaItemId);
                command.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
            }

            var deleted = service.DeleteMediaItem(mediaItemId, "Manual");

            Assert.NotNull(deleted);
            Assert.Null(new LibraryQueryService(database).GetAllMediaItems().SingleOrDefault(m => m.Id == mediaItemId));
        });
    }

    [Fact]
    public void A_trashed_items_rotation_survives_until_purged()
    {
        // The Recycle Bin's own viewer needs to keep showing a trashed
        // item's rotation, so RemoveMediaItem deliberately leaves its Edit
        // rows behind (unlike Tag/Face/Picasa-import/OrganizeMove, which are
        // gone immediately — see DeleteMediaItem's usual regression test
        // above). DeleteDeletedItemRow is what finally cleans them up, once
        // the item is actually purged.
        WithTempLibrary((root, database, repository, service) =>
        {
            var mediaItemId = SeedFile(root, database, "photo.jpg");
            new LibraryRepository(database).RotateMediaItem(mediaItemId, 90);

            var deleted = service.DeleteMediaItem(mediaItemId, "Manual")!;

            var edit = Assert.Single(service.GetEditsForDeletedItem(deleted.Id));
            Assert.Equal(EditOperationType.Rotate, edit.OperationType);

            service.DeletePermanently(deleted.Id);

            Assert.Empty(new LibraryQueryService(database).GetEditsForMediaItemId(mediaItemId));
        });
    }

    [Fact]
    public void Restoring_a_trashed_item_also_cleans_up_its_now_orphaned_edit()
    {
        // A restored item is re-discovered by the next scan under a brand
        // new MediaItemId (Restore never recreates the old MediaItem row —
        // see its own doc comment), so the old Edit row can never be
        // reattached to anything; Restore cleans it up rather than leaving
        // it behind as permanent garbage.
        WithTempLibrary((root, database, repository, service) =>
        {
            var mediaItemId = SeedFile(root, database, "photo.jpg");
            new LibraryRepository(database).RotateMediaItem(mediaItemId, 90);
            var deleted = service.DeleteMediaItem(mediaItemId, "Manual")!;

            service.Restore(deleted.Id);

            Assert.Empty(new LibraryQueryService(database).GetEditsForMediaItemId(mediaItemId));
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

    [Fact]
    public void Deleting_records_a_permanent_fingerprint_that_survives_purge()
    {
        // Regression: an iCloud sync (Curato.iCloud.iCloudSyncRunner) has no
        // idea a photo was deleted on purpose once its MediaItem row is gone
        // — checking only the still-pending Recycle Bin closed that gap for
        // the 30-day retention window, but the exact same re-download could
        // still slip back in once the item was actually purged. The
        // fingerprint recorded at delete time must outlive that purge.
        WithTempLibrary((root, database, repository, service) =>
        {
            var mediaItemId = SeedFile(root, database, "photo.jpg");
            var deleted = service.DeleteMediaItem(mediaItemId, "Manual")!;

            var purged = service.DeletePermanently(deleted.Id);

            Assert.True(purged);
            Assert.Empty(service.ListPendingDeletion());
            var fingerprint = Assert.Single(repository.GetAllDeletedFingerprints());
            Assert.Equal(3, fingerprint.FileSizeBytes); // SeedFile writes [1, 2, 3]
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
            FileSizeBytes = new FileInfo(fullPath).Length,
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
