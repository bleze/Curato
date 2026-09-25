using Curato.Core;
using Curato.Media;

namespace Curato.Tests;

/// <summary>
/// Exercises MediaFileScanner's EXIF/video-metadata date reading against
/// real, genuinely-tagged files rather than synthetic blank images (every
/// other test in this suite uses PIL-generated images with no real EXIF at
/// all, since they only need *some* MediaItem to exist — they never
/// actually verify this specific code path). The fixtures under TestAssets
/// all carry a DateTimeOriginal/creation_time of 2019-06-15 14:30:00,
/// embedded via piexif/pillow-heif (images) or a hand-built minimal `mvhd`
/// QuickTime atom (the .mov).
/// </summary>
public class MediaFileScannerDateReadingTests
{
    private static readonly DateTime ExpectedTakenAt = new(2019, 6, 15, 14, 30, 0);

    [Fact]
    public void Reads_exif_date_taken_from_a_real_jpeg()
    {
        WithScannedFixture("exif-2019-06-15.jpg", item =>
        {
            Assert.Equal(TakenAtSource.ExifDateTaken, item.TakenAtSource);
            Assert.Equal(ExpectedTakenAt, item.TakenAt);
        });
    }

    [Fact]
    public void Reads_exif_date_taken_from_a_real_heic()
    {
        WithScannedFixture("exif-2019-06-15.heic", item =>
        {
            Assert.Equal(TakenAtSource.ExifDateTaken, item.TakenAtSource);
            Assert.Equal(ExpectedTakenAt, item.TakenAt);
        });
    }

    [Fact]
    public void Reads_creation_time_from_a_real_quicktime_movie_header()
    {
        // The fixture's mvhd atom embeds 2019-06-15 14:30:00 UTC (QuickTime's
        // movie-header creation date is UTC by spec, unlike EXIF's naive
        // local DateTimeOriginal) — ReadVideoTakenAt converts it to local
        // wall-clock time so it's consistent with every other TakenAt value.
        // Computed here rather than hardcoded so the expectation stays
        // correct regardless of the machine's local timezone.
        var expectedLocal = new DateTime(2019, 6, 15, 14, 30, 0, DateTimeKind.Utc).ToLocalTime();

        WithScannedFixture("quicktime-2019-06-15.mov", item =>
        {
            Assert.Equal(TakenAtSource.VideoMetadata, item.TakenAtSource);
            Assert.Equal(expectedLocal, item.TakenAt);
        });
    }

    private static void WithScannedFixture(string fixtureFileName, Action<MediaItem> assert)
    {
        var sourcePath = Path.Combine(AppContext.BaseDirectory, "TestAssets", fixtureFileName);
        Assert.True(File.Exists(sourcePath), $"Missing test fixture: {sourcePath}");

        var tempDir = Directory.CreateTempSubdirectory("curato-datereading-test-");
        try
        {
            var copyPath = Path.Combine(tempDir.FullName, fixtureFileName);
            File.Copy(sourcePath, copyPath);

            var item = Assert.Single(new MediaFileScanner().Scan(tempDir.FullName));
            assert(item);
        }
        finally
        {
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
