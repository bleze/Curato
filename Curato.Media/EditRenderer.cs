using System.Text.Json;
using Curato.Core;
using SkiaSharp;

namespace Curato.Media;

/// <summary>
/// PLAN.md §10 — applies a MediaItem's non-destructive Edit pipeline at
/// render time. The original file is only ever read, never written; this
/// always hands back a possibly-new in-memory bitmap for the caller to
/// encode and dispose.
///
/// Only Crop and Rotate exist yet (from Picasa import, §8 — native editing
/// UI is later Phase 3 scope). Crop is always applied before Rotate here,
/// regardless of Edit.PipelineOrder: PLAN.md §8's imported crop rectangles
/// were computed against the *original*, unrotated file's raw pixel
/// dimensions (that's what ImageDimensionReader reads), so cropping first
/// keeps those coordinates self-consistent. PipelineOrder still orders
/// multiple edits of the same kind against each other; it was never meant
/// to order Crop against Rotate.
/// </summary>
public static class EditRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static SKBitmap Apply(SKBitmap source, IReadOnlyList<Edit> edits)
    {
        var current = source;
        var ownsCurrent = false;

        var crop = edits.FirstOrDefault(e => e.OperationType == EditOperationType.Crop);
        if (crop is not null && TryParseCrop(crop.Parameters, out var cropRect))
        {
            var subset = SKRectI.Create(cropRect.X, cropRect.Y, cropRect.Width, cropRect.Height);
            var cropped = new SKBitmap();
            if (cropRect.Width > 0 && cropRect.Height > 0 && current.ExtractSubset(cropped, subset))
            {
                if (ownsCurrent)
                {
                    current.Dispose();
                }

                current = cropped;
                ownsCurrent = true;
            }
            else
            {
                cropped.Dispose(); // out-of-bounds/degenerate rect — skip rather than produce a broken image
            }
        }

        var rotate = edits.FirstOrDefault(e => e.OperationType == EditOperationType.Rotate);
        if (rotate is not null && TryParseRotateDegrees(rotate.Parameters, out var degrees) && degrees != 0)
        {
            var rotated = RotateBitmap(current, degrees);
            if (ownsCurrent)
            {
                current.Dispose();
            }

            current = rotated;
        }

        return current;
    }

    private static SKBitmap RotateBitmap(SKBitmap source, int degrees)
    {
        var normalized = ((degrees % 360) + 360) % 360;
        var swapDimensions = normalized == 90 || normalized == 270;
        var width = swapDimensions ? source.Height : source.Width;
        var height = swapDimensions ? source.Width : source.Height;

        var rotated = new SKBitmap(width, height);
        using var canvas = new SKCanvas(rotated);
        canvas.Clear(SKColors.Black);
        canvas.Translate(width / 2f, height / 2f);
        canvas.RotateDegrees(normalized); // clockwise — Picasa's own rotate(N) direction is undocumented; treated as N quarter-turns clockwise
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint: null);
        return rotated;
    }

    private static bool TryParseCrop(string json, out PixelRect rect)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<CropParameters>(json, JsonOptions);
            if (parsed is null)
            {
                rect = default;
                return false;
            }

            rect = new PixelRect(parsed.X, parsed.Y, parsed.Width, parsed.Height);
            return true;
        }
        catch (JsonException)
        {
            rect = default;
            return false;
        }
    }

    private static bool TryParseRotateDegrees(string json, out int degrees)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<RotateParameters>(json, JsonOptions);
            degrees = parsed?.Degrees ?? 0;
            return parsed is not null;
        }
        catch (JsonException)
        {
            degrees = 0;
            return false;
        }
    }

    private sealed record CropParameters(int X, int Y, int Width, int Height);
    private sealed record RotateParameters(int Degrees);
}
