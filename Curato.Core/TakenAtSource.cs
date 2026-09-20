namespace Curato.Core;

/// <summary>
/// Where a MediaItem's <see cref="MediaItem.TakenAt"/> came from — EXIF is
/// trusted for grouping decisions; filesystem dates are a last-resort fallback
/// (PLAN.md §1: RAW/HEIC EXIF is read even before a preview is decoded).
/// </summary>
public enum TakenAtSource
{
    ExifDateTaken,
    /// <summary>EXIF IFD0's plain "last modified" tag — used only when DateTimeOriginal/DateTimeDigitized are both missing (some sharing/transfer paths strip them). Less authoritative than ExifDateTaken but still beats a filesystem timestamp.</summary>
    ExifModifyDate,
    VideoMetadata,
    FileSystem
}
