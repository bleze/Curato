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

    /// <summary>
    /// Re-reads a single already-indexed file's real Exif/video-metadata
    /// date, the same way a fresh scan would — the "Reset to Exif" action
    /// (Lightbox) uses this to recover a photo's true date after a bad
    /// manual edit, without needing the original value remembered anywhere:
    /// the file itself was never touched, so its own metadata is always the
    /// ground truth to fall back to.
    /// </summary>
    public static (DateTime TakenAt, TakenAtSource Source) ReadTakenAt(string filePath, MediaKind kind)
    {
        var info = new FileInfo(filePath);
        return kind == MediaKind.Photo ? ReadPhotoTakenAt(filePath, info) : ReadVideoTakenAt(filePath, info);
    }

    private static MediaItem BuildMediaItem(string rootPath, string filePath, MediaKind kind)
    {
        var info = new FileInfo(filePath);
        var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');

        var (takenAt, source) = ReadTakenAt(filePath, kind);

        var (latitude, longitude) = kind == MediaKind.Photo ? ReadGpsLocation(filePath) : (null, null);

        return new MediaItem
        {
            RelativePath = relativePath,
            Kind = kind,
            FileSizeBytes = info.Length,
            QuickHash = QuickHasher.ComputeQuickHash(filePath, info.Length),
            TakenAt = takenAt,
            TakenAtSource = source,
            Latitude = latitude,
            Longitude = longitude,
            ImportedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// EXIF GPS only — confirmed against the installed MetadataExtractor
    /// build that its QuickTime support exposes no GPS/ISO6709 location tag
    /// at all, so video never gets its own coordinates here. A Live Photo's
    /// paired video (<see cref="MediaItem.LivePhotoVideoItemId"/>) is treated
    /// as sharing its still-frame photo's location wherever a location is
    /// displayed, rather than duplicating the column for a value that's
    /// always derivable from the pairing. Public so a one-time backfill for
    /// a library scanned before GPS extraction existed can re-read each
    /// already-indexed photo's coordinates without duplicating this logic —
    /// UpsertMediaItems' own `ON CONFLICT DO NOTHING` never touches an
    /// existing row's Latitude/Longitude on an ordinary re-scan.
    /// </summary>
    public static (double? Latitude, double? Longitude) ReadGpsLocation(string filePath)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(filePath);
            var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
            var location = gps?.GetGeoLocation();
            return location is not { IsZero: false } loc
                ? (null, null)
                : (loc.Latitude, loc.Longitude);
        }
        catch (ImageProcessingException)
        {
            return (null, null);
        }
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
    /// Reads the QuickTime/MP4-family creation date (covers .mov/.mp4/.m4v/
    /// .3gp — the ISO-base-media-file-format family, by far the common case
    /// for phone-recorded video). MetadataExtractor has no reader for AVI/
    /// Matroska/WebM/WMV creation dates, so those still fall back to the
    /// file's filesystem date, same as before this method existed.
    /// </summary>
    private static (DateTime TakenAt, TakenAtSource Source) ReadVideoTakenAt(string filePath, FileInfo info)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(filePath);

            // Apple's own "com.apple.quicktime.creationdate" key (exposed
            // here as QuickTimeMetadataHeaderDirectory.TagCreationDate) is
            // written by the iOS Camera app for every photo/video/Live Photo
            // it records, and — confirmed live by dumping a real file's raw
            // tag value — it's already the true local wall-clock time AT THE
            // RECORDING LOCATION (e.g. 18:51:14 for a video whose mvhd UTC
            // time was 17:51:14, a real Denmark-in-November file: correctly
            // +01:00, not a guessed offset). This is fundamentally different
            // from — and more correct than — the movie header's plain UTC
            // timestamp below: reported live as a Live Photo's HEIC (EXIF,
            // naive local time, always correct) and its paired MOV (movie-
            // header UTC converted using the *server's* own timezone) ending
            // up a full hour apart for a video shot in Brighton, UK while the
            // Curato server ran in Denmark — the UK wasn't in the same DST
            // transition window as Denmark that week. Using Apple's own
            // location-aware value sidesteps needing to know the recording
            // location's timezone at all, exactly like EXIF's DateTimeOriginal
            // already does for photos, so it's preferred whenever present.
            var metadataHeader = directories.OfType<QuickTimeMetadataHeaderDirectory>().FirstOrDefault();
            if (metadataHeader is not null &&
                metadataHeader.TryGetDateTime(QuickTimeMetadataHeaderDirectory.TagCreationDate, out var creationDate) &&
                creationDate.Year > 1904)
            {
                return (creationDate, TakenAtSource.VideoMetadata);
            }

            // Fallback for video without Apple's metadata key (Android,
            // desktop-recorded, re-encoded, etc.): the "movie header"
            // creation date, defined by spec to be UTC. Converting via the
            // *server's* local timezone is only a best-effort guess — it's
            // wrong for anything recorded while traveling outside that
            // timezone, exactly the gap the metadata-header path above
            // exists to close for ordinary iPhone-recorded video.
            var movieHeader = directories.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();
            if (movieHeader is not null &&
                movieHeader.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var created) &&
                created.Year > 1904) // some encoders write an all-zero "unknown" creation time (decodes to the 1904 epoch itself) rather than omitting the field
            {
                // Some non-Apple encoders (older camcorders, certain Android
                // models, re-encoding tools) write a Unix-epoch (1970-based)
                // timestamp into this field instead of the QuickTime-spec-
                // mandated 1904-based one — reported live as a real video
                // whose day/month/time-of-day all looked correct but the
                // year was exactly the two epochs' gap too early (e.g. 1947
                // for a video actually from decades later), since
                // MetadataExtractor correctly decodes the raw value against
                // the 1904 epoch regardless of which one the encoder
                // actually meant. The two epochs differ by a whole number of
                // days (24,107 = exactly 66 years including leap days), so
                // this misinterpretation never touches the time-of-day, only
                // the date — safe to detect (QuickTime/MP4 containers didn't
                // exist before the 1990s, so a decoded year earlier than
                // that is never a genuine recording date) and correct exactly
                // by adding the gap back, rather than leaving it wrong or
                // falling through to the far-less-accurate filesystem date.
                if (created.Year < 1990)
                {
                    created += QuickTimeEpoch.ToUnixEpochGap;
                }

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
