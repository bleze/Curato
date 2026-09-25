using Curato.Data;
using Curato.Organizer;

namespace Curato.Api;

/// <summary>
/// Resolves place names for whatever GPS coordinates the library view
/// (LibraryController.GetLibrary) hasn't seen resolved yet — deliberately
/// out-of-band from that request, which only ever reads ReverseGeocoder's
/// cache (TryGetCached), never calls Nominatim itself. Doing the real,
/// rate-limited (1 req/sec) resolution here instead means opening the
/// library right after a big backfill of previously-GPS-less photos doesn't
/// hang the page for however long a full library's worth of distinct places
/// takes to resolve one at a time — locations simply appear progressively as
/// this catches up, same idea as iCloudSyncBackgroundService not blocking a
/// page load on a slow external call.
/// </summary>
public sealed class GeocodeWarmupBackgroundService(
    ServerDatabase serverDatabase, ILogger<GeocodeWarmupBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                WarmOnce();
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Geocode warmup pass threw unexpectedly.");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    private void WarmOnce()
    {
        var libraryFolder = new LibraryFolderRepository(serverDatabase).Get();
        if (libraryFolder is null || !Directory.Exists(libraryFolder))
        {
            return;
        }

        var database = new LibraryDatabase(libraryFolder);
        database.EnsureSchema();
        var geocoder = new ReverseGeocoder(database, new NominatimClient());
        var query = new LibraryQueryService(database);

        var homeLabel = new HomeLocationRepository(serverDatabase).Get();
        var mediaItems = query.GetAllMediaItems();
        var homeLocation = GpsTripGate.DetectHomeLocation(mediaItems);

        // Every distinct ~1km bucket any GPS-tagged MediaItem falls into —
        // resolving by bucket (not by item, and not by each item's own exact
        // coordinate, which almost never repeats bit-for-bit even for two
        // photos taken side by side) is what keeps this cheap: a library
        // with thousands of photos from the same handful of real places
        // still only needs a handful of real Nominatim calls. Matches
        // ReverseGeocoder's own bucket size exactly, so resolving this
        // rounded point warms the same cache row a GetLibrary lookup for any
        // of that bucket's real items will find.
        var buckets = mediaItems
            .Where(m => m.Latitude is not null && m.Longitude is not null)
            .Select(m => (Lat: Math.Round(m.Latitude!.Value, 2), Lon: Math.Round(m.Longitude!.Value, 2)))
            .Distinct()
            .ToList();

        foreach (var (lat, lon) in buckets)
        {
            if (GpsTripGate.HomeOverrideLabel(homeLocation, homeLabel, (lat, lon)) is not null)
            {
                continue; // the user's own Home label always wins here — no need to ever resolve/cache a real name
            }

            if (geocoder.TryGetCached(lat, lon) is not null)
            {
                continue; // already warmed by a previous pass
            }

            geocoder.Resolve(lat, lon); // rate-limited internally; caches on success
        }
    }
}
