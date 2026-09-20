using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

/// <summary>
/// Regression: deleting an Event's last remaining MediaItem left the Event
/// row behind with zero members — confirmed live as a photo-less stub
/// lingering in the library view. RecycleBinRepository.RemoveMediaItem now
/// removes an Event (and its TripGroup, if that's also left empty) once its
/// last member is gone.
/// </summary>
public class RecycleBinEmptyEventCleanupTests
{
    [Fact]
    public void Deleting_the_last_item_in_an_event_removes_the_now_empty_event()
    {
        WithTempLibrary((root, database, query, service) =>
        {
            File.WriteAllBytes(Path.Combine(root, "photo.jpg"), [1, 2, 3]);
            new ScanService(new LibraryRepository(database), query, database).Scan(root);

            var @event = Assert.Single(query.GetAllEvents());
            var mediaItemId = Assert.Single(@event.MediaItemIds);

            service.DeleteMediaItem(mediaItemId, "Manual");

            Assert.Empty(query.GetAllEvents());
        });
    }

    [Fact]
    public void Deleting_one_of_several_items_in_an_event_leaves_the_event_in_place()
    {
        WithTempLibrary((root, database, query, service) =>
        {
            File.WriteAllBytes(Path.Combine(root, "a.jpg"), [1]);
            File.WriteAllBytes(Path.Combine(root, "b.jpg"), [2]);
            new ScanService(new LibraryRepository(database), query, database).Scan(root);

            var @event = Assert.Single(query.GetAllEvents());
            var firstId = @event.MediaItemIds[0];

            service.DeleteMediaItem(firstId, "Manual");

            var remaining = Assert.Single(query.GetAllEvents());
            Assert.Single(remaining.MediaItemIds);
        });
    }

    [Fact]
    public void Deleting_the_last_item_in_an_organized_event_also_removes_its_now_empty_folder()
    {
        WithTempLibrary((root, database, query, service) =>
        {
            File.WriteAllBytes(Path.Combine(root, "photo.jpg"), [1, 2, 3]);
            var repository = new LibraryRepository(database);
            new ScanService(repository, query, database).Scan(root);
            new OrganizeService(database, query, new OrganizeRepository(database)).Organize();

            var @event = Assert.Single(query.GetAllEvents());
            Assert.NotNull(@event.FolderPath);
            var folderFullPath = Path.Combine(root, @event.FolderPath!);
            Assert.True(Directory.Exists(folderFullPath));
            var mediaItemId = Assert.Single(@event.MediaItemIds);

            service.DeleteMediaItem(mediaItemId, "Manual");

            Assert.Empty(query.GetAllEvents());
            Assert.False(Directory.Exists(folderFullPath));
        });
    }

    private static void WithTempLibrary(
        Action<string, LibraryDatabase, LibraryQueryService, RecycleBinService> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-empty-event-cleanup-test-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            var query = new LibraryQueryService(database);
            var service = new RecycleBinService(database, new RecycleBinRepository(database));
            test(tempDir.FullName, database, query, service);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
