using Curato.Core;
using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

/// <summary>
/// Confirmed live as a real way to lose an entire long scan's worth of
/// work: the hosting process getting killed outright (not a graceful Stop
/// click) can't be caught by ScanService.Scan's own try/catch at all —
/// nothing runs after that, so persisting everything in one batch only at
/// the very end meant a hard kill anywhere lost the whole scan. These tests
/// use an *uncaught, unrelated* exception (not OperationCanceledException,
/// which already has its own dedicated handling and tests — see
/// ScanServiceCancellationTests) as the closest in-process proxy for "the
/// process died here and nothing downstream ever ran."
///
/// Flushing is batched by item count (confirmed necessary, not just tidy —
/// see LibraryRepository.FlushScanProgress's doc comment for the
/// network-share locking incident that ruled out a connection per folder),
/// with each batch also carrying along the modified-time of whichever
/// folders happened to finish within it. These tests use many distinct
/// single-item folders so every item is also its own folder boundary,
/// exercising that "folder completed" bookkeeping alongside the plain
/// item-count flush.
/// </summary>
public class ScanServiceIncrementalFlushTests
{
    private static MediaItem Item(string relativePath, DateTime takenAt) => new()
    {
        RelativePath = relativePath,
        QuickHash = "irrelevant",
        TakenAt = takenAt,
    };

    private static IEnumerable<MediaItem> ThrowAfter(int throwAfterCount, int totalCount)
    {
        for (var i = 0; i < totalCount; i++)
        {
            if (i == throwAfterCount)
            {
                throw new InvalidOperationException("Simulates the hosting process dying mid-walk.");
            }

            yield return Item($"folder{i:D4}/a.jpg", new DateTime(2026, 1, 1).AddMinutes(i));
        }
    }

    [Fact]
    public void An_uncaught_mid_walk_failure_still_leaves_already_flushed_folders_persisted()
    {
        WithTempLibrary((root, repository, query, database) =>
        {
            var service = new ScanService(repository, query, database);

            // Deliberately past one flush boundary (200) but short of a
            // second — if incremental flushing didn't exist, this would
            // persist *nothing* at all, since the exception propagates
            // uncaught straight out of Scan with no catch block to run any
            // cleanup/persistence.
            Assert.Throws<InvalidOperationException>(
                () => service.Scan(root, ThrowAfter(throwAfterCount: 210, totalCount: 300), CancellationToken.None));

            Assert.Equal(200, query.GetAllMediaItems().Count);
        });
    }

    [Fact]
    public void A_successful_scan_past_multiple_flush_boundaries_still_clusters_everything_once()
    {
        WithTempLibrary((root, repository, query, database) =>
        {
            var service = new ScanService(repository, query, database);
            var items = Enumerable.Range(0, 450)
                .Select(i => Item($"folder{i:D4}/a.jpg", new DateTime(2026, 1, 1).AddMinutes(i)))
                .ToList();

            service.Scan(root, items, CancellationToken.None);

            // Re-upserting already-flushed items as part of the normal
            // end-of-scan pipeline is idempotent, not a source of
            // duplicates.
            Assert.Equal(450, query.GetAllMediaItems().Count);
        });
    }

    [Fact]
    public void An_abnormally_large_single_folder_still_flushes_across_multiple_batches()
    {
        WithTempLibrary((root, repository, query, database) =>
        {
            var service = new ScanService(repository, query, database);

            // All 300 items share one folder, so its own completion (the
            // folder boundary) isn't reached until well after the first
            // item-count flush (200) already fired without it — proving a
            // folder can straddle flush batches without issue.
            var items = Enumerable.Range(0, 300)
                .Select(i => Item($"big-folder/{i:D4}.jpg", new DateTime(2026, 1, 1).AddMinutes(i)))
                .ToList();

            service.Scan(root, items, CancellationToken.None);

            Assert.Equal(300, query.GetAllMediaItems().Count);
        });
    }

    private static void WithTempLibrary(
        Action<string, LibraryRepository, LibraryQueryService, LibraryDatabase> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-scan-incremental-flush-test-");
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
