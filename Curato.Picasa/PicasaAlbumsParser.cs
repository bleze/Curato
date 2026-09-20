namespace Curato.Picasa;

/// <summary>
/// Parses a `Picasa2Albums.ini` (one section per cross-folder album, keyed by
/// album id) into an albumId -&gt; name map. Per-photo membership in these
/// albums is recorded separately, via the `albums=` key on that photo's own
/// `.picasa.ini` section (see <see cref="PicasaPhotoMetadata.AlbumIds"/>).
/// </summary>
public static class PicasaAlbumsParser
{
    public static IReadOnlyDictionary<string, string> Parse(string iniContent)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in PicasaIniParser.Parse(iniContent))
        {
            if (section.Values.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name))
            {
                result[section.Name] = name;
            }
        }

        return result;
    }
}
