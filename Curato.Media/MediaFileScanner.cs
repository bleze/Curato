using Curato.Core;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;

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

    /// <summary>Whether <paramref name="extension"/> (with leading dot) is a recognized video type.</summary>
    public static bool IsVideoExtension(string extension) => VideoExtensions.Contains(extension);

    /// <summary>
    /// Recursively scans <paramref name="rootPath"/>. Every path stored on the
    /// resulting items is relative to it, per PLAN.md §2's portability rule.
    /// </summary>
    public IEnumerable<MediaItem> Scan(string rootPath)
    {
        foreach (var filePath in System.IO.Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            // .curato/ holds Curato's own data (library.db, the Recycle Bin's
            // trashed originals, the thumbnail cache) — never real library
            // content, and RecycleBin/thumbnail-cache both contain real
            // .jpg files that would otherwise get indexed as new photos on
            // every re-scan.
            var normalized = filePath.Replace('\\', '/');
            if (normalized.Contains("/.curato/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

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
            : ReadVideoTakenAt(filePath, info);

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

            // DateTimeOriginal is the real "when this was taken" tag, but
            // some sharing/transfer paths (iMessage, WhatsApp, some cloud
            // re-encodes) strip it while leaving other EXIF fields intact —
            // reported live as an iPhone photo with no usable EXIF at all
            // once that happens. DateTimeDigitized is effectively the same
            // moment for a camera/phone (the shot is digitized as it's
            // taken) so it's treated as equally trustworthy; IFD0's plain
            // DateTime tag is technically "last modified" rather than
            // "taken" but still comes from the device/software that wrote
            // the file, which beats a filesystem timestamp that only
            // reflects whenever the file was last copied.
            if (exifSubIfd is not null &&
                exifSubIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dateTaken))
            {
                return (dateTaken, TakenAtSource.ExifDateTaken);
            }

            if (exifSubIfd is not null &&
                exifSubIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var digitized))
            {
                return (digitized, TakenAtSource.ExifDateTaken);
            }

            var exifIfd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            if (exifIfd0 is not null &&
                exifIfd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var modified))
            {
                return (modified, TakenAtSource.ExifModifyDate);
            }
        }
        catch (ImageProcessingException)
        {
            // Not a format MetadataExtractor understands, or no EXIF present —
            // fall through to the filesystem-date fallback below.
        }

        return (info.LastWriteTimeUtc, TakenAtSource.FileSystem);
    }

    /// <summary>
    /// Reads the QuickTime/MP4-family "movie header" creation date (covers
    /// .mov/.mp4/.m4v/.3gp — the ISO-base-media-file-format family, by far
    /// the common case for phone-recorded video). MetadataExtractor has no
    /// reader for AVI/Matroska/WebM/WMV creation dates, so those still fall
    /// back to the file's filesystem date, same as before this method existed.
    /// </summary>
    private static (DateTime TakenAt, TakenAtSource Source) ReadVideoTakenAt(string filePath, FileInfo info)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(filePath);
            var movieHeader = directories.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();
            if (movieHeader is not null &&
                movieHeader.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var created) &&
                created.Year > 1904) // some encoders write an all-zero "unknown" creation time (decodes to the 1904 epoch itself) rather than omitting the field
            {
                // Unlike EXIF's DateTimeOriginal (a naive local wall-clock
                // value, no timezone concept — see ReadPhotoTakenAt above),
                // the QuickTime movie header's creation date is defined by
                // spec to be UTC. MetadataExtractor hands it back with
                // Kind=Unspecified rather than Kind=Utc, so storing it
                // as-is silently skipped the UTC->local conversion every
                // other TakenAt value already gets for free by being a
                // genuine local value — reported live as video timestamps
                // sitting exactly one timezone-offset (2h, CEST) early.
                // DateTime.ToLocalTime() treats an Unspecified source as
                // UTC by its own documented contract, which is exactly the
                // assumption QuickTime's spec makes, so this both converts
                // and matches what ReadPhotoTakenAt's values already are.
                return (created.ToLocalTime(), TakenAtSource.VideoMetadata);
            }
        }
        catch (ImageProcessingException)
        {
            // Not a format MetadataExtractor understands, or no usable header present —
            // fall through to the filesystem-date fallback below.
        }

        return (info.LastWriteTimeUtc, TakenAtSource.FileSystem);
    }
}
