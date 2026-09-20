using Curato.Core;
using Curato.Media;
using SkiaSharp;

namespace Curato.Tests;

public class EditRendererTests
{
    [Fact]
    public void Crop_extracts_the_requested_pixel_region()
    {
        using var source = new SKBitmap(10, 10);
        FillRegion(source, 0, 0, 10, 10, SKColors.Blue);
        FillRegion(source, 2, 2, 3, 3, SKColors.Red); // a red patch at (2,2)-(5,5)

        var crop = new Edit { MediaItemId = 1, OperationType = EditOperationType.Crop, Parameters = """{"x":2,"y":2,"width":3,"height":3}""" };

        using var result = EditRenderer.Apply(source, [crop]);

        Assert.Equal(3, result.Width);
        Assert.Equal(3, result.Height);
        Assert.Equal(SKColors.Red, result.GetPixel(1, 1)); // interior of the cropped patch
    }

    [Fact]
    public void Rotate_90_degrees_swaps_dimensions_and_moves_the_top_left_toward_the_top_right()
    {
        using var source = new SKBitmap(40, 20);
        FillRegion(source, 0, 0, 40, 20, SKColors.Blue);
        FillRegion(source, 0, 0, 20, 10, SKColors.Red); // top-left quadrant

        var rotate = new Edit { MediaItemId = 1, OperationType = EditOperationType.Rotate, Parameters = """{"degrees":90}""" };

        using var result = EditRenderer.Apply(source, [rotate]);

        Assert.Equal(20, result.Width); // dimensions swap for a 90-degree turn
        Assert.Equal(40, result.Height);
        Assert.Equal(SKColors.Red, result.GetPixel(15, 5)); // former top-left quadrant, now near the top-right
        Assert.Equal(SKColors.Blue, result.GetPixel(5, 5)); // former top-right quadrant, now near the top-left
    }

    [Fact]
    public void Rotate_zero_degrees_is_a_no_op()
    {
        using var source = new SKBitmap(10, 10);
        FillRegion(source, 0, 0, 10, 10, SKColors.Green);

        var rotate = new Edit { MediaItemId = 1, OperationType = EditOperationType.Rotate, Parameters = """{"degrees":0}""" };

        using var result = EditRenderer.Apply(source, [rotate]);

        Assert.Equal(10, result.Width);
        Assert.Equal(10, result.Height);
        Assert.Equal(SKColors.Green, result.GetPixel(5, 5));
    }

    [Fact]
    public void Crop_is_applied_before_rotate_using_original_image_coordinates()
    {
        // A 40x20 source: right half red, left half blue. Crop keeps only the
        // right (red) half using ORIGINAL coordinates, then rotates 90.
        using var source = new SKBitmap(40, 20);
        FillRegion(source, 0, 0, 40, 20, SKColors.Blue);
        FillRegion(source, 20, 0, 20, 20, SKColors.Red);

        var crop = new Edit { MediaItemId = 1, OperationType = EditOperationType.Crop, Parameters = """{"x":20,"y":0,"width":20,"height":20}""" };
        var rotate = new Edit { MediaItemId = 1, OperationType = EditOperationType.Rotate, Parameters = """{"degrees":90}""" };

        using var result = EditRenderer.Apply(source, [rotate, crop]); // order in the list shouldn't matter

        Assert.Equal(20, result.Width); // cropped to 20x20, then a 90-degree turn keeps it square
        Assert.Equal(20, result.Height);
        Assert.Equal(SKColors.Red, result.GetPixel(10, 10)); // wholly red after crop — rotation can't reintroduce blue
    }

    private static void FillRegion(SKBitmap bitmap, int x, int y, int width, int height, SKColor color)
    {
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = color };
        canvas.DrawRect(new SKRect(x, y, x + width, y + height), paint);
    }
}
