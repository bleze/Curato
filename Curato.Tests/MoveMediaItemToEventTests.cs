using Curato.Core;
using Curato.Data;
using Curato.Organizer;
using Dapper;

namespace Curato.Tests;

/// <summary>
/// Covers LibraryRepository.MoveMediaItemToEvent and RefreshLooseEventBoundaries —
/// added after a real-library bug where a bulk TakenAt repair (bypassing
/// SetTakenAt) left Event StartAt/EndAt stale and one item stranded in an
/// Event whose other members were days away.
/// </summary>
public class MoveMediaItemToEventTests
{
    private static MediaItem Item(string relativePath, DateTime takenAt) => new()
    {
        RelativePath = relativePath,
        QuickHash = "irrelevant",
        TakenAt = takenAt
    };

    [Fact]
    public void Moving_an_item_to_a_new_standalone_event_removes_it_from_the_old_one()
    {
        WithTempLibrary((repository, query, database) =>
        {
            var rawItems = new[]
            {
                Item("a.jpg", new DateTime(2026, 1, 1, 9, 0, 0)),
                Item("b.jpg", new DateTime(2026, 1, 1, 9, 5, 0)),
            };
            RunScanPass(repository, query, database, rawItems);

            var bId = query.GetAllMediaItems().Single(m => m.RelativePath == "b.jpg").Id;
            var result = repository.MoveMediaItemToEvent(bId, targetEventId: null);

            Assert.Equal(MoveMediaItemResult.Success, result);

            var events = query.GetAllEvents();
            Assert.Equal(2, events.Count);

            var oldEvent = events.Single(e => e.MediaItemIds.Contains(query.GetAllMediaItems().Single(m => m.RelativePath == "a.jpg").Id));
            Assert.Single(oldEvent.MediaItemIds);

            var newEvent = events.Single(e => e.MediaItemIds.Contains(bId));
            Assert.Single(newEvent.MediaItemIds);
            Assert.Equal(new DateTime(2026, 1, 1, 9, 5, 0), newEvent.StartAt);
            Assert.Equal(new DateTime(2026, 1, 1, 9, 5, 0), newEvent.EndAt);
            Assert.True(newEvent.TitleIsAuto);
        });
    }

