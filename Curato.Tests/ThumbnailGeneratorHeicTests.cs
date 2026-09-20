using Curato.Media;
using SkiaSharp;

namespace Curato.Tests;

/// <summary>
/// Confirmed live that HEIC preview generation was never implemented at
/// all (plain SkiaSharp has no built-in HEIF codec) — every synced iPhone
/// photo showed "No preview" until HeicDecoder (libheif via LibHeifSharp)
/// existed. This exercises it against a real HEIC fixture (same one
/// MediaFileScannerDateReadingTests uses for EXIF date reading) rather
/// than just trusting the library calls compile.
/// </summary>
public class ThumbnailGeneratorHeicTests
{
    [Fact]
    public void GenerateJpeg_decodes_a_real_heic_into_a_valid_downscaled_jpeg()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "TestAssets", "exif-2019-06-15.heic");

        var jpegBytes = ThumbnailGenerator.GenerateJpeg(fixturePath, maxDimension: 200);

        Assert.NotEmpty(jpegBytes);

        using var decoded = SKBitmap.Decode(jpegBytes);
        Assert.NotNull(decoded);
        Assert.True(decoded.Width <= 200 && decoded.Height <= 200);
        Assert.True(decoded.Width > 0 && decoded.Height > 0);
    }
}
