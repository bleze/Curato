namespace Curato.Core;

/// <summary>
/// A "vacation": a run of nearby Events (PLAN.md §3, Step 2).
/// </summary>
public sealed class TripGroup
{
    public long Id { get; set; }
    public required DateOnly StartDate { get; set; }
    public required DateOnly EndDate { get; set; }
    public required string Title { get; set; }
    public bool TitleIsAuto { get; set; }
    public string? FolderPath { get; set; }
    public NamingMode NamingMode { get; set; }
    public List<long> EventIds { get; set; } = [];
}
