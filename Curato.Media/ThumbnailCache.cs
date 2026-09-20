using System.IO.Hashing;
using System.Text;
using Curato.Core;

namespace Curato.Media;

/// <summary>
/// PLAN.md §11 — a disk cache for generated thumbnails/viewer images, so
/// repeated requests for the same rendered output (a page reload, scrolling
/// back up, a different client asking) don't pay SkiaSharp's decode-resize-
/// encode cost again — the render pipeline now also does EXIF orientation
/// correction and Edit application (§10) on every request, so this is more
/// than just a resize.
///
/// Deliberately NOT keyed by the expensive, lazily-computed ContentHash
/// (§7) — that would force a full-file hash on every first thumbnail
/// request, defeating the whole "cheap" point of QuickHash existing at all.
/// Keyed instead by a cheap composite that invalidates on everything that
/// actually changes rendered output: the source file's own size+mtime
/// (content replaced), the requested pixel size, and which Edit rows apply
/// (an Edit is only ever inserted, never mutated in place, so the
/// count+max-id pair is a safe, cheap stand-in for "did the edit set
/// change" without hashing every Edit's Parameters).
///
/// No eviction policy yet: a replaced file's old cache entry just becomes
/// orphaned dead weight rather than being cleaned up. Fine at a personal
/// library's scale (each entry is a few tens of KB); worth revisiting if
/// disk usage on a NAS deployment ever becomes a real complaint.
/// </summary>
public static class ThumbnailCache
{
    private const string CacheFolderName = "thumbnail-cache";

    public static byte[] GetOrGenerate(
        string libraryRoot, string relativePath, FileInfo sourceFile, int maxDimension,
        IReadOnlyList<Edit> edits, Func<byte[]> generate)
    {
        var cacheDir = Path.Combine(libraryRoot, ".curato", CacheFolderName);
        var cachePath = Path.Combine(cacheDir, BuildCacheFileName(relativePath, sourceFile, maxDimension, edits));

        if (File.Exists(cachePath))
        {
            try
            {
                return File.ReadAllBytes(cachePath);
            }
            catch (IOException)
            {
                // Fall through and regenerate rather than fail the request over a transient read issue.
            }
        }

        var bytes = generate();

        try
        {
            Directory.CreateDirectory(cacheDir);
            File.WriteAllBytes(cachePath, bytes);
        }
        catch (IOException)
        {
            // Caching is a pure optimization — a write failure (disk full,
            // permissions) should never turn a request that just succeeded
            // into a failed one.
        }

        return bytes;
    }

    private static string BuildCacheFileName(string relativePath, FileInfo sourceFile, int maxDimension, IReadOnlyList<Edit> edits)
    {
        var editStamp = edits.Count == 0 ? "0" : $"{edits.Count}:{edits.Max(e => e.Id)}";
        var key = $"{relativePath}|{sourceFile.Length}|{sourceFile.LastWriteTimeUtc.Ticks}|{maxDimension}|{editStamp}";
        var hash = Convert.ToHexStringLower(XxHash128.Hash(Encoding.UTF8.GetBytes(key)));
        return $"{hash}.jpg";
    }
}
