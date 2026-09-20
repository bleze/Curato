using Curato.Data;
using Curato.Organizer;
using Microsoft.AspNetCore.Mvc;

namespace Curato.Api.Controllers;

/// <summary>
/// PLAN.md §7 — "Find Duplicates" is a separate, manually-triggered action;
/// scanning/organizing never runs it on its own. Confirmed removals route
/// through the Recycle Bin (§5), same as any other deletion.
///
/// Protected by the fallback auth policy (§15) — requires a logged-in session.
/// </summary>
[ApiController]
[Route("api/duplicates")]
public sealed class DuplicateFinderController : ControllerBase
{
    public sealed record MediaItemDto(long Id, string RelativePath, string Kind, DateTime TakenAt);
    public sealed record DuplicateGroupDto(IReadOnlyList<MediaItemDto> Items, long SuggestedKeepId);

    public sealed record ResolveRequest(string Path, long KeepMediaItemId, IReadOnlyList<long> RemoveMediaItemIds);
    public sealed record ResolveResponse(int ItemsRemoved);

    [HttpPost("scan")]
    public ActionResult<IReadOnlyList<DuplicateGroupDto>> Scan([FromBody] ScanDuplicatesRequest request)
    {
        if (!Directory.Exists(request.Path))
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        var database = new LibraryDatabase(request.Path);
        database.EnsureSchema();
        var query = new LibraryQueryService(database);

        var service = new DuplicateFinderService(database, query, new LibraryRepository(database));
        var result = service.FindDuplicates();

        var mediaById = query.GetAllMediaItems().ToDictionary(m => m.Id);
        var groupDtos = result.Groups
            .Select(g => new DuplicateGroupDto(
                g.MediaItemIds
                    .Where(mediaById.ContainsKey)
                    .Select(id => mediaById[id])
                    .Select(m => new MediaItemDto(m.Id, m.RelativePath, m.Kind.ToString(), m.TakenAt))
                    .ToList(),
                g.SuggestedKeepId))
            .ToList();

        return Ok(groupDtos);
    }

    public sealed record ScanDuplicatesRequest(string Path);

    [HttpPost("resolve")]
    public ActionResult<ResolveResponse> Resolve([FromBody] ResolveRequest request)
    {
        if (!Directory.Exists(request.Path))
        {
            return NotFound($"Folder not found: {request.Path}");
        }

        var database = new LibraryDatabase(request.Path);
        database.EnsureSchema();
        var recycleBinService = new RecycleBinService(database, new RecycleBinRepository(database));

        var removed = 0;
        foreach (var mediaItemId in request.RemoveMediaItemIds)
        {
            if (mediaItemId == request.KeepMediaItemId)
            {
                continue; // never remove the one the user chose to keep
            }

            if (recycleBinService.DeleteMediaItem(mediaItemId, "DuplicateFinder") is not null)
            {
                removed++;
            }
        }

        return Ok(new ResolveResponse(removed));
    }
}
