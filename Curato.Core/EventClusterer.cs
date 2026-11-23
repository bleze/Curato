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
    /// Confirmed live as a real gap: moving a single straggler photo into an
    /// otherwise-tight Event (e.g. one shot the night before, well past this
    /// Event's own <see cref="GapThreshold"/> from everything else in it)
    /// dragged the whole Event's displayed StartAt back by a full day even
    /// though every other member still clustered normally — a plain min/max
    /// over every member (LibraryRepository.ResortEventMembership's old
    /// behavior) has no concept of "this one doesn't actually belong with
    /// the rest in time." Reapplies the exact same gap rule <see cref="Cluster"/>
    /// uses to form an Event from scratch — including correctly keeping a
    /// real midnight-crossing night out as one unbroken run — and returns
    /// just that run's boundary when it's a genuine majority of the members,
    /// rather than a flat "largest wins" rule. The straggler stays a member either way;
    /// this only decides what StartAt/EndAt (and therefore the Event's
    /// displayed date, sort position, and title's time-of-day bucket) say.
    /// </summary>
    public (DateTime StartAt, DateTime EndAt) ComputeDominantRange(IReadOnlyList<DateTime> takenAts)
    {
        var sorted = takenAts.OrderBy(t => t).ToList();
        var runs = new List<List<DateTime>>();

        foreach (var takenAt in sorted)
        {
            var lastRun = runs.Count > 0 ? runs[^1] : null;
            if (lastRun is null || takenAt - lastRun[^1] > GapThreshold)
            {
                runs.Add([takenAt]);
            }
            else
            {
                lastRun.Add(takenAt);
            }
        }

        if (runs.Count == 1)
        {
            return (runs[0][0], runs[0][^1]);
        }

        // Only overrides the full span when one run is a genuine majority
        // (strictly more members than every other run combined) — a close
        // call (two same-size runs, or no single run standing out) has no
        // real "this is the outlier" story to tell, so it falls back to the
        // full span across everything instead, same as before this existed.
        // Confirmed live as needed: without this, deliberately combining two
        // comparably-sized, genuinely separate Events (one far-away single
        // photo joining another single photo, say) got its *minority* side
        // silently dropped from the boundary instead of the two being
        // treated as equally real.
        var largest = runs.OrderByDescending(r => r.Count).First();
        var isGenuineMajority = largest.Count > sorted.Count - largest.Count;

        return isGenuineMajority
            ? (largest[0], largest[^1])
            : (sorted[0], sorted[^1]);
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
