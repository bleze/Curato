using Curato.Core;

namespace Curato.Tests;

public class TripClustererTests
{
    private static Event MakeEvent(long id, DateTime startAt, DateTime endAt) => new()
    {
        Id = id,
        StartAt = startAt,
        EndAt = endAt,
        Title = "Event",
        NamingMode = NamingMode.CuratoManaged
    };

    [Fact]
    public void Nearby_events_spanning_multiple_days_become_a_trip()
    {
        var clusterer = new TripClusterer { GapThreshold = TimeSpan.FromHours(36), MinSpanDays = 2 };
        var events = new[]
        {
            MakeEvent(1, new DateTime(2026, 7, 10, 9, 0, 0), new DateTime(2026, 7, 10, 18, 0, 0)),
            MakeEvent(2, new DateTime(2026, 7, 11, 9, 0, 0), new DateTime(2026, 7, 11, 20, 0, 0)),
            MakeEvent(3, new DateTime(2026, 7, 12, 9, 0, 0), new DateTime(2026, 7, 12, 20, 0, 0)),
        };

        var trips = clusterer.Cluster(events);

        Assert.Single(trips);
        Assert.Equal([1L, 2L, 3L], trips[0].EventIds);
        Assert.Equal(new DateOnly(2026, 7, 10), trips[0].StartDate);
        Assert.Equal(new DateOnly(2026, 7, 12), trips[0].EndDate);
    }

    [Fact]
    public void A_single_busy_day_with_two_events_does_not_become_a_trip()
    {
        // PLAN.md §3 Step 2: a single busy day producing 2 Events should *not*
        // become a "trip" of its own.
        var clusterer = new TripClusterer { GapThreshold = TimeSpan.FromHours(36), MinSpanDays = 2 };
        var events = new[]
        {
            MakeEvent(1, new DateTime(2026, 7, 11, 9, 0, 0), new DateTime(2026, 7, 11, 12, 0, 0)),
            MakeEvent(2, new DateTime(2026, 7, 11, 19, 0, 0), new DateTime(2026, 7, 11, 22, 0, 0)),
        };

        var trips = clusterer.Cluster(events);

        Assert.Empty(trips);
    }

    [Fact]
    public void Events_far_apart_in_time_do_not_merge_into_one_trip()
    {
        var clusterer = new TripClusterer { GapThreshold = TimeSpan.FromHours(36), MinSpanDays = 2 };
        var events = new[]
        {
            MakeEvent(1, new DateTime(2026, 7, 10, 9, 0, 0), new DateTime(2026, 7, 10, 18, 0, 0)),
            MakeEvent(2, new DateTime(2026, 9, 19, 9, 0, 0), new DateTime(2026, 9, 20, 18, 0, 0)),
        };

        var trips = clusterer.Cluster(events);

        Assert.Single(trips);
        Assert.Equal([2L], trips[0].EventIds);
    }
}
