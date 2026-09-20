using Curato.Media;
using SkiaSharp;

namespace Curato.Tests;

public class ExifOrientationCorrectorTests
{
    // A 40x20 source, top-left quadrant red, everything else blue — lets a
    // test tell "which corner did the marked quadrant end up in" apart for
    // every orientation, including the mirror cases (which a purely
    // rotation-based test bitmap can't distinguish).
    private static SKBitmap CreateMarkedBitmap()
    {
        var bitmap = new SKBitmap(40, 20);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Blue);
        using var paint = new SKPaint { Color = SKColors.Red };
        canvas.DrawRect(new SKRect(0, 0, 20, 10), paint);
        return bitmap;
    }

    [Fact]
    public void TopLeft_orientation_1_is_a_no_op()
    {
        using var source = CreateMarkedBitmap();
        using var result = ExifOrientationCorrector.Apply(source, SKEncodedOrigin.TopLeft);

        Assert.Same(source, result);
    }

    [Fact]
    public void TopRight_orientation_2_mirrors_horizontally()
    {
        using var source = CreateMarkedBitmap();
        using var result = ExifOrientationCorrector.Apply(source, SKEncodedOrigin.TopRight);

        Assert.Equal(40, result.Width);
        Assert.Equal(20, result.Height);
        Assert.Equal(SKColors.Red, result.GetPixel(30, 5)); // was top-left, now top-right
        Assert.Equal(SKColors.Blue, result.GetPixel(10, 5));
    }

    [Fact]
    public void BottomRight_orientation_3_rotates_180()
    {
        using var source = CreateMarkedBitmap();
        using var result = ExifOrientationCorrector.Apply(source, SKEncodedOrigin.BottomRight);

        Assert.Equal(40, result.Width);
        Assert.Equal(20, result.Height);
        Assert.Equal(SKColors.Red, result.GetPixel(30, 15)); // was top-left, now bottom-right
        Assert.Equal(SKColors.Blue, result.GetPixel(10, 5));
    }

    [Fact]
    public void BottomLeft_orientation_4_mirrors_vertically()
    {
        using var source = CreateMarkedBitmap();
        using var result = ExifOrientationCorrector.Apply(source, SKEncodedOrigin.BottomLeft);

        Assert.Equal(40, result.Width);
        Assert.Equal(20, result.Height);
        Assert.Equal(SKColors.Red, result.GetPixel(10, 15)); // was top-left, now bottom-left
        Assert.Equal(SKColors.Blue, result.GetPixel(30, 5));
    }

    [Fact]
    public void RightTop_orientation_6_rotates_90_clockwise()
    {
        using var source = CreateMarkedBitmap();
        using var result = ExifOrientationCorrector.Apply(source, SKEncodedOrigin.RightTop);

        Assert.Equal(20, result.Width); // dimensions swap for a 90-degree turn
        Assert.Equal(40, result.Height);
        Assert.Equal(SKColors.Red, result.GetPixel(15, 5)); // top-left rotates toward top-right
        Assert.Equal(SKColors.Blue, result.GetPixel(5, 5));
    }

    [Fact]
    public void LeftBottom_orientation_8_rotates_270_clockwise()
    {
        using var source = CreateMarkedBitmap();
        using var result = ExifOrientationCorrector.Apply(source, SKEncodedOrigin.LeftBottom);

        Assert.Equal(20, result.Width);
        Assert.Equal(40, result.Height);
        Assert.Equal(SKColors.Red, result.GetPixel(5, 35)); // top-left rotates toward bottom-left
        Assert.Equal(SKColors.Blue, result.GetPixel(15, 5));
    }

    [Fact]
    public void RightBottom_orientation_7_mirrors_and_rotates_90_clockwise()
    {
        using var source = CreateMarkedBitmap();
        using var result = ExifOrientationCorrector.Apply(source, SKEncodedOrigin.RightBottom);

        Assert.Equal(20, result.Width);
        Assert.Equal(40, result.Height);
        Assert.Equal(SKColors.Red, result.GetPixel(15, 35)); // mirror then rotate lands it bottom-right
        Assert.Equal(SKColors.Blue, result.GetPixel(5, 5));
    }

    [Fact]
    public void LeftTop_orientation_5_mirrors_and_rotates_270_clockwise()
    {
        using var source = CreateMarkedBitmap();
        using var result = ExifOrientationCorrector.Apply(source, SKEncodedOrigin.LeftTop);

        Assert.Equal(20, result.Width);
        Assert.Equal(40, result.Height);
        Assert.Equal(SKColors.Red, result.GetPixel(5, 5)); // mirror then rotate leaves it top-left
        Assert.Equal(SKColors.Blue, result.GetPixel(15, 5));
    }
}
