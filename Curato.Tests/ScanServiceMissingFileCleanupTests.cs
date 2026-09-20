using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

/// <summary>
/// Regression: deleting a file directly on disk (outside Curato) left its
/// MediaItem row behind forever — confirmed live as a thumbnail stuck
/// showing "No preview" after the underlying file was removed. A re-scan
/// now detects previously-indexed RelativePaths missing from the current
/// disk walk and routes them through the RecycleBin, same as an explicit
/// delete would.
/// </summary>
public class ScanServiceMissingFileCleanupTests
{
    [Fact]
    public void Rescanning_after_a_file_is_deleted_on_disk_removes_its_media_item()
    {
        WithTempLibrary((root, repository, query, database) =>
        {
            var keepPath = Path.Combine(root, "keep.jpg");
            var deletedPath = Path.Combine(root, "deleted.jpg");
            File.WriteAllBytes(keepPath, [1, 2, 3]);
            File.WriteAllBytes(deletedPath, [4, 5, 6]);

            new ScanService(repository, query, database).Scan(root);
            Assert.Equal(2, query.GetAllMediaItems().Count);

            File.Delete(deletedPath);

            var result = new ScanService(repository, query, database).Scan(root);

            Assert.Equal(1, result.RemovedMissingFileCount);
            var remaining = Assert.Single(query.GetAllMediaItems());
            Assert.Equal("keep.jpg", remaining.RelativePath);
        });
    }

    [Fact]
    public void A_mass_disappearance_of_files_is_left_alone_as_a_safety_guard()
    {
        // Protects against a transient/broken mount (e.g. an unmounted NAS
        // share) looking identical to the user having deleted everything.
        WithTempLibrary((root, repository, query, database) =>
        {
            // The guard's threshold is max(20, indexedCount / 2), so this
            // needs enough files that "all of them vanished" clears it.
            const int fileCount = 50;
            for (var i = 0; i < fileCount; i++)
            {
                File.WriteAllBytes(Path.Combine(root, $"photo{i}.jpg"), [(byte)i]);
            }
            new ScanService(repository, query, database).Scan(root);
            Assert.Equal(fileCount, query.GetAllMediaItems().Count);

            foreach (var file in Directory.GetFiles(root))
            {
                File.Delete(file);
            }

            var result = new ScanService(repository, query, database).Scan(root);

            Assert.Equal(0, result.RemovedMissingFileCount);
            Assert.Equal(fileCount, query.GetAllMediaItems().Count);
        });
    }

    private static void WithTempLibrary(
        Action<string, LibraryRepository, LibraryQueryService, LibraryDatabase> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-missing-file-cleanup-test-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            test(tempDir.FullName, new LibraryRepository(database), new LibraryQueryService(database), database);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
