using Curato.Core;
using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

public class LivePhotoScanIntegrationTests
{
    private static MediaItem Item(string relativePath, DateTime takenAt) => new()
    {
        RelativePath = relativePath,
        Kind = relativePath.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ? MediaKind.Video : MediaKind.Photo,
        QuickHash = "irrelevant",
        TakenAt = takenAt
    };

    [Fact]
    public void A_photo_and_its_mov_scanned_together_are_linked()
    {
        WithTempLibrary((repository, query, service) =>
        {
            var takenAt = new DateTime(2026, 6, 1, 10, 0, 0);
            service.ScanItems([Item("IMG_0001.JPG", takenAt), Item("IMG_0001.MOV", takenAt.AddSeconds(1))]);

            var items = query.GetAllMediaItems();
            var photo = items.Single(i => i.RelativePath == "IMG_0001.JPG");
            var video = items.Single(i => i.RelativePath == "IMG_0001.MOV");

            Assert.Equal(video.Id, photo.LivePhotoVideoItemId);
        });
    }

    [Fact]
    public void A_photo_scanned_first_still_pairs_once_its_mov_arrives_in_a_later_scan()
    {
        WithTempLibrary((repository, query, service) =>
        {
            var takenAt = new DateTime(2026, 6, 1, 10, 0, 0);
            service.ScanItems([Item("IMG_0002.JPG", takenAt)]);
            service.ScanItems([Item("IMG_0002.JPG", takenAt), Item("IMG_0002.MOV", takenAt.AddSeconds(1))]);

            var photo = query.GetAllMediaItems().Single(i => i.RelativePath == "IMG_0002.JPG");
            var video = query.GetAllMediaItems().Single(i => i.RelativePath == "IMG_0002.MOV");

            Assert.Equal(video.Id, photo.LivePhotoVideoItemId);
        });
    }

    private static void WithTempLibrary(Action<LibraryRepository, LibraryQueryService, ScanService> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-livephoto-scan-test-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            var repository = new LibraryRepository(database);
            var query = new LibraryQueryService(database);
            test(repository, query, new ScanService(repository, query));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
