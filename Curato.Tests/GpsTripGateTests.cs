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
}
