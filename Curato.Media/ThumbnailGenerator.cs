using SkiaSharp;

namespace Curato.Media;

/// <summary>
/// Decodes + downscales a photo for grid/thumbnail display (PLAN.md §11).
/// SkiaSharp specifically: pure MIT license, no registration/key required
/// (unlike SixLabors.ImageSharp 4.x, which gates Release builds behind a
/// license-portal signup — not something this project wants to depend on
/// for basic thumbnailing), and works cross-platform including the Linux
/// Docker image via SkiaSharp.NativeAssets.Linux.NoDependencies.
///
/// RAW/HEIC preview extraction (§1) isn't implemented yet; this only handles
/// formats Skia can decode natively (JPEG/PNG/WebP/etc.) for now.
/// </summary>
public static class ThumbnailGenerator
{
    public static byte[] GenerateJpeg(string filePath, int maxDimension = 320)
    {
        using var input = File.OpenRead(filePath);
        using var original = SKBitmap.Decode(input)
            ?? throw new InvalidOperationException($"Could not decode image: {filePath}");

        // Never upscale — only shrink to fit within maxDimension x maxDimension,
        // matching a typical "fit" thumbnail behavior.
        var scale = Math.Min(1f, Math.Min((float)maxDimension / original.Width, (float)maxDimension / original.Height));

        SKBitmap? resized = null;
        try
        {
            var bitmapToEncode = original;
            if (scale < 1f)
            {
                var targetWidth = Math.Max(1, (int)Math.Round(original.Width * scale));
                var targetHeight = Math.Max(1, (int)Math.Round(original.Height * scale));
                resized = original.Resize(new SKImageInfo(targetWidth, targetHeight), new SKSamplingOptions(SKFilterMode.Linear));
                bitmapToEncode = resized ?? original;
            }

            using var image = SKImage.FromBitmap(bitmapToEncode);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
            return data.ToArray();
        }
        finally
        {
            resized?.Dispose();
        }
    }
}
