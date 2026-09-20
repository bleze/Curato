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
    public sealed record MediaItemDto(long Id, string RelativePath, string Kind, DateTime TakenAt, string TakenAtSource, string? LiveVideoRelativePath);

    public sealed record EventDto(
        long Id, string Title, string NamingMode, string? FolderPath, string? ProposedFolderPath,
        DateTime StartAt, DateTime EndAt, IReadOnlyList<MediaItemDto> Items);

    public sealed record TripGroupDto(
        long Id, string Title, string NamingMode, string? FolderPath, string? ProposedFolderPath,
        DateOnly StartDate, DateOnly EndDate, IReadOnlyList<EventDto> Events);

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

        var mediaById = query.GetAllMediaItems().ToDictionary(m => m.Id);

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
                : null);

        EventDto ToEventDto(Event e) => new(
            e.Id, e.Title, e.NamingMode.ToString(), e.FolderPath, ProposeEventPath(e), e.StartAt, e.EndAt,
            e.MediaItemIds
                .Where(id => mediaById.ContainsKey(id) && !pairedVideoIds.Contains(id))
                .Select(id => mediaById[id])
                .Select(ToMediaItemDto)
                .ToList());

        var eventsById = events.ToDictionary(e => e.Id);
        var tripDtos = trips
            .Select(t => new TripGroupDto(
                t.Id, t.Title, t.NamingMode.ToString(), t.FolderPath, ProposeTripPath(t), t.StartDate, t.EndDate,
                t.EventIds
                    .Where(eventsById.ContainsKey)
                    .Select(id => ToEventDto(eventsById[id]))
                    .OrderBy(e => e.StartAt)
                    .ToList()))
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

        return service.RenameEvent(request.EventId, request.Title)
            ? NoContent()
            : BadRequest("Title cannot be empty, or no Event with that id exists.");
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

        return service.RenameTripGroup(request.TripGroupId, request.Title)
            ? NoContent()
            : BadRequest("Title cannot be empty, or no TripGroup with that id exists.");
    }
}
