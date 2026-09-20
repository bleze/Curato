namespace Curato.Core;

/// <summary>
/// PLAN.md §3, Step 0: whether Curato owns this Event/Trip's folder naming
/// (<see cref="CuratoManaged"/>) or is only indexing a folder the user already
/// organized, which is never renamed/moved automatically (<see cref="Adopted"/>).
/// </summary>
public enum NamingMode
{
    Adopted,
    CuratoManaged
}
