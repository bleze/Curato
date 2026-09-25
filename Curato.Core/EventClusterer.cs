namespace Curato.Core;

/// <summary>
/// PLAN.md §3, Step 1 — gap-based Event clustering. Pure and deterministic:
/// given a set of MediaItems, starts a new Event whenever the gap to the
/// previous item (sorted by TakenAt) exceeds <see cref="GapThreshold"/>. This
/// is what keeps a midnight-crossing party in one Event and splits a single
/// calendar day into two when there's a multi-hour lull.
///
/// Deliberately does not know about folders — PLAN.md §3, Step 0's
/// "existing folders are always the boundary" rule is applied by the caller,
/// which only ever hands this clusterer the loose/unstructured items it's
/// allowed to auto-organize.
/// </summary>
public sealed class EventClusterer
{
    public TimeSpan GapThreshold { get; init; } = TimeSpan.FromHours(4);

    public IReadOnlyList<Event> Cluster(IEnumerable<MediaItem> items)
    {
        var sorted = items.OrderBy(item => item.TakenAt).ToList();
        var groups = new List<List<MediaItem>>();

        foreach (var item in sorted)
        {
            var lastGroup = groups.Count > 0 ? groups[^1] : null;
            if (lastGroup is null || item.TakenAt - lastGroup[^1].TakenAt > GapThreshold)
            {
                groups.Add([item]);
            }
            else
            {
                lastGroup.Add(item);
            }
        }

        return groups.Select(ToEvent).ToList();
    }

    private static Event ToEvent(List<MediaItem> group)
    {
        var startAt = group[0].TakenAt;
        return new Event
        {
            StartAt = startAt,
            EndAt = group[^1].TakenAt,
            Title = AutoTitle(startAt),
            TitleIsAuto = true,
            NamingMode = NamingMode.CuratoManaged,
            MediaItemIds = group.Select(m => m.Id).ToList()
        };
    }

    /// <summary>
    /// Public so a single-item manual move (LibraryRepository.MoveMediaItemToEvent)
    /// can title the new standalone Event it creates the same way a fresh
    /// cluster would, instead of duplicating this time-of-day bucketing.
    /// </summary>
    public static string AutoTitle(DateTime startAt) => startAt.Hour switch
    {
        >= 5 and < 12 => "Morning",
        >= 12 and < 17 => "Afternoon",
        >= 17 and < 21 => "Evening",
        _ => "Night"
    };
}
