namespace Curato.Core;

public sealed record LivePhotoPair(long PhotoMediaItemId, long VideoMediaItemId);

/// <summary>
/// Detects "motion photo" pairs: a still-frame image plus a same-named video
/// clip sitting side by side in the same folder — the shape Apple's Live
/// Photos take when exported from iCloud/Photos (a JPEG/HEIC + `.MOV`). The
/// still frame side matches any <see cref="MediaKind.Photo"/> item — jpg,
/// heic, png, a RAW format, whatever <c>MediaFileScanner</c> recognizes —
/// since Apple's format is about the paired *video* file, not the photo
/// format. This only ever *identifies* which two MediaItems represent one
/// logical photo; nothing on disk is touched, renamed, or merged, and both
/// files remain independently tracked, organized, and recoverable.
///
/// The sidecar-video extension list is a deliberately narrow, explicit
/// whitelist rather than "any video with the same name": two genuinely
/// unrelated files that happen to share a filename (e.g. IMG_0001.JPG and an
/// unrelated IMG_0001.MP4 from a different export) would otherwise get
/// wrongly paired and one hidden from the gallery. Every format added here
/// should be one actually known to be a still+motion sidecar convention, not
/// just "a video format that exists" — currently only Apple's `.MOV`.
/// </summary>
public static class LivePhotoPairer
{
    private static readonly HashSet<string> MotionSidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mov" // Apple Live Photos (iCloud/Photos export) — the only convention currently handled.
    };

    // Generous enough for filesystem timestamp jitter between the two
    // sidecar files, tight enough that two unrelated files sharing a name
    // by coincidence (rare, given they'd also need to share a folder) won't
    // accidentally pair.
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(5);

    public static IReadOnlyList<LivePhotoPair> FindPairs(IEnumerable<MediaItem> items)
    {
        var pairs = new List<LivePhotoPair>();
        var byFolder = FolderGrouper.GroupByContainingFolder(items);

        foreach (var folder in byFolder)
        {
            var photosByStem = new Dictionary<string, MediaItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var photo in folder.Where(i => i.Kind == MediaKind.Photo))
            {
                photosByStem.TryAdd(Stem(photo.RelativePath), photo);
            }

            foreach (var video in folder.Where(i => i.Kind == MediaKind.Video && IsMotionSidecar(i.RelativePath)))
            {
                if (photosByStem.TryGetValue(Stem(video.RelativePath), out var photo) &&
                    (photo.TakenAt - video.TakenAt).Duration() <= TimestampTolerance)
                {
                    pairs.Add(new LivePhotoPair(photo.Id, video.Id));
                }
            }
        }

        return pairs;
    }

    private static string Stem(string relativePath) => Path.GetFileNameWithoutExtension(relativePath);

    private static bool IsMotionSidecar(string relativePath) =>
        MotionSidecarExtensions.Contains(Path.GetExtension(relativePath));
}
