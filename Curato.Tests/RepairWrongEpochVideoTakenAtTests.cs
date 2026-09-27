using Curato.Core;
using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

/// <summary>
/// Self-heals videos indexed before MediaFileScanner started correcting the
/// wrong-epoch encoder bug (see QuickTimeEpoch and
/// MediaFileScannerDateReadingTests' own coverage of the scan-time fix) — a
/// re-scan alone can never fix an already-indexed item, since
/// UpsertMediaItems' ON CONFLICT DO NOTHING never touches an existing row.
/// </summary>
public class RepairWrongEpochVideoTakenAtTests
{
    private static MediaItem Item(string relativePath, DateTime takenAt, TakenAtSource source, MediaKind kind = MediaKind.Video) => new()
    {
        RelativePath = relativePath,
        QuickHash = "irrelevant",
        Kind = kind,
        TakenAt = takenAt,
        TakenAtSource = source,
    };

    [Fact]
    public void Corrects_a_video_metadata_sourced_date_that_looks_wrong_epoch()
    {
        WithTempLibrary((repository, query, _) =>
        {
            var wrong = new DateTime(1947, 6, 15, 14, 30, 0);
            repository.UpsertMediaItems([Item("a.mov", wrong, TakenAtSource.VideoMetadata)]);

            var affected = repository.RepairWrongEpochVideoTakenAt();

            Assert.Equal(1, affected);
            var item = Assert.Single(query.GetAllMediaItems());
            Assert.Equal(wrong + QuickTimeEpoch.ToUnixEpochGap, item.TakenAt);
            Assert.Equal(TakenAtSource.VideoMetadata, item.TakenAtSource); // still metadata-derived, not reclassified as Manual
        });
    }

    [Fact]
    public void Leaves_a_manually_corrected_date_alone_even_if_it_looks_old()
    {
        WithTempLibrary((repository, query, _) =>
        {
            var deliberatelyOld = new DateTime(1947, 6, 15);
            repository.UpsertMediaItems([Item("a.mov", deliberatelyOld, TakenAtSource.Manual)]);

            var affected = repository.RepairWrongEpochVideoTakenAt();

            Assert.Equal(0, affected);
            Assert.Equal(deliberatelyOld, Assert.Single(query.GetAllMediaItems()).TakenAt);
        });
    }

    [Fact]
    public void Leaves_a_plausible_video_metadata_date_alone()
    {
        WithTempLibrary((repository, query, _) =>
        {
            var plausible = new DateTime(2023, 6, 15);
            repository.UpsertMediaItems([Item("a.mov", plausible, TakenAtSource.VideoMetadata)]);

            var affected = repository.RepairWrongEpochVideoTakenAt();

            Assert.Equal(0, affected);
            Assert.Equal(plausible, Assert.Single(query.GetAllMediaItems()).TakenAt);
        });
    }

    [Fact]
    public void Is_idempotent()
    {
        WithTempLibrary((repository, _, _) =>
        {
            repository.UpsertMediaItems([Item("a.mov", new DateTime(1947, 6, 15), TakenAtSource.VideoMetadata)]);

            repository.RepairWrongEpochVideoTakenAt();
            var secondPassAffected = repository.RepairWrongEpochVideoTakenAt();

            Assert.Equal(0, secondPassAffected);
        });
    }

    [Fact]
    public void Repairing_directly_widens_the_containing_events_date_range_to_match()
    {
        WithTempLibrary((repository, query, database) =>
        {
            // Simulates a library scanned before this repair pass existed —
            // ScanItems already self-heals this in one pass (see the
            // ScanService-level test below), so this drives the repository
            // method directly to isolate its own Event-range-refresh behavior.
            var goodItem = Item("good.jpg", new DateTime(2023, 6, 15, 10, 0, 0), TakenAtSource.ExifDateTaken, MediaKind.Photo);
            var wrongVideoItem = Item("wrong.mov", new DateTime(1947, 6, 15, 10, 5, 0), TakenAtSource.VideoMetadata);
            var saved = repository.UpsertMediaItems([goodItem, wrongVideoItem]);
            repository.SaveEvents([new Event
            {
                Title = "Test",
                TitleIsAuto = true,
                NamingMode = NamingMode.CuratoManaged,
                StartAt = wrongVideoItem.TakenAt,
                EndAt = wrongVideoItem.TakenAt,
                MediaItemIds = saved.Select(i => i.Id).ToList(),
            }]);

            repository.RepairWrongEpochVideoTakenAt();

            // The corrected video (2013-ish, since 1947 + the epoch gap
            // isn't quite 2023) still lands before the photo's 2023 date —
            // min/max, not "whichever was the video", is what should drive
            // the range either way.
            var eventAfter = Assert.Single(query.GetAllEvents());
            Assert.Equal(wrongVideoItem.TakenAt + QuickTimeEpoch.ToUnixEpochGap, eventAfter.StartAt);
            Assert.Equal(new DateTime(2023, 6, 15, 10, 0, 0), eventAfter.EndAt);
        });
    }

    [Fact]
    public void ScanItems_self_heals_an_affected_events_date_range_in_one_pass()
    {
        WithTempLibrary((repository, query, database) =>
        {
            // Both videos share the same wrong-epoch bug and are close
            // together in their (wrong) recorded time, so they cluster into
            // one Event exactly as they would have before this fix existed —
            // the point here is that ScanItems' built-in repair then
            // corrects both afterward, in the same pass, and the Event's
            // range reflects the corrected dates by the time ScanItems returns.
            var video1 = Item("a.mov", new DateTime(1947, 6, 15, 10, 0, 0), TakenAtSource.VideoMetadata);
            var video2 = Item("b.mov", new DateTime(1947, 6, 15, 10, 5, 0), TakenAtSource.VideoMetadata);
            var saved = repository.UpsertMediaItems([video1, video2]);

            new ScanService(repository, query, database).ScanItems(saved);

            var scannedEvent = Assert.Single(query.GetAllEvents());
            Assert.Equal(video1.TakenAt + QuickTimeEpoch.ToUnixEpochGap, scannedEvent.StartAt);
            Assert.Equal(video2.TakenAt + QuickTimeEpoch.ToUnixEpochGap, scannedEvent.EndAt);
        });
    }

    private static void WithTempLibrary(
        Action<LibraryRepository, LibraryQueryService, LibraryDatabase> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-repair-wrong-epoch-test-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            test(new LibraryRepository(database), new LibraryQueryService(database), database);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
