namespace Curato.Picasa;

/// <summary>One `[SectionName]` block from a `.picasa.ini`/`Picasa2Albums.ini` file, with its raw key=value pairs.</summary>
public sealed record PicasaIniSection(string Name, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Generic INI reader shared by `.picasa.ini` (per-folder, one section per photo
/// filename) and `Picasa2Albums.ini` (one section per album). Deliberately
/// dumb: it has no idea what a "photo" or "album" section means - that's
/// PicasaPhotoMetadataParser/PicasaAlbumsParser's job, kept separate so a
/// format quirk in one doesn't risk breaking the other.
/// </summary>
public static class PicasaIniParser
{
    public static IReadOnlyList<PicasaIniSection> Parse(string iniContent)
    {
        var sections = new List<PicasaIniSection>();
        string? currentName = null;
        Dictionary<string, string>? currentValues = null;

        foreach (var rawLine in iniContent.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 1)
            {
                if (currentName is not null)
                {
                    sections.Add(new PicasaIniSection(currentName, currentValues!));
                }

                currentName = line[1..^1];
                currentValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            if (currentValues is null)
            {
                continue; // stray content before any section header
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            currentValues[line[..separatorIndex].Trim()] = line[(separatorIndex + 1)..].Trim();
        }

        if (currentName is not null)
        {
            sections.Add(new PicasaIniSection(currentName, currentValues!));
        }

        return sections;
    }
}
