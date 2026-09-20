using Curato.Core;
using Curato.Data;
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
public sealed class LibraryController : ControllerBase
{
    public sealed record MediaItemDto(long Id, string RelativePath, string Kind, DateTime TakenAt, string? LiveVideoRelativePath);

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
            m.Id, m.RelativePath, m.Kind.ToString(), m.TakenAt,
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
}
