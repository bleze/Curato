namespace Curato.Core;

/// <summary>
/// Deletes a folder left with nothing in it once its owning Event/Trip row
/// is gone (RecycleBinService's delete/restore) or once every one of its
/// members has been physically relocated elsewhere (LibraryRepository's
/// MergeEventInto, once organized into an organized target). Never
/// recursive — a folder that still has something in it (even something
/// Curato doesn't track) is left alone rather than guessed at.
/// </summary>
public static class FolderCleanup
{
    public static void DeleteIfEmpty(string libraryRoot, string? relativeFolderPath)
    {
        if (relativeFolderPath is null)
        {
            return;
        }

        var fullPath = Path.Combine(libraryRoot, relativeFolderPath);
        if (Directory.Exists(fullPath) && Directory.GetFileSystemEntries(fullPath).Length == 0)
        {
            Directory.Delete(fullPath);
        }
    }
}
