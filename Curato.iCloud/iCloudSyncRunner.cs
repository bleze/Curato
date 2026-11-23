using System.Diagnostics;
using System.Text;
using Curato.Data;
using Curato.Media;
using Curato.Organizer;
using Microsoft.Extensions.Logging;

namespace Curato.iCloud;

public sealed record iCloudSyncResult(
    bool Success, string? ErrorMessage, int ItemsDownloaded, ScanResult? ScanResult, bool TwoFactorRequired = false);

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
    /// True while a manual sync is parked waiting for a 2FA code — exposed
    /// so the Settings panel's existing status poll (originally built only
    /// to show <see cref="CurrentActivity"/> live) can notice this
    /// independently of whether the original "Sync now" POST's own response
    /// ever makes it back to the browser. Confirmed live as a real gap, not
    /// just a hypothetical: a sync slow enough to reach Apple's 2FA prompt
    /// can also be slow enough for the dev proxy's own connection handling
    /// to drop that one long-lived request along the way — the same failure
    /// mode already identified for a long Scan (see PLAN's "scan completion
    /// resilience" notes) — in which case this is the only signal left that
    /// a code is actually needed.
    /// </summary>
    public bool TwoFactorRequired => _pendingSync is { } pending && !pending.Process.HasExited;

    /// <summary>
    /// Shared with <see cref="iCloudAuthClient"/>'s own detection of the same
    /// prompt (its "authentication code"/"two-step authentication code"
    /// comment explains the wording). Confirmed live as a real way to wedge
    /// the whole library, not a hypothetical: Apple can re-challenge an
    /// already-connected account at any time, and icloudpd's plain
    /// <c>input()</c> call for the resulting code just blocks forever if
    /// nothing ever answers it.
    ///
    /// A manual "Sync now" click has someone actually watching (they're the
    /// one who'll see the code land on their phone), so that path keeps the
    /// process alive and waits for <see cref="SubmitTwoFactorCodeAsync"/> —
    /// see <see cref="_pendingSync"/>. The scheduled background run doesn't:
    /// nobody would see the prompt before Apple's code expires, and leaving
    /// a process (and this sync's placeholder stub files) parked
    /// indefinitely waiting for an answer that will never come isn't
    /// survivable — that path kills the process immediately instead and
    /// reports a clear "reconnect in Settings" error.
    /// </summary>
    private const string TwoFactorPromptMarker = "authentication code";

    /// <summary>
    /// A manual sync parked at Apple's 2FA prompt, waiting for
    /// <see cref="SubmitTwoFactorCodeAsync"/> to answer it on this exact
    /// process's stdin. Deliberately not disposed/cleaned up until then (or
    /// until <see cref="CancelPendingSync"/>) — see <see cref="TwoFactorPromptMarker"/>.
    /// Singleton-scoped (same lifetime as this class), so it survives across
    /// the request that triggered it and the later one that answers it.
    /// </summary>
    private PendingSync? _pendingSync;

    private sealed record PendingSync(
        Process Process, StdoutPump Pump, List<string> StdoutLines, List<string> StderrLines,
        List<string> StubPaths, string LibraryFolder, string? HomeLabel,
        string AppleUsername, string CookieDirectory);

    /// <summary>
    /// Reads icloudpd's stdout raw, not line-by-line — confirmed live as the
    /// actual reason the 2FA prompt was never detected while the process was
    /// still alive: .NET's <c>Process.BeginOutputReadLine</c>/
    /// <c>OutputDataReceived</c> (what this file used before, and still uses
    /// for stderr) only ever raises its event once a complete, newline-
    /// terminated line has arrived. icloudpd's final prompt text ("Please
    /// enter two-factor authentication code...") is written via Python's
    /// <c>input(prompt)</c>, which flushes the prompt WITHOUT a trailing
    /// newline (it's meant to keep the cursor on the same line) — so that
    /// text just sat in .NET's internal line buffer forever, invisible to
    /// <see cref="TwoFactorPromptMarker"/> detection, while the process
    /// itself sat forever waiting for stdin. (It only ever showed up in logs
    /// *after* the process was later killed, because closing the pipe forces
    /// .NET to flush whatever partial line it was holding — which is exactly
    /// why it looked like detection "used to work.") Reading raw characters
    /// instead (same technique <see cref="iCloudAuthClient.ReadUntilPromptOrExitAsync"/>
    /// already uses successfully for this exact prompt) sees the marker the
    /// moment it's flushed, newline or not.
    /// </summary>
    private sealed class StdoutPump(Process process, List<string> lines, Action<string> onLine, string marker)
    {
        private readonly StringBuilder _pending = new();
        private readonly char[] _buffer = new char[1024];

        /// <summary>
        /// Reads until either the marker appears anywhere in the not-yet-
        /// newline-terminated tail (returns true, process still running) or
        /// the stream closes because the process exited (returns false).
        /// Safe to call again afterward — including after already returning
        /// false — since a closed/exhausted reader just returns 0 bytes
        /// immediately rather than erroring.
        /// </summary>
        public async Task<bool> PumpUntilPromptOrEndAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                int read;
                try
                {
                    read = await process.StandardOutput.ReadAsync(_buffer.AsMemory(), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                if (read == 0)
                {
                    FlushRemainder();
                    return false;
                }

                _pending.Append(_buffer, 0, read);

                int newlineIndex;
                while ((newlineIndex = IndexOfNewline(_pending)) >= 0)
                {
                    var line = _pending.ToString(0, newlineIndex).TrimEnd('\r');
                    _pending.Remove(0, newlineIndex + 1);
                    FlushLine(line);
                }

                if (_pending.Length > 0 && Contains(_pending, marker))
                {
                    return true;
                }
            }
        }

        private void FlushRemainder()
        {
            if (_pending.Length == 0)
            {
                return;
            }

            FlushLine(_pending.ToString());
            _pending.Clear();
        }

        private void FlushLine(string line)
        {
            lines.Add(line);
            onLine(line);
        }

        private static int IndexOfNewline(StringBuilder text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool Contains(StringBuilder text, string value)
            => text.ToString().Contains(value, StringComparison.OrdinalIgnoreCase);
    }

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
        if (_pendingSync is { } existingPending && !existingPending.Process.HasExited)
        {
            // Already parked waiting for a code from an earlier "Sync now"
            // click — a manual click (e.g. the user reopened Settings and
            // clicked it again) should just re-surface that same pending
            // state rather than starting a second icloudpd process; a
            // scheduled background tick should just skip this round rather
            // than interfere with it.
            return ignoreSchedule ? new iCloudSyncResult(false, null, 0, null, TwoFactorRequired: true) : null;
        }

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

        // Only a manual click (ignoreSchedule) has someone present to
        // actually answer a 2FA prompt — see TwoFactorPromptMarker's doc
        // comment.
        var result = await RunAsync(
            libraryFolder, config.AppleUsername, config.CookieDirectory,
            config.SinceDate, config.LookbackDays, config.RecentItemCap,
            allowInteractiveTwoFactor: ignoreSchedule, cancellationToken);

        if (!result.TwoFactorRequired)
        {
            repository.RecordSyncResult(
                DateTime.UtcNow,
                status: result.Success ? "Success" : "Error",
                message: result.ErrorMessage,
                itemCount: result.ItemsDownloaded);
        }

        return result;
    }

    /// <summary>
    /// Answers a manual sync parked at <see cref="_pendingSync"/> with the
    /// code the user read off their phone, then runs the exact same
    /// post-exit pipeline (stub cleanup, dedup, Scan) a sync that never
    /// needed 2FA goes through — see <see cref="FinishSyncAsync"/>.
    /// </summary>
    public async Task<iCloudSyncResult> SubmitTwoFactorCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        if (_pendingSync is not { } pending || pending.Process.HasExited)
        {
            _pendingSync = null;
            return new iCloudSyncResult(false, "No iCloud download is waiting for a code — click Download from iCloud again.", 0, null);
        }

        _pendingSync = null;

        await pending.Process.StandardInput.WriteLineAsync(code);
        await pending.Process.StandardInput.FlushAsync(cancellationToken);

        var result = await FinishSyncAsync(
            pending.Process, pending.Pump, pending.StdoutLines, pending.StderrLines, pending.StubPaths,
            pending.LibraryFolder, pending.HomeLabel, pending.AppleUsername, pending.CookieDirectory, cancellationToken);

        new iCloudSyncRepository(database).RecordSyncResult(
            DateTime.UtcNow,
            status: result.Success ? "Success" : "Error",
            message: result.ErrorMessage,
            itemCount: result.ItemsDownloaded);

        return result;
    }

    /// <summary>Backs out of a pending 2FA-parked sync (e.g. the user leaves the Settings screen) without completing it.</summary>
    public void CancelPendingSync()
    {
        if (_pendingSync is not { } pending)
        {
            return;
        }

        _pendingSync = null;
        try
        {
            if (!pending.Process.HasExited)
            {
                pending.Process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Already exited — nothing more to do.
        }
        finally
        {
            pending.Process.Dispose();
        }
    }

    private async Task<iCloudSyncResult> RunAsync(
        string libraryFolder, string appleUsername, string cookieDirectory,
        DateOnly? sinceDate, int lookbackDays, int recentItemCap,
        bool allowInteractiveTwoFactor, CancellationToken cancellationToken)
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
            var seedQuery = new LibraryQueryService(seedDatabase);

            // Self-heals stubs orphaned by a *previous* sync that never
            // reached CleanupStubs below — confirmed live as a real way to
            // lose ~2000 files' worth of root clutter: the hosting process
            // being killed outright (e.g. during an unrelated rebuild/
            // restart) leaves no chance for any try/finally in this file to
            // run. Unbounded by this run's own lookback window (unlike
            // seeding new stubs below) since an orphan could be arbitrarily
            // old by the time it's next noticed. ScanService.Scan now runs
            // this same check too (OrphanedStubCleanup, shared rather than
            // duplicated) — confirmed live as needed there as well: an
            // orphaned stub left behind between syncs doesn't wait for the
            // *next sync* to be noticed if an ordinary Scan runs first,
            // which would otherwise index it as a real (all-zero) MediaItem.
            OrphanedStubCleanup.CleanupOrphanedStubs(libraryFolder, seedQuery);

            stubPaths = SeedAlreadyDownloadedStubs(libraryFolder, seedQuery, cutoff, recentItemCap);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = options.IcloudpdExecutable,
            // Always redirected now (previously only true for the one-time
            // login handshake): a manual sync needs it to relay a 2FA code
            // through SubmitTwoFactorCodeAsync; harmless for a scheduled
            // background run, which never writes to it.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[]
                 {
                     "--username", appleUsername,
                     "--cookie-directory", cookieDirectory,
                     "--directory", libraryFolder,
                     // Confirmed live as a real gap: a photo edited on the
                     // phone (rotate, crop, filter) leaves iCloud's
                     // "original" asset untouched and stores the edit as a
                     // separate "adjusted" rendition — icloudpd's own
                     // default with no --size at all only ever fetches
                     // "original", so an edited photo downloaded exactly as
                     // it looked before the edit, with nothing wrong in
                     // Curato's own EXIF-aware rendering to explain it.
                     // Requesting both prefers "adjusted" (the edited
                     // version) when one exists, falling back to "original"
                     // for everything else — "original" is documented to
                     // never get a filename suffix, so this doesn't change
                     // naming for the common (unedited) case at all.
                     "--size", "adjusted",
                     "--size", "original",
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
        var homeLabel = new HomeLocationRepository(database).Get();

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        // stderr never carries the interactive prompt in practice, and is
        // always newline-terminated — the ordinary line-based event API is
        // fine here. Stdout is NOT (see StdoutPump's doc comment), so it's
        // pumped manually below instead of via BeginOutputReadLine.
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
            process.Dispose();
            throw new InvalidOperationException("Failed to start icloudpd.");
        }

        process.BeginErrorReadLine();

        var stdoutPump = new StdoutPump(
            process, stdoutLines,
            line =>
            {
                CurrentActivity = line;
                logger.LogInformation("icloudpd: {Line}", line);
            },
            TwoFactorPromptMarker);

        var sawPrompt = await stdoutPump.PumpUntilPromptOrEndAsync(cancellationToken);

        if (sawPrompt && !process.HasExited)
        {
            if (allowInteractiveTwoFactor)
            {
                // Deliberately left running, not disposed —
                // SubmitTwoFactorCodeAsync answers this exact process's
                // stdin once the user has the code from their phone.
                CurrentActivity = null;
                _pendingSync = new PendingSync(
                    process, stdoutPump, stdoutLines, stderrLines, stubPaths, libraryFolder, homeLabel, appleUsername, cookieDirectory);
                return new iCloudSyncResult(Success: false, ErrorMessage: null, ItemsDownloaded: 0, ScanResult: null, TwoFactorRequired: true);
            }

            // Scheduled/background run — nobody's there to answer within
            // Apple's short code-validity window, and leaving the process
            // (and this sync's stub placeholders) parked indefinitely isn't
            // survivable. Fail fast and point at the flow that *can* answer
            // it instead.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already exiting on its own — nothing more to do.
            }

            CurrentActivity = null;
            using (await gate.AcquireAsync(libraryFolder, CancellationToken.None))
            {
                CleanupStubs(stubPaths);
            }
            process.Dispose();

            return new iCloudSyncResult(
                Success: false,
                ErrorMessage: "Apple asked for a new verification code, which a background download has no way to answer. " +
                    "Go to Settings → iCloud, click Disconnect, then connect your account again to re-verify.",
                ItemsDownloaded: 0,
                ScanResult: null);
        }

        // Finished on its own without ever needing 2FA — the common case.
        return await FinishSyncAsync(
            process, stdoutPump, stdoutLines, stderrLines, stubPaths, libraryFolder, homeLabel,
            appleUsername, cookieDirectory, cancellationToken);
    }

    /// <summary>
    /// Everything that happens once icloudpd has actually finished running —
    /// shared by the common "never needed 2FA" path above and
    /// <see cref="SubmitTwoFactorCodeAsync"/>'s "just answered the prompt"
    /// path, since both end up in exactly the same place from here: wait for
    /// the exit code, always clean up this sync's stub placeholders
    /// regardless of outcome, then (on success) dedup and Scan.
    /// </summary>
    private async Task<iCloudSyncResult> FinishSyncAsync(
        Process process, StdoutPump stdoutPump, List<string> stdoutLines, List<string> stderrLines, List<string> stubPaths,
        string libraryFolder, string? homeLabel, string appleUsername, string cookieDirectory, CancellationToken cancellationToken)
    {
        int exitCode;
        try
        {
            // Drains (and logs) whatever icloudpd prints from here on —
            // a no-op if it already ran to completion above (a closed
            // reader just returns 0 bytes immediately), the real remaining
            // work if this is resuming right after a 2FA code was just
            // written to stdin.
            await stdoutPump.PumpUntilPromptOrEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            exitCode = process.ExitCode;
        }
        finally
        {
            CurrentActivity = null;
            // Always clean up our own placeholders regardless of outcome —
            // they were never real content, and icloudpd's own de-dup
            // policy is documented to fall back to a size-suffixed *new*
            // filename rather than overwriting an existing file in place
            // when a stub's size didn't actually match, so a stub is never
            // at risk of silently becoming (or masking) real downloaded
            // content.
            using (await gate.AcquireAsync(libraryFolder, CancellationToken.None))
            {
                CleanupStubs(stubPaths);
            }
            process.Dispose();
        }

        if (exitCode != 0)
        {
            var stderr = string.Join('\n', stderrLines);
            var stdout = string.Join('\n', stdoutLines);
            return new iCloudSyncResult(
                Success: false,
                ErrorMessage: string.IsNullOrWhiteSpace(stderr) ? stdout : stderr,
                ItemsDownloaded: 0,
                ScanResult: null);
        }

        var itemsDownloaded = stdoutLines.Count(line => line.Contains("Downloading", StringComparison.OrdinalIgnoreCase));

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

        var deletedOnPhoneRemoved = await RemoveItemsStillInRecentlyDeleted(
            libraryFolder, appleUsername, cookieDirectory, cancellationToken);
        if (deletedOnPhoneRemoved > 0)
        {
            logger.LogInformation(
                "Discarded {Count} file(s) that are in iCloud's own Recently Deleted album.", deletedOnPhoneRemoved);
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
    internal static List<string> SeedAlreadyDownloadedStubs(
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

    internal static void CleanupStubs(IReadOnlyList<string> stubPaths)
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

    /// <summary>
    /// Confirmed live as a real gap, not a hypothetical: icloudpd's default
    /// (no <c>--album</c> given, meant to mean "the whole collection") still
    /// returned assets the account's own "Recently Deleted" album currently
    /// lists — reproduced directly against the real account, cross-checked
    /// against the library's own indexed items by filename. icloudpd has no
    /// flag to exclude that album from the main download, so this runs it a
    /// second time, scoped to exactly that album with
    /// <c>--only-print-filenames --dry-run</c> (lists without downloading or
    /// touching anything), and deletes any file this sync just landed at the
    /// root whose name matches — before Scan ever gets a chance to index it
    /// as if it were a normal new photo.
    ///
    /// Filename-only matching, not (size, hash) like RemoveExactRedownloads
    /// above: a dry-run listing has no local file to hash, only the name
    /// icloudpd would have saved it under.
    /// </summary>
    private async Task<int> RemoveItemsStillInRecentlyDeleted(
        string libraryFolder, string appleUsername, string cookieDirectory, CancellationToken cancellationToken)
    {
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
                     "--album", "Recently Deleted",
                     "--only-print-filenames",
                     "--dry-run",
                     "--no-progress-bar",
                 })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return 0;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            // Best-effort — a failure here just means this run's exclusion
            // check didn't happen; nothing already downloaded is at risk,
            // and the next sync tries again.
            return 0;
        }

        var deletedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                deletedNames.Add(Path.GetFileName(trimmed));
            }
        }

        if (deletedNames.Count == 0)
        {
            return 0;
        }

        var removed = 0;
        foreach (var filePath in Directory.EnumerateFiles(libraryFolder))
        {
            if (deletedNames.Contains(Path.GetFileName(filePath)))
            {
                File.Delete(filePath);
                removed++;
            }
        }

        return removed;
    }
}
