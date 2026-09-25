namespace Curato.Core;

/// <summary>
/// A user-supplied Event/Trip title (RenameService) eventually becomes a
/// real filesystem folder name (FolderNameTemplate) once Organize runs, so
/// it has to be valid everywhere Curato might deploy. Windows is the most
/// restrictive target (reserved characters, reserved device names, no
/// trailing dot/space) — validating against its rules guarantees safety on
/// Linux/Synology too, where only "/" and the null byte are actually
/// forbidden.
/// </summary>
public static class FolderNameValidator
{
    // Headroom for FolderNameTemplate's own date prefix/suffix within NTFS's 255-char component limit.
    private const int MaxLength = 200;

    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static bool IsValid(string title, out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            errorMessage = "Title cannot be empty.";
            return false;
        }

        if (title.Length > MaxLength)
        {
            errorMessage = $"Title is too long (max {MaxLength} characters).";
            return false;
        }

        if (title.IndexOfAny(InvalidChars) >= 0 || title.Any(c => c < 32))
        {
            errorMessage = "Title can't contain any of: < > : \" / \\ | ? *";
            return false;
        }

        if (title[^1] is '.' or ' ')
        {
            errorMessage = "Title can't end with a period or space.";
            return false;
        }

        var dotIndex = title.IndexOf('.');
        var nameWithoutExtension = dotIndex < 0 ? title : title[..dotIndex];
        if (ReservedNames.Contains(nameWithoutExtension))
        {
            errorMessage = $"\"{nameWithoutExtension}\" is a reserved name on Windows and can't be used.";
            return false;
        }

        errorMessage = null;
        return true;
    }
}
