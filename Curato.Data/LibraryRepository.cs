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

    /// <summary>
    /// Every MediaItemId that already belongs to some Event — re-scanning
    /// must never re-cluster these into a *second* Event alongside the one
    /// they're already in (that's the "scanning the same folder twice makes
    /// duplicates" bug).
    /// </summary>
    public HashSet<long> GetMediaItemIdsAlreadyInAnyEvent()
    {
        using var connection = database.OpenConnection();
        var ids = new HashSet<long>();
        foreach (var row in connection.Query("SELECT DISTINCT MediaItemId FROM EventMediaItem"))
        {
            ids.Add((long)row.MediaItemId);
        }

        return ids;
    }

    /// <summary>
    /// Existing Adopted Events keyed by folder path, each with its current
    /// member MediaItemIds — so a re-scan can add newly-arrived files in an
    /// already-adopted folder to the *same* Event instead of creating a
    /// duplicate one for that folder.
    /// </summary>
    public Dictionary<string, (long Id, List<long> MediaItemIds)> GetAdoptedEventsByFolderPath()
    {
        using var connection = database.OpenConnection();

        var links = new List<(long EventId, long MediaItemId)>();
        foreach (var row in connection.Query("SELECT * FROM EventMediaItem"))
        {
            links.Add(((long)row.EventId, (long)row.MediaItemId));
        }

        var mediaItemIdsByEvent = links.ToLookup(l => l.EventId, l => l.MediaItemId);

        var result = new Dictionary<string, (long, List<long>)>();
        foreach (var row in connection.Query("SELECT Id, FolderPath FROM Event WHERE NamingMode = 'Adopted'"))
        {
            long id = row.Id;
            string? folderPath = row.FolderPath;
            if (folderPath is not null)
            {
                result[folderPath] = (id, mediaItemIdsByEvent[id].ToList());
            }
        }

        return result;
    }

    /// <summary>
    /// Adds newly-found items to an already-existing Event (from a folder
    /// that was already adopted, or an auto-organized moment new files
    /// arrived into) and widens its StartAt/EndAt to cover them, rather than
    /// creating a second Event for the same folder/moment.
    /// </summary>
    public void AddMediaItemsToEvent(long eventId, IReadOnlyList<MediaItem> newItems)
    {
        if (newItems.Count == 0)
        {
            return;
        }

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var nextSortOrder = connection.ExecuteScalar<int>(
            "SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM EventMediaItem WHERE EventId = @EventId",
            new { EventId = eventId }, transaction);

        foreach (var item in newItems)
        {
            connection.Execute("""
                INSERT INTO EventMediaItem (EventId, MediaItemId, SortOrder)
                VALUES (@EventId, @MediaItemId, @SortOrder)
                """, new { EventId = eventId, MediaItemId = item.Id, SortOrder = nextSortOrder++ }, transaction);
        }

        var newStartAt = newItems.Min(i => i.TakenAt);
        var newEndAt = newItems.Max(i => i.TakenAt);
        connection.Execute("""
            UPDATE Event
            SET StartAt = MIN(StartAt, @NewStartAt), EndAt = MAX(EndAt, @NewEndAt)
            WHERE Id = @EventId
            """, new
        {
            EventId = eventId,
            NewStartAt = newStartAt.ToString("O"),
            NewEndAt = newEndAt.ToString("O")
        }, transaction);

        transaction.Commit();
    }

    /// <summary>
    /// Un-does auto-formed (NamingMode = CuratoManaged) TripGroups: their
    /// member Events are ungrouped (TripGroupId set back to null, becoming
    /// standalone Events again — no file or MediaItem is touched) and the
    /// TripGroup row itself is deleted. Adopted TripGroups (real folder
    /// structure the user made themselves) are never touched. Idempotent —
    /// a no-op once none are left. Cleans up what auto-Trip formation
    /// already wrote to existing libraries before it was disabled (see
    /// ScanService); running this on every scan means an existing library
    /// self-heals the next time it's scanned, with no separate migration
    /// step to remember.
    /// </summary>
    public int RemoveAutoFormedTripGroups()
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var tripIds = new List<long>();
        foreach (var row in connection.Query(
            "SELECT Id FROM TripGroup WHERE NamingMode = 'CuratoManaged'", transaction: transaction))
        {
            tripIds.Add((long)row.Id);
        }

        foreach (var tripId in tripIds)
        {
            connection.Execute(
                "UPDATE Event SET TripGroupId = NULL WHERE TripGroupId = @TripId", new { TripId = tripId }, transaction);
            connection.Execute("DELETE FROM TripGroup WHERE Id = @TripId", new { TripId = tripId }, transaction);
        }

        transaction.Commit();
        return tripIds.Count;
    }

    /// <summary>Links a Live Photo's still frame to its motion clip (idempotent — a no-op once already set).</summary>
    public void SetLivePhotoPair(long photoMediaItemId, long videoMediaItemId)
    {
        using var connection = database.OpenConnection();
        connection.Execute(
            "UPDATE MediaItem SET LivePhotoVideoItemId = @VideoId WHERE Id = @PhotoId AND LivePhotoVideoItemId IS NULL",
            new { PhotoId = photoMediaItemId, VideoId = videoMediaItemId });
    }

    /// <summary>Caches a lazily-computed ContentHash (§7's expensive exact-confirmation tier) so it's never recomputed once known.</summary>
    public void SetContentHash(long mediaItemId, string contentHash)
    {
        using var connection = database.OpenConnection();
        connection.Execute(
            "UPDATE MediaItem SET ContentHash = @ContentHash WHERE Id = @Id",
            new { Id = mediaItemId, ContentHash = contentHash });
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
