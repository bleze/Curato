using Curato.Core;
using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

/// <summary>
/// Covers the last piece of manual override control PLAN.md §3 Step 5
/// promised but never built: renaming an Event/TripGroup's title. Before
/// Organize there's no folder yet, so a rename is a pure database write;
/// once a folder exists, the folder-naming template renders the title
/// straight into the path, so the folder on disk has to follow the rename or
/// the library view and the filesystem would disagree.
/// </summary>
public class RenameServiceTests
{
    [Fact]
    public void Renaming_an_unorganized_event_only_updates_the_title_since_there_is_no_folder_yet()
    {
        WithTempLibrary((root, database, query, repository, organizeRepository) =>
        {
            File.WriteAllBytes(Path.Combine(root, "photo.jpg"), [1, 2, 3]);
            new ScanService(repository, query, database).Scan(root);
            var eventId = Assert.Single(query.GetAllEvents()).Id;

            var renamed = new RenameService(database, query, repository, organizeRepository)
                .RenameEvent(eventId, "Beach Day");

            Assert.True(renamed);
            var @event = Assert.Single(query.GetAllEvents());
            Assert.Equal("Beach Day", @event.Title);
            Assert.False(@event.TitleIsAuto);
            Assert.Null(@event.FolderPath);
        });
    }

    [Fact]
    public void Renaming_an_organized_event_also_renames_its_folder_and_its_items_paths()
    {
        WithTempLibrary((root, database, query, repository, organizeRepository) =>
        {
            File.WriteAllBytes(Path.Combine(root, "photo.jpg"), [1, 2, 3]);
            new ScanService(repository, query, database).Scan(root);
            new OrganizeService(database, query, organizeRepository).Organize();

            var before = Assert.Single(query.GetAllEvents());
            var oldFolderFullPath = Path.Combine(root, before.FolderPath!);
            Assert.True(Directory.Exists(oldFolderFullPath));

            var renamed = new RenameService(database, query, repository, organizeRepository)
                .RenameEvent(before.Id, "Beach Day");

            Assert.True(renamed);
            var after = Assert.Single(query.GetAllEvents());
            Assert.Equal("Beach Day", after.Title);
            Assert.EndsWith("Beach Day", after.FolderPath);
            Assert.False(Directory.Exists(oldFolderFullPath));
            Assert.True(Directory.Exists(Path.Combine(root, after.FolderPath!)));

            var item = Assert.Single(query.GetAllMediaItems());
            Assert.Equal($"{after.FolderPath}/photo.jpg", item.RelativePath);
            Assert.True(File.Exists(Path.Combine(root, item.RelativePath)));
        });
    }

