using System.Text.Json;
using Dapper;

namespace Curato.Data;

/// <summary>
/// Turns GPS coordinates into a short place name, for the location text next
/// to dates and for GPS-aware Trip formation (PLAN.md §12, scoped down from
/// the full Map view — no tiles/pins yet, just names). The one deliberate
/// exception to Curato's local-only posture: coordinates leave the server as
/// an anonymous reverse-geocoding request, same trust boundary §12 already
/// calls out for map tiles.
///
/// Caches by coordinate bucket (<see cref="INominatimClient"/>'s caller
/// rounds to ~1km) in <c>GeocodeCache</c> so a personal library's naturally
/// clustered GPS points — the same handful of real places, over and over —
/// cost only a handful of real network calls, ever. A cache miss enforces
/// Nominatim's own "no more than 1 request/second" usage policy via a
/// process-wide last-call timestamp; a network failure returns null and
/// leaves nothing cached (worth retrying later) rather than throwing —
/// location is cosmetic and must never block a scan or the library view.
/// </summary>
public sealed class ReverseGeocoder(LibraryDatabase database, INominatimClient client, TimeSpan? minIntervalBetweenCalls = null)
{
    // Register this type as a singleton in DI (Program.cs) so this instance —
    // and therefore the rate limit below — is genuinely shared across every
    // request in the process, not reset per-controller-instantiation.
    // Instance state (not static) so tests can construct an isolated
    // geocoder with a near-zero interval instead of eating a real second per
    // uncached lookup.
    private readonly TimeSpan _minInterval = minIntervalBetweenCalls ?? TimeSpan.FromSeconds(1);
    private readonly object _rateLimitLock = new();
    private DateTime _nextCallAllowedAtUtc = DateTime.MinValue;

    /// <summary>
    /// Cache-only — never calls Nominatim, never blocks on the rate limit.
    /// For a latency-sensitive read path (the library view) that would
    /// otherwise have to synchronously resolve every distinct place a whole
    /// library was ever photographed at, one at a time at 1 req/sec, the
    /// first time anyone loads it after new GPS data lands. Real resolution
    /// happens out-of-band instead — see GeocodeWarmupBackgroundService
    /// (Curato.Api) — so a location simply isn't shown yet for anything the
    /// warmer hasn't gotten to, rather than the page hanging until it has.
    /// </summary>
    public string? TryGetCached(double latitude, double longitude)
    {
        var (latBucket, lonBucket) = Bucket(latitude, longitude);
        using var connection = database.OpenConnection();
        return connection.QuerySingleOrDefault<string?>("""
            SELECT PlaceName FROM GeocodeCache WHERE LatBucket = @LatBucket AND LonBucket = @LonBucket
            """, new { LatBucket = latBucket, LonBucket = lonBucket });
    }

    public string? Resolve(double latitude, double longitude)
    {
        var (latBucket, lonBucket) = Bucket(latitude, longitude);

        var cached = TryGetCached(latitude, longitude);
        if (cached is not null)
        {
            return cached;
        }

        EnforceRateLimit();

        string? placeName;
        try
        {
            placeName = client.ReverseGeocode(latitude, longitude);
        }
        catch (Exception)
        {
            // Network hiccup, DNS failure, Nominatim outage, etc. — never let
            // a cosmetic lookup break the caller (a scan, or the library view).
            return null;
        }

        if (placeName is null)
        {
            return null;
        }

        using (var connection = database.OpenConnection())
        {
            connection.Execute("""
                INSERT INTO GeocodeCache (LatBucket, LonBucket, PlaceName, ResolvedAt)
                VALUES (@LatBucket, @LonBucket, @PlaceName, @ResolvedAt)
                ON CONFLICT (LatBucket, LonBucket) DO UPDATE SET PlaceName = excluded.PlaceName, ResolvedAt = excluded.ResolvedAt
                """, new
            {
                LatBucket = latBucket,
                LonBucket = lonBucket,
                PlaceName = placeName,
                ResolvedAt = DateTime.UtcNow.ToString("O")
            });
        }

        return placeName;
    }

    /// <summary>~1km buckets — precise enough that two real places never
    /// collide, coarse enough that "the same trip to the same city" shares
    /// one lookup no matter how many photos were taken there.</summary>
    private static (double LatBucket, double LonBucket) Bucket(double latitude, double longitude) =>
        (Math.Round(latitude, 2), Math.Round(longitude, 2));

    private void EnforceRateLimit()
    {
        TimeSpan waitTime;
        lock (_rateLimitLock)
        {
            var now = DateTime.UtcNow;
            waitTime = _nextCallAllowedAtUtc > now ? _nextCallAllowedAtUtc - now : TimeSpan.Zero;
            _nextCallAllowedAtUtc = (waitTime > TimeSpan.Zero ? _nextCallAllowedAtUtc : now) + _minInterval;
        }

        if (waitTime > TimeSpan.Zero)
        {
            Thread.Sleep(waitTime);
        }
    }
}

/// <summary>Isolates the actual HTTP call so tests never hit the real network.</summary>
public interface INominatimClient
{
    /// <summary>Null on any failure (network error, no result, unparseable response).</summary>
    string? ReverseGeocode(double latitude, double longitude);
}

/// <summary>
/// Real Nominatim (OpenStreetMap) client — free, no API key, but its usage
/// policy requires a real identifying User-Agent and no more than one
/// request/second (enforced by <see cref="ReverseGeocoder"/>, not here).
/// </summary>
public sealed class NominatimClient : INominatimClient
{
    private static readonly HttpClient Http = BuildClient();

    private static HttpClient BuildClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Curato/1.0 (self-hosted personal photo library; https://github.com/)");
        return http;
    }

    public string? ReverseGeocode(double latitude, double longitude)
    {
        var url = $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat={latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}&lon={longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}&zoom=10&addressdetails=1";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = Http.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var stream = response.Content.ReadAsStream();
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("address", out var address))
        {
            return document.RootElement.TryGetProperty("display_name", out var displayName)
                ? displayName.GetString()
                : null;
        }

        // Prefer the smallest place name a photo caption actually wants
        // (town/city, not house numbers or postcodes), falling back wider
        // when a rural/remote point has no city-level entry.
        string? place = FirstPresent(address, "city", "town", "village", "hamlet", "municipality", "county");
        string? country = FirstPresent(address, "country");

        return (place, country) switch
        {
            (not null, not null) => $"{place}, {country}",
            (not null, null) => place,
            (null, not null) => country,
            _ => null
        };
    }

    private static string? FirstPresent(JsonElement address, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (address.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }
}
