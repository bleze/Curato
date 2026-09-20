using System.Data;
using Dapper;

namespace Curato.Data;

public sealed record DeletedItemRecord(
    long Id, string OriginalRelativePath, string TrashRelativePath, string Kind,
    DateTime DeletedAt, DateTime PurgeAt, string Source);

/// <summary>An Event left with zero MediaItems after a delete — the caller can also clean up its now-empty on-disk folder.</summary>
public sealed record EmptiedEvent(long Id, string? FolderPath);

/// <summary>
/// Persistence for the Recycle Bin (PLAN.md §5). Deciding *what* to delete,
/// where trashed files live on disk, and when they've aged out is
/// <c>Curato.Organizer.RecycleBinService</c>'s job; this only ever writes
/// what it's told and reads it back.
/// </summary>
public sealed class RecycleBinRepository(LibraryDatabase database)
{
    public (long Id, string RelativePath, string Kind, long? LivePhotoVideoItemId)? GetMediaItemPathAndKind(long mediaItemId)
    {
        using var connection = database.OpenConnection();
        var row = connection.QuerySingleOrDefault(
            "SELECT Id, RelativePath, Kind, LivePhotoVideoItemId FROM MediaItem WHERE Id = @Id", new { Id = mediaItemId });

        if (row is null)
        {
            return null;
        }

        string relativePath = row.RelativePath;
        string kind = row.Kind;
        long? livePhotoVideoItemId = row.LivePhotoVideoItemId;
        return ((long)row.Id, relativePath, kind, livePhotoVideoItemId);
    }

    /// <summary>
    /// Cleans up every table with a foreign key into MediaItem (Schema.cs)
    /// before dropping the row itself — confirmed live that only
    /// EventMediaItem was ever handled here, so deleting any item that
    /// happened to have a Tag, Edit, Face, or Picasa-import marker (or was
    /// referenced by another OrganizeMove log entry) failed outright with a
    /// raw SQLite "FOREIGN KEY constraint failed", no indication of which
    /// table. LivePhotoVideoItemId (MediaItem's own self-reference) is
    /// deliberately handled by clearing the pointer rather than deleting
    /// the referencing row — that row is a separate photo the caller isn't
    /// deleting, it just no longer has a paired Live Photo video.
    ///
    /// Also removes any Event left with zero members by this delete (and, in
    /// turn, any TripGroup left with zero Events) — confirmed live that an
    /// Event surviving with no photos in it just sits in the library looking
    /// like a broken/empty folder. Returns the emptied Events so the caller
    /// (which owns filesystem access, not this class) can also remove their
    /// now-empty on-disk folder.
    /// </summary>
    public IReadOnlyList<EmptiedEvent> RemoveMediaItem(long mediaItemId)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        connection.Execute(
            "UPDATE MediaItem SET LivePhotoVideoItemId = NULL WHERE LivePhotoVideoItemId = @Id",
            new { Id = mediaItemId }, transaction);

        var candidateEventIds = connection.Query<long>(
            "SELECT EventId FROM EventMediaItem WHERE MediaItemId = @Id", new { Id = mediaItemId }, transaction).ToList();

        connection.Execute(
            "DELETE FROM EventMediaItem WHERE MediaItemId = @Id", new { Id = mediaItemId }, transaction);
        connection.Execute(
            "DELETE FROM MediaItemTag WHERE MediaItemId = @Id", new { Id = mediaItemId }, transaction);
        connection.Execute(
            "DELETE FROM Edit WHERE MediaItemId = @Id", new { Id = mediaItemId }, transaction);
        connection.Execute(
            "DELETE FROM Face WHERE MediaItemId = @Id", new { Id = mediaItemId }, transaction);
        connection.Execute(
            "DELETE FROM PicasaImportedMediaItem WHERE MediaItemId = @Id", new { Id = mediaItemId }, transaction);
        connection.Execute(
            "DELETE FROM OrganizeMove WHERE MediaItemId = @Id", new { Id = mediaItemId }, transaction);
        connection.Execute(
            "DELETE FROM MediaItem WHERE Id = @Id", new { Id = mediaItemId }, transaction);

        var emptiedEvents = RemoveNowEmptyEvents(connection, transaction, candidateEventIds);

        transaction.Commit();
        return emptiedEvents;
    }

    private static List<EmptiedEvent> RemoveNowEmptyEvents(
        IDbConnection connection, IDbTransaction transaction, IReadOnlyList<long> candidateEventIds)
    {
        var emptiedEvents = new List<EmptiedEvent>();
        var candidateTripIds = new List<long>();

        foreach (var eventId in candidateEventIds)
        {
            var remainingMembers = connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM EventMediaItem WHERE EventId = @EventId", new { EventId = eventId }, transaction);
            if (remainingMembers > 0)
            {
                continue;
            }

            var row = connection.QuerySingleOrDefault(
                "SELECT FolderPath, TripGroupId FROM Event WHERE Id = @EventId", new { EventId = eventId }, transaction);
            if (row is null)
            {
                continue;
            }

            string? folderPath = row.FolderPath;
            long? tripGroupId = row.TripGroupId;
            if (tripGroupId is { } tripId)
            {
                candidateTripIds.Add(tripId);
            }

            connection.Execute("DELETE FROM Event WHERE Id = @EventId", new { EventId = eventId }, transaction);
            emptiedEvents.Add(new EmptiedEvent(eventId, folderPath));
        }

        foreach (var tripId in candidateTripIds.Distinct())
        {
            var remainingEvents = connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM Event WHERE TripGroupId = @TripId", new { TripId = tripId }, transaction);
            if (remainingEvents == 0)
            {
                connection.Execute("DELETE FROM TripGroup WHERE Id = @TripId", new { TripId = tripId }, transaction);
            }
        }

        return emptiedEvents;
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
