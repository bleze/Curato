using Curato.Core;
using Curato.Data;

namespace Curato.Tests;

/// <summary>
/// Backs the "Fix dates…" dialog's default prefill for a
/// FolderTitleParser.IsBeforeDigitalScansFolder Event — "definitely before real
/// digital photos start", derived from the library's own data rather than
/// requiring a year in the folder name.
/// </summary>
public class GetEarliestReliableTakenAtTests
{
    [Fact]
    public void Returns_the_earliest_takenat_among_reliable_sources_only()
    {
        WithTempLibrary((repository, query) =>
        {
            repository.UpsertMediaItems([
                new MediaItem
                {
                    RelativePath = "filesystem-only.jpg", QuickHash = "a",
                    TakenAt = new DateTime(1990, 1, 1), TakenAtSource = TakenAtSource.FileSystem,
                },
                new MediaItem
                {
                    RelativePath = "manual.jpg", QuickHash = "b",
                    TakenAt = new DateTime(1991, 1, 1), TakenAtSource = TakenAtSource.Manual,
                },
                new MediaItem
                {
                    RelativePath = "real-exif.jpg", QuickHash = "c",
                    TakenAt = new DateTime(2003, 5, 1), TakenAtSource = TakenAtSource.ExifDateTaken,
                },
                new MediaItem
                {
                    RelativePath = "real-video.mov", QuickHash = "d", Kind = MediaKind.Video,
                    TakenAt = new DateTime(2001, 6, 1), TakenAtSource = TakenAtSource.VideoMetadata,
                },
            ]);

            // Earlier FileSystem/Manual-sourced dates (1990, 1991) are
            // deliberately ignored — only 2003 and 2001 are "reliable", so
            // the earliest of those (2001) wins.
            Assert.Equal(new DateTime(2001, 6, 1), query.GetEarliestReliableTakenAt());
        });
    }

    [Fact]
    public void Returns_null_when_no_reliable_dated_item_exists()
    {
        WithTempLibrary((repository, query) =>
        {
            repository.UpsertMediaItems([
                new MediaItem
                {
                    RelativePath = "a.jpg", QuickHash = "a",
                    TakenAt = new DateTime(1990, 1, 1), TakenAtSource = TakenAtSource.FileSystem,
                },
            ]);

            Assert.Null(query.GetEarliestReliableTakenAt());
        });
    }

    [Fact]
    public void Returns_null_on_an_empty_library()
    {
        WithTempLibrary((_, query) => Assert.Null(query.GetEarliestReliableTakenAt()));
    }

    private static void WithTempLibrary(Action<LibraryRepository, LibraryQueryService> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-earliest-reliable-takenat-test-");
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
