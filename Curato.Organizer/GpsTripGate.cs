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

    /// <summary>How close a point has to be to "home" for the user's own
    /// <see cref="Curato.Data.HomeLocationRepository"/> label to override
    /// whatever Nominatim would otherwise return for it. Confirmed live that
    /// 15km was much too generous — Danish towns sit close enough together
    /// that it swallowed a genuinely different, real nearby city's photos
    /// under the home label (Kirke Såby vs. Roskilde, ~10km apart, both
    /// showing as "Kirke Såby"). Tight enough to cover GPS noise/rounding
    /// right around the literal home address, not a whole neighboring town —
    /// well clear of <see cref="AwayFromHomeThresholdKm"/> either way
    /// (nothing that would ever qualify as "away" should also be eligible
    /// for the home override).</summary>
    public const double HomeOverrideRadiusKm = 3.0;

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

    /// <summary>
    /// Trims a time-contiguous candidate group (Event ids already sorted by
    /// StartAt, as TripClusterer produces) down to the span from its first
    /// to its last away-from-home member. Confirmed live as a real gap:
    /// TripClusterer's own time-gap chaining has no bound on how long a
    /// contiguous run can grow — a long string of ordinary at-home days can
    /// bridge all the way to a real trip if no single gap in the chain is
    /// wide enough to break it — and checking only whether *something,
    /// anywhere* in that run is away from home doesn't stop weeks of home
    /// padding on either side from being swept in wholesale once it does. A
    /// "home" or "unknown" (no GPS) member is kept if it falls *within* the
    /// trimmed span (plausibly a transit/indoor day of the real trip) but
    /// dropped if it sits outside it. Empty if nothing in the group is away
    /// from home at all.
    /// </summary>
    public static IReadOnlyList<long> TrimToAwayCore(
        IReadOnlyList<long> eventIdsSortedByTime,
        IReadOnlyDictionary<long, (double Lat, double Lon)?> eventLocations,
        (double Lat, double Lon)? home)
    {
        var awayIndexes = eventIdsSortedByTime
            .Select((id, index) => (Index: index, IsAway: IsAwayFromHome(home, eventLocations[id])))
            .Where(x => x.IsAway)
            .Select(x => x.Index)
            .ToList();

        if (awayIndexes.Count == 0)
        {
            return [];
        }

        return eventIdsSortedByTime.Skip(awayIndexes[0]).Take(awayIndexes[^1] - awayIndexes[0] + 1).ToList();
    }

    /// <summary>
    /// The user's custom "Home" label (Settings), if <paramref name="point"/>
    /// is close enough to the auto-detected home location to use it instead
    /// of a real reverse-geocode — null (meaning "no override, geocode
    /// normally") whenever there's no home location, no custom label set, or
    /// the point is too far away to plausibly still be "home".
    /// </summary>
    public static string? HomeOverrideLabel((double Lat, double Lon)? home, string? homeLabel, (double Lat, double Lon) point) =>
        home is { } h && homeLabel is not null &&
        GeoDistance.HaversineKm(h.Lat, h.Lon, point.Lat, point.Lon) <= HomeOverrideRadiusKm
            ? homeLabel
            : null;
}
