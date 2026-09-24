using Curato.Core;
using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

/// <summary>
/// Regression: RecycleBinRepository.RemoveMediaItem prevents a *new* delete
/// from leaving an empty Event behind, but that fix only guards its own
/// codepath — it never swept up a zero-member Event a library's database
/// already had from before the fix existed. Confirmed live as a photo-less
/// "Afternoon" Event still showing in the library view even after upgrading.
/// LibraryRepository.RemoveEmptyEvents, run on every scan, self-heals this
/// exactly like the CuratoManaged/bare-year Trip self-heals already do.
/// </summary>
public class ScanServiceEmptyEventCleanupTests
{
    [Fact]
    public void A_leftover_empty_event_from_before_the_fix_is_removed_on_the_next_scan()
    {
        WithTempLibrary((root, database, query, repository) =>
        {
            // Simulate a pre-fix database's leftover: an Event whose members
            // are already all gone, with a real (but now-empty) Organized
            // folder still sitting on disk.
            var folderFullPath = Path.Combine(root, "2026", "2026-09-19 Afternoon");
            Directory.CreateDirectory(folderFullPath);
            repository.SaveEvents(
            [
                new Event
                {
                    StartAt = new DateTime(2026, 9, 19, 15, 0, 0), EndAt = new DateTime(2026, 9, 19, 15, 0, 0),
                    Title = "Afternoon", TitleIsAuto = true, NamingMode = NamingMode.CuratoManaged,
                    FolderPath = "2026/2026-09-19 Afternoon", MediaItemIds = []
                }
            ]);
            Assert.Single(query.GetAllEvents());

            new ScanService(repository, query, database).ScanItems([]); // any scan pass triggers the self-heal

            Assert.Empty(query.GetAllEvents());
            Assert.False(Directory.Exists(folderFullPath));
        });
    }

    [Fact]
    public void A_leftover_empty_event_removes_its_trip_too_once_that_trip_has_no_events_left()
    {
        WithTempLibrary((root, database, query, repository) =>
        {
            var events = new List<Event>
            {
                new()
                {
                    StartAt = new DateTime(2026, 9, 19), EndAt = new DateTime(2026, 9, 19),
                    Title = "Empty Day", TitleIsAuto = true, NamingMode = NamingMode.CuratoManaged, MediaItemIds = []
                }
            };
            repository.SaveEvents(events);
            repository.SaveTripGroups(
            [
                new TripGroup
                {
                    StartDate = new DateOnly(2026, 9, 19), EndDate = new DateOnly(2026, 9, 19),
                    Title = "Lonely Trip", TitleIsAuto = true, NamingMode = NamingMode.CuratoManaged,
                    EventIds = [events[0].Id]
                }
            ]);
            Assert.Single(query.GetAllTripGroups());

            new ScanService(repository, query, database).ScanItems([]);

            Assert.Empty(query.GetAllEvents());
            Assert.Empty(query.GetAllTripGroups());
        });
    }

    [Fact]
    public void A_real_event_with_members_survives_the_sweep()
    {
        WithTempLibrary((root, database, query, repository) =>
        {
            File.WriteAllBytes(Path.Combine(root, "photo.jpg"), [1, 2, 3]);
            new ScanService(repository, query, database).Scan(root);
            Assert.Single(query.GetAllEvents());

            new ScanService(repository, query, database).ScanItems([]);

            Assert.Single(query.GetAllEvents());
        });
    }

    private static void WithTempLibrary(
        Action<string, LibraryDatabase, LibraryQueryService, LibraryRepository> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-empty-event-selfheal-test-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            test(tempDir.FullName, database, new LibraryQueryService(database), new LibraryRepository(database));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
