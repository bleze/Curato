using System.Diagnostics;
using Curato.Data;
using Curato.Organizer;

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
/// </summary>
public sealed class iCloudSyncRunner(iCloudProcessOptions options, LibraryOperationGate gate, ServerDatabase database)
{
    // Not dot-prefixed, so MediaFileScanner's recursive walk (which only
    // excludes .curato/) picks this up as ordinary loose content.
    public const string StagingFolderName = "iCloud Sync";

    // icloudpd stops paginating once it finds this many consecutive
    // already-downloaded items — the cheap way to make a periodic re-run
    // fast instead of re-listing the whole remote library every time. A
    // first-ever sync should omit this (or pass a much larger value) to
    // actually backfill everything.
    private const int IncrementalUntilFoundCount = 50;

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

        var result = config.LastSyncAt is null
            ? await RunFullBackfillAsync(libraryFolder, config.AppleUsername, config.CookieDirectory, cancellationToken)
            : await RunIncrementalAsync(libraryFolder, config.AppleUsername, config.CookieDirectory, cancellationToken);

        repository.RecordSyncResult(
            DateTime.UtcNow,
            status: result.Success ? "Success" : "Error",
            message: result.ErrorMessage,
            itemCount: result.ItemsDownloaded);

        return result;
    }

    private Task<iCloudSyncResult> RunIncrementalAsync(
        string libraryFolder, string appleUsername, string cookieDirectory, CancellationToken cancellationToken) =>
        RunAsync(libraryFolder, appleUsername, cookieDirectory, IncrementalUntilFoundCount, cancellationToken);

    private Task<iCloudSyncResult> RunFullBackfillAsync(
        string libraryFolder, string appleUsername, string cookieDirectory, CancellationToken cancellationToken) =>
        RunAsync(libraryFolder, appleUsername, cookieDirectory, untilFound: null, cancellationToken);

    private async Task<iCloudSyncResult> RunAsync(
        string libraryFolder, string appleUsername, string cookieDirectory, int? untilFound, CancellationToken cancellationToken)
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
                 })
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (untilFound is { } count)
        {
            startInfo.ArgumentList.Add("--until-found");
            startInfo.ArgumentList.Add(count.ToString());
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start icloudpd.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            return new iCloudSyncResult(
                Success: false,
                ErrorMessage: string.IsNullOrWhiteSpace(stderr) ? stdout : stderr,
                ItemsDownloaded: 0,
                ScanResult: null);
        }

        var itemsDownloaded = CountDownloadedLines(stdout);

        using var release = await gate.AcquireAsync(libraryFolder, cancellationToken);
        var database = new LibraryDatabase(libraryFolder);
        database.EnsureSchema();
        var query = new LibraryQueryService(database);
        var scanResult = new ScanService(new LibraryRepository(database), query).Scan(libraryFolder);

        return new iCloudSyncResult(Success: true, ErrorMessage: null, itemsDownloaded, scanResult);
    }

    // icloudpd logs one line containing "Downloading" per file actually
    // saved — a rough-but-good-enough count for the UI. Exact log wording
    // to confirm against a real run during implementation/first live test.
    private static int CountDownloadedLines(string stdout) =>
        stdout.Split('\n').Count(line => line.Contains("Downloading", StringComparison.OrdinalIgnoreCase));
}
