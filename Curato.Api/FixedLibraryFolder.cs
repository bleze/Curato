namespace Curato.Api;

/// <summary>
/// The Library Folder set by configuration (<c>Curato:LibraryFolder</c>, which
/// the Docker image sets to its /photos mount) rather than in Settings; null
/// when Settings decides. See Program.cs.
/// </summary>
public sealed record FixedLibraryFolder(string? Path);
