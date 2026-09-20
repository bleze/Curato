using Curato.Core;
using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

/// <summary>
/// Regression: <c>LibraryRepository.GetEventsByFolderPath</c> (formerly
/// <c>GetAdoptedEventsByFolderPath</c>) only recognized folders belonging to
/// `NamingMode = 'Adopted'` Events. Once Organize gives a CuratoManaged
/// Event a real FolderPath by moving its files into a dated folder, a later
/// re-scan didn't recognize that folder as already-tracked, fell through to
/// AdoptedFolderPlanner, and (re-)classified it as a brand-new Adopted
/// Event for content that already belonged to an existing one — confirmed
/// live as auto-organized photos showing up marked "Adopted" after a
/// second scan.
/// </summary>
public class ReScanAfterOrganizeTests
{
    [Fact]
    public void Rescanning_after_organize_does_not_reclassify_the_folder_as_adopted()
    {
        WithTempLibrary((root, database) =>
        {
            var loosePath = Path.Combine(root, "photo.jpg");
            File.WriteAllBytes(loosePath, [1, 2, 3]);

            var query = new LibraryQueryService(database);
            var repository = new LibraryRepository(database);
            new ScanService(repository, query).Scan(root);

            var beforeOrganize = query.GetAllEvents();
            var @event = Assert.Single(beforeOrganize);
            Assert.Equal(NamingMode.CuratoManaged, @event.NamingMode);
            var eventId = @event.Id;

            new OrganizeService(database, query, new OrganizeRepository(database)).Organize();

            var afterOrganize = Assert.Single(query.GetAllEvents());
            Assert.NotNull(afterOrganize.FolderPath);

            // The file physically moved — walking the library root again is
            // exactly what a real periodic re-scan (or another iCloud sync
            // pass) does.
            new ScanService(repository, query).Scan(root);

            var afterRescan = Assert.Single(query.GetAllEvents());
            Assert.Equal(eventId, afterRescan.Id);
            Assert.Equal(NamingMode.CuratoManaged, afterRescan.NamingMode);
            Assert.Single(afterRescan.MediaItemIds);
        });
    }

    private static void WithTempLibrary(Action<string, LibraryDatabase> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-rescan-after-organize-test-");
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
