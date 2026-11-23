namespace Curato.Core;

/// <summary>
/// PLAN.md §3, Step 0 — the structural rule that decides Adopt vs.
/// Auto-organize without asking anyone: an item sitting directly in the
/// library root (no subfolder) is "loose" and goes through gap-based
/// clustering; an item inside any existing subfolder belongs to that
/// folder's atomic, never-reclustered group.
///
/// <see cref="ImportFolderName"/> is the one deliberate exception: confirmed
/// live as a real need — copying in a folder of photos that already has its
/// own (irrelevant, pre-existing) subfolder structure got every one of those
/// subfolders Adopted as-is instead of auto-clustered, forcing a manual
/// flatten-to-root first to get the clustering actually wanted. Anything
/// found anywhere under it is treated as loose regardless of how deeply
/// nested it actually is, so it's auto-clustered by date like a flat drop at
/// the root would be — Organize (already CuratoManaged-only) then physically
/// relocates it out into the real folder structure, same as any other
/// auto-organized Event.
/// </summary>
public static class FolderGrouper
{
    /// <summary>Key for loose, root-level items — the only ones eligible for auto-clustering.</summary>
    public const string LooseItemsKey = "";

    /// <summary>
    /// A library-root folder a user can dump newly-copied content into
    /// (nested folders and all) to force auto-clustering instead of
    /// Adoption — see this type's own doc comment. Dot-prefixed to match
    /// <c>.curato</c>/<c>.Pre</c>'s existing convention for a folder with
    /// special meaning to Curato; unlike <c>.curato</c>, MediaFileScanner
    /// does not exclude it from scanning.
    /// </summary>
    public const string ImportFolderName = ".Import";

    public static ILookup<string, MediaItem> GroupByContainingFolder(IEnumerable<MediaItem> items)
        => items.ToLookup(item => GetContainingFolder(item.RelativePath));

    /// <summary>The folder one level up from <paramref name="folderPath"/>, or "" if it has none.</summary>
    public static string GetParentFolder(string folderPath)
    {
        var lastSlash = folderPath.LastIndexOf('/');
        return lastSlash < 0 ? string.Empty : folderPath[..lastSlash];
    }

    /// <summary>The last path segment of <paramref name="folderPath"/> — its own folder name.</summary>
    public static string GetLeafName(string folderPath)
    {
        var lastSlash = folderPath.LastIndexOf('/');
        return lastSlash < 0 ? folderPath : folderPath[(lastSlash + 1)..];
    }

    private static string GetContainingFolder(string relativePath)
    {
        var lastSlash = relativePath.LastIndexOf('/');
        if (lastSlash < 0)
        {
            return LooseItemsKey;
        }

        var containingFolder = relativePath[..lastSlash];
        return IsUnderImportFolder(containingFolder) ? LooseItemsKey : containingFolder;
    }

    private static bool IsUnderImportFolder(string folderPath)
        => folderPath.Equals(ImportFolderName, StringComparison.OrdinalIgnoreCase)
            || folderPath.StartsWith(ImportFolderName + "/", StringComparison.OrdinalIgnoreCase);
}
