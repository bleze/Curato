using System.Diagnostics;
using Curato.Data;
using Curato.Media;
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
/// reimplemented here) directly into the shared library folder's root
/// (not a subfolder — see below), then feeds the result through the
/// *existing* scan pipeline exactly like a browser upload does
/// (Curato.Api/Controllers/UploadController.cs) — zero new
/// clustering/auto-titling logic, since loose new files there already get
/// auto-clustered into Events and auto-titled.
///
/// Confirmed live why it has to be the root and not a subfolder: an
/// earlier version landed synced files in an "iCloud Sync/" staging
/// subfolder for tidiness, which `AdoptedFolderPlanner` (PLAN.md §3 Step
/// 0) then treated as a single deliberately user-organized folder and
/// "adopted" whole — exactly the same way a real folder like "Family
/// Reunion 2019" is meant to be adopted, since the planner has no way to
/// distinguish a folder Curato itself created from one the user actually
/// organized. Landing files at the root, same as UploadController already
/// does, is what makes them "loose" and eligible for normal auto-clustering.
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
    /// <summary>The most recent line icloudpd printed, or null when no sync is currently running.</summary>
    public string? CurrentActivity { get; private set; }

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
            libraryFolder, config.AppleUsername, config.CookieDirectory,
            config.SinceDate, config.LookbackDays, config.RecentItemCap, cancellationToken);

        repository.RecordSyncResult(
            DateTime.UtcNow,
            status: result.Success ? "Success" : "Error",
            message: result.ErrorMessage,
            itemCount: result.ItemsDownloaded);

        return result;
    }

    private async Task<iCloudSyncResult> RunAsync(
        string libraryFolder, string appleUsername, string cookieDirectory,
        DateOnly? sinceDate, int lookbackDays, int recentItemCap,
        CancellationToken cancellationToken)
    {
        // A fixed calendar date (e.g. "since my last manual sync 2.5 years
        // ago") takes precedence over the rolling N-days-back window when
        // both would otherwise apply — icloudpd's --skip-created-before
        // accepts either form directly (a relative interval like "365d", or
        // an absolute ISO date), so no client-side date math is needed here.
        var skipCreatedBefore = sinceDate is { } date ? date.ToString("yyyy-MM-dd") : $"{lookbackDays}d";

        // See SeedAlreadyDownloadedStubs: pre-create correctly-sized
        // placeholder files for anything icloudpd's own de-dup would
        // otherwise consider re-downloading this run, so it skips them with
        // zero network transfer. Bounded by the exact same cutoff/count used
        // for icloudpd's own --skip-created-before/--recent below, so this
        // never does more work than icloudpd itself is about to do anyway.
        var cutoff = sinceDate is { } d ? d.ToDateTime(TimeOnly.MinValue) : DateTime.UtcNow.AddDays(-lookbackDays);
        List<string> stubPaths;
        using (await gate.AcquireAsync(libraryFolder, cancellationToken))
        {
            var seedDatabase = new LibraryDatabase(libraryFolder);
            seedDatabase.EnsureSchema();
            stubPaths = SeedAlreadyDownloadedStubs(libraryFolder, new LibraryQueryService(seedDatabase), cutoff, recentItemCap);
        }

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
                     "--directory", libraryFolder,
                     // icloudpd's own default (`{:%Y/%m/%d}`) creates nested
                     // date subfolders — each one would trip the exact same
                     // AdoptedFolderPlanner problem landing files at the
                     // root fixes above, just fragmented into many small
                     // folders instead of one big one. Flat only: files land
                     // directly in libraryFolder, loose, for Curato's own
                     // clustering to organize.
                     "--folder-structure", "none",
                     "--set-exif-datetime",
                     "--no-progress-bar",
                     // Bounds every sync (not just the first) to items
                     // created within this window — without it, a
                     // never-yet-synced account with years of history would
                     // try to pull all of it in one go.
                     "--skip-created-before", skipCreatedBefore,
                     // A separate performance ceiling, not a scope control:
                     // confirmed live that --skip-created-before alone still
                     // walks its entire slice checking each item's date one
                     // by one (a "Skipping ..." line each) — --recent N
                     // slices the *first* N items off the listing
                     // (itertools.islice) before any filtering runs at all,
                     // capping total work regardless of what the date filter
                     // finds. Caps by "most recently added to iCloud," not by
                     // creation date, so a bulk-imported batch of old photos
                     // added more recently than N other items could in
                     // principle push genuinely in-window content outside the
                     // slice — user-configurable specifically so a wide
                     // SinceDate/LookbackDays can be paired with a large
                     // enough cap to actually cover it.
                     "--recent", recentItemCap.ToString(),
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

        // Always clean up our own placeholders regardless of outcome — they
        // were never real content, and icloudpd's own de-dup policy is
        // documented to fall back to a size-suffixed *new* filename rather
        // than overwriting an existing file in place when a stub's size
        // didn't actually match, so a stub is never at risk of silently
        // becoming (or masking) real downloaded content.
        using (await gate.AcquireAsync(libraryFolder, cancellationToken))
        {
            CleanupStubs(stubPaths);
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

        var homeLabel = new HomeLocationRepository(database).Get();

        using var release = await gate.AcquireAsync(libraryFolder, cancellationToken);
        var libraryDatabase = new LibraryDatabase(libraryFolder);
        libraryDatabase.EnsureSchema();
        var query = new LibraryQueryService(libraryDatabase);

        var redownloadsRemoved = RemoveExactRedownloads(libraryFolder, libraryDatabase, query);
        if (redownloadsRemoved > 0)
        {
            logger.LogInformation(
                "Discarded {Count} re-downloaded file(s) already indexed elsewhere in the library.", redownloadsRemoved);
        }

        var scanResult = new ScanService(new LibraryRepository(libraryDatabase), query, libraryDatabase, homeLabel: homeLabel)
            .Scan(libraryFolder);

        return new iCloudSyncResult(Success: true, ErrorMessage: null, itemsDownloaded, scanResult);
    }

    /// <summary>
    /// Confirmed live this is needed: icloudpd's own dedup only checks
    /// whether a file with the same name already exists in its designated
    /// download folder (the library root here) — it has no idea Curato's
    /// own Organize feature physically moves files out of that root into
    /// dated folders afterward. Once that happens, the *next* sync finds
    /// nothing at the root for that name and re-downloads the exact same
    /// asset, forever, every sync. Rather than fighting icloudpd's
    /// filename-based model (or giving synced content its own permanent,
    /// never-organized folder — rejected earlier: that's what caused the
    /// "why is this all lumped into one Adopted folder" problem in the
    /// first place), this compares each newly-landed, not-yet-indexed loose
    /// file's (size, QuickHash) against every already-indexed MediaItem in
    /// the library — a match means this is a byte-identical re-download of
    /// content already organized under some other path, so it's deleted
    /// immediately rather than ever reaching ScanService, where it would
    /// otherwise get indexed as a second, cluttering copy.
    ///
    /// Also confirmed live: deleting a photo in Curato doesn't delete it
    /// from iCloud, so the very next sync happily re-downloaded it right
    /// back — the item's MediaItem row (all this check used to look at) is
    /// gone the moment something's recycled, so nothing recognized it as
    /// already-seen. Checking the Recycle Bin's still-present trashed files
    /// only closed that gap for the 30-day retention window, though — once
    /// actually purged, the exact same re-download could still slip back in.
    /// `DeletedMediaFingerprint` (recorded permanently at delete time,
    /// independent of the Recycle Bin's own purge cycle) fixes that for
    /// good: a fingerprint recorded once keeps matching forever, so anything
    /// ever deleted here never silently comes back from a later sync.
    /// </summary>
    /// <summary>
    /// icloudpd's own de-dup (--file-match-policy, default name-size-dedup-
    /// with-suffix per its own --help text) treats an existing file in the
    /// destination directory as "already downloaded" when its name and size
    /// match what iCloud is offering, and skips it — no network transfer at
    /// all. But it only ever looks in the flat destination directory, so
    /// once Organize moves a file into a dated folder, icloudpd sees nothing
    /// there and re-downloads the exact same bytes from iCloud every single
    /// sync. RemoveExactRedownloads below already stopped that from becoming
    /// a duplicate in the library, but not the redundant download itself —
    /// user asked specifically to avoid causing IO we don't need to.
    ///
    /// Pre-creating a placeholder file of the exact right size at the
    /// original filename gives icloudpd's own de-dup exactly the (name,
    /// size) pair it's looking for, so it skips with zero bytes transferred.
    /// If the size guess is ever wrong, icloudpd's documented fallback is to
    /// download under a size-suffixed *new* filename rather than overwrite
    /// what's there — so getting this wrong only costs the same redundant
    /// download that already happens today, never data loss or corruption.
    ///
    /// Bounded to the same window icloudpd itself is about to consider
    /// (skip-created-before's cutoff, recent-N's count) rather than every
    /// item ever indexed — stubbing the whole library on every sync would
    /// itself be pointless IO for items icloudpd wouldn't even look at.
    /// </summary>
    private static List<string> SeedAlreadyDownloadedStubs(
        string libraryFolder, LibraryQueryService query, DateTime cutoff, int recentItemCap)
    {
        var created = new List<string>();
        var candidates = query.GetAllMediaItems()
            .Where(m => m.TakenAt >= cutoff)
            .OrderByDescending(m => m.TakenAt)
            .Take(recentItemCap);

        foreach (var item in candidates)
        {
            var stubPath = Path.Combine(libraryFolder, Path.GetFileName(item.RelativePath));
            if (File.Exists(stubPath))
            {
                // Already sitting at the root for real (not yet organized) —
                // never touch a real file.
                continue;
            }

            try
            {
                using var stream = new FileStream(stubPath, FileMode.CreateNew, FileAccess.Write);
                stream.SetLength(item.FileSizeBytes);
                created.Add(stubPath);
            }
            catch (IOException)
            {
                // Best-effort optimization — a failure here just means this
                // one item gets redundantly re-downloaded, same as before
                // this existed.
            }
        }

        return created;
    }

    private static void CleanupStubs(IReadOnlyList<string> stubPaths)
    {
        foreach (var path in stubPaths)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    private int RemoveExactRedownloads(string libraryFolder, LibraryDatabase database, LibraryQueryService query)
    {
        var existingByHash = new HashSet<(long Size, string Hash)>(
            query.GetAllMediaItems().Select(m => (m.FileSizeBytes, m.QuickHash)));

        foreach (var fingerprint in new RecycleBinRepository(database).GetAllDeletedFingerprints())
        {
            existingByHash.Add((fingerprint.FileSizeBytes, fingerprint.QuickHash));
        }

        if (existingByHash.Count == 0)
        {
            return 0;
        }

        var knownRelativePaths = new HashSet<string>(
            query.GetAllMediaItems().Select(m => m.RelativePath), StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        foreach (var filePath in Directory.EnumerateFiles(libraryFolder))
        {
            var fileName = Path.GetFileName(filePath);
            if (knownRelativePaths.Contains(fileName) || !MediaFileScanner.IsSupportedExtension(Path.GetExtension(fileName)))
            {
                continue;
            }

            var info = new FileInfo(filePath);
            var quickHash = QuickHasher.ComputeQuickHash(filePath, info.Length);
            if (existingByHash.Contains((info.Length, quickHash)))
            {
                File.Delete(filePath);
                removed++;
            }
        }

        return removed;
    }
}
