using Curato.Core;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace Curato.Media;

/// <summary>
/// Walks a folder and turns each recognized photo/video into a
/// <see cref="MediaItem"/> — EXIF read (PLAN.md §1: read even out of RAW
/// files, before decoding any preview), QuickHash computed (§7), file system
/// dates used only as a last resort (§1's TakenAtSource).
/// </summary>
public sealed class MediaFileScanner
{
    // Common consumer + RAW formats, not an exhaustive list of every
    // container format that exists. RAW/HEIC preview extraction and
    // BMP/TIFF/WebP/GIF thumbnailing both depend on what SkiaSharp's decoder
    // actually supports at render time (§1) — being recognized here (indexed,
    // hashed, clustered, organized) is independent of whether a thumbnail
    // renders correctly for it yet; a file the thumbnail pipeline can't
    // decode falls back to "no preview" (MediaController) rather than being
    // excluded from the library entirely.
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".bmp", ".tif", ".tiff", ".webp", ".gif",
        ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".m4v", ".3gp", ".wmv", ".webm", ".flv", ".mts", ".m2ts"
    };

    /// <summary>Whether <paramref name="extension"/> (with leading dot) is a recognized photo/video type.</summary>
    public static bool IsSupportedExtension(string extension) =>
        PhotoExtensions.Contains(extension) || VideoExtensions.Contains(extension);

    /// <summary>
    /// Recursively scans <paramref name="rootPath"/>. Every path stored on the
    /// resulting items is relative to it, per PLAN.md §2's portability rule.
    /// </summary>
    public IEnumerable<MediaItem> Scan(string rootPath)
    {
        foreach (var filePath in System.IO.Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(filePath);
            MediaKind kind;
            if (PhotoExtensions.Contains(extension))
            {
                kind = MediaKind.Photo;
            }
            else if (VideoExtensions.Contains(extension))
            {
                kind = MediaKind.Video;
            }
            else
            {
                continue;
            }

            yield return BuildMediaItem(rootPath, filePath, kind);
        }
    }

    private static MediaItem BuildMediaItem(string rootPath, string filePath, MediaKind kind)
    {
        var info = new FileInfo(filePath);
        var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');

        var (takenAt, source) = kind == MediaKind.Photo
            ? ReadPhotoTakenAt(filePath, info)
            : (info.LastWriteTimeUtc, TakenAtSource.FileSystem);

        return new MediaItem
        {
            RelativePath = relativePath,
            Kind = kind,
            FileSizeBytes = info.Length,
            QuickHash = QuickHasher.ComputeQuickHash(filePath, info.Length),
            TakenAt = takenAt,
            TakenAtSource = source,
            ImportedAt = DateTime.UtcNow
        };
    }

    private static (DateTime TakenAt, TakenAtSource Source) ReadPhotoTakenAt(string filePath, FileInfo info)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(filePath);
            var exifSubIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (exifSubIfd is not null &&
                exifSubIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dateTaken))
            {
                return (dateTaken, TakenAtSource.ExifDateTaken);
            }
        }
        catch (ImageProcessingException)
        {
            // Not a format MetadataExtractor understands, or no EXIF present —
            // fall through to the filesystem-date fallback below.
        }

        return (info.LastWriteTimeUtc, TakenAtSource.FileSystem);
    }
}
