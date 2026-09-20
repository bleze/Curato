using Dapper;

namespace Curato.Data;

public sealed record DeletedItemRecord(
    long Id, string OriginalRelativePath, string TrashRelativePath, string Kind,
    DateTime DeletedAt, DateTime PurgeAt, string Source);

/// <summary>
/// Persistence for the Recycle Bin (PLAN.md §5). Deciding *what* to delete,
/// where trashed files live on disk, and when they've aged out is
/// <c>Curato.Organizer.RecycleBinService</c>'s job; this only ever writes
/// what it's told and reads it back.
/// </summary>
public sealed class RecycleBinRepository(LibraryDatabase database)
{
    public (long Id, string RelativePath, string Kind)? GetMediaItemPathAndKind(long mediaItemId)
    {
        using var connection = database.OpenConnection();
        var row = connection.QuerySingleOrDefault(
            "SELECT Id, RelativePath, Kind FROM MediaItem WHERE Id = @Id", new { Id = mediaItemId });

        if (row is null)
        {
            return null;
        }

        string relativePath = row.RelativePath;
        string kind = row.Kind;
        return ((long)row.Id, relativePath, kind);
    }

    public void RemoveMediaItem(long mediaItemId)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        connection.Execute(
            "DELETE FROM EventMediaItem WHERE MediaItemId = @Id", new { Id = mediaItemId }, transaction);
        connection.Execute(
            "DELETE FROM MediaItem WHERE Id = @Id", new { Id = mediaItemId }, transaction);
        transaction.Commit();
    }

    public long InsertDeletedItem(
        string originalRelativePath, string trashRelativePath, string kind,
        DateTime deletedAt, DateTime purgeAt, string source)
    {
        using var connection = database.OpenConnection();
        return connection.ExecuteScalar<long>("""
            INSERT INTO DeletedItem
                (OriginalRelativePath, TrashRelativePath, Kind, DeletedAt, PurgeAt, Source)
            VALUES
                (@OriginalRelativePath, @TrashRelativePath, @Kind, @DeletedAt, @PurgeAt, @Source)
            RETURNING Id
            """, new
        {
            OriginalRelativePath = originalRelativePath,
            TrashRelativePath = trashRelativePath,
            Kind = kind,
            DeletedAt = deletedAt.ToString("O"),
            PurgeAt = purgeAt.ToString("O"),
            Source = source
        });
    }

    public IReadOnlyList<DeletedItemRecord> GetAllDeletedItems()
    {
        using var connection = database.OpenConnection();
        var items = new List<DeletedItemRecord>();
        foreach (var row in connection.Query("SELECT * FROM DeletedItem ORDER BY DeletedAt DESC"))
        {
            string originalRelativePath = row.OriginalRelativePath;
            string trashRelativePath = row.TrashRelativePath;
            string kind = row.Kind;
            string deletedAt = row.DeletedAt;
            string purgeAt = row.PurgeAt;
            string source = row.Source;

            items.Add(new DeletedItemRecord(
                (long)row.Id, originalRelativePath, trashRelativePath, kind,
                DateTime.Parse(deletedAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
                DateTime.Parse(purgeAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
                source));
        }

        return items;
    }

    public DeletedItemRecord? GetDeletedItem(long id)
    {
        using var connection = database.OpenConnection();
        var row = connection.QuerySingleOrDefault("SELECT * FROM DeletedItem WHERE Id = @Id", new { Id = id });
        if (row is null)
        {
            return null;
        }

        string originalRelativePath = row.OriginalRelativePath;
        string trashRelativePath = row.TrashRelativePath;
        string kind = row.Kind;
        string deletedAt = row.DeletedAt;
        string purgeAt = row.PurgeAt;
        string source = row.Source;

        return new DeletedItemRecord(
            (long)row.Id, originalRelativePath, trashRelativePath, kind,
            DateTime.Parse(deletedAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTime.Parse(purgeAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
            source);
    }

    public void DeleteDeletedItemRow(long id)
    {
        using var connection = database.OpenConnection();
        connection.Execute("DELETE FROM DeletedItem WHERE Id = @Id", new { Id = id });
    }
}
