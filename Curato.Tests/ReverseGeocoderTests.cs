using Curato.Data;

namespace Curato.Tests;

public class ReverseGeocoderTests
{
    private sealed class FakeNominatimClient : INominatimClient
    {
        public int CallCount;
        public string? Response = "Copenhagen, Denmark";
        public Exception? ThrowOnCall;

        public string? ReverseGeocode(double latitude, double longitude)
        {
            CallCount++;
            if (ThrowOnCall is not null) throw ThrowOnCall;
            return Response;
        }
    }

    [Fact]
    public void First_lookup_for_a_coordinate_calls_the_client_and_caches_the_result()
    {
        WithTempDatabase((database, client) =>
        {
            var geocoder = NewGeocoder(database, client);
            var result = geocoder.Resolve(55.6761, 12.5683);

            Assert.Equal("Copenhagen, Denmark", result);
            Assert.Equal(1, client.CallCount);
        });
    }

    [Fact]
    public void A_second_lookup_for_the_same_bucket_never_calls_the_client_again()
    {
        WithTempDatabase((database, client) =>
        {
            var geocoder = NewGeocoder(database, client);
            geocoder.Resolve(55.6761, 12.5683);
            var second = geocoder.Resolve(55.6761, 12.5683); // exact same point
            var third = geocoder.Resolve(55.6759, 12.5680); // rounds to the same ~1km bucket

            Assert.Equal("Copenhagen, Denmark", second);
            Assert.Equal("Copenhagen, Denmark", third);
            Assert.Equal(1, client.CallCount); // only the very first call hit the network
        });
    }

    [Fact]
    public void A_client_exception_returns_null_and_caches_nothing()
    {
        WithTempDatabase((database, client) =>
        {
            client.ThrowOnCall = new HttpRequestException("simulated network failure");
            var geocoder = NewGeocoder(database, client);

            var result = geocoder.Resolve(55.6761, 12.5683);

            Assert.Null(result);
        });
    }

    [Fact]
    public void A_null_response_is_not_cached_either()
    {
        WithTempDatabase((database, client) =>
        {
            client.Response = null;
            var geocoder = NewGeocoder(database, client);

            Assert.Null(geocoder.Resolve(55.6761, 12.5683));
            Assert.Equal(1, client.CallCount);

            client.Response = "Copenhagen, Denmark"; // now the real place resolves
            Assert.Equal("Copenhagen, Denmark", geocoder.Resolve(55.6761, 12.5683));
            Assert.Equal(2, client.CallCount); // not cached, so it retried
        });
    }

    /// <summary>Zero rate-limit interval — production enforces Nominatim's real
    /// 1 req/sec policy, but a test suite shouldn't pay a real second per case.</summary>
    private static ReverseGeocoder NewGeocoder(LibraryDatabase database, FakeNominatimClient client) =>
        new(database, client, TimeSpan.Zero);

    private static void WithTempDatabase(Action<LibraryDatabase, FakeNominatimClient> test)
    {
        var tempDir = Directory.CreateTempSubdirectory("curato-geocoder-test-");
        try
        {
            var database = new LibraryDatabase(tempDir.FullName);
            database.EnsureSchema();
            test(database, new FakeNominatimClient());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir.FullName, recursive: true);
        }
    }
}
