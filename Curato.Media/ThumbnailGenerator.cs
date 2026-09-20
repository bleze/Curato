using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Curato.Media;

/// <summary>
/// Decodes + downscales a photo for grid/thumbnail display (PLAN.md §11).
/// ImageSharp is used specifically because it's pure-.NET and cross-platform
/// (works in the Linux Docker image, not just on Windows) — unlike
/// System.Drawing.
///
/// RAW/HEIC preview extraction (§1) isn't implemented yet; this only handles
/// formats ImageSharp can decode natively (JPEG/PNG/etc.) for now.
/// </summary>
public static class ThumbnailGenerator
{
    public static byte[] GenerateJpeg(string filePath, int maxDimension = 320)
    {
        using var image = Image.Load(filePath);
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new Size(maxDimension, maxDimension)
        }));

        using var output = new MemoryStream();
        image.Save(output, new JpegEncoder { Quality = 80 });
        return output.ToArray();
    }
}
