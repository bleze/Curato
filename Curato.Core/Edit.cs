namespace Curato.Core;

/// <summary>
/// PLAN.md §10 — non-destructive; never touches the original file's bytes.
/// Applied at render time only. <see cref="Parameters"/> is op-specific JSON.
/// </summary>
public sealed class Edit
{
    public long Id { get; set; }
    public long MediaItemId { get; set; }
    public EditOperationType OperationType { get; set; }
    public required string Parameters { get; set; }
    public int PipelineOrder { get; set; }
    public RecordSource Source { get; set; }
}
