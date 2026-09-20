using System.Diagnostics;
using Curato.Data;
using Curato.Organizer;
using Microsoft.Extensions.Logging;

namespace Curato.iCloud;

public sealed record iCloudSyncResult(bool Success, string? ErrorMessage, int ItemsDownloaded, ScanResult? ScanResult);

/// <summary>
/// Runs one iCloud sync pass (PLAN.md §4): invokes the already-installed
/// `icloudpd` CLI (MIT-licensed, mature — pagination, dedup-by-filename
/// against the destination, Live Photos, RAW+JPEG, and the
/// --set-exif-datetime flag that fixes the actual metadata-stripping
/// problem this feature exists for, all already handled by it, none
/// reimplemented here) into a staging subfolder inside the shared library
/// folder, then feeds the result through the *existing* scan pipeline
/// exactly like a browser upload does (Curato.Api/Controllers/
/// UploadController.cs) — zero new clustering/auto-titling logic, since
/// loose new files there already get auto-clustered into Events and
/// auto-titled.
///
/// A "Sync now" click is a synchronous request/response (PLAN.md §4's
/// deliberate v1 simplification — no polling infra), which left the user
/// with zero visibility into a long-running sync: icloudpd's own output
/// was captured but only ever surfaced on failure, so a slow-but-working
/// run looked identical to a stuck one. <see cref="CurrentActivity"/>
/// exposes the latest output line live (read by
/// <c>iCloudSyncController.GetStatus</c>, polled by the Settings panel
/// while a sync is in flight) and every line is also logged via
/// <see cref="ILogger"/> for real-time visibility in the server's own
/// console/log, not just after the fact.
/// </summary>
public sealed class iCloudSyncRunner(
    iCloudProcessOptions options, LibraryOperationGate gate, ServerDatabase database, ILogger<iCloudSyncRunner> logger)
{
    // Not dot-prefixed, so MediaFileScanner's recursive walk (which only
    // excludes .curato/) picks this up as ordinary loose content.
    public const string StagingFolderName = "iCloud Sync";

    /// <summary>The most recent line icloudpd printed, or null when no sync is currently running.</summary>
    public string? CurrentActivity { get; private set; }

    // icloudpd stops paginating once it finds this many consecutive
    // already-processed items — the cheap way to avoid walking the whole
    // remote library every run. Applied on every sync, including the
    // first, now that --skip-created-before (below) is the real scope
    // control: confirmed live that --skip-created-before alone still
    // enumerates and date-checks every single remote item one by one
    // (just skipping the download for old ones), so a 9604-item library
    // took just as long with a 1-day lookback as an unbounded one — the
    // lookback bounds what gets *downloaded*, not how long the walk takes.
    // Originally the very first sync deliberately omitted --until-found
    // entirely for a true unbounded backfill; that's no longer the right
    // default now that LookbackDays exists to express "how much history"
    // directly and predictably, rather than via this consecutive-count
    // heuristic.
    private const int UntilFoundCount = 50;

    /// <summary>
    /// The single entry point both the manual "Sync now" button
    /// (<c>iCloudSyncController</c>) and the periodic background service
    /// (<c>iCloudSyncBackgroundService</c>) call — keeps the
    /// backfill-vs-incremental decision and result bookkeeping in one
    /// place rather than duplicated between them. Returns null when
    /// there's nothing to do: not configured, or (when
    /// <paramref name="ignoreSchedule"/> is false) auto-sync is disabled or
    /// not yet due. <paramref name="ignoreSchedule"/> is true for a manual
    /// click, which always runs regardless of the enabled toggle/interval —
    /// those only govern the *background* schedule, not an explicit request.
    /// </summary>
    public async Task<iCloudSyncResult?> SyncIfConfiguredAsync(bool ignoreSchedule, CancellationToken cancellationToken = default)
    {
        var libraryFolder = new LibraryFolderRepository(database).Get();
        var repository = new iCloudSyncRepository(database);
        var config = repository.Get();
        if (libraryFolder is null || config is null)
        {
            return null;
        }

        if (!ignoreSchedule)
        {
            var due = config.LastSyncAt is null ||
                DateTime.UtcNow - config.LastSyncAt.Value >= TimeSpan.FromMinutes(config.IntervalMinutes);
            if (!config.Enabled || !due)
            {
                return null;
            }
        }

        var result = await RunAsync(
            libraryFolder, config.AppleUsername, config.CookieDirectory, config.LookbackDays, cancellationToken);

        repository.RecordSyncResult(
            DateTime.UtcNow,
            status: result.Success ? "Success" : "Error",
            message: result.ErrorMessage,
            itemCount: result.ItemsDownloaded);

        return result;
    }

    private async Task<iCloudSyncResult> RunAsync(
        string libraryFolder, string appleUsername, string cookieDirectory, int lookbackDays,
        CancellationToken cancellationToken)
    {
        var stagingFolder = Path.Combine(libraryFolder, StagingFolderName);
        Directory.CreateDirectory(stagingFolder);

        var startInfo = new ProcessStartInfo
        {
            FileName = options.IcloudpdExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[]
                 {
                     "--username", appleUsername,
                     "--cookie-directory", cookieDirectory,
                     "--directory", stagingFolder,
                     "--set-exif-datetime",
                     "--no-progress-bar",
                     // Bounds every sync (not just the first) to items
                     // created within this window — without it, a
                     // never-yet-synced account with years of history would
                     // try to pull all of it in one go. icloudpd accepts a
                     // relative interval like "365d" here as well as an
                     // absolute ISO date.
                     "--skip-created-before", $"{lookbackDays}d",
                     "--until-found", UntilFoundCount.ToString(),
                 })
        {
            startInfo.ArgumentList.Add(arg);
        }

        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();
        var itemsDownloaded = 0;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (stdoutLines)
            {
                stdoutLines.Add(e.Data);
            }

            CurrentActivity = e.Data;
            logger.LogInformation("icloudpd: {Line}", e.Data);
            if (e.Data.Contains("Downloading", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref itemsDownloaded);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (stderrLines)
            {
                stderrLines.Add(e.Data);
            }

            CurrentActivity = e.Data;
            logger.LogWarning("icloudpd (stderr): {Line}", e.Data);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start icloudpd.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            CurrentActivity = null;
        }

        if (process.ExitCode != 0)
        {
            var stderr = string.Join('\n', stderrLines);
            var stdout = string.Join('\n', stdoutLines);
            return new iCloudSyncResult(
                Success: false,
                ErrorMessage: string.IsNullOrWhiteSpace(stderr) ? stdout : stderr,
                ItemsDownloaded: 0,
                ScanResult: null);
        }

        using var release = await gate.AcquireAsync(libraryFolder, cancellationToken);
        var database = new LibraryDatabase(libraryFolder);
        database.EnsureSchema();
        var query = new LibraryQueryService(database);
        var scanResult = new ScanService(new LibraryRepository(database), query).Scan(libraryFolder);

        return new iCloudSyncResult(Success: true, ErrorMessage: null, itemsDownloaded, scanResult);
    }
}
