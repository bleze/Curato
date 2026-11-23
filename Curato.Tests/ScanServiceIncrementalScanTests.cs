using Curato.Core;
using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

/// <summary>
/// The full persist/reload round-trip for MediaFileScanner's incremental
/// skip logic — ScanService.Scan(libraryRoot) records each folder's
/// modified-time via LibraryRepository.UpsertFolderModifiedTime as soon as
/// that folder's items are flushed, and feeds it back via
/// LibraryQueryService.GetFolderModifiedTimes on the *next* call. See
/// MediaFileScannerIncrementalScanTests for the lower-level skip logic
/// itself.
/// </summary>
public class ScanServiceIncrementalScanTests
{
    [Fact]
    public void A_second_scan_records_and_reuses_folder_modified_times()
    {
        WithTempLibrary((libraryRoot, repository, query, service) =>
        {
            File.WriteAllBytes(Path.Combine(libraryRoot, "a.jpg"), [1, 2, 3]);

            Assert.Empty(query.GetFolderModifiedTimes());

            service.Scan(libraryRoot);

            var recorded = query.GetFolderModifiedTimes();
            Assert.True(recorded.ContainsKey(""));

            var firstItem = query.GetAllMediaItems().Single();

            // Overwriting the file's content in place after the first scan
            // doesn't change the library root's own modified-time, so the
            // second scan should treat it as unchanged and never re-read
            // this file — if it had, QuickHash/FileSizeBytes below would
            // reflect the new bytes instead of the stale, already-indexed
            // ones.
            File.WriteAllBytes(Path.Combine(libraryRoot, "a.jpg"), [9, 9, 9, 9, 9, 9, 9, 9, 9, 9]);

            service.Scan(libraryRoot);

            var secondItem = query.GetAllMediaItems().Single();
            Assert.Equal(firstItem.QuickHash, secondItem.QuickHash);
            Assert.Equal(firstItem.FileSizeBytes, secondItem.FileSizeBytes);
        });
    }

    [Fact]
    public void A_new_file_in_a_previously_scanned_folder_is_still_indexed()
    {
        WithTempLibrary((libraryRoot, repository, query, service) =>
        {
            File.WriteAllBytes(Path.Combine(libraryRoot, "a.jpg"), [1, 2, 3]);
            service.Scan(libraryRoot);
            Assert.Single(query.GetAllMediaItems());

            Thread.Sleep(50); // ensure a measurably later folder modified-time
            File.WriteAllBytes(Path.Combine(libraryRoot, "b.jpg"), [4, 5, 6]);
            service.Scan(libraryRoot);

            Assert.Equal(2, query.GetAllMediaItems().Count);
        });
    }

    private static void WithTempLibrary(Action<string, LibraryRepository, LibraryQueryService, ScanService> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-incremental-scanservice-test-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            var repository = new LibraryRepository(database);
            var query = new LibraryQueryService(database);
            test(tempDir.FullName, repository, query, new ScanService(repository, query, database));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
