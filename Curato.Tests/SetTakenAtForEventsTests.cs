using Curato.Core;
using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

/// <summary>
/// Bulk date-fix for a whole folder of undated/mis-dated scans (e.g. a
/// legacy "pre-1998" album) — see LibraryRepository.SetTakenAtForEvents's
/// own doc comment for why every member gets set to one identical date
/// rather than trying to preserve relative ordering.
/// </summary>
public class SetTakenAtForEventsTests
{
    private static MediaItem Item(string relativePath, DateTime takenAt) => new()
    {
        RelativePath = relativePath,
        QuickHash = "irrelevant",
        TakenAt = takenAt
    };

    [Fact]
    public void Every_member_of_an_adopted_event_gets_the_same_new_date()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("!Pre 1998/scan001.jpg", new DateTime(2015, 6, 1, 10, 0, 0)), // wrong: this is when it was scanned in
                Item("!Pre 1998/scan002.jpg", new DateTime(2015, 6, 1, 10, 0, 5)),
            ]);

            var adoptedEvent = Assert.Single(query.GetAllEvents());
            Assert.Equal(NamingMode.Adopted, adoptedEvent.NamingMode);
            var newDate = new DateTime(1990, 1, 1);

            var affected = repository.SetTakenAtForEvents([adoptedEvent.Id], newDate);

            Assert.Equal(2, affected);
            foreach (var item in query.GetAllMediaItems())
            {
                Assert.Equal(newDate, item.TakenAt);
                Assert.Equal(TakenAtSource.Manual, item.TakenAtSource);
            }

            var updatedEvent = Assert.Single(query.GetAllEvents());
            Assert.Equal(newDate, updatedEvent.StartAt);
            Assert.Equal(newDate, updatedEvent.EndAt);
        });
    }

    [Fact]
    public void Existing_thumbnail_order_survives_even_though_every_date_now_ties()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("!Pre 1998/scan001.jpg", new DateTime(2015, 6, 1, 10, 0, 0)),
                Item("!Pre 1998/scan002.jpg", new DateTime(2015, 6, 1, 10, 0, 5)),
                Item("!Pre 1998/scan003.jpg", new DateTime(2015, 6, 1, 10, 0, 10)),
            ]);

            var eventBefore = Assert.Single(query.GetAllEvents());
            var orderedBefore = eventBefore.MediaItemIds;

            repository.SetTakenAtForEvents([eventBefore.Id], new DateTime(1990, 1, 1));

            var eventAfter = Assert.Single(query.GetAllEvents());
            Assert.Equal(orderedBefore, eventAfter.MediaItemIds);
        });
    }

    [Fact]
    public void Fixing_one_events_dates_also_widens_its_trip_groups_range()
    {
        WithTempLibrary((repository, query, database) =>
        {
            // A shared, non-bare-year parent folder ("Trip") with two
            // sibling day-subfolders forms an Adopted TripGroup — see
            // AdoptedFolderPlanner.PlanTrips.
            RunScanPass(repository, query, database,
            [
                Item("Trip/Day1/a.jpg", new DateTime(2015, 6, 1, 10, 0, 0)), // wrong scan-in date
                Item("Trip/Day2/b.jpg", new DateTime(2000, 1, 2, 10, 0, 0)), // this one's date is already correct
            ]);

            var events = query.GetAllEvents();
            Assert.Equal(2, events.Count);
            var trip = Assert.Single(query.GetAllTripGroups());
            var day1Event = events.Single(e => e.FolderPath == "Trip/Day1");

            repository.SetTakenAtForEvents([day1Event.Id], new DateTime(2000, 1, 1));

            var updatedTrip = Assert.Single(query.GetAllTripGroups());
            Assert.Equal(new DateOnly(2000, 1, 1), updatedTrip.StartDate);
            Assert.Equal(new DateOnly(2000, 1, 2), updatedTrip.EndDate);
        });
    }

    [Fact]
    public void Fixing_multiple_events_in_one_call_updates_all_of_them()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database,
            [
                Item("2003/a.jpg", new DateTime(2003, 5, 1)),
                Item("2003 Mille/b.jpg", new DateTime(2003, 5, 1)),
            ]);

            var events = query.GetAllEvents();
            Assert.Equal(2, events.Count); // confirms the sibling split survives adoption, unrelated to this bulk fix
            var newDate = new DateTime(2003, 6, 15);

            var affected = repository.SetTakenAtForEvents(events.Select(e => e.Id).ToList(), newDate);

            Assert.Equal(2, affected);
            Assert.All(query.GetAllEvents(), e =>
            {
                Assert.Equal(newDate, e.StartAt);
                Assert.Equal(newDate, e.EndAt);
            });
        });
    }

    [Fact]
    public void An_unknown_event_id_is_skipped_rather_than_throwing()
    {
        WithTempLibrary((repository, query, database) =>
        {
            RunScanPass(repository, query, database, [Item("a.jpg", new DateTime(2026, 1, 1))]);

            var affected = repository.SetTakenAtForEvents([999], new DateTime(1990, 1, 1));

            Assert.Equal(0, affected);
        });
    }

    private static void RunScanPass(
        LibraryRepository repository, LibraryQueryService query, LibraryDatabase database, MediaItem[] rawItems) =>
        new ScanService(repository, query, database).ScanItems(rawItems);

    private static void WithTempLibrary(Action<LibraryRepository, LibraryQueryService, LibraryDatabase> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-set-taken-at-for-events-test-");
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
