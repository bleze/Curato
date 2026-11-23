using Curato.Core;
using Curato.Data;
using Curato.Organizer;

namespace Curato.Tests;

/// <summary>
/// Confirmed live as a real incident: a leftover iCloud-sync placeholder
/// stub (see OrphanedStubCleanup's own doc comment) got indexed as a real,
/// all-zero MediaItem because an ordinary Scan reached it before the next
/// iCloud sync ever had a chance to clean it up. ScanService.Scan now runs
/// the same cleanup itself, before walking the library, so this self-heals
/// regardless of what triggers the next Scan.
/// </summary>
public class ScanServiceOrphanedStubCleanupTests
{
    [Fact]
    public void A_leftover_stub_is_cleaned_up_before_an_ordinary_scan_can_index_it()
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-scan-stub-cleanup-test-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            var repository = new LibraryRepository(database);
            var query = new LibraryQueryService(database);

            var organizedDir = Path.Combine(tempDir.FullName, "2024", "2024-01-01");
            Directory.CreateDirectory(organizedDir);
            var realBytes = new byte[500];
            new Random(1).NextBytes(realBytes);
            realBytes[0] = 7; // guarantee non-zero, same reasoning as OrphanedStubCleanup's own tests
            File.WriteAllBytes(Path.Combine(organizedDir, "IMG_0001.HEIC"), realBytes);

            new ScanService(repository, query, database).Scan(tempDir.FullName);
            Assert.Single(query.GetAllMediaItems());

            // Simulate exactly what an interrupted sync leaves behind: a
            // zero-byte, correctly-sized placeholder at the root for
            // something already indexed (the real file scanned above).
            var stubPath = Path.Combine(tempDir.FullName, "IMG_0001.HEIC");
            using (var stream = new FileStream(stubPath, FileMode.CreateNew, FileAccess.Write))
            {
                stream.SetLength(realBytes.Length);
            }

            new ScanService(repository, query, database).Scan(tempDir.FullName);

            Assert.False(File.Exists(stubPath));
            // Still just the one real MediaItem — the stub was removed
            // before this second Scan's own walk ever reached it, not
            // indexed as a second, all-zero item.
            Assert.Single(query.GetAllMediaItems());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
