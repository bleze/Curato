using System.Runtime.InteropServices;
using LibHeifSharp;
using SkiaSharp;

namespace Curato.Media;

/// <summary>
/// Decodes HEIC/HEIF files via libheif (LibHeifSharp) — plain SkiaSharp has
/// no built-in HEIF codec at all, confirmed live: every synced iPhone photo
/// showed "No preview" until this existed. On Windows the native libheif
/// comes from the LibHeif.Native.Runtime package (Curato.Media.csproj); on
/// Linux it's the system package (see the static constructor).
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
    /// <summary>
    /// On Linux, load the distro's own libheif (apt: libheif1 +
    /// libheif-plugin-libde265, installed by the Dockerfile and CI) instead of
    /// LibHeif.Native.Runtime's bundled libheif.so: that one is built against
    /// an older distro and needs libdav1d.so.5, which Ubuntu 24.04 (the .NET 10
    /// runtime image and the CI runner) doesn't have — it fails to load with
    /// DllNotFoundException. Windows keeps using the bundled DLL. Falls back to
    /// the default lookup when no system libheif is installed.
    /// </summary>
    static HeicDecoder()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(HeifContext).Assembly, (name, _, _) =>
                name == "libheif" && NativeLibrary.TryLoad("libheif.so.1", out var handle) ? handle : IntPtr.Zero);
        }
        catch (InvalidOperationException)
        {
            // A resolver is already registered for LibHeifSharp; keep that one.
        }
    }

    /// <summary>
    /// Decodes a HEIC/HEIF file. <paramref name="maxDimension"/> and
    /// <paramref name="allowEmbeddedThumbnail"/> let a caller that only
    /// needs a small render (a grid thumbnail) opt into using the file's
    /// own embedded preview image instead of the full-resolution primary
    /// one — confirmed live this matters a lot: decoding a real ~12MP
    /// iPhone HEIC via libheif took 200-400ms per file, all just to
    /// immediately downscale it to 320px. iPhone photos typically embed a
    /// much smaller thumbnail image directly in the container specifically
    /// for this kind of use, which decodes in a fraction of the time.
    /// Deliberately opt-in (not automatic) rather than always preferring
    /// it: a Crop edit's rectangle is computed against the *original* file's
    /// full-resolution pixel coordinates (EditRenderer), so applying one to
    /// the smaller embedded thumbnail would crop the wrong region entirely
    /// — the caller (ThumbnailGenerator) only passes true when there's no
    /// Crop edit to apply, and the full-resolution viewer render never
    /// passes it at all.
    /// </summary>
    public static SKBitmap Decode(string filePath, int maxDimension = int.MaxValue, bool allowEmbeddedThumbnail = false)
    {
        using var context = new HeifContext(filePath);
        using var primaryHandle = context.GetPrimaryImageHandle();

        if (allowEmbeddedThumbnail)
        {
            var thumbnailIds = primaryHandle.GetThumbnailImageIds();
            if (thumbnailIds.Count > 0)
            {
                using var thumbnailHandle = primaryHandle.GetThumbnailImage(thumbnailIds[0]);
                // Only worth it if the embedded preview is actually big
                // enough for what was asked — using a tiny embedded image
                // for a larger request would just look visibly soft.
                if (Math.Max(thumbnailHandle.Width, thumbnailHandle.Height) >= maxDimension)
                {
                    return DecodeHandle(thumbnailHandle);
                }
            }
        }

        return DecodeHandle(primaryHandle);
    }

    private static SKBitmap DecodeHandle(HeifImageHandle handle)
    {
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
