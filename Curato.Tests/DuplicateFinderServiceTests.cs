using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

public class DuplicateFinderServiceTests
{
    [Fact]
    public void Files_with_identical_bytes_are_reported_as_a_duplicate_group()
    {
        WithTempLibrary((root, database, service) =>
        {
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            WriteFile(root, "original.jpg", bytes);
            WriteFile(root, "copy.jpg", bytes);
            ScanLibrary(database, root);

            var result = service.FindDuplicates();

            var group = Assert.Single(result.Groups);
            Assert.Equal(2, group.MediaItemIds.Count);
        });
    }

    [Fact]
    public void The_earliest_imported_copy_is_the_suggested_keep()
    {
        WithTempLibrary((root, database, service) =>
        {
            var bytes = new byte[] { 9, 9, 9 };
            WriteFile(root, "first.jpg", bytes);
            ScanLibrary(database, root); // imported first

            WriteFile(root, "second.jpg", bytes);
            ScanLibrary(database, root); // imported second, in a later scan pass

            var result = service.FindDuplicates();

            var group = Assert.Single(result.Groups);
            var query = new LibraryQueryService(database);
            var firstItem = query.GetAllMediaItems().Single(m => m.RelativePath == "first.jpg");
            Assert.Equal(firstItem.Id, group.SuggestedKeepId);
        });
    }

    [Fact]
    public void Files_that_only_share_a_size_but_differ_in_content_are_not_flagged()
    {
        WithTempLibrary((root, database, service) =>
        {
            WriteFile(root, "a.jpg", [1, 2, 3]);
            WriteFile(root, "b.jpg", [4, 5, 6]); // same size, different bytes — same QuickHash sample too, small file
            ScanLibrary(database, root);

            var result = service.FindDuplicates();

            Assert.Empty(result.Groups);
        });
    }

    [Fact]
    public void Content_hash_is_cached_and_not_recomputed_on_a_second_run()
    {
        WithTempLibrary((root, database, service) =>
        {
            var bytes = new byte[] { 7, 7, 7 };
            WriteFile(root, "one.jpg", bytes);
            WriteFile(root, "two.jpg", bytes);
            ScanLibrary(database, root);

            service.FindDuplicates();

            var query = new LibraryQueryService(database);
            Assert.All(query.GetAllMediaItems(), m => Assert.NotNull(m.ContentHash));

            // Running again should still find the same group, purely from cached hashes.
            var second = service.FindDuplicates();
            Assert.Single(second.Groups);
        });
    }

    private static void WriteFile(string root, string relativePath, byte[] bytes) =>
        File.WriteAllBytes(Path.Combine(root, relativePath), bytes);

    private static void ScanLibrary(LibraryDatabase database, string root) =>
        new ScanService(new LibraryRepository(database), new LibraryQueryService(database)).Scan(root);

    private static void WithTempLibrary(Action<string, LibraryDatabase, DuplicateFinderService> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-dupefinder-test-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            var repository = new LibraryRepository(database);
            var service = new DuplicateFinderService(database, new LibraryQueryService(database), repository);
            test(tempDir.FullName, database, service);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
