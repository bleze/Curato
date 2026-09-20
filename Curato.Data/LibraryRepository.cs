using Curato.Core;
using Dapper;

namespace Curato.Data;

/// <summary>
/// First, minimal persistence slice for Phase 1: save a scan's MediaItems and
/// the Events/TripGroups clustered from them. Re-scanning the same folder is
/// idempotent on <see cref="MediaItem.RelativePath"/> (insert-or-ignore, then
/// read back the id) — good enough for this milestone; richer
/// changed-file/undo handling belongs to the real import pipeline (§4).
/// </summary>
public sealed class LibraryRepository(LibraryDatabase database)
{
    public IReadOnlyList<MediaItem> UpsertMediaItems(IEnumerable<MediaItem> items)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var saved = new List<MediaItem>();
        foreach (var item in items)
        {
            connection.Execute("""
                INSERT INTO MediaItem
                    (RelativePath, Kind, FileSizeBytes, QuickHash, ContentHash,
                     TakenAt, TakenAtSource, Width, Height, DurationMs, ImportedAt)
                VALUES
                    (@RelativePath, @Kind, @FileSizeBytes, @QuickHash, @ContentHash,
                     @TakenAt, @TakenAtSource, @Width, @Height, @DurationMs, @ImportedAt)
                ON CONFLICT (RelativePath) DO NOTHING
                """, ToRow(item), transaction);

            var id = connection.ExecuteScalar<long>(
                "SELECT Id FROM MediaItem WHERE RelativePath = @RelativePath",
                new { item.RelativePath }, transaction);

            item.Id = id;
            saved.Add(item);
        }

        transaction.Commit();
        return saved;
    }

    public void SaveEvents(IReadOnlyList<Event> events)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var @event in events)
        {
            @event.Id = connection.ExecuteScalar<long>("""
                INSERT INTO Event (StartAt, EndAt, Title, TitleIsAuto, FolderPath, TripGroupId, NamingMode)
                VALUES (@StartAt, @EndAt, @Title, @TitleIsAuto, @FolderPath, @TripGroupId, @NamingMode)
                RETURNING Id
                """, ToRow(@event), transaction);

            var sortOrder = 0;
            foreach (var mediaItemId in @event.MediaItemIds)
            {
                connection.Execute("""
                    INSERT INTO EventMediaItem (EventId, MediaItemId, SortOrder)
                    VALUES (@EventId, @MediaItemId, @SortOrder)
                    """, new { EventId = @event.Id, MediaItemId = mediaItemId, SortOrder = sortOrder++ }, transaction);
            }
        }

        transaction.Commit();
    }

    public void SaveTripGroups(IReadOnlyList<TripGroup> tripGroups)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var trip in tripGroups)
        {
            trip.Id = connection.ExecuteScalar<long>("""
                INSERT INTO TripGroup (StartDate, EndDate, Title, TitleIsAuto, FolderPath, NamingMode)
                VALUES (@StartDate, @EndDate, @Title, @TitleIsAuto, @FolderPath, @NamingMode)
                RETURNING Id
                """, ToRow(trip), transaction);

            foreach (var eventId in trip.EventIds)
            {
                connection.Execute(
                    "UPDATE Event SET TripGroupId = @TripId WHERE Id = @EventId",
                    new { TripId = trip.Id, EventId = eventId }, transaction);
            }
        }

        transaction.Commit();
    }

    private static object ToRow(MediaItem item) => new
    {
        item.RelativePath,
        Kind = item.Kind.ToString(),
        item.FileSizeBytes,
        item.QuickHash,
        item.ContentHash,
        TakenAt = item.TakenAt.ToString("O"),
        TakenAtSource = item.TakenAtSource.ToString(),
        item.Width,
        item.Height,
        item.DurationMs,
        ImportedAt = item.ImportedAt.ToString("O")
    };

    private static object ToRow(Event @event) => new
    {
        StartAt = @event.StartAt.ToString("O"),
        EndAt = @event.EndAt.ToString("O"),
        @event.Title,
        @event.TitleIsAuto,
        @event.FolderPath,
        @event.TripGroupId,
        NamingMode = @event.NamingMode.ToString()
    };

    private static object ToRow(TripGroup trip) => new
    {
        StartDate = trip.StartDate.ToString("O"),
        EndDate = trip.EndDate.ToString("O"),
        trip.Title,
        trip.TitleIsAuto,
        trip.FolderPath,
        NamingMode = trip.NamingMode.ToString()
    };
}
