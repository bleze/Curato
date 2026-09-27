using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

/// <summary>
/// Regression: confirmed live as a real crash, not a hypothetical — something
/// changed a file outside Curato (renamed/moved/deleted) after the last Scan,
/// leaving a stale RelativePath in the database. Organize blindly trusted it
/// and let File.Move's FileNotFoundException blow up the whole request,
/// taking down every other event's move still queued behind it in the same
/// batch, not just the one with the missing file.
/// </summary>
public class OrganizeServiceMissingFileTests
{
    [Fact]
    public void A_missing_member_is_skipped_without_blocking_the_rest_of_its_own_event()
    {
        WithTempLibrary((root, database) =>
        {
            var survivorPath = Path.Combine(root, "survivor.jpg");
            var missingPath = Path.Combine(root, "missing.jpg");
            File.WriteAllBytes(survivorPath, [1, 2, 3]);
            File.WriteAllBytes(missingPath, [4, 5, 6]);

            var query = new LibraryQueryService(database);
            var repository = new LibraryRepository(database);
            new ScanService(repository, query, database).Scan(root);

            var @event = Assert.Single(query.GetAllEvents());
            Assert.Equal(2, @event.MediaItemIds.Count);

            // Simulate an external rename/delete that happened after the
            // scan — Curato's database never finds out until something
            // (a re-scan, or this Organize run) notices the mismatch.
            File.Delete(missingPath);

            var result = new OrganizeService(database, query, new OrganizeRepository(database)).Organize();

            Assert.Equal(1, result.EventsOrganized);
            Assert.Equal(1, result.FilesMoved);
            Assert.Equal(1, result.FilesSkippedMissing);

            var reloaded = Assert.Single(query.GetAllEvents());
            Assert.NotNull(reloaded.FolderPath); // still marked organized — true for the member that does exist

            var survivor = query.GetAllMediaItems().Single(m => m.RelativePath.Contains("survivor"));
            Assert.True(File.Exists(Path.Combine(root, survivor.RelativePath)));
            Assert.NotEqual("survivor.jpg", survivor.RelativePath); // actually relocated into the dated folder

            var missing = query.GetAllMediaItems().Single(m => m.RelativePath.Contains("missing"));
            Assert.Equal("missing.jpg", missing.RelativePath); // left untouched, not moved to a location that also doesn't exist
        });
    }

    [Fact]
    public void A_missing_file_in_one_event_does_not_stop_other_events_from_organizing()
    {
        WithTempLibrary((root, database) =>
        {
            var missingPath = Path.Combine(root, "missing.jpg");
            var otherPath = Path.Combine(root, "other.jpg");
            File.WriteAllBytes(missingPath, [1, 2, 3]);
            // No real Exif on a fake byte array — MediaFileScanner falls
            // back to the filesystem timestamp, so setting these directly
            // (rather than patching TakenAt after the fact) is what
            // actually gets the two into separate Events (>4h gap).
            File.SetLastWriteTime(missingPath, new DateTime(2026, 1, 1, 9, 0, 0));
            File.WriteAllBytes(otherPath, [4, 5, 6]);
            File.SetLastWriteTime(otherPath, new DateTime(2026, 1, 5, 9, 0, 0));

            var query = new LibraryQueryService(database);
            var repository = new LibraryRepository(database);
            new ScanService(repository, query, database).Scan(root);

            Assert.Equal(2, query.GetAllEvents().Count);
            File.Delete(missingPath);

            var result = new OrganizeService(database, query, new OrganizeRepository(database)).Organize();

            Assert.Equal(2, result.EventsOrganized);
            Assert.Equal(1, result.FilesMoved);
            Assert.Equal(1, result.FilesSkippedMissing);
            Assert.All(query.GetAllEvents(), e => Assert.NotNull(e.FolderPath));

            var other = query.GetAllMediaItems().Single(m => m.RelativePath.Contains("other"));
            Assert.True(File.Exists(Path.Combine(root, other.RelativePath)));
        });
    }

    private static void WithTempLibrary(Action<string, LibraryDatabase> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-organize-missing-file-test-");
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