    [Fact]
    public void Renaming_an_organized_trip_renames_its_folder_and_cascades_to_nested_events_and_items()
    {
        WithTempLibrary((root, database, query, repository, organizeRepository) =>
        {
            // Auto-Trip formation is disabled (ScanServiceTripFormationTests)
            // and the manual "Group into trip" control (PLAN.md §3 Step 5)
            // isn't built yet, so a CuratoManaged TripGroup is seeded
            // directly here — exactly how it'll land once that control
            // exists, and how a leftover pre-fix library already has it.
            File.WriteAllBytes(Path.Combine(root, "day1.jpg"), [1]);
            File.WriteAllBytes(Path.Combine(root, "day2.jpg"), [2]);
            var savedItems = repository.UpsertMediaItems(
            [
                new MediaItem { RelativePath = "day1.jpg", QuickHash = "a", TakenAt = new DateTime(2026, 7, 10, 9, 0, 0) },
                new MediaItem { RelativePath = "day2.jpg", QuickHash = "b", TakenAt = new DateTime(2026, 7, 11, 9, 0, 0) },
            ]);
            var events = new List<Event>
            {
                new()
                {
                    StartAt = savedItems[0].TakenAt, EndAt = savedItems[0].TakenAt, Title = "Day 1",
                    TitleIsAuto = true, NamingMode = NamingMode.CuratoManaged, MediaItemIds = [savedItems[0].Id]
                },
                new()
                {
                    StartAt = savedItems[1].TakenAt, EndAt = savedItems[1].TakenAt, Title = "Day 2",
                    TitleIsAuto = true, NamingMode = NamingMode.CuratoManaged, MediaItemIds = [savedItems[1].Id]
                },
            };
            repository.SaveEvents(events);
            repository.SaveTripGroups(
            [
                new TripGroup
                {
                    StartDate = DateOnly.FromDateTime(events[0].StartAt), EndDate = DateOnly.FromDateTime(events[1].StartAt),
                    Title = "Original Trip", TitleIsAuto = true, NamingMode = NamingMode.CuratoManaged,
                    EventIds = [events[0].Id, events[1].Id]
                }
            ]);

            new OrganizeService(database, query, organizeRepository).Organize();

            var tripBefore = Assert.Single(query.GetAllTripGroups());
            var oldTripFullPath = Path.Combine(root, tripBefore.FolderPath!);
            Assert.True(Directory.Exists(oldTripFullPath));
            var nestedEventsBefore = query.GetAllEvents().Where(e => e.TripGroupId == tripBefore.Id).ToList();
            Assert.Equal(2, nestedEventsBefore.Count);

            var renamed = new RenameService(database, query, repository, organizeRepository)
                .RenameTripGroup(tripBefore.Id, "Summer Vacation");

            Assert.True(renamed);
            var tripAfter = Assert.Single(query.GetAllTripGroups());
            Assert.Equal("Summer Vacation", tripAfter.Title);
            Assert.EndsWith("Summer Vacation", tripAfter.FolderPath);
            Assert.False(Directory.Exists(oldTripFullPath));
            Assert.True(Directory.Exists(Path.Combine(root, tripAfter.FolderPath!)));

            foreach (var nestedEvent in query.GetAllEvents().Where(e => e.TripGroupId == tripAfter.Id))
            {
                Assert.StartsWith(tripAfter.FolderPath!, nestedEvent.FolderPath);
                Assert.True(Directory.Exists(Path.Combine(root, nestedEvent.FolderPath!)));
            }

            Assert.All(query.GetAllMediaItems(), item => Assert.True(File.Exists(Path.Combine(root, item.RelativePath))));
        });
    }

    [Fact]
    public void Renaming_an_adopted_event_updates_the_title_but_never_touches_its_real_folder()
    {
        WithTempLibrary((root, database, query, repository, organizeRepository) =>
        {
            var folderFullPath = Path.Combine(root, "Family Reunion 2019");
            Directory.CreateDirectory(folderFullPath);
            File.WriteAllBytes(Path.Combine(folderFullPath, "pic.jpg"), [1, 2, 3]);
            new ScanService(repository, query, database).Scan(root);

            var before = Assert.Single(query.GetAllEvents());
            Assert.Equal(NamingMode.Adopted, before.NamingMode);

            var renamed = new RenameService(database, query, repository, organizeRepository)
                .RenameEvent(before.Id, "Corrected Title");

            Assert.True(renamed);
            var after = Assert.Single(query.GetAllEvents());
            Assert.Equal("Corrected Title", after.Title);
            Assert.Equal("Family Reunion 2019", after.FolderPath); // untouched on disk
            Assert.True(Directory.Exists(folderFullPath));
        });
    }

    [Fact]
    public void An_empty_title_is_rejected()
    {
        WithTempLibrary((root, database, query, repository, organizeRepository) =>
        {
            File.WriteAllBytes(Path.Combine(root, "photo.jpg"), [1, 2, 3]);
            new ScanService(repository, query, database).Scan(root);
            var eventId = Assert.Single(query.GetAllEvents()).Id;

            var renamed = new RenameService(database, query, repository, organizeRepository)
                .RenameEvent(eventId, "   ");

            Assert.False(renamed);
        });
    }

    [Fact]
    public void Renaming_an_unknown_event_id_returns_false()
    {
        WithTempLibrary((root, database, query, repository, organizeRepository) =>
        {
            var renamed = new RenameService(database, query, repository, organizeRepository)
                .RenameEvent(999, "Anything");

            Assert.False(renamed);
        });
    }

    private static void WithTempLibrary(
        Action<string, LibraryDatabase, LibraryQueryService, LibraryRepository, OrganizeRepository> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-rename-test-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            test(
                tempDir.FullName, database, new LibraryQueryService(database),
                new LibraryRepository(database), new OrganizeRepository(database));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
