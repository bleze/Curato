using Curato.Core;
using Curato.Data;
using Curato.Organizer;
using Microsoft.AspNetCore.Mvc;

namespace Curato.Api.Controllers;

/// <summary>
/// Read-only view of an already-scanned library, for the web UI (§11).
/// Includes a dry-run preview (§4: "nothing happens without the user seeing
/// the plan first") — any CuratoManaged Event/Trip that hasn't been
/// organized yet gets its <c>ProposedFolderPath</c> computed on the fly
/// (never persisted here), so the UI can show exactly where Organize would
/// put it before the user ever clicks the button.
///
/// Protected by the fallback auth policy (§15) — requires a logged-in session.
/// </summary>
[ApiController]
[Route("api")]
public sealed class LibraryController(LibraryOperationGate gate) : ControllerBase
{
    public sealed record MediaItemDto(
        long Id, string RelativePath, string Kind, DateTime TakenAt, string TakenAtSource,
        string? LiveVideoRelativePath, string? Location);

    public sealed record EventDto(
        long Id, string Title, string NamingMode, string? FolderPath, string? ProposedFolderPath,
        DateTime StartAt, DateTime EndAt, string? Location, long? TripGroupId, IReadOnlyList<MediaItemDto> Items);

    public sealed record TripGroupDto(
        long Id, string Title, string NamingMode, string? FolderPath, string? ProposedFolderPath,
        DateOnly StartDate, DateOnly EndDate, string? Location, IReadOnlyList<EventDto> Events);

    public sealed record LibraryDto(
        IReadOnlyList<TripGroupDto> Trips, IReadOnlyList<EventDto> StandaloneEvents);

    [HttpGet("library")]
    public ActionResult<LibraryDto> GetLibrary([FromQuery] string path)
    {
        if (!Directory.Exists(path))
        {
            return NotFound($"Folder not found: {path}");
        }

        var database = new LibraryDatabase(path);
        database.EnsureSchema();
        var query = new LibraryQueryService(database);
        var geocoder = new ReverseGeocoder(database, new NominatimClient());

        var mediaById = query.GetAllMediaItems().ToDictionary(m => m.Id);

        // Cache-only (see ReverseGeocoder.TryGetCached) — a library view must
        // stay fast, not synchronously resolve every distinct place it was
        // ever photographed at, one at a time at Nominatim's 1 req/sec limit,
        // the first time anyone opens it after new GPS data lands. Real
        // resolution happens out-of-band in GeocodeWarmupBackgroundService;
        // an unresolved location is simply null here until that catches up,
        // and the next library view picks up whatever it filled in since.
        // Memoized per ~1km bucket for this one request only to skip
        // redundant SQLite round-trips within a single view that might
        // reference the same place hundreds of times (every photo of a trip).
        var locationCache = new Dictionary<(double, double), string?>();
        string? ResolveLocation((double Lat, double Lon)? location)
        {
            if (location is not { } loc)
            {
                return null;
            }

            var bucket = (Math.Round(loc.Lat, 2), Math.Round(loc.Lon, 2));
            if (!locationCache.TryGetValue(bucket, out var placeName))
            {
                placeName = geocoder.TryGetCached(loc.Lat, loc.Lon);
                locationCache[bucket] = placeName;
            }

            return placeName;
        }

        // Apple Live Photos (JPG+MOV pairs, §3's "no double files" rule):
        // a video that's paired to some photo is represented by that photo's
        // tile, never shown as its own — nothing on disk changes, this is
        // purely which items the grid enumerates.
        var pairedVideoIds = mediaById.Values
            .Where(m => m.LivePhotoVideoItemId is not null)
            .Select(m => m.LivePhotoVideoItemId!.Value)
            .ToHashSet();

        var events = query.GetAllEvents();
        var trips = query.GetAllTripGroups();
        var tripsById = trips.ToDictionary(t => t.Id);

        // FolderNameTemplate.RenderEventPath/RenderTripPath always compute
        // fresh from the current Title/dates, regardless of what's already
        // persisted in FolderPath — exactly what a "what would Organize do
        // right now" preview needs.
        string? ProposeEventPath(Event e) =>
            e.NamingMode != NamingMode.CuratoManaged || e.FolderPath is not null
                ? null
                : FolderNameTemplate.RenderEventPath(
                    e, e.TripGroupId is { } tripId ? tripsById.GetValueOrDefault(tripId) : null);

        string? ProposeTripPath(TripGroup t) =>
            t.NamingMode != NamingMode.CuratoManaged || t.FolderPath is not null
                ? null
                : FolderNameTemplate.RenderTripPath(t);

        MediaItemDto ToMediaItemDto(MediaItem m) => new(
            m.Id, m.RelativePath, m.Kind.ToString(), m.TakenAt, m.TakenAtSource.ToString(),
            m.LivePhotoVideoItemId is { } videoId && mediaById.TryGetValue(videoId, out var video)
                ? video.RelativePath
                : null,
            ResolveLocation(m.Latitude is { } lat && m.Longitude is { } lon ? (lat, lon) : null));

        EventDto ToEventDto(Event e) => new(
            e.Id, e.Title, e.NamingMode.ToString(), e.FolderPath, ProposeEventPath(e), e.StartAt, e.EndAt,
            ResolveLocation(GpsTripGate.RepresentativeLocation(e, mediaById)), e.TripGroupId,
            e.MediaItemIds
                .Where(id => mediaById.ContainsKey(id) && !pairedVideoIds.Contains(id))
                .Select(id => mediaById[id])
                .Select(ToMediaItemDto)
                .ToList());

        var eventsById = events.ToDictionary(e => e.Id);
        var tripDtos = trips
            .Select(t =>
            {
                var eventDtos = t.EventIds
                    .Where(eventsById.ContainsKey)
                    .Select(id => ToEventDto(eventsById[id]))
                    .OrderBy(e => e.StartAt)
                    .ToList();

                // A Trip's own location is the average of whichever member
                // Events actually resolved one — a multi-country trip still
                // gets *a* representative point rather than none at all.
                var eventLocations = t.EventIds
                    .Where(eventsById.ContainsKey)
                    .Select(id => GpsTripGate.RepresentativeLocation(eventsById[id], mediaById))
                    .Where(loc => loc is not null)
                    .Select(loc => loc!.Value)
                    .ToList();
                var tripLocation = eventLocations.Count == 0
                    ? null
                    : ResolveLocation((eventLocations.Average(l => l.Lat), eventLocations.Average(l => l.Lon)));

                return new TripGroupDto(
                    t.Id, t.Title, t.NamingMode.ToString(), t.FolderPath, ProposeTripPath(t), t.StartDate, t.EndDate,
                    tripLocation, eventDtos);
            })
            .OrderBy(t => t.StartDate)
            .ToList();

        var standaloneEvents = events
            .Where(e => e.TripGroupId is null)
            .Select(ToEventDto)
            .OrderBy(e => e.StartAt)
            .ToList();

        return Ok(new LibraryDto(tripDtos, standaloneEvents));
    }

