namespace Curato.Core;

public sealed record LivePhotoPair(long PhotoMediaItemId, long VideoMediaItemId);

/// <summary>
/// Detects "motion photo" pairs: a still-frame image plus a same-named video
/// clip sitting side by side in the same folder. Covers two real sidecar-pair
/// conventions: Apple Live Photos (JPEG/HEIC + `.MOV`, from iCloud/Photos
/// export) and Samsung/Google Motion Photos when exported or backed up as a
/// split `photo.jpg` + `photo.mp4` pair rather than their more common
/// single-file form (one JPEG with the clip embedded as trailing data plus an
/// XMP marker — that form has no "double files" problem to solve here at
/// all, since it's already one file; playing its embedded clip back would be
/// a separate feature: extracting the trailing MP4 via the XMP offset, not
/// pairing two MediaItems). The still-frame side matches any
/// <see cref="MediaKind.Photo"/> item — jpg, heic, png, a RAW format,
/// whatever <c>MediaFileScanner</c> recognizes. This only ever *identifies*
/// which two MediaItems represent one logical photo; nothing on disk is
/// touched, renamed, or merged, and both files remain independently tracked,
/// organized, and recoverable.
///
/// The sidecar-video extension list is a deliberately narrow, explicit
/// whitelist rather than "any video with the same name": two genuinely
/// unrelated files that happen to share a filename would otherwise get
/// wrongly paired and one hidden from the gallery. `.MOV` carries very low
/// collision risk since it's essentially exclusive to this convention;
/// `.mp4` is a much more generic, widely-used video format, so its
/// collision risk is real — a same-folder, same-name-stem coincidence is
/// still the dominant safeguard, but it's a weaker one for `.mp4` than for
/// `.MOV`. Worth revisiting (e.g. a stricter tolerance, or requiring a short
/// DurationMs once video metadata reading exists) if a false pair ever
/// actually shows up in practice.
/// </summary>
public static class LivePhotoPairer
{
    private static readonly HashSet<string> MotionSidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mov", // Apple Live Photos (iCloud/Photos export).
        ".mp4"  // Samsung/Google Motion Photos, when exported/backed up as a split pair.
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
