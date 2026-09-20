using Curato.Core;
using Curato.Data;
using Curato.Organizer;
using SkiaSharp;

namespace Curato.Tests;

public class PicasaImportServiceTests
{
    // A 200x100 face rect covering roughly the left half of a 400x200 image:
    // left=0x0000, top=0x0000, right=0x8000 (~0.5), bottom=0xFFFF (1.0)
    private const string LeftHalfRectHex = "000000008000ffff";

    [Fact]
    public void Imports_star_faces_rotate_and_crop_for_a_matched_photo()
    {
        WithTempLibrary((root, database, query, repository, service) =>
        {
            CreateJpeg(Path.Combine(root, "IMG_0001.JPG"), 400, 200);
            ScanLibrary(database, root);

            File.WriteAllText(Path.Combine(root, "contacts.xml"), """
                <contacts>
                  <contact id="contactA" name="Jane Doe" />
                </contacts>
                """);

            File.WriteAllText(Path.Combine(root, "Picasa.ini"), $$"""
                [IMG_0001.JPG]
                star=yes
                rotate=rotate(1)
                crop=rect64({{LeftHalfRectHex}})
                faces=rect64({{LeftHalfRectHex}}),contactA
                """);

            var result = service.Import();

            Assert.Equal(1, result.IniFilesFound);
            Assert.True(result.ContactsFileFound);
            Assert.Equal(1, result.PhotosMatched);
            Assert.Equal(1, result.PhotosWithNewData);
            Assert.Equal(1, result.FacesImported);
            Assert.Equal(1, result.PeopleMatched);
            Assert.Equal(2, result.EditsImported); // rotate + crop
            Assert.Equal(1, result.TagsApplied); // Favorite

            var mediaItem = query.GetAllMediaItems().Single();
            Assert.True(repository.HasBeenImported(mediaItem.Id));
        });
    }

    [Fact]
    public void Faces_without_a_matching_contact_import_with_no_person_assigned()
    {
        WithTempLibrary((root, database, query, repository, service) =>
        {
            CreateJpeg(Path.Combine(root, "IMG_0002.JPG"), 400, 200);
            ScanLibrary(database, root);

            // No contacts.xml at all — the face box should still import.
            File.WriteAllText(Path.Combine(root, "Picasa.ini"), $$"""
                [IMG_0002.JPG]
                faces=rect64({{LeftHalfRectHex}}),unknownContact
                """);

            var result = service.Import();

            Assert.False(result.ContactsFileFound);
            Assert.Equal(1, result.FacesImported);
            Assert.Equal(0, result.PeopleMatched);
        });
    }

    [Fact]
    public void Re_running_import_does_not_duplicate_data_for_already_imported_photos()
    {
        WithTempLibrary((root, database, query, repository, service) =>
        {
            CreateJpeg(Path.Combine(root, "IMG_0003.JPG"), 400, 200);
            ScanLibrary(database, root);

            File.WriteAllText(Path.Combine(root, "Picasa.ini"), """
                [IMG_0003.JPG]
                star=yes
                """);

            var first = service.Import();
            var second = service.Import();

            Assert.Equal(1, first.PhotosWithNewData);
            Assert.Equal(0, second.PhotosWithNewData); // already imported — skipped, not duplicated
            Assert.Equal(1, second.PhotosMatched); // still recognized as a match, just not re-processed
        });
    }

    [Fact]
    public void Re_running_import_does_not_reapply_a_star_only_photos_tag()
    {
        // Regression: a photo whose only Picasa data is star=yes leaves no
        // Face/Edit row behind, so idempotency can't be inferred from those
        // tables alone — it needs its own explicit "already imported" marker.
        WithTempLibrary((root, database, query, repository, service) =>
        {
            CreateJpeg(Path.Combine(root, "IMG_0005.JPG"), 400, 200);
            ScanLibrary(database, root);

            File.WriteAllText(Path.Combine(root, "Picasa.ini"), """
                [IMG_0005.JPG]
                star=yes
                """);

            service.Import();
            var second = service.Import();

            Assert.Equal(0, second.PhotosWithNewData);
            Assert.Equal(0, second.TagsApplied);
        });
    }

    [Fact]
    public void Sections_that_do_not_match_a_scanned_media_item_are_ignored()
    {
        WithTempLibrary((root, database, query, repository, service) =>
        {
            CreateJpeg(Path.Combine(root, "IMG_0004.JPG"), 400, 200);
            ScanLibrary(database, root);

            File.WriteAllText(Path.Combine(root, "Picasa.ini"), """
                [.album:main]
                name=Some Picasa metadata section, not a photo

                [IMG_9999_never_scanned.JPG]
                star=yes
                """);

            var result = service.Import();

            Assert.Equal(0, result.PhotosMatched);
            Assert.Equal(0, result.TagsApplied);
        });
    }

    private static void CreateJpeg(string path, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static void ScanLibrary(LibraryDatabase database, string root) =>
        new ScanService(new LibraryRepository(database), new LibraryQueryService(database), database).Scan(root);

    private static void WithTempLibrary(
        Action<string, LibraryDatabase, LibraryQueryService, PicasaImportRepository, PicasaImportService> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-picasa-test-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            var query = new LibraryQueryService(database);
            var repository = new PicasaImportRepository(database);
            var service = new PicasaImportService(database, query, repository);
            test(tempDir.FullName, database, query, repository, service);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
