namespace Curato.Core;

/// <summary>
/// The QuickTime/MP4 movie-header creation-time field is spec'd as seconds
/// since 1904-01-01 UTC (the classic Mac epoch), not the Unix epoch. Some
/// non-Apple encoders (older camcorders, certain Android models, re-encoding
/// tools) write a Unix-epoch (1970-based) value into it anyway — decoded
/// (correctly, per spec) against 1904, the result lands exactly this gap too
/// early. The two epochs differ by a whole number of days, so the
/// misinterpretation never touches the time-of-day, only the date — shared
/// by <c>Curato.Media.MediaFileScanner</c> (detects and corrects this on
/// scan) and <c>Curato.Data.LibraryRepository</c>'s repair pass for videos
/// already indexed with the wrong date before that fix existed.
/// </summary>
public static class QuickTimeEpoch
{
    public static readonly TimeSpan ToUnixEpochGap =
        new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc) - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}
