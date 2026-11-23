using Curato.Core;
using Curato.Data;
using Curato.Organizer;
using Dapper;

namespace Curato.Tests;

/// <summary>Covers LibraryRepository.CreateTripGroup and UngroupTripGroup — the
/// manual "group these Events into a Trip by hand" escape hatch for whenever
/// GPS-gated auto-formation doesn't apply.</summary>
public class CreateTripGroupTests
{
    private static MediaItem Item(string relativePath, DateTime takenAt) => new()
    {
        RelativePath = relativePath,
        QuickHash = "irrelevant",
        TakenAt = takenAt
    };

    [Fact]
    public void Grouping_two_standalone_events_creates_a_trip_spanning_both()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("day1.jpg", new DateTime(2026, 6, 1, 9, 0, 0)),
                Item("day3.jpg", new DateTime(2026, 6, 3, 9, 0, 0)), // >4h gap, own Event
            ]);

            var events = query.GetAllEvents();
            Assert.Equal(2, events.Count);

            var (result, trip) = repository.CreateTripGroup(events.Select(e => e.Id).ToList());

            Assert.Equal(CreateTripGroupResult.Success, result);
            Assert.NotNull(trip);
            Assert.Equal(new DateOnly(2026, 6, 1), trip.StartDate);
            Assert.Equal(new DateOnly(2026, 6, 3), trip.EndDate);
            Assert.True(trip.TitleIsAuto);

            var reloadedEvents = query.GetAllEvents();
            Assert.All(reloadedEvents, e => Assert.Equal(trip.Id, e.TripGroupId));
        });
    }

    [Fact]
    public void Grouping_an_already_organized_event_is_allowed_since_no_file_moves()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("day1.jpg", new DateTime(2026, 6, 1, 9, 0, 0)),
                Item("day3.jpg", new DateTime(2026, 6, 3, 9, 0, 0)),
            ]);

            var organizedEvent = query.GetAllEvents()[0];
            using (var connection = database.OpenConnection())
            {
                connection.Execute("UPDATE Event SET FolderPath = @Path WHERE Id = @Id",
                    new { Path = "2026-06-01 Morning", Id = organizedEvent.Id });
            }

            var (result, trip) = repository.CreateTripGroup(query.GetAllEvents().Select(e => e.Id).ToList());

            Assert.Equal(CreateTripGroupResult.Success, result);
            Assert.NotNull(trip);
        });
    }

    [Fact]
    public void Grouping_an_unknown_event_id_returns_not_found_and_changes_nothing()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database, [Item("day1.jpg", new DateTime(2026, 6, 1, 9, 0, 0))]);
            var realEventId = query.GetAllEvents()[0].Id;

            var (result, trip) = repository.CreateTripGroup([realEventId, 999]);

            Assert.Equal(CreateTripGroupResult.EventNotFound, result);
            Assert.Null(trip);
            Assert.Empty(query.GetAllTripGroups());
            Assert.Null(query.GetAllEvents()[0].TripGroupId); // untouched, not partially grouped
        });
    }

    [Fact]
    public void Grouping_an_event_already_in_a_trip_is_refused()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("day1.jpg", new DateTime(2026, 6, 1, 9, 0, 0)),
                Item("day3.jpg", new DateTime(2026, 6, 3, 9, 0, 0)),
                Item("day5.jpg", new DateTime(2026, 6, 5, 9, 0, 0)),
            ]);
            var events = query.GetAllEvents();
            repository.CreateTripGroup([events[0].Id, events[1].Id]);

            var (result, trip) = repository.CreateTripGroup([events[0].Id, events[2].Id]);

            Assert.Equal(CreateTripGroupResult.EventAlreadyInATrip, result);
            Assert.Null(trip);
        });
    }

    [Fact]
    public void Empty_event_list_is_refused()
    {
        WithTempLibrary((repository, _, _) =>
        {
            var (result, trip) = repository.CreateTripGroup([]);
            Assert.Equal(CreateTripGroupResult.NoEventsGiven, result);
            Assert.Null(trip);
        });
    }

    [Fact]
    public void Ungrouping_a_trip_returns_its_events_to_standalone()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("day1.jpg", new DateTime(2026, 6, 1, 9, 0, 0)),
                Item("day3.jpg", new DateTime(2026, 6, 3, 9, 0, 0)),
            ]);
            var (_, trip) = repository.CreateTripGroup(query.GetAllEvents().Select(e => e.Id).ToList());

            var ungrouped = repository.UngroupTripGroup(trip!.Id);

            Assert.True(ungrouped);
            Assert.Empty(query.GetAllTripGroups());
            Assert.All(query.GetAllEvents(), e => Assert.Null(e.TripGroupId));
        });
    }

    [Fact]
    public void Ungrouping_an_unknown_trip_returns_false()
    {
        WithTempLibrary((repository, _, _) =>
        {
            Assert.False(repository.UngroupTripGroup(999));
        });
    }

    [Fact]
    public void Removing_one_event_leaves_the_trip_intact_for_the_rest()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("day1.jpg", new DateTime(2026, 6, 1, 9, 0, 0)),
                Item("day3.jpg", new DateTime(2026, 6, 3, 9, 0, 0)),
                Item("day5.jpg", new DateTime(2026, 6, 5, 9, 0, 0)),
            ]);
            var events = query.GetAllEvents();
            var (_, trip) = repository.CreateTripGroup(events.Select(e => e.Id).ToList());

            var removed = repository.RemoveEventFromTrip(events[0].Id);

            Assert.True(removed);
            var ejectedEvent = query.GetAllEvents().Single(e => e.Id == events[0].Id);
            Assert.Null(ejectedEvent.TripGroupId);

            var survivingTrip = Assert.Single(query.GetAllTripGroups());
            Assert.Equal(trip!.Id, survivingTrip.Id);
            Assert.Equal(2, survivingTrip.EventIds.Count);
            Assert.DoesNotContain(events[0].Id, survivingTrip.EventIds);
        });
    }

    [Fact]
    public void Removing_the_last_event_from_a_trip_deletes_the_trip()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("day1.jpg", new DateTime(2026, 6, 1, 9, 0, 0)),
                Item("day3.jpg", new DateTime(2026, 6, 3, 9, 0, 0)),
            ]);
            var events = query.GetAllEvents();
            repository.CreateTripGroup(events.Select(e => e.Id).ToList());

            repository.RemoveEventFromTrip(events[0].Id);
            var removed = repository.RemoveEventFromTrip(events[1].Id);

            Assert.True(removed);
            Assert.Empty(query.GetAllTripGroups());
            Assert.All(query.GetAllEvents(), e => Assert.Null(e.TripGroupId));
        });
    }

    [Fact]
    public void Removing_an_unknown_event_returns_false()
    {
        WithTempLibrary((repository, _, _) =>
        {
            Assert.False(repository.RemoveEventFromTrip(999));
        });
    }

    [Fact]
    public void Removing_an_event_that_is_not_in_a_trip_returns_false()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database, [Item("day1.jpg", new DateTime(2026, 6, 1, 9, 0, 0))]);
            var eventId = query.GetAllEvents()[0].Id;

            Assert.False(repository.RemoveEventFromTrip(eventId));
        });
    }

    [Fact]
    public void Adding_a_standalone_event_to_an_existing_trip_extends_its_date_range()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("day1.jpg", new DateTime(2026, 6, 1, 9, 0, 0)),
                Item("day3.jpg", new DateTime(2026, 6, 3, 9, 0, 0)),
                Item("day5.jpg", new DateTime(2026, 6, 5, 9, 0, 0)), // stays standalone
            ]);
            var events = query.GetAllEvents();
            var (_, trip) = repository.CreateTripGroup([
                events.Single(e => e.StartAt.Day == 1).Id,
                events.Single(e => e.StartAt.Day == 3).Id,
            ]);
            var standaloneEvent = events.Single(e => e.StartAt.Day == 5);

            var result = repository.AddEventToTripGroup(standaloneEvent.Id, trip!.Id);

            Assert.Equal(AddEventToTripGroupResult.Success, result);
            var updatedTrip = query.GetAllTripGroups().Single(t => t.Id == trip.Id);
            Assert.Equal(3, updatedTrip.EventIds.Count);
            Assert.Contains(standaloneEvent.Id, updatedTrip.EventIds);
            Assert.Equal(new DateOnly(2026, 6, 1), updatedTrip.StartDate);
            Assert.Equal(new DateOnly(2026, 6, 5), updatedTrip.EndDate); // extended to cover the newly-added Event
        });
    }

    [Fact]
    public void Adding_an_unknown_event_to_a_trip_returns_not_found()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("day1.jpg", new DateTime(2026, 6, 1, 9, 0, 0)),
                Item("day3.jpg", new DateTime(2026, 6, 3, 9, 0, 0)),
            ]);
            var (_, trip) = repository.CreateTripGroup(query.GetAllEvents().Select(e => e.Id).ToList());

            var result = repository.AddEventToTripGroup(999, trip!.Id);

            Assert.Equal(AddEventToTripGroupResult.EventNotFound, result);
        });
    }

    [Fact]
    public void Adding_an_event_to_an_unknown_trip_returns_not_found_and_leaves_the_event_standalone()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database, [Item("day1.jpg", new DateTime(2026, 6, 1, 9, 0, 0))]);
            var eventId = query.GetAllEvents()[0].Id;

            var result = repository.AddEventToTripGroup(eventId, 999);

            Assert.Equal(AddEventToTripGroupResult.TripGroupNotFound, result);
            Assert.Null(query.GetAllEvents()[0].TripGroupId);
        });
    }

    [Fact]
    public void Adding_an_event_already_in_a_trip_is_refused()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("day1.jpg", new DateTime(2026, 6, 1, 9, 0, 0)),
                Item("day3.jpg", new DateTime(2026, 6, 3, 9, 0, 0)),
                Item("day10.jpg", new DateTime(2026, 6, 10, 9, 0, 0)),
                Item("day12.jpg", new DateTime(2026, 6, 12, 9, 0, 0)),
            ]);
            var events = query.GetAllEvents();
            var (_, firstTrip) = repository.CreateTripGroup([
                events.Single(e => e.StartAt.Day == 1).Id,
                events.Single(e => e.StartAt.Day == 3).Id,
            ]);
            var (_, secondTrip) = repository.CreateTripGroup([
                events.Single(e => e.StartAt.Day == 10).Id,
                events.Single(e => e.StartAt.Day == 12).Id,
            ]);
            var alreadyGroupedEventId = events.Single(e => e.StartAt.Day == 1).Id;

            var result = repository.AddEventToTripGroup(alreadyGroupedEventId, secondTrip!.Id);

            Assert.Equal(AddEventToTripGroupResult.EventAlreadyInATrip, result);
            var untouchedTrip = query.GetAllTripGroups().Single(t => t.Id == firstTrip!.Id);
            Assert.Equal(2, untouchedTrip.EventIds.Count); // unchanged
        });
    }

    private static void RunScanPass(
        LibraryRepository repository, LibraryQueryService query, LibraryDatabase database, MediaItem[] rawItems) =>
        new ScanService(repository, query, database).ScanItems(rawItems);

    private static void WithTempLibrary(Action<LibraryRepository, LibraryQueryService, LibraryDatabase> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-createtrip-test-");
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
