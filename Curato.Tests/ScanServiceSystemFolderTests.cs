using Curato.Core;
using Curato.Data;
using Curato.Media;
using Curato.Organizer;
using Dapper;

namespace Curato.Tests;

/// <summary>
/// A NAS keeps its own folders inside shared folders (Synology: an @eaDir
/// with Synology Photos' SYNOPHOTO_THUMB_*.jpg previews next to every photo
/// folder). The first scan run on the NAS itself indexed ~53,000 of them.
/// These pin down that the scan never enters them, forgets rows already
/// indexed from them without touching the files, and never "recycles" (=
/// moves) a file that still exists.
/// </summary>
public class ScanServiceSystemFolderTests
{
    [Theory]
    [InlineData("2010/@eaDir/DSC00341.JPG/SYNOPHOTO_THUMB_XL.jpg", true)]
    [InlineData("#recycle/old.jpg", true)]
    [InlineData("2010/@EADIR/x.jpg", true)]
    [InlineData("2010/2010-07-10 Flying Super Kids/DSC00341.JPG", false)]
    [InlineData("@eaDir.jpg", false)]
    [InlineData("2010/.picasaoriginals/DSC00341.JPG", false)]
    public void IsInSystemFolder_matches_only_folder_segments(string relativePath, bool expected) =>
        Assert.Equal(expected, MediaFileScanner.IsInSystemFolder(relativePath));

    [Fact]
    public void A_scan_never_indexes_files_inside_eaDir()
    {
        WithTempLibrary((libraryRoot, _, query, service) =>
        {
            Write(libraryRoot, "2010/DSC00341.JPG");
            Write(libraryRoot, "2010/@eaDir/DSC00341.JPG/SYNOPHOTO_THUMB_XL.jpg");

            service.Scan(libraryRoot);

            Assert.Equal(["2010/DSC00341.JPG"], query.GetAllMediaItems().Select(i => i.RelativePath));
        });
    }

    [Fact]
    public void Rows_already_indexed_from_eaDir_are_forgotten_and_their_files_left_in_place()
    {
        WithTempLibrary((libraryRoot, repository, query, service) =>
        {
            Write(libraryRoot, "2010/DSC00341.JPG");
            var thumbnail = Write(libraryRoot, "2010/@eaDir/DSC00341.JPG/SYNOPHOTO_THUMB_XL.jpg");
            // As the scan before the fix indexed it.
            repository.UpsertMediaItems([NewItem("2010/@eaDir/DSC00341.JPG/SYNOPHOTO_THUMB_XL.jpg")]);

            service.Scan(libraryRoot);

            Assert.Equal(["2010/DSC00341.JPG"], query.GetAllMediaItems().Select(i => i.RelativePath));
            Assert.True(File.Exists(thumbnail));
            Assert.Equal(0, CountRows(libraryRoot, "DeletedItem"));
            Assert.Equal(0, CountRows(libraryRoot, "DeletedMediaFingerprint"));
        });
    }

    [Fact]
    public void An_indexed_file_the_walk_did_not_report_is_not_recycled_while_it_still_exists()
    {
        WithTempLibrary((libraryRoot, repository, _, service) =>
        {
            var present = Write(libraryRoot, "2010/a.jpg");
            Write(libraryRoot, "2010/b.jpg");
            service.Scan(libraryRoot);

            // The walk supplies only b.jpg, as if a.jpg's path had been
            // skipped or spelled differently — a.jpg is still on disk.
            var onlyB = new MediaFileScanner().Scan(libraryRoot).Where(i => i.RelativePath == "2010/b.jpg").ToList();
            service.Scan(libraryRoot, onlyB);

            Assert.True(File.Exists(present));
            Assert.Equal(0, CountRows(libraryRoot, "DeletedItem"));
        });
    }

    private static MediaItem NewItem(string relativePath) => new()
    {
        RelativePath = relativePath,
        Kind = MediaKind.Photo,
        FileSizeBytes = 3,
        QuickHash = "thumb",
        TakenAt = new DateTime(2014, 11, 9),
        ImportedAt = DateTime.UtcNow,
    };

    private static string Write(string libraryRoot, string relativePath)
    {
        var fullPath = Path.Combine(libraryRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, [1, 2, 3]);
        return fullPath;
    }

    private static long CountRows(string libraryRoot, string table)
    {
        using var connection = new LibraryDatabase(libraryRoot).OpenConnection();
        return connection.ExecuteScalar<long>($"SELECT COUNT(*) FROM {table}");
    }

    private static void WithTempLibrary(Action<string, LibraryRepository, LibraryQueryService, ScanService> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-system-folder-test-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            var repository = new LibraryRepository(database);
            var query = new LibraryQueryService(database);
            test(tempDir.FullName, repository, query, new ScanService(repository, query, database));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