    public sealed record SetTakenAtRequest(string Path, long MediaItemId, DateTime TakenAt);

    /// <summary>
    /// Manual last-resort fix for a MediaItem whose Exif is missing/wrong
    /// (§1's TakenAtSource.Manual) — e.g. photos whose sharing/transfer path
    /// stripped DateTimeOriginal entirely, which no automatic fallback can
    /// recover. Never moves the item into a different Event even if the
    /// correction lands it on another day; see LibraryRepository.SetTakenAt.
    /// </summary>
    [HttpPost("media-items/taken-at")]
    public IActionResult SetTakenAt([FromBody] SetTakenAtRequest request)
    {
        if (!Directory.Exists(request.Path))
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        var database = new LibraryDatabase(request.Path);
        database.EnsureSchema();
        var repository = new LibraryRepository(database);

        return repository.SetTakenAt(request.MediaItemId, request.TakenAt)
            ? NoContent()
            : NotFound($"No MediaItem with id {request.MediaItemId}");
    }

    public sealed record RotateRequest(string Path, long MediaItemId, int DeltaDegrees);

    /// <summary>
    /// The Lightbox toolbar's Rotate Left/Right buttons — a pure database
    /// write (PLAN.md §10: non-destructive, the original file is never
    /// touched), same mechanism Picasa-imported rotate edits already use.
    /// See LibraryRepository.RotateMediaItem.
    /// </summary>
    [HttpPost("media-items/rotate")]
    public IActionResult Rotate([FromBody] RotateRequest request)
    {
        if (!Directory.Exists(request.Path))
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        var database = new LibraryDatabase(request.Path);
        database.EnsureSchema();
        new LibraryRepository(database).RotateMediaItem(request.MediaItemId, request.DeltaDegrees);
        return NoContent();
    }

    public sealed record MoveMediaItemToEventRequest(string Path, long MediaItemId, long? TargetEventId);

