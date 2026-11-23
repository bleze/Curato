using Curato.Core;

namespace Curato.Tests;

public class EventClustererComputeDominantRangeTests
{
    [Fact]
    public void A_single_contiguous_run_just_uses_its_own_full_span()
    {
        var clusterer = new EventClusterer();
        var takenAts = new[]
        {
            new DateTime(2026, 3, 10, 14, 0, 0),
            new DateTime(2026, 3, 10, 16, 0, 0),
            new DateTime(2026, 3, 10, 18, 0, 0),
        };

        var (startAt, endAt) = clusterer.ComputeDominantRange(takenAts);

        Assert.Equal(new DateTime(2026, 3, 10, 14, 0, 0), startAt);
        Assert.Equal(new DateTime(2026, 3, 10, 18, 0, 0), endAt);
    }

    [Fact]
    public void A_midnight_crossing_run_within_the_gap_threshold_is_never_split()
    {
        var clusterer = new EventClusterer();
        var takenAts = new[]
        {
            new DateTime(2026, 3, 10, 21, 0, 0),
            new DateTime(2026, 3, 10, 23, 30, 0),
            new DateTime(2026, 3, 11, 1, 0, 0), // past midnight, but <4h gap from the previous
        };

        var (startAt, endAt) = clusterer.ComputeDominantRange(takenAts);

        Assert.Equal(new DateTime(2026, 3, 10, 21, 0, 0), startAt);
        Assert.Equal(new DateTime(2026, 3, 11, 1, 0, 0), endAt);
    }

    [Fact]
    public void A_small_minority_run_is_excluded_from_the_boundary()
    {
        var clusterer = new EventClusterer();
        var takenAts = new List<DateTime> { new(2026, 3, 9, 23, 50, 0) }; // lone straggler, >4h from the rest
        takenAts.AddRange(Enumerable.Range(0, 10).Select(i => new DateTime(2026, 3, 10, 14, i, 0)));

        var (startAt, endAt) = clusterer.ComputeDominantRange(takenAts);

        Assert.Equal(new DateTime(2026, 3, 10, 14, 0, 0), startAt);
        Assert.Equal(new DateTime(2026, 3, 10, 14, 9, 0), endAt);
    }

    [Fact]
    public void Two_equally_sized_runs_have_no_real_majority_so_the_full_span_is_kept()
    {
        var clusterer = new EventClusterer();
        var takenAts = new[]
        {
            new DateTime(2026, 3, 9, 9, 0, 0),
            new DateTime(2026, 3, 14, 9, 0, 0), // >4h gap, separate run, same size as the first
        };

        var (startAt, endAt) = clusterer.ComputeDominantRange(takenAts);

        Assert.Equal(new DateTime(2026, 3, 9, 9, 0, 0), startAt);
        Assert.Equal(new DateTime(2026, 3, 14, 9, 0, 0), endAt);
    }

    [Fact]
    public void A_near_tie_with_no_single_run_over_half_also_keeps_the_full_span()
    {
        var clusterer = new EventClusterer();
        // 4 + 3 + 3 = 10 total; the largest run (4) is not a majority (not > 6).
        var takenAts = new List<DateTime>();
        takenAts.AddRange(Enumerable.Range(0, 4).Select(i => new DateTime(2026, 1, 1, 9, i, 0)));
        takenAts.AddRange(Enumerable.Range(0, 3).Select(i => new DateTime(2026, 1, 6, 9, i, 0)));
        takenAts.AddRange(Enumerable.Range(0, 3).Select(i => new DateTime(2026, 1, 11, 9, i, 0)));

        var (startAt, endAt) = clusterer.ComputeDominantRange(takenAts);

        Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0), startAt);
        Assert.Equal(new DateTime(2026, 1, 11, 9, 2, 0), endAt);
    }
}
