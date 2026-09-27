using System.Data;
using System.Text.Json;
using Curato.Core;
using Dapper;

namespace Curato.Data;

public enum MoveMediaItemResult { Success, MediaItemNotFound, TargetEventNotFound }

public enum CreateTripGroupResult { Success, NoEventsGiven, EventNotFound, EventAlreadyInATrip }

public enum MergeEventResult { Success, SourceEqualsTarget, SourceEventNotFound, TargetEventNotFound }

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
                     TakenAt, TakenAtSource, Width, Height, DurationMs, ImportedAt, Latitude, Longitude)
                VALUES
                    (@RelativePath, @Kind, @FileSizeBytes, @QuickHash, @ContentHash,
                     @TakenAt, @TakenAtSource, @Width, @Height, @DurationMs, @ImportedAt, @Latitude, @Longitude)
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
    /// Rotates a MediaItem by a relative amount (+/-90 from a Lightbox
    /// toolbar button) — PLAN.md §10's non-destructive Edit pipeline, so
    /// this only ever writes a database row; the original file's bytes are
    /// never touched, exactly like Picasa-imported rotate/crop edits
    /// already work. `EditRenderer` only ever applies the *first* Rotate
    /// edit it finds for an item, so this replaces that single row (reading
    /// its current angle first to rotate relative to it) rather than
    /// accumulating a new row on every click. Landing back at 0 degrees
    /// removes the row entirely rather than storing a no-op rotation.
    /// </summary>
    public void RotateMediaItem(long mediaItemId, int deltaDegrees)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var currentParameters = connection.QuerySingleOrDefault<string?>("""
            SELECT Parameters FROM Edit WHERE MediaItemId = @MediaItemId AND OperationType = @OperationType
            """, new { MediaItemId = mediaItemId, OperationType = nameof(EditOperationType.Rotate) }, transaction);

        var currentDegrees = 0;
        if (currentParameters is not null)
        {
            using var parsed = JsonDocument.Parse(currentParameters);
            currentDegrees = parsed.RootElement.GetProperty("Degrees").GetInt32();
        }

        var newDegrees = ((currentDegrees + deltaDegrees) % 360 + 360) % 360;

        connection.Execute(
            "DELETE FROM Edit WHERE MediaItemId = @MediaItemId AND OperationType = @OperationType",
            new { MediaItemId = mediaItemId, OperationType = nameof(EditOperationType.Rotate) }, transaction);

        if (newDegrees != 0)
        {
            connection.Execute("""
                INSERT INTO Edit (MediaItemId, OperationType, Parameters, PipelineOrder, Source)
                VALUES (@MediaItemId, @OperationType, @Parameters, 0, @Source)
                """, new
            {
                MediaItemId = mediaItemId,
                OperationType = nameof(EditOperationType.Rotate),
                Parameters = $$"""{"Degrees":{{newDegrees}}}""",
                Source = nameof(RecordSource.Local)
            }, transaction);
        }

        transaction.Commit();
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
    /// Refreshes StartAt/EndAt for every never-Organized Event
    /// (FolderPath IS NULL) to match its members' *current* TakenAt.
    /// Needed after any bulk TakenAt correction that writes MediaItem.TakenAt
    /// directly rather than through <see cref="SetTakenAt"/> (e.g. a
    /// metadata-repair pass) — confirmed live as a video's corrected date not
    /// being reflected in the Event it's grouped/labeled under (still showing
    /// its stale pre-correction date range). Adopted/already-Organized Events
    /// are skipped: their range is never supposed to auto-shift from file
    /// metadata alone. Membership itself is untouched — an item whose
    /// correction moves it far enough that it no longer belongs with its
    /// Event's other members needs <see cref="MoveMediaItemToEvent"/>
    /// instead, not just a boundary refresh.
    /// </summary>
    public int RefreshLooseEventBoundaries()
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var eventIds = new List<long>();
        foreach (var row in connection.Query(
            "SELECT Id FROM Event WHERE FolderPath IS NULL", transaction: transaction))
        {
            eventIds.Add((long)row.Id);
        }

        foreach (var eventId in eventIds)
        {
            ResortEventMembership(connection, transaction, eventId);
        }

        transaction.Commit();
        return eventIds.Count;
    }

    /// <summary>
    /// Moves one MediaItem out of whichever Event it's currently in and into
    /// a different one — or, with <paramref name="targetEventId"/> null, into
    /// a brand new standalone Event of its own — re-sorting/re-bounding both
    /// the old and new Event afterward and deleting the old one if that
    /// leaves it empty. The manual escape hatch for whatever automatic
    /// clustering can't safely infer on its own: a wrong timestamp that put
    /// an item in the wrong moment, or a correction that moves it far enough
    /// from its old Event's other members that it no longer belongs there
    /// (<see cref="SetTakenAt"/>'s own doc comment calls this out as
    /// deliberately out of scope for a single-field fix).
    ///
    /// Deliberately refuses to touch either side once it has a real
    /// FolderPath (Adopted, or already Organized) — Curato never auto-moves
    /// files for such content, so a DB-only membership change there would
    /// desync the database from what's actually on disk. Moving photos in or
    /// out of organized folders needs a real file move, out of scope here.
    /// </summary>
    public MoveMediaItemResult MoveMediaItemToEvent(long mediaItemId, long? targetEventId)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var itemRow = connection.QuerySingleOrDefault<(string TakenAt, string RelativePath)?>(
            "SELECT TakenAt, RelativePath FROM MediaItem WHERE Id = @Id", new { Id = mediaItemId }, transaction);
        if (itemRow is null)
        {
            transaction.Rollback();
            return MoveMediaItemResult.MediaItemNotFound;
        }

        var takenAtRaw = itemRow.Value.TakenAt;
        var currentRelativePath = itemRow.Value.RelativePath;

        var sourceEventId = connection.ExecuteScalar<long?>(
            "SELECT EventId FROM EventMediaItem WHERE MediaItemId = @Id", new { Id = mediaItemId }, transaction);

        long resolvedTargetId;
        string? targetFolderPath;
        if (targetEventId is { } requestedTargetId)
        {
            var targetRow = connection.QuerySingleOrDefault<(string? FolderPath, bool Found)?>("""
                SELECT FolderPath, 1 AS Found FROM Event WHERE Id = @Id
                """, new { Id = requestedTargetId }, transaction);

            if (targetRow is null)
            {
                transaction.Rollback();
                return MoveMediaItemResult.TargetEventNotFound;
            }

            resolvedTargetId = requestedTargetId;
            targetFolderPath = targetRow.Value.FolderPath;
        }
        else
        {
            var takenAt = ParseDateTime(takenAtRaw);
            resolvedTargetId = connection.ExecuteScalar<long>("""
                INSERT INTO Event (StartAt, EndAt, Title, TitleIsAuto, FolderPath, TripGroupId, NamingMode)
                VALUES (@StartAt, @StartAt, @Title, 1, NULL, NULL, @NamingMode)
                RETURNING Id
                """, new
            {
                StartAt = takenAtRaw,
                Title = Curato.Core.EventClusterer.AutoTitle(takenAt),
                NamingMode = nameof(Curato.Core.NamingMode.CuratoManaged)
            }, transaction);
            targetFolderPath = null;
        }

        // A real on-disk folder on either side no longer refuses the move
        // outright — Curato never lets its database and the real filesystem
        // disagree about where a file lives (PLAN.md §4), so a target that's
        // already organized gets the file physically moved into it right
        // now, the same collision-free way Organize itself would. A target
        // that ISN'T organized yet needs no physical move at all: the file
        // just stays exactly where it is (even if that's inside an
        // already-organized source's real folder) until some future
        // Organize run gives the target its own folder and picks it up —
        // identical to how a never-yet-moved item already works today.
        if (targetFolderPath is not null)
        {
            var libraryRoot = database.LibraryRoot;
            var oldFullPath = Path.Combine(libraryRoot, currentRelativePath);
            var fileName = Path.GetFileName(currentRelativePath);
            var newRelativePath = CollisionFreePath.Resolve(libraryRoot, targetFolderPath, fileName, new HashSet<string>());
            var newFullPath = Path.Combine(libraryRoot, newRelativePath);

            if (!string.Equals(Path.GetFullPath(oldFullPath), Path.GetFullPath(newFullPath), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newFullPath)!);
                File.Move(oldFullPath, newFullPath);
                connection.Execute(
                    "UPDATE MediaItem SET RelativePath = @Path WHERE Id = @Id",
                    new { Path = newRelativePath, Id = mediaItemId }, transaction);
            }
        }

        if (sourceEventId is { } sourceIdToLeave)
        {
            connection.Execute(
                "DELETE FROM EventMediaItem WHERE EventId = @EventId AND MediaItemId = @MediaItemId",
                new { EventId = sourceIdToLeave, MediaItemId = mediaItemId }, transaction);
        }

        connection.Execute("""
            INSERT INTO EventMediaItem (EventId, MediaItemId, SortOrder) VALUES (@EventId, @MediaItemId, 0)
            """, new { EventId = resolvedTargetId, MediaItemId = mediaItemId }, transaction);
        ResortEventMembership(connection, transaction, resolvedTargetId);

        if (sourceEventId is { } sourceIdToCheck)
        {
            var remaining = connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM EventMediaItem WHERE EventId = @EventId",
                new { EventId = sourceIdToCheck }, transaction);

            if (remaining == 0)
            {
                connection.Execute("DELETE FROM Event WHERE Id = @EventId", new { EventId = sourceIdToCheck }, transaction);
            }
            else
            {
                ResortEventMembership(connection, transaction, sourceIdToCheck);
            }
        }

        transaction.Commit();
        return MoveMediaItemResult.Success;
    }

    /// <summary>
    /// Merges every MediaItem out of <paramref name="sourceEventId"/> into
    /// <paramref name="targetEventId"/> in one pass and deletes the
    /// now-empty source — the bulk counterpart to <see cref="MoveMediaItemToEvent"/>
    /// for "these were wrongly split into two Events" (a bad clustering gap,
    /// or a batch of photos someone else shared with wrong iCloud dates that
    /// landed in their own Event instead of joining the real one). Same rule
    /// as a single-item move: a target that's already organized gets every
    /// member physically relocated into its real folder right now
    /// (collision-free); a target that isn't organized yet needs no physical
    /// move — members just stay wherever they are (even inside an
    /// already-organized source's real folder) until some future Organize
    /// run gives the target its own folder. Once every member has actually
    /// been relocated out, the now-empty source folder is removed too —
    /// never recursively, so any unexpected leftover content (something
    /// Curato doesn't track) is left alone rather than guessed at.
    /// </summary>
    public MergeEventResult MergeEventInto(long sourceEventId, long targetEventId)
    {
        if (sourceEventId == targetEventId)
        {
            return MergeEventResult.SourceEqualsTarget;
        }

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var sourceRow = connection.QuerySingleOrDefault(
            "SELECT FolderPath FROM Event WHERE Id = @Id", new { Id = sourceEventId }, transaction);
        if (sourceRow is null)
        {
            transaction.Rollback();
            return MergeEventResult.SourceEventNotFound;
        }

        string? sourceFolderPath = sourceRow.FolderPath;

        var targetRow = connection.QuerySingleOrDefault(
            "SELECT FolderPath FROM Event WHERE Id = @Id", new { Id = targetEventId }, transaction);
        if (targetRow is null)
        {
            transaction.Rollback();
            return MergeEventResult.TargetEventNotFound;
        }

        string? targetFolderPath = targetRow.FolderPath;

        if (targetFolderPath is not null)
        {
            var libraryRoot = database.LibraryRoot;
            var claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var members = connection.Query("""
                SELECT MediaItem.Id AS MediaItemId, MediaItem.RelativePath AS RelativePath
                FROM EventMediaItem
                JOIN MediaItem ON MediaItem.Id = EventMediaItem.MediaItemId
                WHERE EventMediaItem.EventId = @SourceId
                """, new { SourceId = sourceEventId }, transaction);

            foreach (var member in members)
            {
                long mediaItemId = member.MediaItemId;
                string currentRelativePath = member.RelativePath;
                var oldFullPath = Path.Combine(libraryRoot, currentRelativePath);
                var fileName = Path.GetFileName(currentRelativePath);
                var newRelativePath = CollisionFreePath.Resolve(libraryRoot, targetFolderPath, fileName, claimedPaths);
                claimedPaths.Add(newRelativePath);
                var newFullPath = Path.Combine(libraryRoot, newRelativePath);

                if (!string.Equals(Path.GetFullPath(oldFullPath), Path.GetFullPath(newFullPath), StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(newFullPath)!);
                    File.Move(oldFullPath, newFullPath);
                    connection.Execute(
                        "UPDATE MediaItem SET RelativePath = @Path WHERE Id = @Id",
                        new { Path = newRelativePath, Id = mediaItemId }, transaction);
                }
            }
        }

        connection.Execute(
            "UPDATE EventMediaItem SET EventId = @TargetId WHERE EventId = @SourceId",
            new { TargetId = targetEventId, SourceId = sourceEventId }, transaction);
        ResortEventMembership(connection, transaction, targetEventId);
        connection.Execute("DELETE FROM Event WHERE Id = @SourceId", new { SourceId = sourceEventId }, transaction);

        transaction.Commit();

        // Only reached a genuinely empty state when every member's file was
        // actually relocated out (target was organized) — otherwise the
        // source folder still holds every member's real file.
        if (targetFolderPath is not null && sourceFolderPath is not null)
        {
            var sourceFullPath = Path.Combine(database.LibraryRoot, sourceFolderPath);
            try
            {
                if (Directory.Exists(sourceFullPath))
                {
                    Directory.Delete(sourceFullPath);
                }
            }
            catch (IOException)
            {
                // Not actually empty (unexpected untracked content) — leave it rather than guess.
            }
        }

        return MergeEventResult.Success;
    }

    private static DateTime ParseDateTime(string value) =>
        DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

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
    /// Manually groups a set of standalone Events into a new TripGroup — the
    /// escape hatch for "these clearly belong together" whenever GPS-gated
    /// auto-formation (see ScanService) doesn't apply: no GPS data on the
    /// photos, or a real trip close enough to home that the distance gate
    /// doesn't recognize it. Unlike <see cref="MoveMediaItemToEvent"/>, this
    /// never touches a file — grouping is purely a TripGroupId assignment, an
    /// Event keeps its own FolderPath (or lack of one) exactly as it was — so
    /// Adopted or already-Organized Events can join a manual Trip just as
    /// freely as untouched loose ones.
    /// </summary>
    public (CreateTripGroupResult Result, TripGroup? TripGroup) CreateTripGroup(IReadOnlyList<long> eventIds)
    {
        if (eventIds.Count == 0)
        {
            return (CreateTripGroupResult.NoEventsGiven, null);
        }

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var startAts = new List<string>();
        var endAts = new List<string>();
        foreach (var eventId in eventIds)
        {
            var row = connection.QuerySingleOrDefault(
                "SELECT StartAt, EndAt, TripGroupId FROM Event WHERE Id = @Id", new { Id = eventId }, transaction);

            if (row is null)
            {
                transaction.Rollback();
                return (CreateTripGroupResult.EventNotFound, null);
            }

            if (row.TripGroupId is not null)
            {
                transaction.Rollback();
                return (CreateTripGroupResult.EventAlreadyInATrip, null);
            }

            startAts.Add((string)row.StartAt);
            endAts.Add((string)row.EndAt);
        }

        var startDate = DateOnly.FromDateTime(ParseDateTime(startAts.Min(StringComparer.Ordinal)!));
        var endDate = DateOnly.FromDateTime(ParseDateTime(endAts.Max(StringComparer.Ordinal)!));
        var title = $"{startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}";

        var tripId = connection.ExecuteScalar<long>("""
            INSERT INTO TripGroup (StartDate, EndDate, Title, TitleIsAuto, FolderPath, NamingMode)
            VALUES (@StartDate, @EndDate, @Title, 1, NULL, @NamingMode)
            RETURNING Id
            """, new
        {
            StartDate = startDate.ToString("O"),
            EndDate = endDate.ToString("O"),
            Title = title,
            NamingMode = nameof(Curato.Core.NamingMode.CuratoManaged)
        }, transaction);

        foreach (var eventId in eventIds)
        {
            connection.Execute(
                "UPDATE Event SET TripGroupId = @TripId WHERE Id = @EventId",
                new { TripId = tripId, EventId = eventId }, transaction);
        }

        transaction.Commit();

        return (CreateTripGroupResult.Success, new TripGroup
        {
            Id = tripId,
            StartDate = startDate,
            EndDate = endDate,
            Title = title,
            TitleIsAuto = true,
            NamingMode = Curato.Core.NamingMode.CuratoManaged,
            EventIds = eventIds.ToList()
        });
    }

    /// <summary>
    /// Undoes a manually- or automatically-formed TripGroup: member Events
    /// are ungrouped back to standalone (TripGroupId set to null — no file or
    /// MediaItem is touched) and the TripGroup row is deleted. Returns false
    /// if the id doesn't exist.
    /// </summary>
    public bool UngroupTripGroup(long tripGroupId)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var exists = connection.ExecuteScalar<long?>(
            "SELECT Id FROM TripGroup WHERE Id = @Id", new { Id = tripGroupId }, transaction);
        if (exists is null)
        {
            transaction.Rollback();
            return false;
        }

        connection.Execute(
            "UPDATE Event SET TripGroupId = NULL WHERE TripGroupId = @TripId", new { TripId = tripGroupId }, transaction);
        connection.Execute("DELETE FROM TripGroup WHERE Id = @TripId", new { TripId = tripGroupId }, transaction);

        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Ejects a single Event from whichever Trip it's currently grouped
    /// into, back to standalone (no file is moved — same as
    /// <see cref="UngroupTripGroup"/>, just for one Event instead of the
    /// whole Trip). Confirmed live as needed: the GPS-gated auto-grouping
    /// (ScanService.FormGpsAwareTrips) only checks whether *some* Event in a
    /// time-contiguous run is away from home, not every Event in it, so a
    /// genuinely-at-home day that happens to fall within the clustering gap
    /// can get swept into an otherwise-real trip with no easy way back out.
    /// Deletes the TripGroup itself if this was its last remaining Event.
    /// Returns false if the Event doesn't exist or isn't currently in a Trip.
    /// </summary>
    public bool RemoveEventFromTrip(long eventId)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var row = connection.QuerySingleOrDefault(
            "SELECT TripGroupId FROM Event WHERE Id = @Id", new { Id = eventId }, transaction);
        if (row is null)
        {
            transaction.Rollback();
            return false;
        }

        long? tripId = row.TripGroupId;
        if (tripId is null)
        {
            transaction.Rollback();
            return false;
        }

        connection.Execute("UPDATE Event SET TripGroupId = NULL WHERE Id = @Id", new { Id = eventId }, transaction);

        var remaining = connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM Event WHERE TripGroupId = @TripId", new { TripId = tripId }, transaction);
        if (remaining == 0)
        {
            connection.Execute("DELETE FROM TripGroup WHERE Id = @TripId", new { TripId = tripId }, transaction);
        }

        transaction.Commit();
        return true;
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
    /// like "2019 Summer Vacation" is untouched. Idempotent, self-healing —
    /// running this on every scan means an existing library heals the next
    /// time it's scanned, with no separate migration step to remember.
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

    /// <summary>
    /// Self-heals any Event left with zero members (and, in turn, any
    /// TripGroup left with zero Events) — normally prevented for a fresh
    /// delete by <c>RecycleBinRepository.RemoveMediaItem</c>, but that fix
    /// only guards its own codepath going forward; it never swept up empty
    /// rows a library's database already had from before it existed.
    /// Confirmed live as a photo-less "Afternoon" event still sitting in the
    /// library view. Idempotent, self-healing on every scan exactly like
    /// <see cref="RemoveAutoFormedTripGroups"/>. Returns the emptied Events
    /// (with their FolderPath, if any) so the caller can also remove their
    /// now-empty on-disk folder — same shape as
    /// <c>RecycleBinRepository.RemoveMediaItem</c> returns for the same
    /// reason.
    /// </summary>
    public IReadOnlyList<EmptiedEvent> RemoveEmptyEvents()
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var emptyEvents = new List<(long Id, string? FolderPath)>();
        foreach (var row in connection.Query("""
            SELECT Event.Id, Event.FolderPath FROM Event
            LEFT JOIN EventMediaItem ON EventMediaItem.EventId = Event.Id
            WHERE EventMediaItem.EventId IS NULL
            """, transaction: transaction))
        {
            long id = row.Id;
            string? folderPath = row.FolderPath;
            emptyEvents.Add((id, folderPath));
        }

        var affectedTripIds = new List<long>();
        var emptiedEvents = new List<EmptiedEvent>();
        foreach (var (eventId, folderPath) in emptyEvents)
        {
            var tripId = connection.ExecuteScalar<long?>(
                "SELECT TripGroupId FROM Event WHERE Id = @EventId", new { EventId = eventId }, transaction);
            if (tripId is { } t)
            {
                affectedTripIds.Add(t);
            }

            connection.Execute("DELETE FROM Event WHERE Id = @EventId", new { EventId = eventId }, transaction);
            emptiedEvents.Add(new EmptiedEvent(eventId, folderPath));
        }

        foreach (var tripId in affectedTripIds.Distinct())
        {
            var remainingEvents = connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM Event WHERE TripGroupId = @TripId", new { TripId = tripId }, transaction);
            if (remainingEvents == 0)
            {
                connection.Execute("DELETE FROM TripGroup WHERE Id = @TripId", new { TripId = tripId }, transaction);
            }
        }

        transaction.Commit();
        return emptiedEvents;
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
        ImportedAt = item.ImportedAt.ToString("O"),
        item.Latitude,
        item.Longitude
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
