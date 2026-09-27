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

    public static HashSet<long> FindOutlierMediaItemIds(IReadOnlyList<(long Id, DateTime TakenAt)> items)
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
