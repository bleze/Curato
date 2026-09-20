using Curato.Core;

namespace Curato.Tests;

public class LivePhotoPairerTests
{
    private static MediaItem Item(long id, string relativePath, MediaKind kind, DateTime takenAt) => new()
    {
        Id = id,
        RelativePath = relativePath,
        Kind = kind,
        QuickHash = "irrelevant",
        TakenAt = takenAt,
    };

    [Fact]
    public void Pairs_a_jpg_and_same_named_mov_in_the_same_folder()
    {
        var takenAt = new DateTime(2026, 1, 1, 12, 0, 0);
        var items = new[]
        {
            Item(1, "Trip/IMG_0001.JPG", MediaKind.Photo, takenAt),
            Item(2, "Trip/IMG_0001.MOV", MediaKind.Video, takenAt.AddSeconds(1)),
        };

        var pairs = LivePhotoPairer.FindPairs(items);

        var pair = Assert.Single(pairs);
        Assert.Equal(1, pair.PhotoMediaItemId);
        Assert.Equal(2, pair.VideoMediaItemId);
    }

    [Fact]
    public void Does_not_pair_files_in_different_folders()
    {
        var takenAt = new DateTime(2026, 1, 1, 12, 0, 0);
        var items = new[]
        {
            Item(1, "A/IMG_0001.JPG", MediaKind.Photo, takenAt),
            Item(2, "B/IMG_0001.MOV", MediaKind.Video, takenAt),
        };

        Assert.Empty(LivePhotoPairer.FindPairs(items));
    }

    [Fact]
    public void Does_not_pair_a_video_that_is_not_a_mov()
    {
        var takenAt = new DateTime(2026, 1, 1, 12, 0, 0);
        var items = new[]
        {
            Item(1, "IMG_0001.JPG", MediaKind.Photo, takenAt),
            Item(2, "IMG_0001.MP4", MediaKind.Video, takenAt),
        };

        Assert.Empty(LivePhotoPairer.FindPairs(items));
    }

    [Fact]
    public void Does_not_pair_timestamps_that_are_too_far_apart()
    {
        var items = new[]
        {
            Item(1, "IMG_0001.JPG", MediaKind.Photo, new DateTime(2026, 1, 1, 12, 0, 0)),
            Item(2, "IMG_0001.MOV", MediaKind.Video, new DateTime(2026, 1, 1, 12, 5, 0)), // 5 minutes later
        };

        Assert.Empty(LivePhotoPairer.FindPairs(items));
    }

    [Fact]
    public void Matching_is_case_insensitive_on_the_filename_stem()
    {
        var takenAt = new DateTime(2026, 1, 1, 12, 0, 0);
        var items = new[]
        {
            Item(1, "img_0001.jpg", MediaKind.Photo, takenAt),
            Item(2, "IMG_0001.MOV", MediaKind.Video, takenAt),
        };

        Assert.Single(LivePhotoPairer.FindPairs(items));
    }
}
