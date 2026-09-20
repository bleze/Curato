using System.Data;
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
    /// Every existing Event that already owns a real folder path — keyed by
    /// that path, each with its current member MediaItemIds — so a re-scan
    /// can add newly-arrived files in an already-known folder to the *same*
    /// Event instead of creating a duplicate one for that folder.
    ///
    /// Deliberately not filtered to `NamingMode = 'Adopted'` (its original
    /// scope, before this bug was confirmed live): once Organize (§4) gives
    /// a CuratoManaged Event a real FolderPath by moving its files into a
    /// dated folder, a later re-scan needs to recognize that folder as
    /// already-tracked too, or it falls through to
    /// <see cref="Curato.Organizer.AdoptedFolderPlanner"/> and gets
    /// (re-)classified as a brand-new Adopted Event for content that
    /// already belongs to an existing one — the exact bug this method
    /// existed to prevent, just for the other NamingMode.
    /// </summary>
    public Dictionary<string, (long Id, List<long> MediaItemIds)> GetEventsByFolderPath()
    {
        using var connection = database.OpenConnection();

        var links = new List<(long EventId, long MediaItemId)>();
        foreach (var row in connection.Query("SELECT * FROM EventMediaItem"))
        {
            links.Add(((long)row.EventId, (long)row.MediaItemId));
        }

        var mediaItemIdsByEvent = links.ToLookup(l => l.EventId, l => l.MediaItemId);

        var result = new Dictionary<string, (long, List<long>)>();
        foreach (var row in connection.Query("SELECT Id, FolderPath FROM Event WHERE FolderPath IS NOT NULL"))
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

        foreach (var item in newItems)
        {
            // SortOrder is fixed up for every member (old and new) right
            // after, so its value here is just a temporary placeholder.
            connection.Execute("""
                INSERT INTO EventMediaItem (EventId, MediaItemId, SortOrder)
                VALUES (@EventId, @MediaItemId, 0)
                """, new { EventId = eventId, MediaItemId = item.Id }, transaction);
        }

        // Re-sort the *whole* membership (existing + newly-added) by
        // TakenAt rather than just appending the new ones — otherwise
        // newly-added items always land at the end of the thumb-grid
        // regardless of when they were actually taken, breaking
        // chronological order within the event.
        ResortEventMembership(connection, transaction, eventId);

        transaction.Commit();
    }

    /// <summary>
    /// Manually corrects one MediaItem's TakenAt (PLAN.md §1's
    /// TakenAtSource.Manual — for files whose Exif was stripped by the time
    /// they reached the library, e.g. reported live for an iPhone photo
    /// shared through a path that kept only a bare Exif version tag) and
    /// re-sorts its Event's membership + StartAt/EndAt to match, the same
    /// way a newly-scanned item is folded in above. Deliberately does *not*
    /// move the item into a different Event even if the correction lands it
    /// on a different day — that needs the full clustering pass, out of
    /// scope for a single-field fix. Returns false if the item doesn't
    /// exist.
    /// </summary>
    public bool SetTakenAt(long mediaItemId, DateTime takenAt)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var updated = connection.Execute("""
            UPDATE MediaItem SET TakenAt = @TakenAt, TakenAtSource = @TakenAtSource
            WHERE Id = @Id
            """, new
        {
            Id = mediaItemId,
            TakenAt = takenAt.ToString("O"),
            TakenAtSource = nameof(Curato.Core.TakenAtSource.Manual)
        }, transaction);

        if (updated == 0)
        {
            transaction.Rollback();
            return false;
        }

        var eventId = connection.ExecuteScalar<long?>(
            "SELECT EventId FROM EventMediaItem WHERE MediaItemId = @MediaItemId",
            new { MediaItemId = mediaItemId }, transaction);

        if (eventId is { } id)
        {
            ResortEventMembership(connection, transaction, id);
        }

        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Re-sorts an Event's SortOrder to match its members' actual TakenAt
    /// and recomputes StartAt/EndAt from them, so both the thumb-grid order
    /// and the Event's own date range stay correct after any change to a
    /// member's TakenAt. TakenAt strings are all stored via
    /// DateTime.ToString("O") (see ToRow below), so ordinal string ordering
    /// matches chronological ordering exactly, without parsing each one
    /// back into a DateTime.
    /// </summary>
    private static void ResortEventMembership(IDbConnection connection, IDbTransaction transaction, long eventId)
    {
        var members = new List<(long MediaItemId, string TakenAt)>();
        foreach (var row in connection.Query("""
            SELECT MediaItem.Id AS MediaItemId, MediaItem.TakenAt FROM EventMediaItem
            JOIN MediaItem ON MediaItem.Id = EventMediaItem.MediaItemId
            WHERE EventMediaItem.EventId = @EventId
            """, new { EventId = eventId }, transaction))
        {
            members.Add(((long)row.MediaItemId, (string)row.TakenAt));
        }

        if (members.Count == 0)
        {
            return;
        }

        var sortOrder = 0;
        foreach (var member in members.OrderBy(m => m.TakenAt, StringComparer.Ordinal))
        {
            connection.Execute("""
                UPDATE EventMediaItem SET SortOrder = @SortOrder
                WHERE EventId = @EventId AND MediaItemId = @MediaItemId
                """, new { SortOrder = sortOrder++, EventId = eventId, MediaItemId = member.MediaItemId }, transaction);
        }

        connection.Execute("""
            UPDATE Event SET StartAt = @StartAt, EndAt = @EndAt WHERE Id = @EventId
            """, new
        {
            StartAt = members.Min(m => m.TakenAt),
            EndAt = members.Max(m => m.TakenAt),
            EventId = eventId
        }, transaction);
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

    /// <summary>
    /// Manual title edit (PLAN.md §3/§4). Also flips TitleIsAuto off so a
    /// later re-scan/re-cluster never silently overwrites a name the user
    /// chose. Renaming an already-Organized Event's on-disk folder to match
    /// is <see cref="Curato.Organizer.RenameService"/>'s job, not this one —
    /// this only ever writes the Title column itself.
    /// </summary>
    public bool SetEventTitle(long eventId, string title)
    {
        using var connection = database.OpenConnection();
        var updated = connection.Execute(
            "UPDATE Event SET Title = @Title, TitleIsAuto = 0 WHERE Id = @Id",
            new { Id = eventId, Title = title });
        return updated > 0;
    }

    /// <summary>Manual title edit for a TripGroup — see <see cref="SetEventTitle"/>.</summary>
    public bool SetTripGroupTitle(long tripId, string title)
    {
        using var connection = database.OpenConnection();
        var updated = connection.Execute(
            "UPDATE TripGroup SET Title = @Title, TitleIsAuto = 0 WHERE Id = @Id",
            new { Id = tripId, Title = title });
        return updated > 0;
    }

    /// <summary>
    /// Un-does bare-year Adopted TripGroups (e.g. a "2019" trip wrapping
    /// every unrelated event that happened to live under a top-level
    /// `2019/` folder) written before <c>AdoptedFolderPlanner.PlanTrips</c>
    /// started excluding bare-year parent folders — confirmed live as a
    /// "why does this year have a Rename button, it's not a trip" report.
    /// Their member Events are ungrouped back to standalone (no file or
    /// MediaItem is touched) and the TripGroup row is deleted. Only ever
    /// matches a Title that's *exactly* a 4-digit year — a real Adopted trip
    /// like "2019 Summer Vacation" is untouched. Idempotent, self-healing on
    /// every scan exactly like <see cref="RemoveAutoFormedTripGroups"/>.
    /// </summary>
    public int RemoveBareYearAdoptedTripGroups()
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var tripIds = new List<long>();
        foreach (var row in connection.Query(
            "SELECT Id, Title FROM TripGroup WHERE NamingMode = 'Adopted'", transaction: transaction))
        {
            long id = row.Id;
            string title = row.Title;
            if (FolderTitleParser.IsBareYear(title))
            {
                tripIds.Add(id);
            }
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
