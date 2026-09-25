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
