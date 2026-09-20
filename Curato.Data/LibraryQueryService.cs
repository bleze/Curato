using System.Globalization;
using Curato.Core;
using Dapper;

namespace Curato.Data;

/// <summary>
/// Read-only queries over an already-scanned library — no scanning, no
/// clustering, just reading back what §3/§4 already persisted. Backs the web
/// UI's "show me what's in the library" views (§11).
///
/// Deliberately uses plain `dynamic` rows read in `foreach` loops rather than
/// Dapper's positional-record materialization or LINQ `.Select` over
/// `dynamic` — the former is brittle about exact nullable-numeric-type
/// matching against SQLite's INTEGER affinity, and the latter silently
/// infects the whole LINQ chain's static type with `dynamic` (any call with
/// a `dynamic` argument returns `dynamic`, even if the real method's return
/// type is concrete). A `foreach` with explicit casts avoids both.
/// </summary>
public sealed class LibraryQueryService(LibraryDatabase database)
{
    public IReadOnlyList<MediaItem> GetAllMediaItems()
    {
        using var connection = database.OpenConnection();
        var items = new List<MediaItem>();
        foreach (var row in connection.Query("SELECT * FROM MediaItem"))
        {
            items.Add(MapMediaItem(row));
        }

        return items;
    }

    public IReadOnlyList<Event> GetAllEvents()
    {
        using var connection = database.OpenConnection();

        var links = new List<(long EventId, long MediaItemId)>();
        foreach (var row in connection.Query("SELECT * FROM EventMediaItem ORDER BY EventId, SortOrder"))
        {
            links.Add(((long)row.EventId, (long)row.MediaItemId));
        }

        var mediaItemIdsByEvent = links.ToLookup(l => l.EventId, l => l.MediaItemId);

        var events = new List<Event>();
        foreach (var row in connection.Query("SELECT * FROM Event"))
        {
            long id = row.Id;
            events.Add(MapEvent(row, mediaItemIdsByEvent[id]));
        }

        return events;
    }

    public IReadOnlyList<TripGroup> GetAllTripGroups()
    {
        using var connection = database.OpenConnection();

        var eventLinks = new List<(long EventId, long TripGroupId)>();
        foreach (var row in connection.Query("SELECT Id, TripGroupId FROM Event WHERE TripGroupId IS NOT NULL"))
        {
            eventLinks.Add(((long)row.Id, (long)row.TripGroupId));
        }

        var eventIdsByTrip = eventLinks.ToLookup(l => l.TripGroupId, l => l.EventId);

        var trips = new List<TripGroup>();
        foreach (var row in connection.Query("SELECT * FROM TripGroup"))
        {
            long id = row.Id;
            trips.Add(MapTripGroup(row, eventIdsByTrip[id]));
        }

        return trips;
    }

    public IReadOnlyList<Tag> GetAllTags()
    {
        using var connection = database.OpenConnection();
        var tags = new List<Tag>();
        foreach (var row in connection.Query("SELECT * FROM Tag ORDER BY SortOrder"))
        {
            tags.Add(MapTag(row));
        }

        return tags;
    }

    public IReadOnlyList<long> GetMediaItemIdsForTag(long tagId)
    {
        using var connection = database.OpenConnection();
        var ids = new List<long>();
        foreach (var row in connection.Query(
            "SELECT MediaItemId FROM MediaItemTag WHERE TagId = @TagId", new { TagId = tagId }))
        {
            ids.Add((long)row.MediaItemId);
        }

        return ids;
    }

    private static Tag MapTag(dynamic row)
    {
        string name = row.Name;
        long isFavoriteShortcut = row.IsFavoriteShortcut;
        long sortOrder = row.SortOrder;

        return new Tag
        {
            Id = (long)row.Id,
            Name = name,
            IsFavoriteShortcut = isFavoriteShortcut != 0,
            SortOrder = (int)sortOrder
        };
    }

