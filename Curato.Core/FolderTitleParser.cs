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

    // "2019" — a bare 4-digit year, used only as a top-level date bucket
    // (e.g. sorting an entire legacy library into 2019/, 2020/, ... folders)
    // rather than a deliberately named trip or event.
    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex BareYearPattern();

    /// <summary>
    /// True for a folder name that's nothing but a 4-digit year — see
    /// <see cref="Curato.Organizer.AdoptedFolderPlanner.PlanTrips"/> for why
    /// this matters: such a folder is almost always just a top-level
    /// organizational bucket, not a real trip whose sibling subfolders are
    /// "days of that trip".
    /// </summary>
    public static bool IsBareYear(string folderName) => BareYearPattern().IsMatch(folderName.Trim());

    // ".Pre", ".Pre 1998", ".Pre-1998", ".pre" — the standard convention for
    // "everything scanned in from before real digital/metadata photos start"
    // (a leading dot so it sorts first on a real filesystem, same reasoning
    // IsBareYear's own folders get sorted into buckets for). No year is
    // parsed out of the name on purpose — LibraryQueryService.
    // GetEarliestReliableTakenAt derives that from the library's own data
    // instead, so the folder name doesn't need to encode or keep in sync
    // with anything. The negative lookahead blocks ".Preston"/".Prep"/
    // ".Present" from false-matching just because they start with "Pre".
    [GeneratedRegex(@"^\.Pre(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex PreDigitalScansPattern();

    /// <summary>
    /// True for a folder recognized as the standard "pre-digital scans"
    /// convention — see <see cref="Curato.Api.Controllers.LibraryController"/>'s
    /// EventDto.NeedsDateReview, which this powers.
    /// </summary>
    public static bool IsPreDigitalScansFolder(string folderName) => PreDigitalScansPattern().IsMatch(folderName.Trim());

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
