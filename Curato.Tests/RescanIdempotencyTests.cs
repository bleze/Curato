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

            RunScanPass(repository, query, rawItems);
            RunScanPass(repository, query, rawItems); // scan again, nothing changed

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

            RunScanPass(repository, query, rawItems);
            RunScanPass(repository, query, rawItems); // scan again, nothing changed

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
            RunScanPass(repository, query, firstPass);

            var secondPass = new[]
            {
                Item("Family Reunion 2019/pic1.jpg", new DateTime(2019, 6, 1, 10, 0, 0)),
                Item("Family Reunion 2019/pic2.jpg", new DateTime(2019, 6, 1, 22, 0, 0)), // new file
            };
            RunScanPass(repository, query, secondPass);

            var events = query.GetAllEvents();
            Assert.Single(events); // still one Event for the folder...
            Assert.Equal(2, events[0].MediaItemIds.Count); // ...now covering both files
        });
    }

    [Fact]
    public void A_later_added_file_taken_earlier_is_inserted_chronologically_not_appended()
    {
        // Regression: AddMediaItemsToEvent used to always append new items
        // with an ever-increasing SortOrder, regardless of their actual
        // TakenAt — so a file added in a later scan always landed at the
        // end of the thumb-grid even if it was chronologically the very
        // first photo in the folder.
        WithTempLibrary((repository, query) =>
        {
            var firstPass = new[]
            {
                Item("Trip/noon.jpg", new DateTime(2019, 6, 1, 12, 0, 0)),
            };
            RunScanPass(repository, query, firstPass);

            var secondPass = new[]
            {
                Item("Trip/noon.jpg", new DateTime(2019, 6, 1, 12, 0, 0)),
                Item("Trip/morning.jpg", new DateTime(2019, 6, 1, 8, 0, 0)), // taken earlier, added later
            };
            RunScanPass(repository, query, secondPass);

            var @event = Assert.Single(query.GetAllEvents());
            var orderedRelativePaths = @event.MediaItemIds
                .Select(id => query.GetAllMediaItems().Single(m => m.Id == id).RelativePath)
                .ToList();

            Assert.Equal(["Trip/morning.jpg", "Trip/noon.jpg"], orderedRelativePaths);
        });
    }

    /// <summary>Delegates to the real ScanService, so a test failure here means the real endpoint is broken too.</summary>
    private static void RunScanPass(LibraryRepository repository, LibraryQueryService query, MediaItem[] rawItems) =>
        new ScanService(repository, query).ScanItems(rawItems);

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
