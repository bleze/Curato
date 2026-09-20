using Dapper;

namespace Curato.Data;

public sealed record iCloudSyncConfig(
    string AppleUsername,
    string CookieDirectory,
    int IntervalMinutes,
    bool Enabled,
    int LookbackDays,
    DateOnly? SinceDate,
    int RecentItemCap,
    DateTime? LastSyncAt,
    string? LastSyncStatus,
    string? LastSyncMessage,
    int? LastSyncItemCount);

/// <summary>
/// One iCloud account synced into the shared <see cref="LibraryFolderRepository"/>
/// destination (PLAN.md §4). A single row — one account per server, same
/// scale as the single admin account (§15). The Apple password is never
/// stored here or anywhere; only pyicloud's own session/cookie files
/// (<see cref="iCloudSyncConfig.CookieDirectory"/>, plain files on disk,
/// same trust boundary as this database) survive the login/2FA handshake.
///
/// Two independent scope controls, both mapped onto icloudpd flags
/// (<c>iCloudSyncRunner</c>) rather than filtered client-side: <see
/// cref="iCloudSyncConfig.SinceDate"/> (a fixed calendar date — e.g. "since
/// my last manual sync 2.5 years ago") when set, otherwise <see
/// cref="iCloudSyncConfig.LookbackDays"/> (a rolling window relative to
/// now, recalculated every sync) bounds *what gets downloaded*; <see
/// cref="iCloudSyncConfig.RecentItemCap"/> bounds *how much gets examined
/// at all* regardless of date (icloudpd's `--recent`), a separate
/// performance ceiling since the date filter alone still walks its entire
/// slice checking each item's date one by one.
/// </summary>
public sealed class iCloudSyncRepository(ServerDatabase database)
{
    public iCloudSyncConfig? Get()
    {
        using var connection = database.OpenConnection();
        foreach (var row in connection.Query("SELECT * FROM iCloudSync WHERE Id = 1"))
        {
            return ToConfig(row);
        }

        return null;
    }

    public void Save(
        string appleUsername, string cookieDirectory, int intervalMinutes, bool enabled,
        int lookbackDays, DateOnly? sinceDate, int recentItemCap)
    {
        using var connection = database.OpenConnection();
        connection.Execute("""
            INSERT INTO iCloudSync (Id, AppleUsername, CookieDirectory, IntervalMinutes, Enabled, LookbackDays, SinceDate, RecentItemCap)
            VALUES (1, @AppleUsername, @CookieDirectory, @IntervalMinutes, @Enabled, @LookbackDays, @SinceDate, @RecentItemCap)
            ON CONFLICT (Id) DO UPDATE SET
                AppleUsername = @AppleUsername,
                CookieDirectory = @CookieDirectory,
                IntervalMinutes = @IntervalMinutes,
                Enabled = @Enabled,
                LookbackDays = @LookbackDays,
                SinceDate = @SinceDate,
                RecentItemCap = @RecentItemCap
            """, new
        {
            AppleUsername = appleUsername,
            CookieDirectory = cookieDirectory,
            IntervalMinutes = intervalMinutes,
            Enabled = enabled,
            LookbackDays = lookbackDays,
            SinceDate = sinceDate?.ToString("O"),
            RecentItemCap = recentItemCap
        });
    }

    public void UpdateSettings(int intervalMinutes, bool enabled, int lookbackDays, DateOnly? sinceDate, int recentItemCap)
    {
        using var connection = database.OpenConnection();
        connection.Execute("""
            UPDATE iCloudSync
            SET IntervalMinutes = @IntervalMinutes, Enabled = @Enabled, LookbackDays = @LookbackDays,
                SinceDate = @SinceDate, RecentItemCap = @RecentItemCap
            WHERE Id = 1
            """, new
        {
            IntervalMinutes = intervalMinutes,
            Enabled = enabled,
            LookbackDays = lookbackDays,
            SinceDate = sinceDate?.ToString("O"),
            RecentItemCap = recentItemCap
        });
    }

    public void RecordSyncResult(DateTime syncedAt, string status, string? message, int itemCount)
    {
        using var connection = database.OpenConnection();
        connection.Execute("""
            UPDATE iCloudSync
            SET LastSyncAt = @SyncedAt, LastSyncStatus = @Status, LastSyncMessage = @Message, LastSyncItemCount = @ItemCount
            WHERE Id = 1
            """, new
        {
            SyncedAt = syncedAt.ToString("O"),
            Status = status,
            Message = message,
            ItemCount = itemCount
        });
    }

    public void Delete()
    {
        using var connection = database.OpenConnection();
        connection.Execute("DELETE FROM iCloudSync WHERE Id = 1");
    }

    private static iCloudSyncConfig ToConfig(dynamic row) => new(
        AppleUsername: (string)row.AppleUsername,
        CookieDirectory: (string)row.CookieDirectory,
        IntervalMinutes: (int)(long)row.IntervalMinutes,
        Enabled: (long)row.Enabled != 0,
        LookbackDays: (int)(long)row.LookbackDays,
        SinceDate: row.SinceDate is null ? null : DateOnly.Parse((string)row.SinceDate),
        RecentItemCap: (int)(long)row.RecentItemCap,
        LastSyncAt: row.LastSyncAt is null ? null : DateTime.Parse((string)row.LastSyncAt).ToUniversalTime(),
        LastSyncStatus: (string?)row.LastSyncStatus,
        LastSyncMessage: (string?)row.LastSyncMessage,
        LastSyncItemCount: row.LastSyncItemCount is null ? null : (int)(long)row.LastSyncItemCount);
}
