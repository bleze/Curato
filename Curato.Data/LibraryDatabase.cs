using Microsoft.Data.Sqlite;

namespace Curato.Data;

/// <summary>
/// PLAN.md §2 — the database lives inside a `.curato/` folder at the root of
/// the library it indexes (`&lt;LibraryRoot&gt;/.curato/library.db`), not in a
/// per-machine AppData path, so redeploying the server against the same
/// folder recovers everything.
/// </summary>
public sealed class LibraryDatabase
{
    public string LibraryRoot { get; }
    public string DatabasePath { get; }

    public LibraryDatabase(string libraryRoot)
    {
        LibraryRoot = Path.GetFullPath(libraryRoot);
        var curatoDir = Path.Combine(LibraryRoot, ".curato");
        Directory.CreateDirectory(curatoDir);
        DatabasePath = Path.Combine(curatoDir, "library.db");
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
        connection.Execute(Schema.CreateTables);

        foreach (var (table, column, definition) in Schema.ColumnMigrations)
        {
            if (!ColumnExists(connection, table, column))
            {
                connection.Execute($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
            }
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

file static class SqliteConnectionExtensions
{
    public static void Execute(this SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
