using Curato.Core;
using Curato.Data;
using Curato.Organizer;
using Dapper;

namespace Curato.Tests;

/// <summary>Covers LibraryRepository.MergeEventInto — the bulk counterpart to
/// MoveMediaItemToEvent for gathering a wrongly-split Event's members into
/// the real one in a single action.</summary>
public class MergeEventIntoTests
{
    private static MediaItem Item(string relativePath, DateTime takenAt) => new()
    {
        RelativePath = relativePath,
        QuickHash = "irrelevant",
        TakenAt = takenAt
    };

    [Fact]
    public void Merging_moves_every_item_and_deletes_the_source_event()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("day1-a.jpg", new DateTime(2026, 1, 1, 9, 0, 0)),
                Item("day1-b.jpg", new DateTime(2026, 1, 1, 9, 5, 0)),
                Item("day5.jpg", new DateTime(2026, 1, 5, 9, 0, 0)), // >4h gap, own Event
            ]);

            var events = query.GetAllEvents();
            var target = events.Single(e => e.MediaItemIds.Count == 2);
            var source = events.Single(e => e.MediaItemIds.Count == 1);

            var result = repository.MergeEventInto(source.Id, target.Id);

            Assert.Equal(MergeEventResult.Success, result);
            var remaining = Assert.Single(query.GetAllEvents()); // source is gone
            Assert.Equal(3, remaining.MediaItemIds.Count);
            Assert.Equal(target.Id, remaining.Id); // target survives, keeping its identity/title
            Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0), remaining.StartAt);
            Assert.Equal(new DateTime(2026, 1, 5, 9, 0, 0), remaining.EndAt);
        });
    }

    [Fact]
    public void Merging_preserves_chronological_order_across_both_events_members()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("noon.jpg", new DateTime(2026, 1, 1, 12, 0, 0)),
                Item("far.jpg", new DateTime(2026, 1, 5, 8, 0, 0)), // earlier time-of-day, later date, own Event
            ]);

            var events = query.GetAllEvents();
            var target = events.Single(e => e.MediaItemIds.Single() == query.GetAllMediaItems().Single(m => m.RelativePath == "noon.jpg").Id);
            var source = events.Single(e => e.Id != target.Id);

            repository.MergeEventInto(source.Id, target.Id);

            var merged = Assert.Single(query.GetAllEvents());
            var orderedPaths = merged.MediaItemIds
                .Select(id => query.GetAllMediaItems().Single(m => m.Id == id).RelativePath)
                .ToList();
            Assert.Equal(["noon.jpg", "far.jpg"], orderedPaths);
        });
    }

    [Fact]
    public void Merging_an_event_into_itself_is_refused()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database, [Item("a.jpg", new DateTime(2026, 1, 1, 9, 0, 0))]);
            var eventId = query.GetAllEvents()[0].Id;

            Assert.Equal(MergeEventResult.SourceEqualsTarget, repository.MergeEventInto(eventId, eventId));
        });
    }

    [Fact]
    public void Merging_an_unknown_source_returns_not_found()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database, [Item("a.jpg", new DateTime(2026, 1, 1, 9, 0, 0))]);
            var eventId = query.GetAllEvents()[0].Id;

            Assert.Equal(MergeEventResult.SourceEventNotFound, repository.MergeEventInto(999, eventId));
        });
    }

    [Fact]
    public void Merging_into_an_unknown_target_returns_not_found()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database, [Item("a.jpg", new DateTime(2026, 1, 1, 9, 0, 0))]);
            var eventId = query.GetAllEvents()[0].Id;

            Assert.Equal(MergeEventResult.TargetEventNotFound, repository.MergeEventInto(eventId, 999));
        });
    }

    [Fact]
    public void Merging_out_of_an_organized_source_into_an_unorganized_target_leaves_files_where_they_are()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("day1.jpg", new DateTime(2026, 1, 1, 9, 0, 0)),
                Item("day5.jpg", new DateTime(2026, 1, 5, 9, 0, 0)),
            ]);
            var events = query.GetAllEvents();
            MarkOrganized(database, events[0].Id, "2026-01-01 Morning");

            var result = repository.MergeEventInto(events[0].Id, events[1].Id);

            Assert.Equal(MergeEventResult.Success, result);
            // The target has no real folder yet, so nothing physically
            // moves — RelativePath (and therefore the file's real location)
            // is untouched, even though it's still inside the old organized
            // source folder.
            var moved = query.GetAllMediaItems().Single(m => m.RelativePath.EndsWith("day1.jpg"));
            Assert.Equal("day1.jpg", moved.RelativePath);
        });
    }

    [Fact]
    public void Merging_into_an_organized_target_physically_relocates_every_member_and_removes_the_empty_source_folder()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("2026-01-01 Morning/day1-a.jpg", new DateTime(2026, 1, 1, 9, 0, 0)),
                Item("2026-01-01 Morning/day1-b.jpg", new DateTime(2026, 1, 1, 9, 5, 0)),
                Item("2026-01-05 Evening/day5.jpg", new DateTime(2026, 1, 5, 9, 0, 0)),
            ]);
            var events = query.GetAllEvents();
            var source = events.Single(e => e.MediaItemIds.Count == 2);
            var target = events.Single(e => e.Id != source.Id);
            MarkOrganized(database, source.Id, "2026-01-01 Morning");
            MarkOrganized(database, target.Id, "2026-01-05 Evening");

            var libraryRoot = database.LibraryRoot;
            Directory.CreateDirectory(Path.Combine(libraryRoot, "2026-01-01 Morning"));
            File.WriteAllText(Path.Combine(libraryRoot, "2026-01-01 Morning", "day1-a.jpg"), "a");
            File.WriteAllText(Path.Combine(libraryRoot, "2026-01-01 Morning", "day1-b.jpg"), "b");

            var result = repository.MergeEventInto(source.Id, target.Id);

            Assert.Equal(MergeEventResult.Success, result);
            Assert.False(Directory.Exists(Path.Combine(libraryRoot, "2026-01-01 Morning"))); // now-empty source folder removed
            var relativePaths = query.GetAllMediaItems().Select(m => m.RelativePath).OrderBy(p => p).ToList();
            Assert.Equal(
                new[] { "2026-01-05 Evening/day1-a.jpg", "2026-01-05 Evening/day1-b.jpg", "2026-01-05 Evening/day5.jpg" },
                relativePaths);
            Assert.True(File.Exists(Path.Combine(libraryRoot, "2026-01-05 Evening", "day1-a.jpg")));
            Assert.True(File.Exists(Path.Combine(libraryRoot, "2026-01-05 Evening", "day1-b.jpg")));
        });
    }

    [Fact]
    public void Merging_into_an_organized_target_gets_collision_free_names()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("source/photo.jpg", new DateTime(2026, 1, 1, 9, 0, 0)),
                Item("target/other.jpg", new DateTime(2026, 1, 5, 9, 0, 0)),
            ]);
            var events = query.GetAllEvents();
            var source = events.Single(e => e.MediaItemIds.Single() == query.GetAllMediaItems().Single(m => m.RelativePath == "source/photo.jpg").Id);
            var target = events.Single(e => e.Id != source.Id);
            MarkOrganized(database, source.Id, "source");
            MarkOrganized(database, target.Id, "target");

            var libraryRoot = database.LibraryRoot;
            Directory.CreateDirectory(Path.Combine(libraryRoot, "source"));
            File.WriteAllText(Path.Combine(libraryRoot, "source", "photo.jpg"), "the file being moved");
            Directory.CreateDirectory(Path.Combine(libraryRoot, "target"));
            File.WriteAllText(Path.Combine(libraryRoot, "target", "other.jpg"), "unrelated");
            // Something already occupies the exact destination name.
            File.WriteAllText(Path.Combine(libraryRoot, "target", "photo.jpg"), "existing file");

            var result = repository.MergeEventInto(source.Id, target.Id);

            Assert.Equal(MergeEventResult.Success, result);
            var moved = query.GetAllMediaItems().Single(m => m.RelativePath.Contains("photo (1)"));
            Assert.Equal("target/photo (1).jpg", moved.RelativePath);
        });
    }

    [Fact]
    public void Merging_leaves_an_unexpectedly_nonempty_source_folder_alone()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("source/photo.jpg", new DateTime(2026, 1, 1, 9, 0, 0)),
                Item("target/other.jpg", new DateTime(2026, 1, 5, 9, 0, 0)),
            ]);
            var events = query.GetAllEvents();
            var source = events.Single(e => e.MediaItemIds.Single() == query.GetAllMediaItems().Single(m => m.RelativePath == "source/photo.jpg").Id);
            var target = events.Single(e => e.Id != source.Id);
            MarkOrganized(database, source.Id, "source");
            MarkOrganized(database, target.Id, "target");

            var libraryRoot = database.LibraryRoot;
            Directory.CreateDirectory(Path.Combine(libraryRoot, "source"));
            File.WriteAllText(Path.Combine(libraryRoot, "source", "photo.jpg"), "the file being moved");
            // Untracked leftover content Curato never put there.
            File.WriteAllText(Path.Combine(libraryRoot, "source", "notes.txt"), "not a MediaItem");
            Directory.CreateDirectory(Path.Combine(libraryRoot, "target"));

            var result = repository.MergeEventInto(source.Id, target.Id);

            Assert.Equal(MergeEventResult.Success, result);
            Assert.True(Directory.Exists(Path.Combine(libraryRoot, "source"))); // left alone, not guessed at
            Assert.True(File.Exists(Path.Combine(libraryRoot, "source", "notes.txt")));
        });
    }

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
        var tempDir = Directory.CreateTempSubdirectory("curato-merge-event-test-");
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
