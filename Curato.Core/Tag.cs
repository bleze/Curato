namespace Curato.Core;

/// <summary>PLAN.md §9 — a Tag *is* an Album: freeform, user-named, cross-folder.</summary>
public sealed class Tag
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public bool IsFavoriteShortcut { get; set; }
    public int SortOrder { get; set; }
}
