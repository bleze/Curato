namespace Curato.Core;

/// <summary>
/// One photo or video, as scanned from disk. Path is always relative to its
/// library root (PLAN.md §2 — never absolute, so a library can be moved without
/// breaking anything).
/// </summary>
public sealed class MediaItem
{
    public long Id { get; set; }
    public required string RelativePath { get; set; }
    public MediaKind Kind { get; set; }
    public long FileSizeBytes { get; set; }
    public required string QuickHash { get; set; }
    public string? ContentHash { get; set; }
    public required DateTime TakenAt { get; set; }
    public TakenAtSource TakenAtSource { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long? DurationMs { get; set; }
    public DateTime ImportedAt { get; set; }
}
