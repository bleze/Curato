using Curato.Core;

namespace Curato.Tests;

public class EventDateOutlierDetectorTests
{
    [Fact]
    public void Flags_a_single_item_stranded_decades_away_from_the_rest()
    {
        var items = new (long Id, DateTime TakenAt)[]
        {
            (1, new DateTime(2024, 4, 11, 9, 0, 0)),
            (2, new DateTime(2024, 4, 11, 9, 5, 0)),
            (3, new DateTime(2024, 4, 11, 9, 10, 0)),
            (4, new DateTime(1990, 1, 1, 12, 0, 0)), // camera clock never set
        };

        var outliers = EventDateOutlierDetector.FindOutlierMediaItemIds(items);

        Assert.Equal([4L], outliers);
    }

    [Fact]
    public void Does_not_flag_anything_in_a_genuine_multi_day_trip()
    {
        var items = new (long Id, DateTime TakenAt)[]
        {
            (1, new DateTime(2024, 4, 11)),
            (2, new DateTime(2024, 4, 12)),
            (3, new DateTime(2024, 4, 13)),
            (4, new DateTime(2024, 4, 14)),
            (5, new DateTime(2024, 4, 15)),
        };

        var outliers = EventDateOutlierDetector.FindOutlierMediaItemIds(items);

        Assert.Empty(outliers);
    }

    [Fact]
    public void Does_not_flag_a_small_same_day_spread()
    {
        var items = new (long Id, DateTime TakenAt)[]
        {
            (1, new DateTime(2024, 4, 11, 9, 0, 0)),
            (2, new DateTime(2024, 4, 11, 9, 30, 0)),
            (3, new DateTime(2024, 4, 11, 14, 0, 0)),
        };

        var outliers = EventDateOutlierDetector.FindOutlierMediaItemIds(items);

        Assert.Empty(outliers);
    }

    [Fact]
    public void Never_flags_anything_with_fewer_than_three_items()
    {
        var items = new (long Id, DateTime TakenAt)[]
        {
            (1, new DateTime(2024, 4, 11)),
            (2, new DateTime(1970, 1, 1)),
        };

        var outliers = EventDateOutlierDetector.FindOutlierMediaItemIds(items);

        Assert.Empty(outliers);
    }
}
