using Curato.Core;

namespace Curato.Tests;

public class EventClustererTests
{
    private static MediaItem Item(long id, DateTime takenAt) => new()
    {
        Id = id,
        RelativePath = $"item-{id}.jpg",
        QuickHash = "irrelevant-for-clustering",
        TakenAt = takenAt
    };

    [Fact]
    public void Photos_taken_close_together_form_one_event()
    {
        var clusterer = new EventClusterer { GapThreshold = TimeSpan.FromHours(4) };
        var items = new[]
        {
            Item(1, new DateTime(2026, 7, 11, 10, 0, 0)),
            Item(2, new DateTime(2026, 7, 11, 10, 5, 0)),
            Item(3, new DateTime(2026, 7, 11, 10, 12, 0)),
        };

        var events = clusterer.Cluster(items);

        Assert.Single(events);
        Assert.Equal([1L, 2L, 3L], events[0].MediaItemIds);
    }

    [Fact]
    public void A_multi_hour_lull_on_the_same_day_splits_into_two_events()
    {
        // PLAN.md §3 Step 1: morning outing -> 6-hour gap -> evening event.
        var clusterer = new EventClusterer { GapThreshold = TimeSpan.FromHours(4) };
        var items = new[]
        {
            Item(1, new DateTime(2026, 7, 11, 9, 0, 0)),
            Item(2, new DateTime(2026, 7, 11, 10, 0, 0)),
            Item(3, new DateTime(2026, 7, 11, 19, 0, 0)),
            Item(4, new DateTime(2026, 7, 11, 20, 0, 0)),
        };

        var events = clusterer.Cluster(items);

        Assert.Equal(2, events.Count);
        Assert.Equal([1L, 2L], events[0].MediaItemIds);
        Assert.Equal([3L, 4L], events[1].MediaItemIds);
    }

    [Fact]
    public void A_party_crossing_midnight_stays_in_one_event()
    {
        var clusterer = new EventClusterer { GapThreshold = TimeSpan.FromHours(4) };
        var items = new[]
        {
            Item(1, new DateTime(2026, 7, 11, 22, 30, 0)),
            Item(2, new DateTime(2026, 7, 11, 23, 45, 0)),
            Item(3, new DateTime(2026, 7, 12, 0, 30, 0)),
            Item(4, new DateTime(2026, 7, 12, 1, 15, 0)),
        };

        var events = clusterer.Cluster(items);

        Assert.Single(events);
        Assert.Equal([1L, 2L, 3L, 4L], events[0].MediaItemIds);
    }

    [Fact]
    public void A_single_item_still_gets_its_own_event()
    {
        var clusterer = new EventClusterer();
        var events = clusterer.Cluster([Item(1, new DateTime(2026, 7, 11, 10, 0, 0))]);

        Assert.Single(events);
        Assert.Equal([1L], events[0].MediaItemIds);
    }
}
