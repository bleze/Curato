using Curato.Data;

namespace Curato.Tests;

public class ServerSettingsRepositoryTests
{
    [Fact]
    public void LibraryFolder_round_trips_and_defaults_to_unset()
    {
        WithTempServerDatabase(database =>
        {
            var repository = new LibraryFolderRepository(database);
            Assert.Null(repository.Get());

            repository.Set("D:/Photos");
            Assert.Equal("D:/Photos", repository.Get());

            repository.Set("D:/OtherPhotos");
            Assert.Equal("D:/OtherPhotos", repository.Get());
        });
    }

    [Fact]
    public void ICloudSync_round_trips_and_records_sync_results()
    {
        WithTempServerDatabase(database =>
        {
            var repository = new iCloudSyncRepository(database);
            Assert.Null(repository.Get());

            repository.Save("me@example.com", "/data/icloud-session", intervalMinutes: 360, enabled: true);
            var saved = repository.Get();
            Assert.NotNull(saved);
            Assert.Equal("me@example.com", saved!.AppleUsername);
            Assert.Equal("/data/icloud-session", saved.CookieDirectory);
            Assert.Equal(360, saved.IntervalMinutes);
            Assert.True(saved.Enabled);
            Assert.Null(saved.LastSyncAt);

            var syncedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            repository.RecordSyncResult(syncedAt, "Success", message: null, itemCount: 7);
            var afterSync = repository.Get();
            Assert.Equal(syncedAt, afterSync!.LastSyncAt);
            Assert.Equal("Success", afterSync.LastSyncStatus);
            Assert.Equal(7, afterSync.LastSyncItemCount);

            repository.UpdateSettings(intervalMinutes: 60, enabled: false);
            Assert.Equal(60, repository.Get()!.IntervalMinutes);
            Assert.False(repository.Get()!.Enabled);

            repository.Delete();
            Assert.Null(repository.Get());
        });
    }

    private static void WithTempServerDatabase(Action<ServerDatabase> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-server-settings-test-");
        try
        {
            var database = new ServerDatabase(tempDir.FullName);
            database.EnsureSchema();
            test(database);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
