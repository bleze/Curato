using Dapper;

namespace Curato.Data;

/// <summary>
/// Persistence for the Organize engine (PLAN.md §4) — recording the undo
/// log and updating records once files have actually been moved on disk.
/// Deciding *what* to move is <c>Curato.Organizer.OrganizeService</c>'s job;
/// this only ever writes what it's told.
/// </summary>
public sealed class OrganizeRepository(LibraryDatabase database)
{
    public long CreateBatch()
    {
        using var connection = database.OpenConnection();
        return connection.ExecuteScalar<long>(
            "INSERT INTO OrganizeBatch (StartedAt) VALUES (@StartedAt) RETURNING Id",
            new { StartedAt = DateTime.UtcNow.ToString("O") });
    }

    public void RecordMove(long batchId, long mediaItemId, string oldRelativePath, string newRelativePath)
    {
        using var connection = database.OpenConnection();
        connection.Execute("""
            INSERT INTO OrganizeMove (BatchId, MediaItemId, OldRelativePath, NewRelativePath)
            VALUES (@BatchId, @MediaItemId, @OldRelativePath, @NewRelativePath)
            """, new { BatchId = batchId, MediaItemId = mediaItemId, OldRelativePath = oldRelativePath, NewRelativePath = newRelativePath });
    }

    public void UpdateMediaItemPath(long mediaItemId, string newRelativePath)
    {
        using var connection = database.OpenConnection();
        connection.Execute(
            "UPDATE MediaItem SET RelativePath = @RelativePath WHERE Id = @Id",
            new { Id = mediaItemId, RelativePath = newRelativePath });
    }

    public void SetEventFolderPath(long eventId, string folderPath)
    {
        using var connection = database.OpenConnection();
        connection.Execute(
            "UPDATE Event SET FolderPath = @FolderPath WHERE Id = @Id",
            new { Id = eventId, FolderPath = folderPath });
    }

    public void SetTripFolderPath(long tripId, string folderPath)
    {
        using var connection = database.OpenConnection();
        connection.Execute(
            "UPDATE TripGroup SET FolderPath = @FolderPath WHERE Id = @Id",
            new { Id = tripId, FolderPath = folderPath });
    }
}