    /// <summary>
    /// The Lightbox's "Move to Event" picker — manually fixes a wrong
    /// grouping (e.g. a since-corrected timestamp that no longer matches its
    /// Event's other members). See LibraryRepository.MoveMediaItemToEvent for
    /// why this refuses to touch either side once it has a real FolderPath.
    /// </summary>
    [HttpPost("media-items/move-to-event")]
    public IActionResult MoveMediaItemToEvent([FromBody] MoveMediaItemToEventRequest request)
    {
        if (!Directory.Exists(request.Path))
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        var database = new LibraryDatabase(request.Path);
        database.EnsureSchema();
        var result = new LibraryRepository(database).MoveMediaItemToEvent(request.MediaItemId, request.TargetEventId);

        return result switch
        {
            MoveMediaItemResult.Success => NoContent(),
            MoveMediaItemResult.MediaItemNotFound => NotFound($"No MediaItem with id {request.MediaItemId}"),
            MoveMediaItemResult.TargetEventNotFound => NotFound($"No Event with id {request.TargetEventId}"),
            MoveMediaItemResult.SourceEventOrganized => Conflict("This item's current Event has already been organized into a real folder — moving it here would desync the database from what's actually on disk."),
            MoveMediaItemResult.TargetEventOrganized => Conflict("That Event has already been organized into a real folder — moving a file into it isn't supported here."),
            _ => StatusCode(500)
        };
    }

    public sealed record CreateTripGroupRequest(string Path, IReadOnlyList<long> EventIds);

    /// <summary>
    /// Manually groups a range of Events into a Trip — the escape hatch for
    /// whenever GPS-gated auto-formation (ScanService) doesn't apply. See
    /// LibraryRepository.CreateTripGroup.
    /// </summary>
    [HttpPost("trips")]
    public IActionResult CreateTripGroup([FromBody] CreateTripGroupRequest request)
    {
        if (!Directory.Exists(request.Path))
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        var database = new LibraryDatabase(request.Path);
        database.EnsureSchema();
        var (result, trip) = new LibraryRepository(database).CreateTripGroup(request.EventIds);

        return result switch
        {
            CreateTripGroupResult.Success => Ok(trip),
            CreateTripGroupResult.NoEventsGiven => BadRequest("Select at least one Event to group into a Trip."),
            CreateTripGroupResult.EventNotFound => NotFound("One of the selected Events no longer exists."),
            CreateTripGroupResult.EventAlreadyInATrip => Conflict("One of the selected Events already belongs to a Trip — ungroup it first."),
            _ => StatusCode(500)
        };
    }

    public sealed record UngroupTripRequest(string Path);

    /// <summary>Undoes a manually- or automatically-formed Trip — see LibraryRepository.UngroupTripGroup.</summary>
    [HttpPost("trips/{tripGroupId:long}/ungroup")]
    public IActionResult UngroupTripGroup(long tripGroupId, [FromBody] UngroupTripRequest request)
    {
        if (!Directory.Exists(request.Path))
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        var database = new LibraryDatabase(request.Path);
        database.EnsureSchema();

        return new LibraryRepository(database).UngroupTripGroup(tripGroupId)
            ? NoContent()
            : NotFound($"No TripGroup with id {tripGroupId}");
    }

    public sealed record RenameEventRequest(string Path, long EventId, string Title);
    public sealed record RenameTripGroupRequest(string Path, long TripGroupId, string Title);

    /// <summary>
    /// Manual title edit for an Event. Before Organize, this is a pure
    /// database write (there's no folder yet); afterward, it also renames the
    /// Event's real folder on disk to match — see <see cref="RenameService"/>.
    /// Guarded by the same <see cref="LibraryOperationGate"/> as Scan/
    /// Organize/Upload since, once organized, this moves a real folder.
    /// </summary>
    [HttpPost("events/rename")]
    public async Task<IActionResult> RenameEvent([FromBody] RenameEventRequest request)
    {
        if (!Directory.Exists(request.Path))
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        using var release = await gate.AcquireAsync(request.Path);
        var database = new LibraryDatabase(request.Path);
        database.EnsureSchema();

        var service = new RenameService(
            database, new LibraryQueryService(database), new LibraryRepository(database), new OrganizeRepository(database));

        var (success, errorMessage) = service.RenameEvent(request.EventId, request.Title);
        return success ? NoContent() : BadRequest(errorMessage);
    }

    /// <summary>Manual title edit for a TripGroup — see <see cref="RenameEvent"/> and <see cref="RenameService"/>.</summary>
    [HttpPost("trips/rename")]
    public async Task<IActionResult> RenameTripGroup([FromBody] RenameTripGroupRequest request)
    {
        if (!Directory.Exists(request.Path))
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        using var release = await gate.AcquireAsync(request.Path);
        var database = new LibraryDatabase(request.Path);
        database.EnsureSchema();

        var service = new RenameService(
            database, new LibraryQueryService(database), new LibraryRepository(database), new OrganizeRepository(database));

        var (success, errorMessage) = service.RenameTripGroup(request.TripGroupId, request.Title);
        return success ? NoContent() : BadRequest(errorMessage);
    }
}
