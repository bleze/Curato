namespace Curato.Organizer;

/// <summary>Deletes a folder left with nothing in it once its owning Event/Trip row is gone.</summary>
internal static class FolderCleanup
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
