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
/// HEIC/HEIF decoding (§1, Apple's format) goes via <see cref="HeicDecoder"/>
/// (libheif — plain Skia has no built-in HEIF codec); every other format
/// Skia can decode natively (JPEG/PNG/WebP/etc.) goes straight through it.
/// RAW preview extraction still isn't implemented.
/// </summary>
public static class ThumbnailGenerator
{
    public static byte[] GenerateJpeg(string filePath, int maxDimension = 320, IReadOnlyList<Edit>? edits = null)
    {
        using var original = DecodeSource(filePath, out var encodedOrigin);

        var edited = edits is { Count: > 0 } ? EditRenderer.Apply(original, edits) : original;
        try
        {
            // Last step: fixes the final visual orientation (§10's crop
            // coordinates assume the file's raw, as-stored dimensions, so
            // this can't run before Edit application without misaligning
            // them for a photo that has both a Picasa crop and a rotated
            // orientation tag).
            var oriented = ExifOrientationCorrector.Apply(edited, encodedOrigin);
            try
            {
                // Never upscale — only shrink to fit within maxDimension x maxDimension,
                // matching a typical "fit" thumbnail behavior.
                var scale = Math.Min(1f, Math.Min((float)maxDimension / oriented.Width, (float)maxDimension / oriented.Height));

                SKBitmap? resized = null;
                try
                {
                    var bitmapToEncode = oriented;
                    if (scale < 1f)
                    {
                        var targetWidth = Math.Max(1, (int)Math.Round(oriented.Width * scale));
                        var targetHeight = Math.Max(1, (int)Math.Round(oriented.Height * scale));
                        resized = oriented.Resize(new SKImageInfo(targetWidth, targetHeight), new SKSamplingOptions(SKFilterMode.Linear));
                        bitmapToEncode = resized ?? oriented;
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
                if (!ReferenceEquals(oriented, edited))
                {
                    oriented.Dispose();
                }
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

    private static SKBitmap DecodeSource(string filePath, out SKEncodedOrigin encodedOrigin)
    {
        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".heif", StringComparison.OrdinalIgnoreCase))
        {
            // TopLeft here means "no further correction needed", not "this
            // file has no rotation" — libheif's own decode already applies
            // the container's orientation transform (see HeicDecoder).
            encodedOrigin = SKEncodedOrigin.TopLeft;
            return HeicDecoder.Decode(filePath);
        }

        using var input = File.OpenRead(filePath);
        using var codec = SKCodec.Create(input)
            ?? throw new InvalidOperationException($"Could not decode image: {filePath}");
        encodedOrigin = codec.EncodedOrigin;
        return SKBitmap.Decode(codec)
            ?? throw new InvalidOperationException($"Could not decode image: {filePath}");
    }
}
