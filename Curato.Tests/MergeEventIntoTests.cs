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
    public void Merging_an_already_organized_source_is_refused_and_changes_nothing()
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

            Assert.Equal(MergeEventResult.SourceEventOrganized, result);
            Assert.Equal(2, query.GetAllEvents().Count); // untouched
        });
    }

    [Fact]
    public void Merging_into_an_already_organized_target_is_refused_and_changes_nothing()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("day1.jpg", new DateTime(2026, 1, 1, 9, 0, 0)),
                Item("day5.jpg", new DateTime(2026, 1, 5, 9, 0, 0)),
            ]);
            var events = query.GetAllEvents();
            MarkOrganized(database, events[1].Id, "2026-01-05 Morning");

            var result = repository.MergeEventInto(events[0].Id, events[1].Id);

            Assert.Equal(MergeEventResult.TargetEventOrganized, result);
            Assert.Equal(2, query.GetAllEvents().Count); // untouched
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
