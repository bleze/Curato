using System.Collections.Concurrent;
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
    // Every controller action constructs a fresh LibraryDatabase and calls
    // EnsureSchema() before doing anything else (simplest correct thing when
    // this type was written — no shared state to worry about). That became a
    // real problem once thumbnail requests started arriving in bursts of
    // dozens (scrolling far in one jump): EnsureSchema's CREATE TABLE IF NOT
    // EXISTS is still a DDL statement even when it's a no-op, and DDL takes
    // SQLite's exclusive/reserved lock in the default rollback-journal mode —
    // so a burst of concurrent thumbnail requests, each re-running schema
    // setup, serialized on that lock instead of reading in parallel. Schema
    // only ever needs establishing once per library per process lifetime, so
    // memoize it per database path; Lazy<> (default thread-safety mode) makes
    // the one real run atomic and lets concurrent callers block on it instead
    // of racing into the DDL together.
    private static readonly ConcurrentDictionary<string, Lazy<bool>> SchemaEnsured = new();

    public string LibraryRoot { get; }
    public string DatabasePath { get; }

    public LibraryDatabase(string libraryRoot)
    {
        LibraryRoot = Path.GetFullPath(libraryRoot);
        var curatoDir = Path.Combine(LibraryRoot, ".curato");
        Directory.CreateDirectory(curatoDir);
        // The leading dot already hides it on Unix-y file managers; Windows
        // Explorer needs the actual Hidden attribute too, so a user browsing
        // the library folder doesn't run into Curato's own database/cache
        // dir. Set on every construction (idempotent, cheap) rather than
        // only right after CreateDirectory, so it's restored even if some
        // outside process (backup tool, sync client) clears it.
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(curatoDir, File.GetAttributes(curatoDir) | FileAttributes.Hidden);
        }
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
        _ = SchemaEnsured.GetOrAdd(DatabasePath, _ => new Lazy<bool>(RunSchemaSetup)).Value;
    }

    private bool RunSchemaSetup()
    {
        using var connection = OpenConnection();
        // WAL lets readers (thumbnail/full-image requests, the common case
        // during a scroll burst) proceed without blocking on whatever single
        // writer might be running a scan/organize at the same time —
        // rollback-journal mode's default takes an exclusive lock for any
        // write, which serialized exactly the concurrent-read case this file
        // most needs to be fast for.
        connection.Execute("PRAGMA journal_mode=WAL;");
        connection.Execute(Schema.CreateTables);

        foreach (var (table, column, definition) in Schema.ColumnMigrations)
        {
            if (!ColumnExists(connection, table, column))
            {
                connection.Execute($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
            }
        }

        return true;
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
