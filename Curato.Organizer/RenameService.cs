using Curato.Core;
using Curato.Data;

namespace Curato.Organizer;

/// <summary>
/// PLAN.md §3/§4 — manual title edits for an Event or TripGroup. Before
/// Organize gives CuratoManaged content a real folder (FolderPath is still
/// null), there's nothing on disk to rename — this only updates the Title in
/// the database, same as any other field edit. Once a folder exists, though,
/// the folder-naming template (§3, Step 4) renders the title directly into
/// the path, so leaving the old folder name in place after a rename would
/// make the library view and the actual disk layout disagree — the folder is
/// renamed to match right away, cascading the new path down to every
/// MediaItem (and, for a Trip, every nested Event) whose RelativePath/
/// FolderPath sits under it.
///
/// Deliberately never renames an *Adopted* folder — PLAN.md §4's hard rule
/// that "Adopted archives keep whatever structure they already had" applies
/// here too: an Adopted Event/Trip's folder is real, pre-existing structure
/// the user made themselves outside Curato, not something Curato's naming
/// template owns. Editing an Adopted item's title only ever corrects how
/// Curato displays it — the folder on disk is intentionally left alone.
/// </summary>
public sealed class RenameService(
    LibraryDatabase database, LibraryQueryService query, LibraryRepository repository, OrganizeRepository organizeRepository)
{
    public (bool Success, string? ErrorMessage) RenameEvent(long eventId, string newTitle)
    {
        newTitle = newTitle.Trim();
        if (!FolderNameValidator.IsValid(newTitle, out var validationError))
        {
            return (false, validationError);
        }

        var @event = query.GetAllEvents().SingleOrDefault(e => e.Id == eventId);
        if (@event is null || !repository.SetEventTitle(eventId, newTitle))
        {
            return (false, "No Event with that id exists.");
        }

        if (@event.NamingMode != NamingMode.CuratoManaged || @event.FolderPath is not { } oldFolderPath)
        {
            return (true, null); // no folder yet, or an Adopted one this never touches
        }

        var trip = @event.TripGroupId is { } tripId
            ? query.GetAllTripGroups().SingleOrDefault(t => t.Id == tripId)
            : null;

        @event.Title = newTitle;
        var candidatePath = FolderNameTemplate.RenderEventPath(@event, trip);
        if (candidatePath == oldFolderPath)
        {
            return (true, null);
        }

        var newFolderPath = ResolveCollisionFreeFolderPath(candidatePath);
        MoveFolderAndCascadeMediaItemPaths(oldFolderPath, newFolderPath, @event.MediaItemIds);
        organizeRepository.SetEventFolderPath(eventId, newFolderPath);
        return (true, null);
    }

    public (bool Success, string? ErrorMessage) RenameTripGroup(long tripId, string newTitle)
    {
        newTitle = newTitle.Trim();
        if (!FolderNameValidator.IsValid(newTitle, out var validationError))
        {
            return (false, validationError);
        }

        var trip = query.GetAllTripGroups().SingleOrDefault(t => t.Id == tripId);
        if (trip is null || !repository.SetTripGroupTitle(tripId, newTitle))
        {
            return (false, "No TripGroup with that id exists.");
        }

        if (trip.NamingMode != NamingMode.CuratoManaged || trip.FolderPath is not { } oldFolderPath)
        {
            return (true, null);
        }

        trip.Title = newTitle;
        var candidatePath = FolderNameTemplate.RenderTripPath(trip);
        if (candidatePath == oldFolderPath)
        {
            return (true, null);
        }

        var newFolderPath = ResolveCollisionFreeFolderPath(candidatePath);
        var nestedEvents = query.GetAllEvents().Where(e => e.TripGroupId == tripId && e.FolderPath is not null).ToList();
        var allMediaItemIds = nestedEvents.SelectMany(e => e.MediaItemIds).ToList();

        // One Directory.Move on the trip's own folder carries every nested
        // Event subfolder and file along with it — only the DB's path
        // strings need rewriting after, not the files themselves.
        MoveFolderAndCascadeMediaItemPaths(oldFolderPath, newFolderPath, allMediaItemIds);

        organizeRepository.SetTripFolderPath(tripId, newFolderPath);
        foreach (var nestedEvent in nestedEvents)
        {
            var rewrittenEventPath = newFolderPath + nestedEvent.FolderPath![oldFolderPath.Length..];
            organizeRepository.SetEventFolderPath(nestedEvent.Id, rewrittenEventPath);
        }

        return (true, null);
    }

    private void MoveFolderAndCascadeMediaItemPaths(
        string oldFolderPath, string newFolderPath, IReadOnlyList<long> mediaItemIds)
    {
        var libraryRoot = database.LibraryRoot;
        var oldFullPath = Path.Combine(libraryRoot, oldFolderPath);
        var newFullPath = Path.Combine(libraryRoot, newFolderPath);

        if (Directory.Exists(oldFullPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newFullPath)!);
            Directory.Move(oldFullPath, newFullPath);
        }

        var itemsById = query.GetAllMediaItems().ToDictionary(m => m.Id);
        var oldPrefix = oldFolderPath + "/";
        foreach (var mediaItemId in mediaItemIds)
        {
            if (!itemsById.TryGetValue(mediaItemId, out var item) ||
                !item.RelativePath.StartsWith(oldPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            organizeRepository.UpdateMediaItemPath(mediaItemId, newFolderPath + item.RelativePath[oldFolderPath.Length..]);
        }
    }

    /// <summary>
    /// Guards against two Events/Trips landing on the exact same rendered
    /// folder name (e.g. renaming two different days to the same title) —
    /// same " (2)"-suffix convention <c>OrganizeService</c> already uses for
    /// colliding filenames, just one level up for folders.
    /// </summary>
    private string ResolveCollisionFreeFolderPath(string candidatePath)
    {
        var libraryRoot = database.LibraryRoot;
        if (!Directory.Exists(Path.Combine(libraryRoot, candidatePath)))
        {
            return candidatePath;
        }

        var counter = 2;
        string suffixed;
        do
        {
            suffixed = $"{candidatePath} ({counter})";
            counter++;
        } while (Directory.Exists(Path.Combine(libraryRoot, suffixed)));

        return suffixed;
    }
}
