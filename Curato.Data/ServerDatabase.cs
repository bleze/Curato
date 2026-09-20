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

    public ServerDatabase(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
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
            """;
        command.ExecuteNonQuery();
    }
}
