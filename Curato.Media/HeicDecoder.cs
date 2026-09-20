using System.Runtime.InteropServices;
using LibHeifSharp;
using SkiaSharp;

namespace Curato.Media;

/// <summary>
/// Decodes HEIC/HEIF files via libheif (LibHeifSharp) — plain SkiaSharp has
/// no built-in HEIF codec at all, confirmed live: every synced iPhone photo
/// showed "No preview" until this existed. Native libheif binaries come
/// from the LibHeif.Native.Runtime package (Curato.Media.csproj) rather
/// than requiring a separately-installed system library.
///
/// Deliberately does NOT apply any EXIF orientation correction afterward —
/// unlike JPEG, the HEIF spec treats the EXIF orientation tag as purely
/// informational; libheif's own decode already returns pixels in the
/// correct display orientation using the container's own transform (every
/// libheif-sharp sample that reads EXIF from a HEIC file explicitly
/// discards the Orientation tag for this exact reason, rather than
/// applying it). Applying <see cref="ExifOrientationCorrector"/> on top of
/// an already-correctly-oriented HEIC decode would rotate/mirror it twice.
/// </summary>
internal static class HeicDecoder
{
    public static SKBitmap Decode(string filePath)
    {
        using var context = new HeifContext(filePath);
        using var handle = context.GetPrimaryImageHandle();
        // Always decode with an alpha channel, even for images that don't
        // have one — keeps the pixel layout uniformly 4-bytes-per-pixel,
        // matching SkiaSharp's Rgba8888 exactly regardless of source alpha.
        using var image = handle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgba32);
        var plane = image.GetPlane(HeifChannel.Interleaved);

        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);

        // Copy row-by-row rather than a single flat copy: libheif's stride
        // can be padded wider than width*4 for alignment, and isn't
        // guaranteed to match SkiaSharp's own RowBytes.
        var rowBytes = Math.Min(plane.Stride, bitmap.RowBytes);
        var rowBuffer = new byte[rowBytes];
        var destination = bitmap.GetPixels();
        for (var y = 0; y < image.Height; y++)
        {
            Marshal.Copy(plane.Scan0 + y * plane.Stride, rowBuffer, 0, rowBytes);
            Marshal.Copy(rowBuffer, 0, destination + y * bitmap.RowBytes, rowBytes);
        }

        return bitmap;
    }
}
