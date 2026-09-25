using Curato.Core;
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
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "TestAssets", "exif-2019-06-15.heic");

    [Fact]
    public void GenerateJpeg_decodes_a_real_heic_into_a_valid_downscaled_jpeg()
    {
        var jpegBytes = ThumbnailGenerator.GenerateJpeg(FixturePath, maxDimension: 200);

        Assert.NotEmpty(jpegBytes);

        using var decoded = SKBitmap.Decode(jpegBytes);
        Assert.NotNull(decoded);
        Assert.True(decoded.Width <= 200 && decoded.Height <= 200);
        Assert.True(decoded.Width > 0 && decoded.Height > 0);
    }

    /// <summary>
    /// Confirmed live a real ~12MP HEIC took 200-400ms to decode via
    /// libheif just to immediately downscale it to a 320px thumbnail —
    /// HeicDecoder now uses the file's own embedded preview image for a
    /// small enough request instead. This just confirms a small request
    /// still round-trips to a valid, correctly-bounded JPEG either way
    /// (whether this particular fixture happens to embed one or not);
    /// the crop test below is what actually protects the fast path's one
    /// real correctness risk.
    /// </summary>
    [Fact]
    public void A_small_thumbnail_request_still_produces_a_correctly_bounded_image()
    {
        var jpegBytes = ThumbnailGenerator.GenerateJpeg(FixturePath, maxDimension: 100);

        using var decoded = SKBitmap.Decode(jpegBytes);
        Assert.True(decoded.Width <= 100 && decoded.Height <= 100);
    }

    /// <summary>
    /// Regression: a Crop edit's rectangle (EditRenderer) is computed
    /// against the *original* file's full-resolution pixel coordinates.
    /// HeicDecoder's embedded-thumbnail fast path must never be used
    /// together with one, or the crop would apply to the wrong (much
    /// smaller) source image and cut out the wrong region entirely. A
    /// small maxDimension alone would normally be exactly the case that
    /// fast path targets — this confirms the presence of a Crop edit
    /// suppresses it regardless.
    /// </summary>
    [Fact]
    public void A_crop_edit_still_applies_against_full_resolution_even_for_a_small_thumbnail_request()
    {
        var fullResolution = ThumbnailGenerator.GenerateJpeg(FixturePath, maxDimension: int.MaxValue);
        using var fullBitmap = SKBitmap.Decode(fullResolution);
        var fullWidth = fullBitmap.Width;
        var fullHeight = fullBitmap.Height;

        // The right half of the full-resolution image — a region an
        // embedded thumbnail (typically a few hundred px) couldn't express
        // in these same coordinates without cropping the wrong area.
        var cropWidth = fullWidth / 2;
        var crop = new Edit
        {
            MediaItemId = 1,
            OperationType = EditOperationType.Crop,
            Parameters = $$"""{"X":{{fullWidth - cropWidth}},"Y":0,"Width":{{cropWidth}},"Height":{{fullHeight}}}""",
            PipelineOrder = 0,
            Source = RecordSource.Local,
        };

        var jpegBytes = ThumbnailGenerator.GenerateJpeg(FixturePath, maxDimension: 100, edits: [crop]);

        using var decoded = SKBitmap.Decode(jpegBytes);
        // A roughly half-width, full-height source should downscale to a
        // roughly half-width, full-height thumbnail — not the near-square
        // (or otherwise differently-proportioned) shape a wrong-resolution
        // crop source would produce.
        var aspectRatio = (double)decoded.Width / decoded.Height;
        var expectedAspectRatio = (double)cropWidth / fullHeight;
        Assert.Equal(expectedAspectRatio, aspectRatio, precision: 1);
    }
}
