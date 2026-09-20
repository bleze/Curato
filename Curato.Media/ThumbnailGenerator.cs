using Curato.Core;
using SkiaSharp;

namespace Curato.Media;

/// <summary>
/// Decodes + downscales a photo for grid/thumbnail and viewer display
/// (PLAN.md §11), applying its non-destructive Edit pipeline first (§10) so
/// crops/rotations — including ones imported from Picasa, §8 — show up
/// correctly without ever touching the source file.
///
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
    public static byte[] GenerateJpeg(string filePath, int maxDimension = 320, IReadOnlyList<Edit>? edits = null)
    {
        using var input = File.OpenRead(filePath);
        using var original = SKBitmap.Decode(input)
            ?? throw new InvalidOperationException($"Could not decode image: {filePath}");

        var edited = edits is { Count: > 0 } ? EditRenderer.Apply(original, edits) : original;
        try
        {
            // Never upscale — only shrink to fit within maxDimension x maxDimension,
            // matching a typical "fit" thumbnail behavior.
            var scale = Math.Min(1f, Math.Min((float)maxDimension / edited.Width, (float)maxDimension / edited.Height));

            SKBitmap? resized = null;
            try
            {
                var bitmapToEncode = edited;
                if (scale < 1f)
                {
                    var targetWidth = Math.Max(1, (int)Math.Round(edited.Width * scale));
                    var targetHeight = Math.Max(1, (int)Math.Round(edited.Height * scale));
                    resized = edited.Resize(new SKImageInfo(targetWidth, targetHeight), new SKSamplingOptions(SKFilterMode.Linear));
                    bitmapToEncode = resized ?? edited;
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
        finally
        {
            if (!ReferenceEquals(edited, original))
            {
                edited.Dispose();
            }
        }
    }
}
