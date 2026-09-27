using Curato.Core;
using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

/// <summary>
/// Real mid-walk cancellation (Stop button) — see ScanService.Scan's own doc
/// comment for why a cancelled run persists whatever was found as plain
/// MediaItem rows but deliberately defers clustering to the next full scan.
/// </summary>
public class ScanServiceCancellationTests
{
    private static MediaItem Item(string relativePath, DateTime takenAt) => new()
    {
        RelativePath = relativePath,
        QuickHash = "irrelevant",
        TakenAt = takenAt
    };

    private static IEnumerable<MediaItem> CancelAfterSecondItem(CancellationTokenSource cts, MediaItem[] items)
    {
        for (var i = 0; i < items.Length; i++)
        {
            yield return items[i];
            if (i == 1)
            {
                cts.Cancel();
            }
        }
    }

    [Fact]
    public void Cancelling_mid_walk_persists_only_what_was_found_and_defers_clustering()
    {
        WithTempLibrary((root, repository, query, database) =>
        {
            var items = new[]
            {
                Item("a.jpg", new DateTime(2026, 1, 1, 9, 0, 0)),
                Item("b.jpg", new DateTime(2026, 1, 1, 9, 5, 0)),
                Item("c.jpg", new DateTime(2026, 1, 1, 9, 10, 0)),
                Item("d.jpg", new DateTime(2026, 1, 1, 9, 15, 0)),
            };
            var cts = new CancellationTokenSource();
            var service = new ScanService(repository, query, database);

            Assert.Throws<OperationCanceledException>(
                () => service.Scan(root, CancelAfterSecondItem(cts, items), cts.Token));

            Assert.Equal(2, query.GetAllMediaItems().Count);
            Assert.Empty(query.GetAllEvents()); // clustering deliberately deferred, not partially run

            // A normal follow-up scan (idempotent on RelativePath) picks up
            // the rest and clusters everything with no duplicates.
            service.Scan(root, items, CancellationToken.None);

            Assert.Equal(4, query.GetAllMediaItems().Count);
            Assert.NotEmpty(query.GetAllEvents());
        });
    }

    [Fact]
    public void An_already_cancelled_token_persists_nothing()
    {
        WithTempLibrary((root, repository, query, database) =>
        {
            var items = new[] { Item("a.jpg", new DateTime(2026, 1, 1)) };
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var service = new ScanService(repository, query, database);

            Assert.Throws<OperationCanceledException>(() => service.Scan(root, items, cts.Token));

            Assert.Empty(query.GetAllMediaItems());
        });
    }

    private static void WithTempLibrary(
        Action<string, LibraryRepository, LibraryQueryService, LibraryDatabase> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-scan-cancellation-test-");
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
