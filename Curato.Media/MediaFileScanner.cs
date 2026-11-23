using System.Globalization;
using Curato.Core;
using MetadataExtractor;
using MetadataExtractor.Formats.Avi;
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

    /// <summary>
    /// Folders a NAS or OS keeps for itself inside shared folders — never
    /// library content. On a Synology, every photo folder has an @eaDir with
    /// Synology Photos' own SYNOPHOTO_THUMB_*.jpg previews: invisible over
    /// SMB, but plain folders to a scan running on the NAS (Docker), which
    /// indexed ~53,000 of them as photos before this existed.
    /// </summary>
    private static readonly HashSet<string> SystemFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "@eaDir", "#recycle", "#snapshot", "@tmp", "@sharebin",          // Synology
        "@Recycle", "@Recently-Snapshot", ".@__thumb", "@__thumb",      // QNAP
        ".AppleDouble", ".AppleDB", "$RECYCLE.BIN", "System Volume Information",
    };

    /// <summary>Whether a folder with this name is a NAS/OS system folder the scan never enters.</summary>
    public static bool IsSystemFolderName(string folderName) => SystemFolderNames.Contains(folderName);

    /// <summary>Whether a library-relative file path ('/'-separated) lies inside a system folder.</summary>
    public static bool IsInSystemFolder(string relativePath)
    {
        var segments = relativePath.Split('/');
        return segments.Take(segments.Length - 1).Any(IsSystemFolderName);
    }

    /// <summary>Whether <paramref name="extension"/> (with leading dot) is a recognized photo/video type.</summary>
    public static bool IsSupportedExtension(string extension) =>
        PhotoExtensions.Contains(extension) || VideoExtensions.Contains(extension);

    /// <summary>Whether <paramref name="extension"/> (with leading dot) is a recognized video type.</summary>
    public static bool IsVideoExtension(string extension) => VideoExtensions.Contains(extension);

    /// <summary>
    /// Recursively scans <paramref name="rootPath"/>. Every path stored on the
    /// resulting items is relative to it, per PLAN.md §2's portability rule.
    ///
    /// <paramref name="previousFolderModifiedTimes"/> and
    /// <paramref name="previouslyIndexedByRelativePath"/> together enable an
    /// incremental re-scan: a folder whose own LastWriteTimeUtc still
    /// matches what's recorded (a directory's own modified-time changes
    /// whenever an entry is added, removed, or renamed inside it — but *not*
    /// when a file's content is merely overwritten in place without
    /// renaming, a known gap accepted for the large speedup this gives a
    /// big, mostly-static library) is assumed unchanged, and every file
    /// inside it is yielded straight from the already-known MediaItem rather
    /// than re-read/re-hashed from disk. A file that's somehow missing from
    /// that lookup anyway (shouldn't happen for a genuinely unchanged
    /// folder, but a corrupt/stale FolderScanState row is cheap to recover
    /// from) still gets the full real read, never silently skipped.
    /// <paramref name="currentFolderModifiedTimes"/>, if supplied, is filled
    /// in with every folder actually visited (unchanged or not) — the
    /// caller persists it after a full, non-cancelled scan so the *next*
    /// scan has something to compare against.
    /// Every folder is still walked and every file still yielded regardless
    /// of any of this — only the expensive per-file metadata work is ever
    /// skipped, never the file's presence in the result, so deletion
    /// detection (ScanService.RemoveMissingFiles) stays exact.
    /// </summary>
    public IEnumerable<MediaItem> Scan(
        string rootPath,
        IReadOnlyDictionary<string, DateTime>? previousFolderModifiedTimes = null,
        IReadOnlyDictionary<string, MediaItem>? previouslyIndexedByRelativePath = null,
        Dictionary<string, DateTime>? currentFolderModifiedTimes = null)
    {
        foreach (var item in ScanDirectory(
                     rootPath, rootPath, previousFolderModifiedTimes, previouslyIndexedByRelativePath, currentFolderModifiedTimes))
        {
            yield return item;
        }
    }

    private static IEnumerable<MediaItem> ScanDirectory(
        string rootPath, string currentDir,
        IReadOnlyDictionary<string, DateTime>? previousFolderModifiedTimes,
        IReadOnlyDictionary<string, MediaItem>? previouslyIndexedByRelativePath,
        Dictionary<string, DateTime>? currentFolderModifiedTimes)
    {
        var relativeDir = currentDir == rootPath ? "" : Path.GetRelativePath(rootPath, currentDir).Replace('\\', '/');

        // .curato/ holds Curato's own data (library.db, the Recycle Bin's
        // trashed originals, the thumbnail cache) — never real library
        // content, and RecycleBin/thumbnail-cache both contain real .jpg
        // files that would otherwise get indexed as new photos on every
        // re-scan.
        if (relativeDir == ".curato" || relativeDir.StartsWith(".curato/", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        var modifiedUtc = System.IO.Directory.GetLastWriteTimeUtc(currentDir);
        if (currentFolderModifiedTimes is not null)
        {
            currentFolderModifiedTimes[relativeDir] = modifiedUtc;
        }

        var unchanged = previousFolderModifiedTimes is not null &&
            previousFolderModifiedTimes.TryGetValue(relativeDir, out var previousModifiedUtc) &&
            previousModifiedUtc == modifiedUtc;

        foreach (var filePath in System.IO.Directory.EnumerateFiles(currentDir))
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

            if (unchanged && previouslyIndexedByRelativePath is not null)
            {
                var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
                if (previouslyIndexedByRelativePath.TryGetValue(relativePath, out var existing))
                {
                    yield return existing;
                    continue;
                }
            }

            yield return BuildMediaItem(rootPath, filePath, kind);
        }

        foreach (var subDir in System.IO.Directory.EnumerateDirectories(currentDir))
        {
            if (IsSystemFolderName(Path.GetFileName(subDir)))
            {
                continue;
            }

            foreach (var item in ScanDirectory(
                         rootPath, subDir, previousFolderModifiedTimes, previouslyIndexedByRelativePath, currentFolderModifiedTimes))
            {
                yield return item;
            }
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

        // .ToLocalTime(), not the raw Utc value: every other TakenAt source
        // (Exif, video metadata, manual) is DateTimeKind.Unspecified and
        // serializes with no timezone suffix at all — the frontend's
        // `new Date(...)` treats that as plain local wall-clock time.
        // Confirmed live as a real bug, not just a style nit: a raw
        // DateTimeKind.Utc value here serializes with a trailing "Z",
        // which `new Date(...)` instead treats as a genuine UTC instant —
        // sorting/comparing a "Z"-tagged FileSystem fallback date next to
        // any Exif/video-metadata date was silently off by exactly the
        // server's own UTC offset, enough to visibly misorder an Event in
        // the library timeline. .ToLocalTime() doesn't change the real
        // instant at all, just how it round-trips through ToString("O") —
        // DateTimeKind.Local serializes with an explicit numeric offset
        // (e.g. "+02:00"), which both .NET and the frontend already parse
        // correctly and consistently (same shape video metadata's own
        // real, already-working timezone offsets use).
        return (info.LastWriteTimeUtc.ToLocalTime(), TakenAtSource.FileSystem);
    }

    /// <summary>
    /// Reads the QuickTime/MP4-family creation date (covers .mov/.mp4/.m4v/
    /// .3gp — the ISO-base-media-file-format family, by far the common case
    /// for phone-recorded video), or an AVI's own creation-date tag (see
    /// <see cref="TryParseAviDateTimeOriginal"/>). MetadataExtractor has no
    /// reader for Matroska/WebM/WMV creation dates, so those still fall back
    /// to the file's filesystem date.
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

            // AVI's own creation-date tag, from the RIFF "IDIT" chunk —
            // Windows Explorer surfaces this same value as "Media created"
            // under a file's Details > Origin. Already local wall-clock
            // time (no timezone/UTC conversion involved, unlike the
            // QuickTime movie header above), matching what a camera would
            // have actually written down.
            var aviHeader = directories.OfType<AviDirectory>().FirstOrDefault();
            if (aviHeader is not null && TryParseAviDateTimeOriginal(aviHeader, out var aviCreated))
            {
                return (aviCreated, TakenAtSource.VideoMetadata);
            }
        }
        catch (ImageProcessingException)
        {
            // Not a format MetadataExtractor understands, or no usable header present —
            // fall through to the filesystem-date fallback below.
        }

        // .ToLocalTime() — see the photo-path fallback above for why a raw
        // DateTimeKind.Utc value here is a real timeline-ordering bug, not
        // just a style nit.
        return (info.LastWriteTimeUtc.ToLocalTime(), TakenAtSource.FileSystem);
    }

    /// <summary>
    /// AVI's TagDateTimeOriginal is a raw C <c>ctime()</c>-style string (e.g.
    /// "Fri Sep 03 13:04:24 2004") — confirmed live that MetadataExtractor's
    /// own <c>TryGetDateTime</c> doesn't understand this format at all
    /// (always returns false) despite <c>GetString</c> returning a perfectly
    /// parseable value, which is why this needs its own parser rather than
    /// reusing the pattern the QuickTime directories above use directly.
    /// The leading day-of-week name is dropped (not validated against the
    /// actual date) before parsing: <c>ctime()</c> space-pads a single-digit
    /// day ("Jan  9" vs "Jan 03"), and collapsing repeated whitespace via
    /// Split/Join handles both paddings with one format string rather than
    /// needing two.
    /// </summary>
    private static bool TryParseAviDateTimeOriginal(AviDirectory directory, out DateTime dateTime)
    {
        dateTime = default;
        var raw = directory.GetString(AviDirectory.TagDateTimeOriginal);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        // The RIFF chunk's declared length sometimes includes a trailing
        // NUL terminator as part of the string content itself (rather than
        // it being outside the chunk's own byte count) — confirmed live
        // this leaves one or more embedded '\0' characters stuck to the
        // year token after Split, which silently fails the exact parse
        // below with no indication why. Trim defensively rather than
        // assume every encoder writes this tag identically.
        raw = raw.TrimEnd('\0', ' ');

        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var withoutDayName = string.Join(' ', parts.Skip(1));
        return DateTime.TryParseExact(
            withoutDayName, "MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out dateTime);
    }
}
