using Curato.Core;
using Curato.Organizer;

namespace Curato.Tests;

public class AdoptedFolderPlannerTests
{
    private static MediaItem Item(long id, string relativePath, DateTime takenAt) => new()
    {
        Id = id,
        RelativePath = relativePath,
        QuickHash = "irrelevant-for-this-test",
        TakenAt = takenAt
    };

    [Fact]
    public void Loose_root_items_are_not_treated_as_adopted()
    {
        var items = new[] { Item(1, "photo.jpg", new DateTime(2026, 7, 11, 10, 0, 0)) };
        var byFolder = FolderGrouper.GroupByContainingFolder(items);

        var adopted = AdoptedFolderPlanner.PlanEvents(byFolder);

        Assert.Empty(adopted);
    }

    [Fact]
    public void A_single_existing_folder_becomes_one_atomic_event_regardless_of_internal_gaps()
    {
        // PLAN.md §3 Step 0: two photos taken seconds apart but placed in
        // different folders must not merge; conversely a folder with a big
        // internal time gap must NOT get split — the folder boundary wins.
        var items = new[]
        {
            Item(1, "Family Reunion/morning.jpg", new DateTime(2026, 7, 11, 9, 0, 0)),
            Item(2, "Family Reunion/evening.jpg", new DateTime(2026, 7, 11, 22, 0, 0)),
        };
        var byFolder = FolderGrouper.GroupByContainingFolder(items);

        var adopted = AdoptedFolderPlanner.PlanEvents(byFolder);

        Assert.Single(adopted);
        Assert.Equal("Family Reunion", adopted[0].Title);
        Assert.Equal(NamingMode.Adopted, adopted[0].NamingMode);
        Assert.Equal([1L, 2L], adopted[0].MediaItemIds);
    }

    [Fact]
    public void Two_photos_seconds_apart_in_different_folders_stay_in_different_events()
    {
        var items = new[]
        {
            Item(1, "Folder A/photo.jpg", new DateTime(2026, 7, 11, 10, 0, 0)),
            Item(2, "Folder B/photo.jpg", new DateTime(2026, 7, 11, 10, 0, 5)),
        };
        var byFolder = FolderGrouper.GroupByContainingFolder(items);

        var adopted = AdoptedFolderPlanner.PlanEvents(byFolder);

        Assert.Equal(2, adopted.Count);
        Assert.DoesNotContain(adopted, e => e.MediaItemIds.Count > 1);
    }

    [Fact]
    public void A_folder_name_with_a_date_prefix_uses_the_title_portion_only()
    {
        var items = new[] { Item(1, "2019-08-03 Beach Day/photo.jpg", new DateTime(2019, 8, 3, 10, 0, 0)) };
        var byFolder = FolderGrouper.GroupByContainingFolder(items);

        var adopted = AdoptedFolderPlanner.PlanEvents(byFolder);

        Assert.Equal("Beach Day", adopted[0].Title);
    }

    [Fact]
    public void Two_level_nesting_groups_adopted_events_into_an_adopted_trip()
    {
        var items = new[]
        {
            Item(1, "2019 Summer/Day 1/photo.jpg", new DateTime(2019, 7, 10, 10, 0, 0)),
            Item(2, "2019 Summer/Day 2/photo.jpg", new DateTime(2019, 7, 11, 10, 0, 0)),
        };
        var byFolder = FolderGrouper.GroupByContainingFolder(items);
        var adoptedEvents = AdoptedFolderPlanner.PlanEvents(byFolder);

        // Simulate ids being assigned on save, as the real pipeline does.
        for (var i = 0; i < adoptedEvents.Count; i++)
        {
            adoptedEvents[i].Id = i + 1;
        }

        var trips = AdoptedFolderPlanner.PlanTrips(adoptedEvents);

        Assert.Single(trips);
        Assert.Equal("2019 Summer", trips[0].Title);
        Assert.Equal(NamingMode.Adopted, trips[0].NamingMode);
        Assert.Equal(2, trips[0].EventIds.Count);
    }

    [Fact]
    public void A_single_event_under_a_parent_folder_does_not_become_a_trip_of_one()
    {
        var items = new[] { Item(1, "2019 Summer/Day 1/photo.jpg", new DateTime(2019, 7, 10, 10, 0, 0)) };
        var byFolder = FolderGrouper.GroupByContainingFolder(items);
        var adoptedEvents = AdoptedFolderPlanner.PlanEvents(byFolder);
        adoptedEvents[0].Id = 1;

        var trips = AdoptedFolderPlanner.PlanTrips(adoptedEvents);

        Assert.Empty(trips);
    }
}