    [Fact]
    public void Moving_the_last_item_out_of_an_event_deletes_that_event()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database, [Item("solo.jpg", new DateTime(2026, 1, 1, 9, 0, 0))]);
            var soloId = query.GetAllMediaItems().Single().Id;

            var result = repository.MoveMediaItemToEvent(soloId, targetEventId: null);

            Assert.Equal(MoveMediaItemResult.Success, result);
            var newEvent = Assert.Single(query.GetAllEvents()); // old (now-empty) one is gone, only the new one remains
            Assert.Single(newEvent.MediaItemIds);
        });
    }

    [Fact]
    public void Moving_an_item_into_an_existing_event_widens_its_date_range()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("morning.jpg", new DateTime(2026, 1, 1, 9, 0, 0)),
                Item("far-away.jpg", new DateTime(2026, 1, 5, 9, 0, 0)), // >4h gap, own Event
            ]);

            var events = query.GetAllEvents();
            Assert.Equal(2, events.Count);

            var targetEvent = events.Single(e => e.StartAt == new DateTime(2026, 1, 1, 9, 0, 0));
            var farAwayId = query.GetAllMediaItems().Single(m => m.RelativePath == "far-away.jpg").Id;

            var result = repository.MoveMediaItemToEvent(farAwayId, targetEventId: targetEvent.Id);

            Assert.Equal(MoveMediaItemResult.Success, result);
            var remainingEvent = Assert.Single(query.GetAllEvents()); // the old far-away Event is now empty and gone
            Assert.Equal(2, remainingEvent.MediaItemIds.Count);
            Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0), remainingEvent.StartAt);
            Assert.Equal(new DateTime(2026, 1, 5, 9, 0, 0), remainingEvent.EndAt);
        });
    }

    [Fact]
    public void Moving_an_unknown_media_item_returns_not_found()
    {
        WithTempLibrary((repository, _, _) =>
        {
            Assert.Equal(MoveMediaItemResult.MediaItemNotFound, repository.MoveMediaItemToEvent(999, null));
        });
    }

    [Fact]
    public void Moving_into_an_unknown_target_event_returns_not_found()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database, [Item("a.jpg", new DateTime(2026, 1, 1, 9, 0, 0))]);
            var id = query.GetAllMediaItems().Single().Id;

            Assert.Equal(MoveMediaItemResult.TargetEventNotFound, repository.MoveMediaItemToEvent(id, targetEventId: 999));
        });
    }

    [Fact]
    public void Moving_out_of_an_already_organized_event_into_a_new_event_leaves_the_file_where_it_is()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database, [Item("a.jpg", new DateTime(2026, 1, 1, 9, 0, 0))]);
            var @event = Assert.Single(query.GetAllEvents());
            MarkOrganized(database, @event.Id, "2026-01-01 Morning");

            var id = query.GetAllMediaItems().Single().Id;
            var result = repository.MoveMediaItemToEvent(id, targetEventId: null);

            Assert.Equal(MoveMediaItemResult.Success, result);
            // The new standalone Event has no real folder yet, so nothing
            // physically moves — the file stays exactly where it is (even
            // though that's still inside the old organized folder) until
            // some future Organize run gives the new Event its own folder.
            var moved = query.GetAllMediaItems().Single();
            Assert.Equal("a.jpg", moved.RelativePath);
        });
    }

    [Fact]
    public void Moving_into_an_already_organized_event_physically_relocates_the_file()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("organized.jpg", new DateTime(2026, 1, 1, 9, 0, 0)),
                Item("loose.jpg", new DateTime(2026, 1, 5, 9, 0, 0)),
            ]);

            var organizedEvent = query.GetAllEvents().Single(e => e.StartAt == new DateTime(2026, 1, 1, 9, 0, 0));
            MarkOrganized(database, organizedEvent.Id, "2026-01-01 Morning");

            var looseId = query.GetAllMediaItems().Single(m => m.RelativePath == "loose.jpg").Id;
            var libraryRoot = database.LibraryRoot;
            var oldFullPath = Path.Combine(libraryRoot, "loose.jpg");
            File.WriteAllText(oldFullPath, "fake image bytes");

            var result = repository.MoveMediaItemToEvent(looseId, targetEventId: organizedEvent.Id);

            Assert.Equal(MoveMediaItemResult.Success, result);
            Assert.False(File.Exists(oldFullPath));
            var moved = query.GetAllMediaItems().Single(m => m.Id == looseId);
            Assert.Equal("2026-01-01 Morning/loose.jpg", moved.RelativePath);
            Assert.True(File.Exists(Path.Combine(libraryRoot, moved.RelativePath)));
        });
    }

    [Fact]
    public void Moving_into_an_organized_event_gets_a_collision_free_name_if_one_already_exists_there()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("organized.jpg", new DateTime(2026, 1, 1, 9, 0, 0)),
                Item("loose.jpg", new DateTime(2026, 1, 5, 9, 0, 0)),
            ]);

            var organizedEvent = query.GetAllEvents().Single(e => e.StartAt == new DateTime(2026, 1, 1, 9, 0, 0));
            MarkOrganized(database, organizedEvent.Id, "2026-01-01 Morning");

            var libraryRoot = database.LibraryRoot;
            Directory.CreateDirectory(Path.Combine(libraryRoot, "2026-01-01 Morning"));
            // Something already occupies the exact destination name/path —
            // unrelated to any of this fixture's own MediaItem rows.
            File.WriteAllText(Path.Combine(libraryRoot, "2026-01-01 Morning", "loose.jpg"), "existing file");

            var looseId = query.GetAllMediaItems().Single(m => m.RelativePath == "loose.jpg").Id;
            File.WriteAllText(Path.Combine(libraryRoot, "loose.jpg"), "the file being moved");

            var result = repository.MoveMediaItemToEvent(looseId, targetEventId: organizedEvent.Id);

            Assert.Equal(MoveMediaItemResult.Success, result);
            var moved = query.GetAllMediaItems().Single(m => m.Id == looseId);
            Assert.Equal("2026-01-01 Morning/loose (1).jpg", moved.RelativePath);
            Assert.True(File.Exists(Path.Combine(libraryRoot, moved.RelativePath)));
        });
    }

    [Fact]
    public void RefreshLooseEventBoundaries_recomputes_ranges_after_a_direct_TakenAt_write()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database, [Item("solo.mov", new DateTime(2026, 1, 1, 9, 0, 0))]);
            var id = query.GetAllMediaItems().Single().Id;

            // Simulate a bulk repair script writing TakenAt directly via raw
            // SQL (bypassing SetTakenAt's own per-item resort), exactly what
            // happened for real via the video-metadata repair this fixes.
            using (var connection = database.OpenConnection())
            {
                connection.Execute("UPDATE MediaItem SET TakenAt = @TakenAt WHERE Id = @Id",
                    new { TakenAt = new DateTime(2026, 1, 3, 14, 0, 0).ToString("O"), Id = id });
            }

            var staleEvent = Assert.Single(query.GetAllEvents());
            Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0), staleEvent.StartAt); // still stale before refresh

            var refreshedCount = repository.RefreshLooseEventBoundaries();

            Assert.Equal(1, refreshedCount);
            var refreshedEvent = Assert.Single(query.GetAllEvents());
            Assert.Equal(new DateTime(2026, 1, 3, 14, 0, 0), refreshedEvent.StartAt);
            Assert.Equal(new DateTime(2026, 1, 3, 14, 0, 0), refreshedEvent.EndAt);
        });
    }

    [Fact]
    public void RefreshLooseEventBoundaries_never_touches_an_already_organized_event()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database, [Item("solo.jpg", new DateTime(2026, 1, 1, 9, 0, 0))]);
            var @event = Assert.Single(query.GetAllEvents());
            MarkOrganized(database, @event.Id, "2026-01-01 Morning");

            var id = query.GetAllMediaItems().Single().Id;
            using (var connection = database.OpenConnection())
            {
                connection.Execute("UPDATE MediaItem SET TakenAt = @TakenAt WHERE Id = @Id",
                    new { TakenAt = new DateTime(2026, 1, 3, 14, 0, 0).ToString("O"), Id = id });
            }

            repository.RefreshLooseEventBoundaries();

            var untouchedEvent = Assert.Single(query.GetAllEvents());
            Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0), untouchedEvent.StartAt); // unchanged — organized Events are skipped
        });
    }

    /// <summary>Delegates to the real ScanService, so a test failure here means the real endpoint is broken too.</summary>
    private static void RunScanPass(
        LibraryRepository repository, LibraryQueryService query, LibraryDatabase database, MediaItem[] rawItems) =>
        new ScanService(repository, query, database).ScanItems(rawItems);

    private static void MarkOrganized(LibraryDatabase database, long eventId, string folderPath)
    {
        using var connection = database.OpenConnection();
        connection.Execute("UPDATE Event SET FolderPath = @FolderPath WHERE Id = @Id",
            new { FolderPath = folderPath, Id = eventId });
    }

    private static void WithTempLibrary(Action<LibraryRepository, LibraryQueryService, LibraryDatabase> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-move-event-test-");
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
