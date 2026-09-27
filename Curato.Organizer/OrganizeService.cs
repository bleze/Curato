using Curato.Core;
using Curato.Data;

namespace Curato.Organizer;

public sealed record OrganizeResult(int EventsOrganized, int FilesMoved, int FilesSkippedMissing);

internal enum MoveOutcome { AlreadyInPlace, Moved, SourceMissing }

/// <summary>
/// PLAN.md §4 — physically arranges CuratoManaged Events/Trips into the
/// folder-naming template's structure (§3, Step 4). Deliberately a separate,
/// explicit action from scanning: a scan only ever indexes and clusters, so
/// nothing about pointing Curato at a folder ever touches a file on its own
/// — Organize is the one action that does, and only for content the user
/// can already see proposed in the library view first.
///
/// Moves only ever happen *within* the library root (a same-volume rename,
/// per §4 — nothing here copies from an external source yet; that's §4a's
/// browser-upload path, not built yet either). Filename collisions get a
/// " (2)"-style suffix; every move is logged for a future undo feature.
/// </summary>
public sealed class OrganizeService(LibraryDatabase database, LibraryQueryService query, OrganizeRepository organizeRepository)
{
    public OrganizeResult Organize()
    {
        var libraryRoot = database.LibraryRoot;
        var allEvents = query.GetAllEvents();
        var tripsById = query.GetAllTripGroups().ToDictionary(t => t.Id);
        var itemsById = query.GetAllMediaItems().ToDictionary(m => m.Id);

        var unorganizedEvents = allEvents
            .Where(e => e.NamingMode == NamingMode.CuratoManaged && e.FolderPath is null)
            .ToList();

        if (unorganizedEvents.Count == 0)
        {
            return new OrganizeResult(0, 0, 0);
        }

        var batchId = organizeRepository.CreateBatch();
        var claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tripsAlreadyGivenAPath = new HashSet<long>();
        var filesMoved = 0;
        var filesSkippedMissing = 0;

        foreach (var @event in unorganizedEvents)
        {
            var trip = ResolveTripFolderPath(@event, tripsById, tripsAlreadyGivenAPath);
            var eventFolderPath = FolderNameTemplate.RenderEventPath(@event, trip);

            foreach (var mediaItemId in @event.MediaItemIds)
            {
                if (!itemsById.TryGetValue(mediaItemId, out var item))
                {
                    continue;
                }

                switch (MoveItemIfNeeded(libraryRoot, batchId, item, eventFolderPath, claimedPaths))
                {
                    case MoveOutcome.Moved:
                        filesMoved++;
                        break;
                    case MoveOutcome.SourceMissing:
                        filesSkippedMissing++;
                        break;
                }
            }

            // Still set even if a member's file turned out to be missing —
            // FolderPath is the right destination for every member whose
            // file actually exists; the missing one is a pre-existing
            // desync (something changed the file outside Curato since the
            // last scan) this doesn't cause and can't itself fix. A Scan
            // (not Organize) is what reconciles that: it already routes
            // anything no longer found on disk through the Recycle Bin.
            organizeRepository.SetEventFolderPath(@event.Id, eventFolderPath);
        }

        return new OrganizeResult(unorganizedEvents.Count, filesMoved, filesSkippedMissing);
    }

    private TripGroup? ResolveTripFolderPath(
        Event @event, Dictionary<long, TripGroup> tripsById, HashSet<long> tripsAlreadyGivenAPath)
    {
        if (@event.TripGroupId is not { } tripId || !tripsById.TryGetValue(tripId, out var trip))
        {
            return null;
        }

        if (trip.NamingMode == NamingMode.CuratoManaged && trip.FolderPath is null && tripsAlreadyGivenAPath.Add(trip.Id))
        {
            trip.FolderPath = FolderNameTemplate.RenderTripPath(trip);
            organizeRepository.SetTripFolderPath(trip.Id, trip.FolderPath);
        }

        return trip;
    }

    /// <summary>
    /// Confirmed live as a real crash, not just a hypothetical: something
    /// renamed/moved/deleted a file outside Curato after the last Scan, and
    /// this blindly trusted the stale RelativePath and let File.Move's
    /// FileNotFoundException blow up the whole Organize request — including
    /// every OTHER event's move still queued behind it in the same batch.
    /// A missing source is now just skipped (reported back, not silently
    /// swallowed) so one stale item can't take an entire Organize run down
    /// with it.
    /// </summary>
    private MoveOutcome MoveItemIfNeeded(
        string libraryRoot, long batchId, MediaItem item, string eventFolderPath, HashSet<string> claimedPaths)
    {
        var oldRelativePath = item.RelativePath;
        var oldFullPath = Path.Combine(libraryRoot, oldRelativePath);
        if (!File.Exists(oldFullPath))
        {
            return MoveOutcome.SourceMissing;
        }

        var fileName = Path.GetFileName(oldRelativePath);
        var newRelativePath = CollisionFreePath.Resolve(libraryRoot, eventFolderPath, fileName, claimedPaths);
        claimedPaths.Add(newRelativePath);

        var newFullPath = Path.Combine(libraryRoot, newRelativePath);
        if (string.Equals(Path.GetFullPath(oldFullPath), Path.GetFullPath(newFullPath), StringComparison.OrdinalIgnoreCase))
        {
            return MoveOutcome.AlreadyInPlace;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(newFullPath)!);
        File.Move(oldFullPath, newFullPath);

        organizeRepository.RecordMove(batchId, item.Id, oldRelativePath, newRelativePath);
        organizeRepository.UpdateMediaItemPath(item.Id, newRelativePath);
        item.RelativePath = newRelativePath;
        return MoveOutcome.Moved;
    }
}
