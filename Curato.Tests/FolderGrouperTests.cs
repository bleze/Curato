using Curato.Core;

namespace Curato.Tests;

public class FolderGrouperTests
{
    private static MediaItem Item(string relativePath) => new()
    {
        RelativePath = relativePath,
        QuickHash = "irrelevant",
        TakenAt = new DateTime(2026, 1, 1),
    };

    [Fact]
    public void An_item_directly_under_the_import_folder_is_treated_as_loose()
    {
        var byFolder = FolderGrouper.GroupByContainingFolder([Item(".Import/a.jpg")]);

        Assert.Single(byFolder[FolderGrouper.LooseItemsKey]);
    }

    [Fact]
    public void An_item_nested_arbitrarily_deep_under_the_import_folder_is_still_loose()
    {
        var byFolder = FolderGrouper.GroupByContainingFolder([Item(".Import/Family/2024/Summer/a.jpg")]);

        Assert.Single(byFolder[FolderGrouper.LooseItemsKey]);
    }

    [Fact]
    public void A_folder_merely_sharing_the_import_folders_name_prefix_is_not_treated_as_loose()
    {
        // ".ImportedStuff" must not be swept up just because it starts with
        // the same characters as ".Import" — only an exact folder name or a
        // real subfolder of it should count.
        var byFolder = FolderGrouper.GroupByContainingFolder([Item(".ImportedStuff/a.jpg")]);

        Assert.Empty(byFolder[FolderGrouper.LooseItemsKey]);
        Assert.Single(byFolder[".ImportedStuff"]);
    }

    [Fact]
    public void An_item_in_an_ordinary_subfolder_is_still_adopted_as_before()
    {
        var byFolder = FolderGrouper.GroupByContainingFolder([Item("2026/2026-01-01 Trip/a.jpg")]);

        Assert.Empty(byFolder[FolderGrouper.LooseItemsKey]);
        Assert.Single(byFolder["2026/2026-01-01 Trip"]);
    }
}
