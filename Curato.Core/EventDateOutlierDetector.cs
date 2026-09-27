namespace Curato.Core;

/// <summary>
/// Flags an Event's own members whose TakenAt sits far outside the rest of
/// that Event — the classic "camera clock wasn't set" symptom: one or two
/// stray photos land on some wildly different date (often decades off)
/// while every other photo in the same folder/moment is correctly dated.
/// A per-item nudge, complementing the per-folder ".Before" convention
/// (FolderTitleParser.IsBeforeDigitalScansFolder) for exactly the folders
/// that convention doesn't cover — ones with a perfectly normal name and
/// mostly-correct dates, just one bad apple dragging the whole Event's
/// displayed date range out to match it.
/// </summary>
public static class EventDateOutlierDetector
{
    // Median-absolute-deviation (MAD) based rather than a fixed day-count
    // threshold — a real multi-day trip has every item some days apart from
    // the median, but *similarly* so, which keeps MAD itself large and the
    // ratio low; a single rogue item stands out precisely because nothing
    // else in the Event is anywhere near as far from the median.
    private const double MadMultiplier = 6.0;
    // Without a floor, a same-day event's near-zero MAD would make even a
    // few hours' difference technically "outlier" by the ratio test alone.
    private const double MinimumDeviationDays = 2.0;

    // A FileSystem/ExifModifyDate item that disagrees with a real Exif/
    // video/manually-confirmed date anywhere else in the same Event is
    // suspect even by a single day — confirmed live: a "majority vote"
    // between four filesystem-dated items sharing one stale copy/sync
    // timestamp and three genuinely Exif-dated ones picked the *larger*
    // group as "normal" and flagged the three correct ones instead, purely
    // because the wrong dates outnumbered the right ones. Reliability, not
    // headcount, has to decide which group is the anchor.
    private const double UnreliableSourceDeviationDays = 1.0;

    // Consumer digital cameras (and therefore genuine Exif capture dates)
    // didn't exist before ~1990 — the same bound MediaFileScanner's own
    // QuickTime-epoch fix and RepairWrongEpochVideoTakenAt already use for
    // videos. A photo whose Exif reads earlier than this isn't "an old
    // photo", it's a camera whose clock was never set at all: confirmed
    // live for a folder where 7 of 8 photos all read TakenAtSource
    // ExifDateTaken yet were seconds apart starting at exactly
    // 1980-01-01T00:00:00 — the camera's power-on default, ticking forward
    // normally from there. That nominal "reliable" source tag doesn't make
    // the date real, so this check runs before — and regardless of —
    // IsAnchor below. Manual is exempt: a person may have deliberately set
    // a real pre-digital date for scanned photos (see
    // FolderTitleParser.IsBeforeDigitalScansFolder).
    private const int EarliestPlausibleDigitalYear = 1990;

    private static bool IsImplausible(TakenAtSource source, DateTime takenAt) =>
        source != TakenAtSource.Manual && takenAt.Year < EarliestPlausibleDigitalYear;

    // ExifDateTaken/VideoMetadata came straight from the file's own
    // metadata; Manual was explicitly confirmed by a person. Either is
    // trusted as an anchor other items get compared against — Manual is
    // additionally never itself flaggable (PLAN.md §1: a person already
    // vouched for it, the same reasoning RepairWrongEpochVideoTakenAt
    // exempts it from automatic correction for).
    private static bool IsAnchor(TakenAtSource source) =>
        source is TakenAtSource.ExifDateTaken or TakenAtSource.VideoMetadata or TakenAtSource.Manual;

    public static HashSet<long> FindOutlierMediaItemIds(IReadOnlyList<(long Id, DateTime TakenAt, TakenAtSource Source)> items)
    {
        if (items.Count < 3)
        {
            return [];
        }

        var outliers = new HashSet<long>();

        var implausibleIds = items.Where(i => IsImplausible(i.Source, i.TakenAt)).Select(i => i.Id).ToHashSet();
        foreach (var id in implausibleIds)
        {
            outliers.Add(id);
        }

        // Whatever's left (excluding the already-flagged implausible ones)
        // gets the normal reliability/statistical comparison — there might
        // still be a subtler disagreement among otherwise-plausible dates
        // (e.g. Tesla vinterhjul's filesystem-vs-Exif split, no implausible
        // years involved at all).
        var remaining = items.Where(i => !implausibleIds.Contains(i.Id)).ToList();
        if (remaining.Count < 3)
        {
            return outliers;
        }

        var anchors = remaining.Where(i => IsAnchor(i.Source)).ToList();
        var nonAnchors = remaining.Where(i => !IsAnchor(i.Source)).ToList();

        // No trustworthy anchor at all (e.g. every remaining item only ever
        // got a filesystem timestamp) — nothing to prefer over anything
        // else, so fall back to comparing everything against everything,
        // same as before this reliability split existed.
        if (anchors.Count == 0)
        {
            foreach (var id in FindStatisticalOutliers(remaining.Select(i => (i.Id, i.TakenAt)).ToList()))
            {
                outliers.Add(id);
            }

            return outliers;
        }

        // Statistical pass *within* the anchor subset — still catches a
        // single mis-set camera clock even when every item in the Event
        // nominally has "real" Exif data. Manual dates count toward the
        // median/spread math (they're trustworthy data points) but are
        // filtered back out below — never themselves flagged.
        foreach (var id in FindStatisticalOutliers(anchors.Select(i => (i.Id, i.TakenAt)).ToList()))
        {
            if (anchors.First(a => a.Id == id).Source != TakenAtSource.Manual)
            {
                outliers.Add(id);
            }
        }

        var anchorMedianDays = Median(anchors.Select(i => ToDays(i.TakenAt)).OrderBy(d => d).ToList());
        foreach (var item in nonAnchors)
        {
            if (Math.Abs(ToDays(item.TakenAt) - anchorMedianDays) > UnreliableSourceDeviationDays)
            {
                outliers.Add(item.Id);
            }
        }

        return outliers;
    }

    private static HashSet<long> FindStatisticalOutliers(IReadOnlyList<(long Id, DateTime TakenAt)> items)
    {
        // Fewer than 3 items means there's no real "typical" cluster to be
        // an outlier from — flagging one of only two dates as "the wrong
        // one" would just be a coin flip.
        if (items.Count < 3)
        {
            return [];
        }

        var sortedDays = items.Select(i => ToDays(i.TakenAt)).OrderBy(d => d).ToList();
        var medianDays = Median(sortedDays);

        var deviations = items.Select(i => Math.Abs(ToDays(i.TakenAt) - medianDays)).OrderBy(d => d).ToList();
        var madDays = Median(deviations);

        var threshold = Math.Max(madDays * MadMultiplier, MinimumDeviationDays);

        var outliers = new HashSet<long>();
        foreach (var item in items)
        {
            if (Math.Abs(ToDays(item.TakenAt) - medianDays) > threshold)
            {
                outliers.Add(item.Id);
            }
        }

        return outliers;
    }

    private static double ToDays(DateTime dateTime) => dateTime.Ticks / (double)TimeSpan.TicksPerDay;

    private static double Median(List<double> sortedValues)
    {
        var mid = sortedValues.Count / 2;
        return sortedValues.Count % 2 == 0
            ? (sortedValues[mid - 1] + sortedValues[mid]) / 2
            : sortedValues[mid];
    }
}
