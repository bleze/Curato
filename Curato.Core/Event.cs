namespace Curato.Core;

/// <summary>
/// A "moment": one logical shoot, produced by gap-based clustering over
/// timestamps (PLAN.md §3). May cross midnight; never crosses a pre-existing
/// folder boundary for Adopted content.
/// </summary>
public sealed class Event
{
    public long Id { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public required string Title { get; set; }
    public bool TitleIsAuto { get; set; }
    public string? FolderPath { get; set; }
    public long? TripGroupId { get; set; }
    public NamingMode NamingMode { get; set; }
    public List<long> MediaItemIds { get; set; } = [];
}
