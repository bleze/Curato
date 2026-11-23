using Curato.Core;
using Curato.Data;
using Curato.iCloud;
using Curato.Organizer;

namespace Curato.Tests;

/// <summary>
/// Confirmed live as a real incident: a sync interrupted between seeding
/// placeholder stubs (see SeedAlreadyDownloadedStubs) and reaching
/// CleanupStubs — the hosting process killed outright during an unrelated
/// rebuild/restart — left ~2000 zero-byte stub files permanently sitting at
/// a real library's root, since nothing ever revisited files a past run
/// created. OrphanedStubCleanup.CleanupOrphanedStubs is the self-healing
/// fix: both the next iCloud sync AND the next ordinary Scan now sweep for
/// and remove any such leftover before it can be mistaken for real content.
/// </summary>
public class iCloudStubCleanupTests
{
    [Fact]
    public void CleanupOrphanedStubs_removes_a_leftover_stub_from_an_interrupted_sync()
    {
        WithTempLibrary((libraryRoot, repository, query) =>
        {
            var item = repository.UpsertMediaItems([
                new MediaItem
                {
                    RelativePath = "2024/2024-01-01/IMG_0001.HEIC",
                    QuickHash = "a",
                    FileSizeBytes = 12345,
                    TakenAt = new DateTime(2024, 1, 1),
                },
            ]).Single();

            // Simulate exactly what SeedAlreadyDownloadedStubs leaves behind
            // when a sync never reaches CleanupStubs.
            var stubPath = Path.Combine(libraryRoot, "IMG_0001.HEIC");
            using (var stream = new FileStream(stubPath, FileMode.CreateNew, FileAccess.Write))
            {
                stream.SetLength(item.FileSizeBytes);
            }

            OrphanedStubCleanup.CleanupOrphanedStubs(libraryRoot, query);

            Assert.False(File.Exists(stubPath));
        });
    }

    [Fact]
    public void CleanupOrphanedStubs_never_removes_a_real_file_with_a_colliding_name_and_size()
    {
        WithTempLibrary((libraryRoot, repository, query) =>
        {
            var realBytes = new byte[500];
            new Random(1).NextBytes(realBytes);
            // Guarantee at least one non-zero byte regardless of what the
            // RNG happened to produce, since that's the exact property
            // CleanupOrphanedStubs relies on to tell a stub from a real file.
            realBytes[0] = 7;

            var item = repository.UpsertMediaItems([
                new MediaItem
                {
                    RelativePath = "2024/2024-01-01/real.jpg",
                    QuickHash = "a",
                    FileSizeBytes = realBytes.Length,
                    TakenAt = new DateTime(2024, 1, 1),
                },
            ]).Single();

            // Not yet organized — genuinely sitting at the root, with real
            // content, same (name, size) a stub for this same item would have.
            var realPath = Path.Combine(libraryRoot, "real.jpg");
            File.WriteAllBytes(realPath, realBytes);
            Assert.Equal(item.FileSizeBytes, realBytes.Length);

            OrphanedStubCleanup.CleanupOrphanedStubs(libraryRoot, query);

            Assert.True(File.Exists(realPath));
            Assert.Equal(realBytes, File.ReadAllBytes(realPath));
        });
    }

    [Fact]
    public void IsAllZeroBytes_distinguishes_a_stub_from_real_content()
    {
        WithTempLibrary((libraryRoot, _, _) =>
        {
            var stubPath = Path.Combine(libraryRoot, "stub.bin");
            using (var stream = new FileStream(stubPath, FileMode.CreateNew, FileAccess.Write))
            {
                stream.SetLength(10_000);
            }

            var realPath = Path.Combine(libraryRoot, "real.bin");
            File.WriteAllBytes(realPath, new byte[] { 0, 0, 0, 1, 0, 0 });

            Assert.True(OrphanedStubCleanup.IsAllZeroBytes(stubPath));
            Assert.False(OrphanedStubCleanup.IsAllZeroBytes(realPath));
        });
    }

    [Fact]
    public void SeedAlreadyDownloadedStubs_then_CleanupStubs_leaves_nothing_behind_on_the_happy_path()
    {
        WithTempLibrary((libraryRoot, repository, query) =>
        {
            repository.UpsertMediaItems([
                new MediaItem
                {
                    RelativePath = "2024/2024-01-01/IMG_0002.HEIC",
                    QuickHash = "a",
                    FileSizeBytes = 999,
                    TakenAt = new DateTime(2024, 1, 1),
                },
            ]);

            var stubPaths = iCloudSyncRunner.SeedAlreadyDownloadedStubs(
                libraryRoot, query, cutoff: new DateTime(2023, 1, 1), recentItemCap: 10);

            var stubPath = Assert.Single(stubPaths);
            Assert.True(File.Exists(stubPath));

            iCloudSyncRunner.CleanupStubs(stubPaths);

            Assert.False(File.Exists(stubPath));
        });
    }

    private static void WithTempLibrary(Action<string, LibraryRepository, LibraryQueryService> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-icloud-stub-test-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            test(tempDir.FullName, new LibraryRepository(database), new LibraryQueryService(database));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
