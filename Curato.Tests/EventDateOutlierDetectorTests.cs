using Curato.Core;

namespace Curato.Tests;

public class EventDateOutlierDetectorTests
{
    private static (long Id, DateTime TakenAt, TakenAtSource Source) Item(
        long id, DateTime takenAt, TakenAtSource source = TakenAtSource.ExifDateTaken) => (id, takenAt, source);

    [Fact]
    public void Flags_a_single_item_stranded_decades_away_from_the_rest()
    {
        var items = new[]
        {
            Item(1, new DateTime(2024, 4, 11, 9, 0, 0)),
            Item(2, new DateTime(2024, 4, 11, 9, 5, 0)),
            Item(3, new DateTime(2024, 4, 11, 9, 10, 0)),
            Item(4, new DateTime(1990, 1, 1, 12, 0, 0)), // camera clock never set
        };

        var outliers = EventDateOutlierDetector.FindOutlierMediaItemIds(items);

        Assert.Equal([4L], outliers);
    }

    [Fact]
    public void Does_not_flag_anything_in_a_genuine_multi_day_trip()
    {
        var items = new[]
        {
            Item(1, new DateTime(2024, 4, 11)),
            Item(2, new DateTime(2024, 4, 12)),
            Item(3, new DateTime(2024, 4, 13)),
            Item(4, new DateTime(2024, 4, 14)),
            Item(5, new DateTime(2024, 4, 15)),
        };

        var outliers = EventDateOutlierDetector.FindOutlierMediaItemIds(items);

        Assert.Empty(outliers);
    }

    [Fact]
    public void Does_not_flag_a_small_same_day_spread()
    {
        var items = new[]
        {
            Item(1, new DateTime(2024, 4, 11, 9, 0, 0)),
            Item(2, new DateTime(2024, 4, 11, 9, 30, 0)),
            Item(3, new DateTime(2024, 4, 11, 14, 0, 0)),
        };

        var outliers = EventDateOutlierDetector.FindOutlierMediaItemIds(items);

        Assert.Empty(outliers);
    }

    [Fact]
    public void Never_flags_anything_with_fewer_than_three_items()
    {
        var items = new[]
        {
            Item(1, new DateTime(2024, 4, 11)),
            Item(2, new DateTime(1970, 1, 1)),
        };

        var outliers = EventDateOutlierDetector.FindOutlierMediaItemIds(items);

        Assert.Empty(outliers);
    }

    /// <summary>
    /// Confirmed live against "Tesla vinterhjul": 4 photos lost their Exif
    /// in transit and fell back to a shared filesystem/sync timestamp
    /// months later, outnumbering the 3 photos that still had their real
    /// Exif date. A pure majority vote picked the 4 filesystem-dated ones as
    /// "normal" and flagged the 3 *correct* ones instead — reliability, not
    /// headcount, has to decide which side is the anchor.
    /// </summary>
    [Fact]
    public void Prefers_reliable_sources_over_a_larger_unreliable_majority()
    {
        var items = new[]
        {
            Item(1, new DateTime(2024, 1, 12, 12, 15, 13)),
            Item(2, new DateTime(2024, 1, 12, 12, 15, 21)),
            Item(3, new DateTime(2024, 1, 12, 12, 16, 7)),
            Item(4, new DateTime(2024, 9, 26, 19, 56, 36), TakenAtSource.FileSystem),
            Item(5, new DateTime(2024, 9, 26, 19, 56, 36), TakenAtSource.FileSystem),
            Item(6, new DateTime(2024, 9, 26, 19, 56, 36), TakenAtSource.FileSystem),
            Item(7, new DateTime(2024, 9, 26, 19, 56, 36), TakenAtSource.FileSystem),
        };

        var outliers = EventDateOutlierDetector.FindOutlierMediaItemIds(items);

        Assert.Equal([4L, 5L, 6L, 7L], outliers.OrderBy(x => x));
    }

    [Fact]
    public void Does_not_flag_an_unreliable_source_that_actually_agrees_with_the_reliable_dates()
    {
        var items = new[]
        {
            Item(1, new DateTime(2024, 1, 12, 12, 15, 13)),
            Item(2, new DateTime(2024, 1, 12, 12, 15, 21)),
            Item(3, new DateTime(2024, 1, 12, 13, 0, 0), TakenAtSource.FileSystem),
        };

        var outliers = EventDateOutlierDetector.FindOutlierMediaItemIds(items);

        Assert.Empty(outliers);
    }

    [Fact]
    public void Falls_back_to_a_plain_majority_when_nothing_in_the_event_is_reliably_sourced()
    {
        var items = new[]
        {
            Item(1, new DateTime(2024, 4, 11, 9, 0, 0), TakenAtSource.FileSystem),
            Item(2, new DateTime(2024, 4, 11, 9, 5, 0), TakenAtSource.FileSystem),
            Item(3, new DateTime(2024, 4, 11, 9, 10, 0), TakenAtSource.FileSystem),
            Item(4, new DateTime(1990, 1, 1, 12, 0, 0), TakenAtSource.FileSystem),
        };

        var outliers = EventDateOutlierDetector.FindOutlierMediaItemIds(items);

        Assert.Equal([4L], outliers);
    }

    [Fact]
    public void Never_flags_a_manually_confirmed_date_even_if_it_looks_isolated()
    {
        var items = new[]
        {
            Item(1, new DateTime(2024, 4, 11, 9, 0, 0)),
            Item(2, new DateTime(2024, 4, 11, 9, 5, 0)),
            Item(3, new DateTime(1970, 1, 1), TakenAtSource.Manual),
        };

        var outliers = EventDateOutlierDetector.FindOutlierMediaItemIds(items);

        Assert.Empty(outliers);
    }
}
