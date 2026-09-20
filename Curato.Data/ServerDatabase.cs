using Dapper;
using Microsoft.Data.Sqlite;

namespace Curato.Data;

/// <summary>
/// Server-wide config (currently: the single admin account, PLAN.md §15) —
/// distinct from a library's own `.curato/library.db` (§2), since auth is a
/// property of the server, not of any one library. Lives in the server's own
/// persistent data directory (the Docker volume mounted at `/data` per the
/// Dockerfile, or a local `./data` folder for dev) so it survives container
/// recreation the same way a library's database survives a redeploy.
/// </summary>
public sealed class ServerDatabase
{
    public string DatabasePath { get; }

    /// <summary>
    /// The server's persistent data directory itself (not just server.db) —
    /// needed by anything that stores its own files alongside the database
    /// rather than rows in it, e.g. the iCloud sync feature's pyicloud
    /// session/cookie directory (PLAN.md §4), which pyicloud manages as
    /// plain files, not something a SQLite row can hold.
    /// </summary>
    public string DataDirectory { get; }

    public ServerDatabase(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        DataDirectory = dataDirectory;
        DatabasePath = Path.Combine(dataDirectory, "server.db");
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        return connection;
    }

    public void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS User (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Username TEXT NOT NULL UNIQUE,
                PasswordHash TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );

            -- PLAN.md §4: the shared destination folder for content pulled in
            -- from an external source (iCloud sync, and any future one) —
            -- deliberately separate from the ad-hoc "path" a manual
            -- scan/organize/upload request supplies, since those keep
            -- working in place on whatever folder they're pointed at. A
            -- single row, matching the single-admin-account philosophy
            -- (§15: personal/family scale, not multi-tenant).
            CREATE TABLE IF NOT EXISTS LibraryFolder (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                Path TEXT NOT NULL
            );

            -- PLAN.md §4: one iCloud account synced into the LibraryFolder
            -- above. The Apple password itself is never stored here (or
            -- anywhere) — only pyicloud's own session/cookie files
            -- (CookieDirectory, plain files on disk, same trust boundary as
            -- this database) survive the login/2FA handshake.
            CREATE TABLE IF NOT EXISTS iCloudSync (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                AppleUsername TEXT NOT NULL,
                CookieDirectory TEXT NOT NULL,
                IntervalMinutes INTEGER NOT NULL,
                Enabled INTEGER NOT NULL,
                LookbackDays INTEGER NOT NULL DEFAULT 365,
                SinceDate TEXT NULL,
                RecentItemCap INTEGER NOT NULL DEFAULT 2000,
                LastSyncAt TEXT NULL,
                LastSyncStatus TEXT NULL,
                LastSyncMessage TEXT NULL,
                LastSyncItemCount INTEGER NULL
            );
            """;
        command.ExecuteNonQuery();

        // SQLite has no `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` — same
        // idempotent-migration approach as LibraryDatabase (Schema.cs), just
        // inline here since server.db's schema is small enough not to need
        // its own separate migrations list.
        if (!ColumnExists(connection, "iCloudSync", "LookbackDays"))
        {
            connection.Execute("ALTER TABLE iCloudSync ADD COLUMN LookbackDays INTEGER NOT NULL DEFAULT 365");
        }

        if (!ColumnExists(connection, "iCloudSync", "SinceDate"))
        {
            connection.Execute("ALTER TABLE iCloudSync ADD COLUMN SinceDate TEXT NULL");
        }

        if (!ColumnExists(connection, "iCloudSync", "RecentItemCap"))
        {
            connection.Execute("ALTER TABLE iCloudSync ADD COLUMN RecentItemCap INTEGER NOT NULL DEFAULT 2000");
        }
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        var nameOrdinal = reader.GetOrdinal("name");
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(nameOrdinal), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
