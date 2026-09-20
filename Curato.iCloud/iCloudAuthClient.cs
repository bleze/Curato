using System.Diagnostics;
using System.Text;

namespace Curato.iCloud;

public sealed record iCloudAuthResult(string Status, string? Message);

/// <summary>
/// Drives the REAL `icloudpd` CLI directly for the one-time Apple ID
/// login/2FA handshake (PLAN.md §4), using its `--auth-only` mode
/// ("performs authentication, persists authentication results
/// (tokens/cookies), and exits without processing assets"). A custom
/// script using the standalone `pyicloud` PyPI package was tried first and
/// confirmed live NOT to work: `icloudpd` vendors its own separate,
/// incompatible fork (`pyicloud_ipd`) with a different session/cookie
/// format, so a session established via plain `pyicloud` was never
/// recognized by icloudpd's own sync run (it just kept re-prompting for
/// 2FA every time). Driving the real CLI guarantees byte-for-byte
/// compatibility, since it's the exact same code path the sync step
/// (<see cref="iCloudSyncRunner"/>) already uses.
///
/// The password is passed via `--password` (a command-line argument —
/// visible to another user on the same machine via a process listing for
/// icloudpd's brief run) rather than piped interactively: icloudpd's own
/// interactive password prompt uses Python's `getpass.getpass()`, which
/// tries to read directly from the console device and behaves unreliably
/// for a background child process with no attached console — exactly the
/// context Curato.Api spawns it in. `--password` is icloudpd's own
/// documented non-interactive alternative for this reason. The 2FA/
/// validation code step, by contrast, uses plain `input()` (confirmed in
/// icloudpd's source), which respects redirected stdin normally on every
/// platform — piped the same way as any ordinary CLI tool.
/// </summary>
public sealed class iCloudAuthClient(iCloudProcessOptions options) : IDisposable
{
    // Common to both icloudpd's 2FA prompt ("Please enter two-factor
    // authentication code or device index...") and its older 2SA prompt
    // ("Please enter two-step authentication code"), so one substring
    // check covers both variants without needing the exact wording.
    private const string TwoFactorPromptMarker = "authentication code";
    private static readonly TimeSpan PromptWaitTimeout = TimeSpan.FromSeconds(30);

    private Process? _pendingProcess;
    private StringBuilder? _pendingOutput;

    public async Task<iCloudAuthResult> LoginAsync(
        string username, string password, string cookieDirectory, CancellationToken cancellationToken = default)
    {
        DisposePending();

        var startInfo = new ProcessStartInfo
        {
            FileName = options.IcloudpdExecutable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[]
                 {
                     "--username", username,
                     "--password", password,
                     "--cookie-directory", cookieDirectory,
                     "--auth-only",
                     "--no-progress-bar",
                 })
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start icloudpd.");

        var output = new StringBuilder();
        var sawPrompt = await ReadUntilPromptOrExitAsync(process, output, TwoFactorPromptMarker, PromptWaitTimeout, cancellationToken);

        if (sawPrompt)
        {
            // Kept alive on purpose — VerifyTwoFactorAsync below writes the
            // code to this same process's stdin.
            _pendingProcess = process;
            _pendingOutput = output;
            return new iCloudAuthResult("2fa_required", null);
        }

        return await FinishAsync(process, output, successStatus: "trusted", cancellationToken);
    }

    public async Task<iCloudAuthResult> VerifyTwoFactorAsync(string code, CancellationToken cancellationToken = default)
    {
        if (_pendingProcess is not { HasExited: false } process || _pendingOutput is not { } output)
        {
            return new iCloudAuthResult("error", "No iCloud sign-in in progress — start over.");
        }

        await process.StandardInput.WriteLineAsync(code);
        await process.StandardInput.FlushAsync(cancellationToken);

        var result = await FinishAsync(process, output, successStatus: "ok", cancellationToken);
        _pendingProcess = null;
        _pendingOutput = null;
        return result;
    }

    /// <summary>Abandons a pending login (e.g. the user backs out of the 2FA screen) without completing it.</summary>
    public void CancelPending() => DisposePending();

    /// <summary>
    /// Reads process output incrementally (not line-buffered — icloudpd's
    /// prompts don't end with a newline until answered) until either
    /// <paramref name="marker"/> appears, the process exits, or the
    /// timeout elapses. Returns true only if the marker was seen while the
    /// process is still running (i.e. genuinely waiting on stdin for it).
    /// </summary>
    private static async Task<bool> ReadUntilPromptOrExitAsync(
        Process process, StringBuilder output, string marker, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var buffer = new char[512];

        while (!process.HasExited)
        {
            Task<int> readTask;
            try
            {
                readTask = process.StandardOutput.ReadAsync(buffer.AsMemory(), linkedCts.Token).AsTask();
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            var exitTask = process.WaitForExitAsync(linkedCts.Token);
            var completed = await Task.WhenAny(readTask, exitTask);

            if (completed != readTask)
            {
                continue; // process exited — loop condition below will end it
            }

            int read;
            try
            {
                read = await readTask;
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (read == 0)
            {
                break; // stdout closed without the marker appearing
            }

            output.Append(buffer, 0, read);
            if (output.ToString().Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<iCloudAuthResult> FinishAsync(
        Process process, StringBuilder output, string successStatus, CancellationToken cancellationToken)
    {
        var remaining = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        output.Append(remaining);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var success = process.ExitCode == 0;
        process.Dispose();

        if (success)
        {
            return new iCloudAuthResult(successStatus, null);
        }

        var message = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : output.ToString().Trim();
        return new iCloudAuthResult("error", string.IsNullOrWhiteSpace(message) ? "Sign-in failed." : message);
    }

    private void DisposePending()
    {
        if (_pendingProcess is null)
        {
            return;
        }

        try
        {
            if (!_pendingProcess.HasExited)
            {
                _pendingProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Already exited or inaccessible — nothing more to do.
        }
        finally
        {
            _pendingProcess.Dispose();
            _pendingProcess = null;
            _pendingOutput = null;
        }
    }

    public void Dispose() => DisposePending();
}
