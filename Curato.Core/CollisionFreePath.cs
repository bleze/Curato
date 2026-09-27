namespace Curato.Core;

/// <summary>
/// Picks a library-relative destination path for a file landing in
/// <paramref name="folderPath"/> that doesn't collide with anything already
/// there (on disk, or already claimed earlier in the same batch) — appends
/// a " (2)"-style suffix instead of overwriting. Shared by OrganizeService's
/// physical Organize pass and LibraryRepository's post-organize
/// MoveMediaItemToEvent (both physically relocate files the same way).
/// </summary>
public static class CollisionFreePath
{
    public static string Resolve(string libraryRoot, string folderPath, string fileName, ISet<string> claimedPaths)
    {
        var candidate = $"{folderPath}/{fileName}";
        if (!claimedPaths.Contains(candidate) && !File.Exists(Path.Combine(libraryRoot, candidate)))
        {
            return candidate;
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var counter = 1;
        string suffixed;
        do
        {
            suffixed = $"{folderPath}/{nameWithoutExtension} ({counter}){extension}";
            counter++;
        } while (claimedPaths.Contains(suffixed) || File.Exists(Path.Combine(libraryRoot, suffixed)));

        return suffixed;
    }
}
