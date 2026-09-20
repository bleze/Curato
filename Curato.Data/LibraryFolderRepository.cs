using Dapper;

namespace Curato.Data;

/// <summary>
/// The shared destination folder for content pulled in from an external
/// source (PLAN.md §4 — iCloud sync, and any future one). A single row: one
/// library folder per server, matching the single-admin-account philosophy
/// (§15). Deliberately separate from the ad-hoc "path" a manual
/// scan/organize/upload request supplies — those keep working in place on
/// whatever folder they're pointed at; this is only consulted by features
/// that need a well-known copy-into destination because there's nothing to
/// "organize in place" for content that doesn't exist locally yet.
/// </summary>
public sealed class LibraryFolderRepository(ServerDatabase database)
{
    public string? Get()
    {
        using var connection = database.OpenConnection();
        return connection.ExecuteScalar<string?>("SELECT Path FROM LibraryFolder WHERE Id = 1");
    }

    public void Set(string path)
    {
        using var connection = database.OpenConnection();
        connection.Execute("""
            INSERT INTO LibraryFolder (Id, Path) VALUES (1, @Path)
            ON CONFLICT (Id) DO UPDATE SET Path = @Path
            """, new { Path = path });
    }
}
