using Curato.Core;
using Curato.Media;

namespace Curato.Tests;

public class ThumbnailCacheTests
{
    private static Edit Edit(long id) => new()
    {
        Id = id,
        MediaItemId = 1,
        OperationType = EditOperationType.Rotate,
        Parameters = """{"degrees":90}""",
        Source = RecordSource.Local
    };

    [Fact]
    public void Second_call_with_identical_inputs_returns_cached_bytes_without_regenerating()
    {
        WithTempLibraryAndFile((root, sourceFile) =>
        {
            var generateCount = 0;
            byte[] Generate()
            {
                generateCount++;
                return [1, 2, 3];
            }

            var first = ThumbnailCache.GetOrGenerate(root, "photo.jpg", sourceFile, 320, [], Generate);
            var second = ThumbnailCache.GetOrGenerate(root, "photo.jpg", sourceFile, 320, [], Generate);

            Assert.Equal(1, generateCount);
            Assert.Equal(first, second);
        });
    }

    [Fact]
    public void Different_requested_size_regenerates_rather_than_reusing_the_cache()
    {
        WithTempLibraryAndFile((root, sourceFile) =>
        {
            var generateCount = 0;
            byte[] Generate()
            {
                generateCount++;
                return [(byte)generateCount];
            }

            ThumbnailCache.GetOrGenerate(root, "photo.jpg", sourceFile, 320, [], Generate);
            ThumbnailCache.GetOrGenerate(root, "photo.jpg", sourceFile, 800, [], Generate);

            Assert.Equal(2, generateCount);
        });
    }

    [Fact]
    public void A_changed_edit_set_regenerates_rather_than_reusing_the_cache()
    {
        WithTempLibraryAndFile((root, sourceFile) =>
        {
            var generateCount = 0;
            byte[] Generate()
            {
                generateCount++;
                return [(byte)generateCount];
            }

            ThumbnailCache.GetOrGenerate(root, "photo.jpg", sourceFile, 320, [], Generate);
            ThumbnailCache.GetOrGenerate(root, "photo.jpg", sourceFile, 320, [Edit(1)], Generate);

            Assert.Equal(2, generateCount);
        });
    }

    [Fact]
    public void A_replaced_file_with_a_new_mtime_regenerates_rather_than_reusing_the_cache()
    {
        WithTempLibraryAndFile((root, sourceFile) =>
        {
            var generateCount = 0;
            byte[] Generate()
            {
                generateCount++;
                return [(byte)generateCount];
            }

            ThumbnailCache.GetOrGenerate(root, "photo.jpg", sourceFile, 320, [], Generate);

            File.SetLastWriteTimeUtc(sourceFile.FullName, DateTime.UtcNow.AddDays(1));
            var replacedInfo = new FileInfo(sourceFile.FullName);
            ThumbnailCache.GetOrGenerate(root, "photo.jpg", replacedInfo, 320, [], Generate);

            Assert.Equal(2, generateCount);
        });
    }

    private static void WithTempLibraryAndFile(Action<string, FileInfo> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-thumbcache-test-");
        try
        {
            var photoPath = Path.Combine(tempDir.FullName, "photo.jpg");
            File.WriteAllBytes(photoPath, [0xFF, 0xD8, 0xFF]); // not a real JPEG — never decoded, generate() is stubbed
            test(tempDir.FullName, new FileInfo(photoPath));
        }
        finally
        {
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
