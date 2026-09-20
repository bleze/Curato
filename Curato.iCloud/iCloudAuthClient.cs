using System.Diagnostics;
using System.Text.Json;

namespace Curato.iCloud;

public sealed record iCloudAuthResult(string Status, string? Message);

/// <summary>
/// Drives the one-time Apple ID login/2FA handshake via
/// tools/icloud-sync/auth_helper.py (PLAN.md §4) — a tiny pyicloud-based
/// script chosen over screen-scraping icloudpd's own interactive CLI
/// prompts, since it speaks a stable one-line-JSON-per-call protocol
/// instead. The Apple password is piped over stdin, never passed as a
/// process argument (so it never shows up in a process listing) and never
/// written to disk by Curato itself.
/// </summary>
public sealed class iCloudAuthClient(iCloudProcessOptions options)
{
    public Task<iCloudAuthResult> LoginAsync(
        string username, string password, string cookieDirectory, CancellationToken cancellationToken = default) =>
        RunAsync(["login", "--username", username, "--cookie-directory", cookieDirectory], password, cancellationToken);

    public Task<iCloudAuthResult> VerifyTwoFactorAsync(
        string username, string password, string code, string cookieDirectory, CancellationToken cancellationToken = default) =>
        RunAsync(
            ["verify-2fa", "--username", username, "--cookie-directory", cookieDirectory, "--code", code],
            password,
            cancellationToken);

    private async Task<iCloudAuthResult> RunAsync(string[] scriptArgs, string password, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.PythonExecutable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(options.AuthHelperScriptPath);
        foreach (var arg in scriptArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the iCloud auth helper process.");

        await process.StandardInput.WriteLineAsync(password);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        // The script prints exactly one JSON line; take the last non-blank
        // line defensively in case a dependency ever writes a stray warning
        // to stdout ahead of it.
        var line = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        if (line is null)
        {
            return new iCloudAuthResult(
                "error",
                string.IsNullOrWhiteSpace(stderr) ? "The iCloud auth helper produced no output." : stderr.Trim());
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
}
