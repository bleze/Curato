using Curato.Organizer;

namespace Curato.Tests;

public class GpsTripGateTests
{
    private const double HomeLat = 55.6761;
    private const double HomeLon = 12.5683;

    [Fact]
    public void HomeOverrideLabel_returns_the_label_for_a_point_near_home()
    {
        // ~1.3km from home — well inside HomeOverrideRadiusKm.
        var nearby = (Lat: 55.6861, Lon: 12.5683);
        Assert.Equal("Our House", GpsTripGate.HomeOverrideLabel((HomeLat, HomeLon), "Our House", nearby));
    }

    [Fact]
    public void HomeOverrideLabel_returns_null_for_a_point_far_from_home()
    {
        var rome = (Lat: 41.3874, Lon: 12.4939);
        Assert.Null(GpsTripGate.HomeOverrideLabel((HomeLat, HomeLon), "Our House", rome));
    }

    [Fact]
    public void HomeOverrideLabel_returns_null_when_no_label_is_configured()
    {
        Assert.Null(GpsTripGate.HomeOverrideLabel((HomeLat, HomeLon), null, (HomeLat, HomeLon)));
    }

    [Fact]
    public void HomeOverrideLabel_returns_null_when_home_is_undetected()
    {
        Assert.Null(GpsTripGate.HomeOverrideLabel(null, "Our House", (HomeLat, HomeLon)));
    }

    [Fact]
    public void Away_from_home_threshold_and_home_override_radius_never_overlap()
    {
        // If these ever crossed, a single point could be both "away enough to
        // start a Trip" and "close enough for the Home label" at once — the
        // two features would contradict each other.
        Assert.True(GpsTripGate.HomeOverrideRadiusKm < GpsTripGate.AwayFromHomeThresholdKm);
    }

    private static readonly (double Lat, double Lon) Home = (HomeLat, HomeLon);
    private static readonly (double Lat, double Lon) Away = (41.3874, 12.4939); // Rome

    [Fact]
    public void TrimToAwayCore_keeps_only_the_span_from_first_to_last_away_member()
    {
        var ids = new long[] { 1, 2, 3, 4, 5 };
        var locations = new Dictionary<long, (double Lat, double Lon)?>
        {
            [1] = Home, [2] = Home, [3] = Away, [4] = Away, [5] = Home,
        };

        var trimmed = GpsTripGate.TrimToAwayCore(ids, locations, Home);

        Assert.Equal([3L, 4L], trimmed);
    }

    [Fact]
    public void TrimToAwayCore_keeps_a_no_gps_member_sandwiched_between_two_away_members()
    {
        var ids = new long[] { 1, 2, 3, 4 };
        var locations = new Dictionary<long, (double Lat, double Lon)?>
        {
            [1] = Home, [2] = Away, [3] = null, [4] = Away,
        };

        var trimmed = GpsTripGate.TrimToAwayCore(ids, locations, Home);

        Assert.Equal([2L, 3L, 4L], trimmed);
    }

    [Fact]
    public void TrimToAwayCore_returns_empty_when_nothing_is_away()
    {
        var ids = new long[] { 1, 2 };
        var locations = new Dictionary<long, (double Lat, double Lon)?> { [1] = Home, [2] = null };

        Assert.Empty(GpsTripGate.TrimToAwayCore(ids, locations, Home));
    }
}
