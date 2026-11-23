using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

/// <summary>
/// Confirmed live as a real need: copying in a folder of photos that
/// already had its own (irrelevant) subfolder structure got every one of
/// those subfolders Adopted as-is, forcing a manual flatten-to-root just to
/// get them auto-clustered instead. Dropping the same content under
/// FolderGrouper.ImportFolderName (".Import"), nested folders and all,
/// should get it auto-clustered and then physically relocated out by
/// Organize, same as any other auto-organized content — see
/// FolderGrouperTests for the lower-level grouping rule itself.
/// </summary>
public class ImportFolderTests
{
    [Fact]
    public void Content_dumped_under_the_import_folder_is_auto_clustered_not_adopted()
    {
        WithTempLibrary((root, database) =>
        {
            var importDir = Path.Combine(root, ".Import", "Family", "2024");
            Directory.CreateDirectory(importDir);
            var filePath = Path.Combine(importDir, "a.jpg");
            File.WriteAllBytes(filePath, [1, 2, 3]);
            File.SetLastWriteTime(filePath, new DateTime(2026, 1, 1, 9, 0, 0));

            var query = new LibraryQueryService(database);
            var repository = new LibraryRepository(database);
            new ScanService(repository, query, database).Scan(root);

            var @event = Assert.Single(query.GetAllEvents());
            Assert.Equal("CuratoManaged", @event.NamingMode.ToString());
            Assert.Null(@event.FolderPath); // not yet organized — still sitting wherever it was dumped
        });
    }

    [Fact]
    public void Organize_relocates_imported_content_out_and_cleans_up_emptied_import_subfolders()
    {
        WithTempLibrary((root, database) =>
        {
            var importDir = Path.Combine(root, ".Import", "Family", "2024");
            Directory.CreateDirectory(importDir);
            var filePath = Path.Combine(importDir, "a.jpg");
            File.WriteAllBytes(filePath, [1, 2, 3]);
            File.SetLastWriteTime(filePath, new DateTime(2026, 1, 1, 9, 0, 0));

            var query = new LibraryQueryService(database);
            var repository = new LibraryRepository(database);
            new ScanService(repository, query, database).Scan(root);

            var result = new OrganizeService(database, query, new OrganizeRepository(database)).Organize();
            Assert.Equal(1, result.FilesMoved);

            var item = Assert.Single(query.GetAllMediaItems());
            Assert.DoesNotContain(".Import", item.RelativePath);
            Assert.True(File.Exists(Path.Combine(root, item.RelativePath)));

            // The now-pointless nested subfolders the import dump arrived
            // in are cleaned up — but the import folder itself survives,
            // ready to be dumped into again.
            Assert.False(Directory.Exists(Path.Combine(root, ".Import", "Family")));
            Assert.True(Directory.Exists(Path.Combine(root, ".Import")));
        });
    }

    [Fact]
    public void A_non_empty_import_subfolder_left_behind_by_another_file_is_not_deleted()
    {
        WithTempLibrary((root, database) =>
        {
            var importDir = Path.Combine(root, ".Import", "Family");
            Directory.CreateDirectory(importDir);
            var movedPath = Path.Combine(importDir, "a.jpg");
            var leftoverPath = Path.Combine(importDir, "not-a-photo.txt");
            File.WriteAllBytes(movedPath, [1, 2, 3]);
            File.SetLastWriteTime(movedPath, new DateTime(2026, 1, 1, 9, 0, 0));
            File.WriteAllText(leftoverPath, "notes");

            var query = new LibraryQueryService(database);
            var repository = new LibraryRepository(database);
            new ScanService(repository, query, database).Scan(root);
            new OrganizeService(database, query, new OrganizeRepository(database)).Organize();

            Assert.True(Directory.Exists(importDir));
            Assert.True(File.Exists(leftoverPath));
        });
    }

    private static void WithTempLibrary(Action<string, LibraryDatabase> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-import-folder-test-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            test(tempDir.FullName, database);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
