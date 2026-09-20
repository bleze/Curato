using Curato.Core;
using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

public class RescanIdempotencyTests
{
    private static MediaItem Item(string relativePath, DateTime takenAt) => new()
    {
        RelativePath = relativePath,
        QuickHash = "irrelevant",
        TakenAt = takenAt
    };

    [Fact]
    public void Scanning_the_same_loose_items_twice_does_not_duplicate_events()
    {
        WithTempLibrary((repository, query) =>
        {
            var rawItems = new[]
            {
                Item("loose1.jpg", new DateTime(2026, 7, 11, 9, 0, 0)),
                Item("loose2.jpg", new DateTime(2026, 7, 11, 9, 10, 0)),
            };

            RunScanPass(repository, rawItems);
            RunScanPass(repository, rawItems); // scan again, nothing changed

            var events = query.GetAllEvents();
            Assert.Single(events); // still just one Event, not two
        });
    }

    [Fact]
    public void Scanning_the_same_adopted_folder_twice_does_not_duplicate_its_event()
    {
        WithTempLibrary((repository, query) =>
        {
            var rawItems = new[]
            {
                Item("Family Reunion 2019/pic1.jpg", new DateTime(2019, 6, 1, 10, 0, 0)),
                Item("Family Reunion 2019/pic2.jpg", new DateTime(2019, 6, 1, 22, 0, 0)),
            };

            RunScanPass(repository, rawItems);
            RunScanPass(repository, rawItems); // scan again, nothing changed

            var events = query.GetAllEvents();
            Assert.Single(events);
            Assert.Equal(2, events[0].MediaItemIds.Count);
        });
    }

    [Fact]
    public void A_new_file_added_to_an_already_adopted_folder_joins_the_existing_event()
    {
        WithTempLibrary((repository, query) =>
        {
            var firstPass = new[]
            {
                Item("Family Reunion 2019/pic1.jpg", new DateTime(2019, 6, 1, 10, 0, 0)),
            };
            RunScanPass(repository, firstPass);

            var secondPass = new[]
            {
                Item("Family Reunion 2019/pic1.jpg", new DateTime(2019, 6, 1, 10, 0, 0)),
                Item("Family Reunion 2019/pic2.jpg", new DateTime(2019, 6, 1, 22, 0, 0)), // new file
            };
            RunScanPass(repository, secondPass);

            var events = query.GetAllEvents();
            Assert.Single(events); // still one Event for the folder...
            Assert.Equal(2, events[0].MediaItemIds.Count); // ...now covering both files
        });
    }

    /// <summary>Mirrors ScanController's orchestration exactly, so a test failure here means the real endpoint is broken too.</summary>
    private static void RunScanPass(LibraryRepository repository, MediaItem[] rawItems)
    {
        var savedItems = repository.UpsertMediaItems(rawItems);
        var byFolder = FolderGrouper.GroupByContainingFolder(savedItems);

        var alreadyClustered = repository.GetMediaItemIdsAlreadyInAnyEvent();
        var newLooseItems = byFolder[FolderGrouper.LooseItemsKey]
            .Where(item => !alreadyClustered.Contains(item.Id))
            .ToList();

        var existingAdoptedByFolder = repository.GetAdoptedEventsByFolderPath();
        var newFolderGroups = new List<IGrouping<string, MediaItem>>();

        foreach (var group in byFolder)
        {
            if (group.Key == FolderGrouper.LooseItemsKey)
            {
                continue;
            }

            if (existingAdoptedByFolder.TryGetValue(group.Key, out var existing))
            {
                var newItemsInFolder = group.Where(item => !existing.MediaItemIds.Contains(item.Id)).ToList();
                repository.AddMediaItemsToEvent(existing.Id, newItemsInFolder);
            }
            else
            {
                newFolderGroups.Add(group);
            }
        }

        var newAutoEvents = new EventClusterer().Cluster(newLooseItems);
        var newAdoptedEvents = AdoptedFolderPlanner.PlanEvents(newFolderGroups);
        var allNewEvents = newAutoEvents.Concat(newAdoptedEvents).ToList();
        repository.SaveEvents(allNewEvents);

        var newAutoTrips = new TripClusterer().Cluster(newAutoEvents);
        var newAdoptedTrips = AdoptedFolderPlanner.PlanTrips(newAdoptedEvents);
        repository.SaveTripGroups(newAutoTrips.Concat(newAdoptedTrips).ToList());
    }

    private static void WithTempLibrary(Action<LibraryRepository, LibraryQueryService> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-rescan-test-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            test(new LibraryRepository(database), new LibraryQueryService(database));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
