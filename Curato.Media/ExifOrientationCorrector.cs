using SkiaSharp;

namespace Curato.Media;

/// <summary>
/// Applies a decoded bitmap's EXIF orientation tag so the pixels come out
/// the way the photo actually looked when taken. SkiaSharp's plain
/// <c>SKBitmap.Decode(Stream)</c> deliberately ignores this tag — it hands
/// back pixels exactly as stored, which for the very common case of a phone
/// held in portrait (camera sensor data stored "sideways" plus an
/// orientation tag saying how to display it) comes out rotated 90°.
///
/// Applied as the last step in the render pipeline, after crop/rotate Edits
/// (§10): Picasa-imported crop rectangles were computed against the file's
/// raw, as-stored dimensions (see PicasaImportService/ImageDimensionReader),
/// so correcting orientation *first* would misalign those coordinates for
/// any photo that has both a Picasa crop and a 90/270° orientation tag.
/// Correcting last keeps that existing math intact and only needs to get
/// the final displayed image right.
/// </summary>
public static class ExifOrientationCorrector
{
    public static SKBitmap Apply(SKBitmap source, SKEncodedOrigin origin)
    {
        var (mirror, rotationDegrees) = Decompose(origin);
        if (!mirror && rotationDegrees == 0)
        {
            return source;
        }

        var swapDimensions = rotationDegrees is 90 or 270;
        var width = swapDimensions ? source.Height : source.Width;
        var height = swapDimensions ? source.Width : source.Height;

        var result = new SKBitmap(width, height);
        using var canvas = new SKCanvas(result);
        canvas.Translate(width / 2f, height / 2f);
        canvas.RotateDegrees(rotationDegrees);
        if (mirror)
        {
            canvas.Scale(-1, 1);
        }

        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint: null);
        return result;
    }

    /// <summary>
    /// Every EXIF orientation (1-8) decomposes into "mirror horizontally in
    /// the image's own frame, then rotate clockwise" — the standard way of
    /// describing all eight without a separate case for each. Orientations
    /// 5 and 7 (mirror + a 90°-family rotation) are vanishingly rare in real
    /// camera/phone output — practically every real photo needing
    /// correction is 3, 6, or 8 — but are included for completeness.
    /// </summary>
    private static (bool Mirror, int RotationDegrees) Decompose(SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.TopLeft => (false, 0),
        SKEncodedOrigin.TopRight => (true, 0),
        SKEncodedOrigin.BottomRight => (false, 180),
        SKEncodedOrigin.BottomLeft => (true, 180),
        SKEncodedOrigin.RightTop => (false, 90),
        SKEncodedOrigin.RightBottom => (true, 90),
        SKEncodedOrigin.LeftBottom => (false, 270),
        SKEncodedOrigin.LeftTop => (true, 270),
        _ => (false, 0)
    };
}
