using Dapper;

namespace Curato.Data;

/// <summary>
/// A user-supplied label for wherever "home" actually is — e.g. "Kirke
/// Hyllinge" rather than whatever county-level name Nominatim's
/// reverse-geocoding happens to return for that area (confirmed live: a
/// real home address resolved to just "Lejre Kommune, Danmark" at the zoom
/// level NominatimClient asks for). A single row, matching the
/// single-admin-account philosophy (§15) other single-row settings
/// (LibraryFolderRepository) already use. Optional: no row means no
/// override anywhere that consults it.
/// </summary>
public sealed class HomeLocationRepository(ServerDatabase database)
{
    public string? Get()
    {
        using var connection = database.OpenConnection();
        return connection.ExecuteScalar<string?>("SELECT Label FROM HomeLocation WHERE Id = 1");
    }

    public void Set(string label)
    {
        using var connection = database.OpenConnection();
        connection.Execute("""
            INSERT INTO HomeLocation (Id, Label) VALUES (1, @Label)
            ON CONFLICT (Id) DO UPDATE SET Label = @Label
            """, new { Label = label });
    }

    public void Clear()
    {
        using var connection = database.OpenConnection();
        connection.Execute("DELETE FROM HomeLocation WHERE Id = 1");
    }
}
