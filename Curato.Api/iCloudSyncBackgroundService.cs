using Curato.iCloud;

namespace Curato.Api;

/// <summary>
/// PLAN.md §4 — periodic iCloud sync. The first hosted/background service in
/// this codebase (nothing like it existed before). Wakes on a short,
/// fixed cadence just to *check* whether a sync is due — the actual sync
/// interval is per-account config (iCloudSyncController's settings
/// endpoint), read fresh from ServerDatabase on every check via
/// <see cref="iCloudSyncRunner.SyncIfConfiguredAsync"/>, which also handles
/// being a no-op when iCloud sync isn't configured/enabled or isn't due yet.
/// </summary>
public sealed class iCloudSyncBackgroundService(
    iCloudSyncRunner syncRunner, ILogger<iCloudSyncBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await syncRunner.SyncIfConfiguredAsync(ignoreSchedule: false, stoppingToken);
                if (result is { Success: false })
                {
                    logger.LogWarning("iCloud background sync failed: {Message}", result.ErrorMessage);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "iCloud background sync pass threw unexpectedly.");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }
}
