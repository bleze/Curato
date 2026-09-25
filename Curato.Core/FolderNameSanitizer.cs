namespace Curato.Core;

/// <summary>
/// A title lives freely in the database — arbitrary characters, no
/// filesystem restrictions at all — but FolderNameTemplate has to turn it
/// into a real folder segment whenever Organize (or renaming an already-
/// Organized item) actually touches disk. Rather than rejecting a title the
/// database itself has no problem with, this silently converts whatever
/// isn't safe on Windows (the most restrictive deployment target — see
/// PLAN.md §1) into something that is, so a title with a ":" or "?" in it,
/// say, just gets a slightly different (still recognizable) folder name
/// instead of being an error the user has to work around.
/// </summary>
public static class FolderNameSanitizer
{
    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string title)
    {
        var chars = title.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(InvalidChars, chars[i]) >= 0 || chars[i] < 32)
            {
                chars[i] = '-';
            }
        }

        var sanitized = new string(chars).Trim().TrimEnd('.', ' ');
        if (sanitized.Length == 0)
        {
            sanitized = "Untitled";
        }

        // A reserved device name is only a problem as the *whole* segment
        // (Windows treats "CON.txt" as the reserved name too, but a longer
        // title that merely starts with "CON" is fine) — suffixing it is
        // enough to dodge the restriction without mangling anything else.
        if (ReservedNames.Contains(sanitized))
        {
            sanitized += "_";
        }

        return sanitized;
    }
}
