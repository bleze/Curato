using Curato.Core;

namespace Curato.Tests;

public class FolderNameTemplateTests
{
    [Fact]
    public void Standalone_event_gets_a_year_prefixed_path()
    {
        var @event = new Event
        {
            StartAt = new DateTime(2026, 9, 19, 18, 0, 0),
            EndAt = new DateTime(2026, 9, 19, 21, 0, 0),
            Title = "Birthday",
            NamingMode = NamingMode.CuratoManaged
        };

        Assert.Equal("2026/2026-09-19 Birthday", FolderNameTemplate.RenderEventPath(@event, trip: null));
    }

    [Fact]
    public void Event_inside_a_trip_nests_under_the_trips_path_without_repeating_the_year()
    {
        var trip = new TripGroup
        {
            StartDate = new DateOnly(2026, 7, 10),
            EndDate = new DateOnly(2026, 7, 14),
            Title = "Summer Vacation",
            NamingMode = NamingMode.CuratoManaged
        };
        var @event = new Event
        {
            StartAt = new DateTime(2026, 7, 11, 20, 0, 0),
            EndAt = new DateTime(2026, 7, 11, 22, 0, 0),
            Title = "Evening Bonfire",
            NamingMode = NamingMode.CuratoManaged
        };

        Assert.Equal(
            "2026/2026-07-10 to 2026-07-14 Summer Vacation/2026-07-11 Evening Bonfire",
            FolderNameTemplate.RenderEventPath(@event, trip));
    }

    [Fact]
    public void Trip_spanning_a_year_boundary_uses_the_start_dates_year()
    {
        var trip = new TripGroup
        {
            StartDate = new DateOnly(2026, 12, 30),
            EndDate = new DateOnly(2027, 1, 2),
            Title = "New Year",
            NamingMode = NamingMode.CuratoManaged
        };

        Assert.Equal("2026/2026-12-30 to 2027-01-02 New Year", FolderNameTemplate.RenderTripPath(trip));
    }
}
