namespace Curato.Core;

/// <summary>
/// PLAN.md §3, Step 2 — groups consecutive Events into a TripGroup when
/// they're close together in time *and* the result spans multiple days. A
/// single busy day that produced two Events should not become a "trip" of
/// its own.
/// </summary>
public sealed class TripClusterer
{
    public TimeSpan GapThreshold { get; init; } = TimeSpan.FromHours(36);
    public int MinSpanDays { get; init; } = 2;

    public IReadOnlyList<TripGroup> Cluster(IReadOnlyList<Event> events)
    {
        var sorted = events.OrderBy(e => e.StartAt).ToList();
        var groups = new List<List<Event>>();

        foreach (var @event in sorted)
        {
            var lastGroup = groups.Count > 0 ? groups[^1] : null;
            if (lastGroup is null || @event.StartAt - lastGroup[^1].EndAt > GapThreshold)
            {
                groups.Add([@event]);
            }
            else
            {
                lastGroup.Add(@event);
            }
        }

        return groups
            .Where(group => SpanDays(group) >= MinSpanDays)
            .Select(ToTripGroup)
            .ToList();
    }

    private static int SpanDays(List<Event> group)
    {
        var startDate = DateOnly.FromDateTime(group[0].StartAt);
        var endDate = DateOnly.FromDateTime(group[^1].EndAt);
        return endDate.DayNumber - startDate.DayNumber + 1;
    }

    private static TripGroup ToTripGroup(List<Event> group)
    {
        var startDate = DateOnly.FromDateTime(group[0].StartAt);
        var endDate = DateOnly.FromDateTime(group[^1].EndAt);
        return new TripGroup
        {
            StartDate = startDate,
            EndDate = endDate,
            Title = $"{startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}",
            TitleIsAuto = true,
            NamingMode = NamingMode.CuratoManaged,
            EventIds = group.Select(e => e.Id).ToList()
        };
    }
}
