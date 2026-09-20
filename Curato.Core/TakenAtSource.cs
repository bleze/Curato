namespace Curato.Core;

/// <summary>
/// Where a MediaItem's <see cref="MediaItem.TakenAt"/> came from — EXIF is
/// trusted for grouping decisions; filesystem dates are a last-resort fallback
/// (PLAN.md §1: RAW/HEIC EXIF is read even before a preview is decoded).
/// </summary>
public enum TakenAtSource
{
    ExifDateTaken,
    VideoMetadata,
    FileSystem
}
