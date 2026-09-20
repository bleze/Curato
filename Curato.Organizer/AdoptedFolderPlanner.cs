using Curato.Core;

namespace Curato.Organizer;

/// <summary>
/// PLAN.md §3, Step 0 — turns existing folders into Adopted Events/TripGroups.
/// Pure: no file moves, no renames, nothing written to disk or the database.
/// Every existing folder is trusted as a single atomic unit exactly as the
/// user made it; this never re-clusters a folder's contents by time gaps.
/// </summary>
public static class AdoptedFolderPlanner
{
    /// <summary>
    /// One Adopted Event per existing folder that directly contains media —
    /// <paramref name="items"/> must already have real database ids (i.e. be
    /// saved). Event.Id is left unset; the caller assigns it on save, the same
    /// way the auto-organize path already works.
    /// </summary>
    public static IReadOnlyList<Event> PlanEvents(ILookup<string, MediaItem> itemsByFolder)
    {
        return itemsByFolder
            .Where(group => group.Key != FolderGrouper.LooseItemsKey)
            .Select(group => BuildEvent(group.Key, group.ToList()))
            .ToList();
    }

    /// <summary>
    /// Groups Adopted Events that sit exactly one folder below a shared
    /// parent into an Adopted TripGroup for that parent (PLAN.md §3, Step 0's
    /// "obvious two-level nesting" case) — e.g. `2019 Summer/Day 1`,
    /// `2019 Summer/Day 2` become two Events under one `2019 Summer` trip.
    /// Deeper or shallower nesting is left as standalone Events; not every
    /// folder shape needs a trip guessed for it.
    /// <paramref name="adoptedEvents"/> must already have real ids (saved).
    /// </summary>
    public static IReadOnlyList<TripGroup> PlanTrips(IReadOnlyList<Event> adoptedEvents)
    {
        return adoptedEvents
            .Where(e => e.FolderPath is { Length: > 0 })
            .Select(e => (Event: e, Parent: FolderGrouper.GetParentFolder(e.FolderPath!)))
            .Where(x => x.Parent.Length > 0)
            .GroupBy(x => x.Parent)
            .Where(g => g.Count() > 1) // a single Event under a folder isn't "a trip"
            .Select(g => BuildTripGroup(g.Key, g.Select(x => x.Event).ToList()))
            .ToList();
    }

    private static Event BuildEvent(string folderPath, List<MediaItem> items)
    {
        var sorted = items.OrderBy(i => i.TakenAt).ToList();
        var (_, title) = FolderTitleParser.Parse(FolderGrouper.GetLeafName(folderPath));

        return new Event
        {
            StartAt = sorted[0].TakenAt,
            EndAt = sorted[^1].TakenAt,
            Title = title,
            TitleIsAuto = false,
            FolderPath = folderPath,
            NamingMode = NamingMode.Adopted,
            MediaItemIds = sorted.Select(i => i.Id).ToList()
        };
    }

    private static TripGroup BuildTripGroup(string folderPath, List<Event> events)
    {
        var (_, title) = FolderTitleParser.Parse(FolderGrouper.GetLeafName(folderPath));

        return new TripGroup
        {
            StartDate = DateOnly.FromDateTime(events.Min(e => e.StartAt)),
            EndDate = DateOnly.FromDateTime(events.Max(e => e.EndAt)),
            Title = title,
            TitleIsAuto = false,
            FolderPath = folderPath,
            NamingMode = NamingMode.Adopted,
            EventIds = events.Select(e => e.Id).ToList()
        };
    }
}
