using Dapper;

namespace Curato.Data;

public sealed record UserRecord(long Id, string Username, string PasswordHash);

/// <summary>
/// The single admin account (PLAN.md §15 — multi-user is a possible later
/// extension, not built since it hasn't been asked for). Password hashing
/// itself lives in the API layer via ASP.NET Core Identity's
/// <c>PasswordHasher</c>; this just stores/reads the resulting hash.
/// </summary>
public sealed class UserRepository(ServerDatabase database)
{
    public bool HasAnyUser()
    {
        using var connection = database.OpenConnection();
        return connection.ExecuteScalar<long>("SELECT COUNT(*) FROM User") > 0;
    }

    public void CreateUser(string username, string passwordHash)
    {
        using var connection = database.OpenConnection();
        connection.Execute("""
            INSERT INTO User (Username, PasswordHash, CreatedAt)
            VALUES (@Username, @PasswordHash, @CreatedAt)
            """, new { Username = username, PasswordHash = passwordHash, CreatedAt = DateTime.UtcNow.ToString("O") });
    }

    public UserRecord? FindByUsername(string username)
    {
        using var connection = database.OpenConnection();
        foreach (var row in connection.Query("SELECT * FROM User WHERE Username = @Username", new { Username = username }))
        {
            return new UserRecord((long)row.Id, (string)row.Username, (string)row.PasswordHash);
        }

        return null;
    }
}
