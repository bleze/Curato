using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

/// <summary>
/// Backs the frontend's live "N files found so far" counter during a scan —
/// see ScanProgressTracker's own doc comment for why this exists at all
/// (Scan() is one long synchronous call with no other way to report
/// partway through).
/// </summary>
public class ScanProgressTrackerTests
{
    [Fact]
    public void Scanning_ticks_the_counter_once_per_file_found()
    {
        WithTempLibrary((root, repository, query, database) =>
        {
            File.WriteAllBytes(Path.Combine(root, "a.jpg"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root, "b.jpg"), [4, 5, 6]);
            File.WriteAllBytes(Path.Combine(root, "c.jpg"), [7, 8, 9]);
            var tracker = new ScanProgressTracker();

            new ScanService(repository, query, database, progressTracker: tracker).Scan(root);

            Assert.Equal(3, tracker.Get(root));
        });
    }

    [Fact]
    public void A_fresh_scan_resets_the_previous_count_first()
    {
        WithTempLibrary((root, repository, query, database) =>
        {
            File.WriteAllBytes(Path.Combine(root, "a.jpg"), [1, 2, 3]);
            var tracker = new ScanProgressTracker();
            new ScanService(repository, query, database, progressTracker: tracker).Scan(root);
            Assert.Equal(1, tracker.Get(root));

            File.WriteAllBytes(Path.Combine(root, "b.jpg"), [4, 5, 6]);
            new ScanService(repository, query, database, progressTracker: tracker).Scan(root);

            // Not 1 + 2 — a new scan starts its own count from zero rather
            // than accumulating across separate Scan() calls.
            Assert.Equal(2, tracker.Get(root));
        });
    }

    [Fact]
    public void An_unknown_path_reports_zero_rather_than_throwing()
    {
        var tracker = new ScanProgressTracker();

        Assert.Equal(0, tracker.Get(Path.GetTempPath()));
    }

    [Fact]
    public void Clearing_makes_a_later_get_report_zero_again()
    {
        WithTempLibrary((root, repository, query, database) =>
        {
            File.WriteAllBytes(Path.Combine(root, "a.jpg"), [1, 2, 3]);
            var tracker = new ScanProgressTracker();
            new ScanService(repository, query, database, progressTracker: tracker).Scan(root);
            Assert.Equal(1, tracker.Get(root));

            tracker.Clear(root);

            Assert.Equal(0, tracker.Get(root));
        });
    }

    private static void WithTempLibrary(
        Action<string, LibraryRepository, LibraryQueryService, LibraryDatabase> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-scan-progress-test-");
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
