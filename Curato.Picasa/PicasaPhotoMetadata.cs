using System.Text.RegularExpressions;

namespace Curato.Picasa;

public sealed record PicasaFaceEntry(string RectHex, string ContactId);

public sealed record PicasaPhotoMetadata(
    bool Star,
    int? RotationDegrees,
    string? CropRectHex,
    IReadOnlyList<PicasaFaceEntry> Faces,
    IReadOnlyList<string> AlbumIds);

/// <summary>
/// Extracts the fields PLAN.md §8 cares about from one photo's section of a
/// `.picasa.ini` file. Deliberately does NOT parse `filters=` (brightness/
/// contrast/fill-light/etc. pipeline string) - that key's format is the least
/// documented part of the whole reverse-engineered spec, and guessing wrong
/// here risks fabricating an edit that was never actually applied. Rotation
/// and crop use the same `rect64`/`rotate(N)` primitives Picasa also uses for
/// face boxes, which are well enough attested to trust.
/// </summary>
public static partial class PicasaPhotoMetadataParser
{
    public static PicasaPhotoMetadata Parse(IReadOnlyDictionary<string, string> values)
    {
        var star = values.TryGetValue("star", out var starValue) &&
            string.Equals(starValue, "yes", StringComparison.OrdinalIgnoreCase);

        int? rotationDegrees = values.TryGetValue("rotate", out var rotateValue)
            ? TryParseRotate(rotateValue)
            : null;

        string? cropRectHex = values.TryGetValue("crop", out var cropValue)
            ? TryExtractRect64(cropValue)
            : null;

        var faces = new List<PicasaFaceEntry>();
        if (values.TryGetValue("faces", out var facesValue))
        {
            foreach (var entry in facesValue.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var commaIndex = entry.LastIndexOf(',');
                if (commaIndex <= 0)
                {
                    continue;
                }

                var rectHex = TryExtractRect64(entry[..commaIndex]);
                var contactId = entry[(commaIndex + 1)..].Trim();
                if (rectHex is not null && contactId.Length > 0)
                {
                    faces.Add(new PicasaFaceEntry(rectHex, contactId));
                }
            }
        }

        var albumIds = values.TryGetValue("albums", out var albumsValue)
            ? albumsValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        return new PicasaPhotoMetadata(star, rotationDegrees, cropRectHex, faces, albumIds);
    }

    private static int? TryParseRotate(string value)
    {
        var match = RotateRegex().Match(value);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var quarterTurns))
        {
            return null;
        }

        return (quarterTurns % 4) * 90;
    }

    private static string? TryExtractRect64(string value)
    {
        var match = Rect64Regex().Match(value);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"rotate\((\d+)\)")]
    private static partial Regex RotateRegex();

    [GeneratedRegex(@"rect64\(([0-9a-fA-F]{16})\)")]
    private static partial Regex Rect64Regex();
}
