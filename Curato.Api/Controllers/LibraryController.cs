using Curato.Core;
using Curato.Data;
using Microsoft.AspNetCore.Mvc;

namespace Curato.Api.Controllers;

/// <summary>
/// Read-only view of an already-scanned library, for the web UI (§11).
///
/// Protected by the fallback auth policy (§15) — requires a logged-in session.
/// </summary>
[ApiController]
[Route("api")]
public sealed class LibraryController : ControllerBase
{
    public sealed record MediaItemDto(long Id, string RelativePath, string Kind, DateTime TakenAt);

    public sealed record EventDto(
        long Id, string Title, string NamingMode, string? FolderPath,
        DateTime StartAt, DateTime EndAt, IReadOnlyList<MediaItemDto> Items);

    public sealed record TripGroupDto(
        long Id, string Title, string NamingMode, string? FolderPath,
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
        var events = query.GetAllEvents();
        var trips = query.GetAllTripGroups();

        EventDto ToEventDto(Event e) => new(
            e.Id, e.Title, e.NamingMode.ToString(), e.FolderPath, e.StartAt, e.EndAt,
            e.MediaItemIds
                .Where(mediaById.ContainsKey)
                .Select(id => mediaById[id])
                .Select(m => new MediaItemDto(m.Id, m.RelativePath, m.Kind.ToString(), m.TakenAt))
                .ToList());

        var eventsById = events.ToDictionary(e => e.Id);
        var tripDtos = trips
            .Select(t => new TripGroupDto(
                t.Id, t.Title, t.NamingMode.ToString(), t.FolderPath, t.StartDate, t.EndDate,
                t.EventIds.Where(eventsById.ContainsKey).Select(id => ToEventDto(eventsById[id])).ToList()))
            .ToList();

        var standaloneEvents = events
            .Where(e => e.TripGroupId is null)
            .Select(ToEventDto)
            .OrderBy(e => e.StartAt)
            .ToList();

        return Ok(new LibraryDto(tripDtos, standaloneEvents));
    }
}
