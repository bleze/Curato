using Curato.Data;
using Curato.iCloud;
using Microsoft.AspNetCore.Mvc;

namespace Curato.Api.Controllers;

/// <summary>
/// PLAN.md §4 — direct iCloud integration (reversing the plan's earlier
/// "official sync client only" decision, given the official path was
/// confirmed stripping Exif and has no equivalent at all on the primary
/// Synology/Linux deployment target). Every route targets the single shared
/// "library folder" setting (<see cref="SettingsController"/>) rather than
/// a client-supplied path — there's exactly one iCloud account per server,
/// same personal/family scale as the single admin account (§15).
///
/// Protected by the fallback auth policy (§15) — requires a logged-in session.
/// </summary>
[ApiController]
[Route("api/icloud-sync")]
public sealed class iCloudSyncController(
    ServerDatabase database,
    iCloudAuthClient authClient,
    iCloudSyncRunner syncRunner) : ControllerBase
{
    // Fixed, not user-configurable: one iCloud account per server, so there's
    // no need for more than one place pyicloud's session files can live.
    private const string CookieDirectoryName = "icloud-session";

    public sealed record LoginRequest(string Username, string Password);
    public sealed record LoginResponse(bool RequiresTwoFactor, string? Error);
    public sealed record VerifyTwoFactorRequest(string Username, string Code);
    public sealed record UpdateSettingsRequest(int IntervalMinutes, bool Enabled);
    public sealed record StatusResponse(
        bool LibraryFolderConfigured,
        bool Connected,
        string? AppleUsername,
        int? IntervalMinutes,
        bool? Enabled,
        DateTime? LastSyncAt,
        string? LastSyncStatus,
        string? LastSyncMessage,
        int? LastSyncItemCount);
    public sealed record SyncNowResponse(bool Success, string? ErrorMessage, int ItemsDownloaded, int NewEventCount);

    [HttpGet("status")]
    public ActionResult<StatusResponse> GetStatus()
    {
        var libraryFolder = new LibraryFolderRepository(database).Get();
        var config = new iCloudSyncRepository(database).Get();

        return Ok(new StatusResponse(
            LibraryFolderConfigured: libraryFolder is not null,
            Connected: config is not null,
            AppleUsername: config?.AppleUsername,
            IntervalMinutes: config?.IntervalMinutes,
            Enabled: config?.Enabled,
            LastSyncAt: config?.LastSyncAt,
            LastSyncStatus: config?.LastSyncStatus,
            LastSyncMessage: config?.LastSyncMessage,
            LastSyncItemCount: config?.LastSyncItemCount));
    }

    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request)
    {
        var cookieDirectory = GetCookieDirectory();
        Directory.CreateDirectory(cookieDirectory);

        var result = await authClient.LoginAsync(request.Username, request.Password, cookieDirectory);
        return result.Status switch
        {
            "trusted" => Ok(new LoginResponse(RequiresTwoFactor: false, Error: null)),
            "2fa_required" => Ok(new LoginResponse(RequiresTwoFactor: true, Error: null)),
            _ => Ok(new LoginResponse(RequiresTwoFactor: false, Error: result.Message ?? "Sign-in failed."))
        };
    }

    [HttpPost("verify-2fa")]
    public async Task<IActionResult> VerifyTwoFactor([FromBody] VerifyTwoFactorRequest request)
    {
        var libraryFolder = new LibraryFolderRepository(database).Get();
        if (libraryFolder is null)
        {
            return BadRequest("Set a library folder in Settings before connecting iCloud.");
        }

        var result = await authClient.VerifyTwoFactorAsync(request.Code);
        if (result.Status != "ok")
        {
            return result.Status == "invalid_code"
                ? BadRequest("That code wasn't accepted.")
                : BadRequest(result.Message ?? "Verification failed.");
        }

        var cookieDirectory = GetCookieDirectory();

        new iCloudSyncRepository(database).Save(request.Username, cookieDirectory, intervalMinutes: 360, enabled: true);
        return Ok();
    }

    /// <summary>Backs out of a pending login (e.g. the user leaves the 2FA screen) without completing it.</summary>
    [HttpPost("cancel-login")]
    public IActionResult CancelLogin()
    {
        authClient.CancelPending();
        return Ok();
    }

    [HttpPost("sync-now")]
    public async Task<ActionResult<SyncNowResponse>> SyncNow()
    {
        var result = await syncRunner.SyncIfConfiguredAsync(ignoreSchedule: true);
        if (result is null)
        {
            return BadRequest("Connect iCloud (and set a library folder) first.");
        }

        return Ok(new SyncNowResponse(
            result.Success, result.ErrorMessage, result.ItemsDownloaded, result.ScanResult?.NewAutoOrganizedEventCount ?? 0));
    }

    [HttpPut("settings")]
    public IActionResult UpdateSettings([FromBody] UpdateSettingsRequest request)
    {
        var repository = new iCloudSyncRepository(database);
        if (repository.Get() is null)
        {
            return BadRequest("Not connected.");
        }

        repository.UpdateSettings(request.IntervalMinutes, request.Enabled);
        return Ok();
    }

    [HttpPost("disconnect")]
    public IActionResult Disconnect()
    {
        var repository = new iCloudSyncRepository(database);
        var config = repository.Get();
        repository.Delete();

        if (config is not null && Directory.Exists(config.CookieDirectory))
        {
            Directory.Delete(config.CookieDirectory, recursive: true);
        }

        return Ok();
    }

    private string GetCookieDirectory() => Path.Combine(database.DataDirectory, CookieDirectoryName);
}