    /// <summary>Every Edit row for the MediaItem at this relative path, for render-time application (§10).</summary>
    public IReadOnlyList<Edit> GetEditsForRelativePath(string relativePath)
    {
        using var connection = database.OpenConnection();
        var edits = new List<Edit>();
        foreach (var row in connection.Query("""
            SELECT Edit.* FROM Edit
            JOIN MediaItem ON MediaItem.Id = Edit.MediaItemId
            WHERE MediaItem.RelativePath = @RelativePath
            """, new { RelativePath = relativePath }))
        {
            edits.Add(MapEdit(row));
        }

        return edits;
    }

    private static Edit MapEdit(dynamic row)
    {
        string operationType = row.OperationType;
        string parameters = row.Parameters;
        long pipelineOrder = row.PipelineOrder;
        string source = row.Source;

        return new Edit
        {
            Id = (long)row.Id,
            MediaItemId = (long)row.MediaItemId,
            OperationType = Enum.Parse<EditOperationType>(operationType),
            Parameters = parameters,
            PipelineOrder = (int)pipelineOrder,
            Source = Enum.Parse<RecordSource>(source)
        };
    }

    private static DateTime ParseDateTime(string value) =>
        DateTime.Parse(value, null, DateTimeStyles.RoundtripKind);

    private static MediaItem MapMediaItem(dynamic row)
    {
        string relativePath = row.RelativePath;
        string kind = row.Kind;
        long fileSizeBytes = row.FileSizeBytes;
        string quickHash = row.QuickHash;
        string? contentHash = row.ContentHash;
        string takenAt = row.TakenAt;
        string takenAtSource = row.TakenAtSource;
        long? width = row.Width;
        long? height = row.Height;
        long? durationMs = row.DurationMs;
        string importedAt = row.ImportedAt;
        long? livePhotoVideoItemId = row.LivePhotoVideoItemId;

        return new MediaItem
        {
            Id = (long)row.Id,
            RelativePath = relativePath,
            Kind = Enum.Parse<MediaKind>(kind),
            FileSizeBytes = fileSizeBytes,
            QuickHash = quickHash,
            ContentHash = contentHash,
            TakenAt = ParseDateTime(takenAt),
            TakenAtSource = Enum.Parse<TakenAtSource>(takenAtSource),
            Width = (int?)width,
            Height = (int?)height,
            DurationMs = durationMs,
            ImportedAt = ParseDateTime(importedAt),
            LivePhotoVideoItemId = livePhotoVideoItemId
        };
    }

    private static Event MapEvent(dynamic row, IEnumerable<long> mediaItemIds)
    {
        string startAt = row.StartAt;
        string endAt = row.EndAt;
        string title = row.Title;
        long titleIsAuto = row.TitleIsAuto;
        string? folderPath = row.FolderPath;
        long? tripGroupId = row.TripGroupId;
        string namingMode = row.NamingMode;

        return new Event
        {
            Id = (long)row.Id,
            StartAt = ParseDateTime(startAt),
            EndAt = ParseDateTime(endAt),
            Title = title,
            TitleIsAuto = titleIsAuto != 0,
            FolderPath = folderPath,
            TripGroupId = tripGroupId,
            NamingMode = Enum.Parse<NamingMode>(namingMode),
            MediaItemIds = mediaItemIds.ToList()
        };
    }

    private static TripGroup MapTripGroup(dynamic row, IEnumerable<long> eventIds)
    {
        string startDate = row.StartDate;
        string endDate = row.EndDate;
        string title = row.Title;
        long titleIsAuto = row.TitleIsAuto;
        string? folderPath = row.FolderPath;
        string namingMode = row.NamingMode;

        return new TripGroup
        {
            Id = (long)row.Id,
            StartDate = DateOnly.Parse(startDate),
            EndDate = DateOnly.Parse(endDate),
            Title = title,
            TitleIsAuto = titleIsAuto != 0,
            FolderPath = folderPath,
            NamingMode = Enum.Parse<NamingMode>(namingMode),
            EventIds = eventIds.ToList()
        };
    }
}
