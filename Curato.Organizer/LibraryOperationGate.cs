using System.Collections.Concurrent;

namespace Curato.Organizer;

/// <summary>
/// Serializes ScanService/OrganizeService calls against the same library
/// root. Nothing enforced this before this feature — every controller opens
/// its own LibraryDatabase per request with no locking anywhere, which was
/// fine when the only caller was ever a single user clicking a button. The
/// iCloud background sync (PLAN.md §4) is the first thing that can
/// genuinely run a scan concurrently with a user-triggered one on the same
/// library, so this closes that gap rather than leaving a newly introduced
/// race undocumented. Registered as a singleton (Curato.Api/Program.cs) —
/// the locks need to be shared across requests, unlike the
/// repositories/services controllers otherwise construct fresh each call.
/// </summary>
public sealed class LibraryOperationGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IDisposable> AcquireAsync(string libraryRoot, CancellationToken cancellationToken = default)
    {
        var normalized = Path.GetFullPath(libraryRoot);
        var semaphore = _locks.GetOrAdd(normalized, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new Release(semaphore);
    }

    private sealed class Release(SemaphoreSlim semaphore) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            semaphore.Release();
        }
    }
}
