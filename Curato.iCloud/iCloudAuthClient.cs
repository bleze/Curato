using System.Diagnostics;
using System.Text.Json;

namespace Curato.iCloud;

public sealed record iCloudAuthResult(string Status, string? Message);

/// <summary>
/// Drives the one-time Apple ID login/2FA handshake via
/// tools/icloud-sync/auth_helper.py (PLAN.md §4). Holds ONE long-lived
/// Python process across the login and verify-2fa steps rather than
/// spawning a fresh process per HTTP request — confirmed live that
/// pyicloud's 2FA API needs the same PyiCloudService object/process for
/// the whole handshake (see auth_helper.py's own header comment for the
/// symptom this fixes). A single mutable pending-process field is
/// proportionate here: one iCloud account per server (PLAN.md §4), so
/// there's only ever one login attempt in flight. Registered as a
/// singleton (Curato.Api/Program.cs) for exactly that reason.
/// </summary>
public sealed class iCloudAuthClient(iCloudProcessOptions options) : IDisposable
{
    private Process? _pendingProcess;

    public async Task<iCloudAuthResult> LoginAsync(
        string username, string password, string cookieDirectory, CancellationToken cancellationToken = default)
    {
        DisposePending();

        var startInfo = new ProcessStartInfo
        {
            FileName = options.PythonExecutable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(options.AuthHelperScriptPath);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the iCloud auth helper process.");

        var result = await SendCommandAsync(
            process, new { command = "login", username, password, cookieDirectory }, cancellationToken);

        if (result.Status == "2fa_required")
        {
            // Kept alive on purpose — VerifyTwoFactorAsync below reuses this
            // exact process/PyiCloudService instance.
            _pendingProcess = process;
        }
        else
        {
            await CloseAsync(process);
        }

        return result;
    }

    public async Task<iCloudAuthResult> VerifyTwoFactorAsync(string code, CancellationToken cancellationToken = default)
    {
        if (_pendingProcess is not { HasExited: false } process)
        {
            return new iCloudAuthResult("error", "No iCloud sign-in in progress — start over.");
        }

        var result = await SendCommandAsync(process, new { command = "verify-2fa", code }, cancellationToken);

        // A wrong code leaves the same process/challenge alive so the user
        // can retry without restarting the whole login; anything else ends it.
        if (result.Status != "invalid_code")
        {
            await CloseAsync(process);
            _pendingProcess = null;
        }

        return result;
    }

    /// <summary>Abandons a pending login (e.g. the user backs out of the 2FA screen) without completing it.</summary>
    public void CancelPending() => DisposePending();

    private static async Task<iCloudAuthResult> SendCommandAsync(
        Process process, object command, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
        await process.StandardInput.FlushAsync(cancellationToken);

        var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
        if (line is null)
        {
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            return new iCloudAuthResult(
                "error",
                string.IsNullOrWhiteSpace(stderr) ? "The iCloud auth helper exited unexpectedly." : stderr.Trim());
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<JsonElement>(line);
            var status = parsed.GetProperty("status").GetString() ?? "error";
            var message = parsed.TryGetProperty("message", out var messageProp) ? messageProp.GetString() : null;
            return new iCloudAuthResult(status, message);
        }
        catch (JsonException)
        {
            return new iCloudAuthResult("error", line);
        }
    }

    private static async Task CloseAsync(Process process)
    {
        try
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token);
            }
        }
        catch
        {
            // Best-effort cleanup — the process exiting late isn't worth
            // failing the actual login/verify result over.
        }
        finally
        {
            process.Dispose();
        }
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
        }
    }

    public void Dispose() => DisposePending();
}
