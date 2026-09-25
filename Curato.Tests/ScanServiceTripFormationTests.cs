using Curato.Core;
using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

public class ScanServiceTripFormationTests
{
    // "Home" for these tests — Copenhagen. Anything within
    // GpsTripGate.AwayFromHomeThresholdKm (50km) of this is "everyday life
    // nearby"; anything farther is a real trip.
    private const double HomeLat = 55.6761;
    private const double HomeLon = 12.5683;
    private const double FarAwayLat = 41.3874; // Rome — genuinely another country
    private const double FarAwayLon = 12.4939;

    private sealed class FakeNominatimClient(string? response = "Rome, Italy") : INominatimClient
    {
        public int CallCount;
        public string? Response { get; set; } = response;

        public string? ReverseGeocode(double latitude, double longitude)
        {
            CallCount++;
            return Response;
        }
    }

    private static MediaItem Item(string relativePath, DateTime takenAt, double? lat = null, double? lon = null) => new()
    {
        RelativePath = relativePath,
        QuickHash = "irrelevant",
        TakenAt = takenAt,
        Latitude = lat,
        Longitude = lon
    };

    [Fact]
    public void Loose_items_spanning_several_days_with_small_gaps_never_auto_form_a_trip_without_gps()
    {
        // TripClusterer (purely time-gap-based) can't tell "a real trip away
        // from home" apart from an ordinary run of days with photos on them
        // without a location signal — anyone who takes photos close to daily
        // would otherwise get their entire library merged into sprawling,
        // meaningless "trips" (every consecutive day's Event is well under
        // the 36-hour gap threshold from the next). With no GPS data at all,
        // GpsTripGate never has a home location to compare against, so the
        // gate never passes and this stays exactly as it always has.
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
    public void Events_with_a_far_away_gps_location_auto_form_a_trip_named_after_the_place()
    {
        WithGpsAwareLibrary((repository, query, service, client) =>
        {
            var items = new List<MediaItem>
            {
                // Plenty of ordinary nearby days — never a trip on their own.
                Item("home1.jpg", new DateTime(2026, 1, 1, 9, 0, 0), HomeLat, HomeLon),
                Item("home2.jpg", new DateTime(2026, 1, 2, 9, 0, 0), HomeLat, HomeLon),
                // A real trip: two consecutive far-away days.
                Item("rome1.jpg", new DateTime(2026, 2, 1, 9, 0, 0), FarAwayLat, FarAwayLon),
                Item("rome2.jpg", new DateTime(2026, 2, 2, 9, 0, 0), FarAwayLat, FarAwayLon),
            };

            service.ScanItems(items);

            var trip = Assert.Single(query.GetAllTripGroups());
            Assert.Equal("Rome, Italy", trip.Title);
            Assert.Equal(2, trip.EventIds.Count);
            Assert.Equal(1, client.CallCount);

            // The two nearby days never joined the trip and stayed standalone.
            var standaloneEvents = query.GetAllEvents().Where(e => e.TripGroupId is null);
            Assert.Equal(2, standaloneEvents.Count());
        });
    }

    [Fact]
    public void Events_all_close_to_home_never_form_a_trip_even_with_gps_data()
    {
        WithGpsAwareLibrary((repository, query, service, client) =>
        {
            var items = new List<MediaItem>();
            for (var day = 0; day < 5; day++)
            {
                items.Add(Item($"IMG_{day:D2}.jpg", new DateTime(2026, 1, 1).AddDays(day).AddHours(12), HomeLat, HomeLon));
            }

            service.ScanItems(items);

            Assert.Empty(query.GetAllTripGroups());
            Assert.Equal(0, client.CallCount); // never even asked to geocode — nothing was away from home
        });
    }

    [Fact]
    public void A_far_away_event_with_no_neighbors_never_forms_a_trip_alone()
    {
        // TripClusterer's own MinSpanDays=2 rule still applies — one far-away
        // day by itself isn't "a trip," GPS or not.
        WithGpsAwareLibrary((repository, query, service, _) =>
        {
            var items = new List<MediaItem>
            {
                Item("home1.jpg", new DateTime(2026, 1, 1, 9, 0, 0), HomeLat, HomeLon),
                Item("rome1.jpg", new DateTime(2026, 2, 1, 9, 0, 0), FarAwayLat, FarAwayLon),
            };

            service.ScanItems(items);

            Assert.Empty(query.GetAllTripGroups());
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
            // "2019" Trip. Each needs a real member, or RemoveEmptyEvents'
            // self-heal (a separate, later fix) would sweep it up too.
            var savedItems = repository.UpsertMediaItems(
            [
                Item("2019/Family Reunion/pic1.jpg", new DateTime(2019, 6, 1)),
                Item("2019/Summer Trip/pic2.jpg", new DateTime(2019, 8, 1)),
            ]);
            var events = new List<Event>
            {
                new()
                {
                    StartAt = new DateTime(2019, 6, 1), EndAt = new DateTime(2019, 6, 1), Title = "Family Reunion",
                    TitleIsAuto = false, FolderPath = "2019/Family Reunion", NamingMode = NamingMode.Adopted,
                    MediaItemIds = [savedItems[0].Id]
                },
                new()
                {
                    StartAt = new DateTime(2019, 8, 1), EndAt = new DateTime(2019, 8, 1), Title = "Summer Trip",
                    TitleIsAuto = false, FolderPath = "2019/Summer Trip", NamingMode = NamingMode.Adopted,
                    MediaItemIds = [savedItems[1].Id]
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

    /// <summary>Same as <see cref="WithTempLibrary"/> but wires a fake
    /// (never-hits-the-network) Nominatim client into the ScanService under
    /// test, and hands it back so a test can assert on how many times it was
    /// actually called.</summary>
    private static void WithGpsAwareLibrary(Action<LibraryRepository, LibraryQueryService, ScanService, FakeNominatimClient> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-gpstriptest-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            var repository = new LibraryRepository(database);
            var query = new LibraryQueryService(database);
            var client = new FakeNominatimClient();
            test(repository, query, new ScanService(repository, query, database, client), client);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
