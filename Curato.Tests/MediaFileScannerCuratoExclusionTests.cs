using Curato.Media;

namespace Curato.Tests;

public class MediaFileScannerCuratoExclusionTests
{
    [Fact]
    public void Files_inside_dot_curato_are_never_scanned_as_library_content()
    {
        // Regression: .curato/ holds Curato's own data — the Recycle Bin's
        // trashed originals and the thumbnail cache both genuinely contain
        // real .jpg files on disk, which without this exclusion would get
        // indexed as new photos on every re-scan, polluting the library
        // with cache/trash artifacts.
        var tempDir = Directory.CreateTempSubdirectory("curato-exclusion-test-");
        try
        {
            File.WriteAllBytes(Path.Combine(tempDir.FullName, "real_photo.jpg"), [1, 2, 3]);

            var recycleBinDir = Path.Combine(tempDir.FullName, ".curato", "RecycleBin", "some-guid");
            Directory.CreateDirectory(recycleBinDir);
            File.WriteAllBytes(Path.Combine(recycleBinDir, "trashed_photo.jpg"), [4, 5, 6]);

            var cacheDir = Path.Combine(tempDir.FullName, ".curato", "thumbnail-cache");
            Directory.CreateDirectory(cacheDir);
            File.WriteAllBytes(Path.Combine(cacheDir, "abc123.jpg"), [7, 8, 9]);

            var items = new MediaFileScanner().Scan(tempDir.FullName).ToList();

            var item = Assert.Single(items);
            Assert.Equal("real_photo.jpg", item.RelativePath);
        }
        finally
        {
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
