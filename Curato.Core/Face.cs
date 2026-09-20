namespace Curato.Core;

/// <summary>
/// PLAN.md §14 — a detected (or Picasa-imported) face box. <see cref="PersonId"/>
/// null means unassigned, distinct from <see cref="Ignored"/> ("not someone to track").
/// </summary>
public sealed class Face
{
    public long Id { get; set; }
    public long MediaItemId { get; set; }
    public long? PersonId { get; set; }
    public PixelRect BoundingBox { get; set; }
    public byte[]? Embedding { get; set; }
    public double? Confidence { get; set; }
    public RecordSource Source { get; set; }
    public bool Ignored { get; set; }
}
