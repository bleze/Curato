using Curato.Core;
using Curato.Media;

namespace Curato.Tests;

/// <summary>
/// MediaFileScanner.Scan's incremental skip logic (confirmed live as a real
/// speedup need for a 54,000+ file library) — a folder whose own modified-
/// time hasn't changed since the last full scan reuses each file's already-
/// known MediaItem instead of re-reading/re-hashing it from disk.
/// </summary>
public class MediaFileScannerIncrementalScanTests
{
    [Fact]
    public void Reuses_the_known_item_for_a_file_in_an_unchanged_folder()
    {
        WithTempLibrary(dir =>
        {
            var filePath = Path.Combine(dir, "a.jpg");
            File.WriteAllBytes(filePath, [1, 2, 3]);

            var scanner = new MediaFileScanner();
            var folderTimes = new Dictionary<string, DateTime>();
            var firstItem = scanner.Scan(dir, currentFolderModifiedTimes: folderTimes).Single();

            // Overwriting a file's content in place (same name, no rename)
            // doesn't change the *parent folder's* own modified-time on a
            // real filesystem — only the file's own. This is exactly the
            // documented gap (an externally content-edited file can be
            // missed) and exactly what proves the reuse path actually
            // fired rather than coincidentally matching: if the second
            // scan re-read this file for real, it would see these new
            // bytes and compute a different hash/size than the first pass did.
            File.WriteAllBytes(filePath, [9, 9, 9, 9, 9, 9, 9, 9, 9, 9]);

            var previouslyIndexed = new Dictionary<string, MediaItem> { [firstItem.RelativePath] = firstItem };
            var secondItem = scanner.Scan(dir, folderTimes, previouslyIndexed).Single();

            Assert.Same(firstItem, secondItem);
        });
    }

    [Fact]
    public void Rebuilds_everything_in_a_folder_whose_modified_time_changed()
    {
        WithTempLibrary(dir =>
        {
            var existingPath = Path.Combine(dir, "a.jpg");
            File.WriteAllBytes(existingPath, [1, 2, 3]);

            var scanner = new MediaFileScanner();
            var folderTimes = new Dictionary<string, DateTime>();
            var firstItem = scanner.Scan(dir, currentFolderModifiedTimes: folderTimes).Single();
            var previouslyIndexed = new Dictionary<string, MediaItem> { [firstItem.RelativePath] = firstItem };

            // Adding a new file changes the folder's own modified-time (a
            // directory's entry list itself changed), so this folder is no
            // longer "unchanged" against the time recorded above.
            Thread.Sleep(50); // ensure a measurably later modified-time on filesystems with coarse timestamp resolution
            File.WriteAllBytes(Path.Combine(dir, "b.jpg"), [4, 5, 6]);

            var results = scanner.Scan(dir, folderTimes, previouslyIndexed).ToList();

            Assert.Equal(2, results.Count);
            // The pre-existing file was rebuilt fresh too (not reused from
            // `previouslyIndexed`) since the whole folder, not just the new
            // file, is what changed — same object reference would mean it
            // was wrongly treated as still-unchanged.
            var rebuiltExisting = results.Single(i => i.RelativePath == "a.jpg");
            Assert.NotSame(firstItem, rebuiltExisting);
        });
    }

    [Fact]
    public void Never_skips_a_file_missing_from_the_previously_indexed_lookup()
    {
        WithTempLibrary(dir =>
        {
            File.WriteAllBytes(Path.Combine(dir, "a.jpg"), [1, 2, 3]);

            var scanner = new MediaFileScanner();
            var folderTimes = new Dictionary<string, DateTime>();
            scanner.Scan(dir, currentFolderModifiedTimes: folderTimes).Single();

            // The folder itself is reported unchanged, but nothing is
            // passed as "previously indexed" for it — a stale/corrupt
            // FolderScanState row shouldn't silently drop a real file.
            var results = scanner.Scan(dir, folderTimes, previouslyIndexedByRelativePath: null).ToList();

            Assert.Single(results);
            Assert.Equal("a.jpg", results[0].RelativePath);
        });
    }

    [Fact]
    public void Records_a_modified_time_for_every_folder_visited()
    {
        WithTempLibrary(dir =>
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllBytes(Path.Combine(dir, "a.jpg"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(dir, "sub", "b.jpg"), [4, 5, 6]);

            var folderTimes = new Dictionary<string, DateTime>();
            new MediaFileScanner().Scan(dir, currentFolderModifiedTimes: folderTimes).ToList();

            Assert.True(folderTimes.ContainsKey("")); // the root itself
            Assert.True(folderTimes.ContainsKey("sub"));
        });
    }

    private static void WithTempLibrary(Action<string> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-incremental-scan-test-");
        try
        {
            test(tempDir.FullName);
        }
        finally
        {
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
