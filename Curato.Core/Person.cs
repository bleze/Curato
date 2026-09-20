namespace Curato.Core;

/// <summary>PLAN.md §14 — a named person. Matching uses every confirmed Face for this Person, not just RepresentativeFaceId.</summary>
public sealed class Person
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public long? RepresentativeFaceId { get; set; }
}
