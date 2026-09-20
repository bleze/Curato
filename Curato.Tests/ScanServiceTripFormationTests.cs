using Curato.Core;
using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

public class ScanServiceTripFormationTests
{
    private static MediaItem Item(string relativePath, DateTime takenAt) => new()
    {
        RelativePath = relativePath,
        QuickHash = "irrelevant",
        TakenAt = takenAt
    };

    [Fact]
    public void Loose_items_spanning_several_days_with_small_gaps_never_auto_form_a_trip()
    {
        // Regression: TripClusterer (purely time-gap-based, no location
        // signal) used to be wired into every scan, which meant anyone who
        // takes photos close to daily got their entire library merged into
        // sprawling, meaningless "trips" — every consecutive day's Event was
        // well under the old 36-hour gap threshold from the next. Disabled
        // per explicit product decision until real GPS-based detection
        // exists (PLAN.md §12); this test locks that in.
        WithTempLibrary((repository, query, service) =>
        {
            var items = new List<MediaItem>();
            for (var day = 0; day < 10; day++)
            {
                items.Add(Item($"IMG_{day:D2}.jpg", new DateTime(2026, 1, 1).AddDays(day).AddHours(12)));
            }

            service.ScanItems(items);

            Assert.Empty(query.GetAllTripGroups());
            Assert.Equal(10, query.GetAllEvents().Count);
        });
    }

    [Fact]
    public void A_leftover_auto_formed_trip_from_before_the_fix_is_removed_on_the_next_scan()
    {
        WithTempLibrary((repository, query, service) =>
        {
            // Simulate what an old scan (before auto-Trip formation was
            // disabled) already wrote to this library's database.
            var events = new List<Event>
            {
                new()
                {
                    StartAt = new DateTime(2026, 1, 1), EndAt = new DateTime(2026, 1, 1),
                    Title = "Day 1", TitleIsAuto = true, NamingMode = NamingMode.CuratoManaged
                }
            };
            repository.SaveEvents(events);
            repository.SaveTripGroups(
            [
                new TripGroup
                {
                    StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 1, 1),
                    Title = "Old bogus trip", TitleIsAuto = true, NamingMode = NamingMode.CuratoManaged,
                    EventIds = [events[0].Id]
                }
            ]);
            Assert.Single(query.GetAllTripGroups());

            service.ScanItems([]); // any scan pass triggers the self-heal

            Assert.Empty(query.GetAllTripGroups());
            var survivingEvent = Assert.Single(query.GetAllEvents());
            Assert.Null(survivingEvent.TripGroupId);
        });
    }

    [Fact]
    public void Adopted_folder_structure_still_forms_a_trip_since_thats_not_a_time_based_guess()
    {
        WithTempLibrary((repository, query, service) =>
        {
            var items = new[]
            {
                Item("2019 Summer/Day 1/pic1.jpg", new DateTime(2019, 6, 1, 10, 0, 0)),
                Item("2019 Summer/Day 2/pic2.jpg", new DateTime(2019, 6, 2, 10, 0, 0)),
            };

            service.ScanItems(items);

            Assert.Single(query.GetAllTripGroups());
        });
    }

    [Fact]
    public void A_bare_year_parent_folder_never_forms_a_fictitious_trip()
    {
        // Regression: confirmed live against a real library sorted into
        // top-level 2019/, 2020/, ... buckets — every unrelated event that
        // happened to be adopted under a given year got swept into one
        // fictitious "2019" Trip by PlanTrips' "sibling folders under a
        // shared parent" heuristic, reported as "these are not trips, they
        // are [just] folders" once a Rename control made the bogus Trip's
        // title visible.
        WithTempLibrary((repository, query, service) =>
        {
            var items = new[]
            {
                Item("2019/Family Reunion/pic1.jpg", new DateTime(2019, 6, 1, 10, 0, 0)),
                Item("2019/Summer Trip/pic2.jpg", new DateTime(2019, 8, 1, 10, 0, 0)),
            };

            service.ScanItems(items);

            Assert.Empty(query.GetAllTripGroups());
            var events = query.GetAllEvents();
            Assert.Equal(2, events.Count);
            Assert.All(events, e => Assert.Null(e.TripGroupId));
        });
    }

    [Fact]
    public void A_leftover_bare_year_adopted_trip_from_before_the_fix_is_removed_on_the_next_scan()
    {
        WithTempLibrary((repository, query, service) =>
        {
            // Simulate what a pre-fix scan already wrote to this library's
            // database: two unrelated Adopted Events swept into one bogus
            // "2019" Trip.
            var events = new List<Event>
            {
                new()
                {
                    StartAt = new DateTime(2019, 6, 1), EndAt = new DateTime(2019, 6, 1), Title = "Family Reunion",
                    TitleIsAuto = false, FolderPath = "2019/Family Reunion", NamingMode = NamingMode.Adopted
                },
                new()
                {
                    StartAt = new DateTime(2019, 8, 1), EndAt = new DateTime(2019, 8, 1), Title = "Summer Trip",
                    TitleIsAuto = false, FolderPath = "2019/Summer Trip", NamingMode = NamingMode.Adopted
                },
            };
            repository.SaveEvents(events);
            repository.SaveTripGroups(
            [
                new TripGroup
                {
                    StartDate = new DateOnly(2019, 6, 1), EndDate = new DateOnly(2019, 8, 1),
                    Title = "2019", TitleIsAuto = false, FolderPath = "2019", NamingMode = NamingMode.Adopted,
                    EventIds = [events[0].Id, events[1].Id]
                }
            ]);
            Assert.Single(query.GetAllTripGroups());

            service.ScanItems([]); // any scan pass triggers the self-heal

            Assert.Empty(query.GetAllTripGroups());
            var survivingEvents = query.GetAllEvents();
            Assert.Equal(2, survivingEvents.Count);
            Assert.All(survivingEvents, e => Assert.Null(e.TripGroupId));
        });
    }

    private static void WithTempLibrary(Action<LibraryRepository, LibraryQueryService, ScanService> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-triptest-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            var repository = new LibraryRepository(database);
            var query = new LibraryQueryService(database);
            test(repository, query, new ScanService(repository, query, database));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
