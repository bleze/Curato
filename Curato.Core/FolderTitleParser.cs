using System.Text.RegularExpressions;

namespace Curato.Core;

/// <summary>
/// PLAN.md §3, Step 0 — best-effort title (and date, unused for now but kept
/// for future display) extraction from an existing folder name, purely for
/// nicer display. Never touches the folder itself, and falls back to the
/// literal folder name when nothing matches.
/// </summary>
public static partial class FolderTitleParser
{
    // "2026-07-11 Title", "2026-07-11_Title", "2026-07-11-Title"
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2})[ _-]+(.+)$")]
    private static partial Regex DatePrefixPattern();

    // "Title (2026-07-11)"
    [GeneratedRegex(@"^(.+?)\s*\((\d{4}-\d{2}-\d{2})\)$")]
    private static partial Regex DateSuffixPattern();

    public static (DateOnly? Date, string Title) Parse(string folderName)
    {
        var prefixMatch = DatePrefixPattern().Match(folderName);
        if (prefixMatch.Success && DateOnly.TryParse(prefixMatch.Groups[1].Value, out var prefixDate))
        {
            return (prefixDate, prefixMatch.Groups[2].Value.Trim());
        }

        var suffixMatch = DateSuffixPattern().Match(folderName);
        if (suffixMatch.Success && DateOnly.TryParse(suffixMatch.Groups[2].Value, out var suffixDate))
        {
            return (suffixDate, suffixMatch.Groups[1].Value.Trim());
        }

        return (null, folderName);
    }
}
