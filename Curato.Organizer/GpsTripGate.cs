using Curato.Core;

namespace Curato.Organizer;

/// <summary>
/// The location signal TripClusterer's pure time-gap grouping was always
/// missing (PLAN.md §3 Step 2/§12) — without it, a candidate Trip can't be
/// told apart from an ordinary run of days with photos on them, which is why
/// ScanService never auto-created Trips before this existed. Pure/testable:
/// no DB, no network — ScanService supplies the GPS-tagged MediaItems and
/// decides what to do with a qualifying group.
/// </summary>
public static class GpsTripGate
{
    /// <summary>How far from "home" a candidate Trip's own location has to be
    /// before it's treated as a real trip rather than everyday life nearby.</summary>
    public const double AwayFromHomeThresholdKm = 50.0;

    /// <summary>An Event's representative point — the average of whichever of
    /// its members actually carry GPS coordinates; null if none do (an Event
    /// with no GPS-tagged members has no location opinion of its own, but can
    /// still be a member of an otherwise-qualifying Trip).</summary>
    public static (double Lat, double Lon)? RepresentativeLocation(Event @event, IReadOnlyDictionary<long, MediaItem> itemsById)
    {
        var points = @event.MediaItemIds
            .Select(id => itemsById.GetValueOrDefault(id))
            .Where(item => item is { Latitude: not null, Longitude: not null })
            .Select(item => (item!.Latitude!.Value, item.Longitude!.Value))
            .ToList();

        return points.Count == 0 ? null : (points.Average(p => p.Item1), points.Average(p => p.Item2));
    }

    /// <summary>
    /// An approximation of "wherever this library's owner lives": the most
    /// common ~1km-rounded GPS bucket across every GPS-tagged MediaItem in
    /// the library (matches <see cref="Curato.Data.ReverseGeocoder"/>'s own
    /// bucket size). Null once there's no GPS data at all, in which case the
    /// gate below never passes — an all-timestamp library behaves exactly as
    /// it always has, zero auto-formed Trips.
    /// </summary>
    public static (double Lat, double Lon)? DetectHomeLocation(IEnumerable<MediaItem> allItems)
    {
        var withGps = allItems
            .Where(item => item.Latitude is not null && item.Longitude is not null)
            .ToList();

        if (withGps.Count == 0)
        {
            return null;
        }

        return withGps
            .GroupBy(item => (Lat: Math.Round(item.Latitude!.Value, 2), Lon: Math.Round(item.Longitude!.Value, 2)))
            .OrderByDescending(g => g.Count())
            .Select(g => ((double Lat, double Lon)?)(g.Average(i => i.Latitude!.Value), g.Average(i => i.Longitude!.Value)))
            .First();
    }

    public static bool IsAwayFromHome((double Lat, double Lon)? home, (double Lat, double Lon)? location) =>
        home is { } h && location is { } loc &&
        GeoDistance.HaversineKm(h.Lat, h.Lon, loc.Lat, loc.Lon) > AwayFromHomeThresholdKm;
}
