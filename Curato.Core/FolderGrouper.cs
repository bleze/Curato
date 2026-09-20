namespace Curato.Core;

/// <summary>
/// PLAN.md §3, Step 0 — the structural rule that decides Adopt vs.
/// Auto-organize without asking anyone: an item sitting directly in the
/// library root (no subfolder) is "loose" and goes through gap-based
/// clustering; an item inside any existing subfolder belongs to that
/// folder's atomic, never-reclustered group.
/// </summary>
public static class FolderGrouper
{
    /// <summary>Key for loose, root-level items — the only ones eligible for auto-clustering.</summary>
    public const string LooseItemsKey = "";

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
        return lastSlash < 0 ? LooseItemsKey : relativePath[..lastSlash];
    }
}
