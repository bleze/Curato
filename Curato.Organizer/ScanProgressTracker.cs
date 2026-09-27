using System.Collections.Concurrent;

namespace Curato.Organizer;

/// <summary>
/// Backs a live "N files found so far" counter during a scan. ScanService's
/// own Scan() call is one long synchronous request with no way to report
/// partway through, so this is a small shared counter it updates as it
/// walks — a separate, lightweight polling request (ScanController's own
/// GET /progress) reads it independently, entirely outside the blocking
/// scan request itself. Registered as a singleton (Curato.Api/Program.cs)
/// for the same reason as LibraryOperationGate: the state needs to be
/// shared across those two concurrent requests, unlike the per-request
/// repositories/services controllers otherwise construct fresh each call.
///
/// Deliberately just a raw file count, not per-Event/Trip progress —
/// clustering needs to see the whole set of loose files at once to bucket
/// them correctly by date gaps, so there's nothing meaningful to report
/// there until the whole walk is done anyway (see PLAN.md's scan-progress
/// note for the fuller reasoning).
/// </summary>
public sealed class ScanProgressTracker
{
    private readonly ConcurrentDictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);

    public void Reset(string libraryRoot) => counts[Normalize(libraryRoot)] = 0;

    public void Increment(string libraryRoot) => counts.AddOrUpdate(Normalize(libraryRoot), 1, (_, count) => count + 1);

    public int Get(string libraryRoot) => counts.GetValueOrDefault(Normalize(libraryRoot));

    /// <summary>Called once a scan finishes (success or failure) so a later poll doesn't report a stale count from a scan that's no longer running.</summary>
    public void Clear(string libraryRoot) => counts.TryRemove(Normalize(libraryRoot), out _);

    private static string Normalize(string libraryRoot) => Path.GetFullPath(libraryRoot);
}
