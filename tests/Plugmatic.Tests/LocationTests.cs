using Plugmatic.Location;

namespace Plugmatic.Tests;

public class LocationTests
{
    private readonly LocationResolver _resolver = new();

    [Fact]
    public void Zip_80525_resolves_to_fort_collins()
    {
        var p = _resolver.Resolve("80525");
        Assert.InRange(p.Lat, 40.4, 40.7);
        Assert.InRange(p.Lon, -105.2, -104.9);
        Assert.Contains("Fort Collins", p.Description);
        Assert.Equal("CO", LocationResolver.StateForZip("80525"));
    }

    [Fact]
    public void Zip_plus4_is_accepted()
    {
        var p = _resolver.Resolve("80525-1234");
        Assert.Contains("Fort Collins", p.Description);
    }

    [Fact]
    public void Decimal_latlong_parses()
    {
        var p = _resolver.Resolve("40.5384, -105.0512");
        Assert.Equal(40.5384, p.Lat, 4);
        Assert.Equal(-105.0512, p.Lon, 4);
    }

    [Fact]
    public void Utm_round_trips_fort_collins()
    {
        // 13T 495000 4487000 ~ Fort Collins area
        var p = _resolver.Resolve("13T 495000 4487000");
        Assert.InRange(p.Lat, 40.4, 40.6);
        Assert.InRange(p.Lon, -105.2, -104.9);
    }

    [Fact]
    public void Unknown_zip_fails_readably()
    {
        var ex = Assert.Throws<FormatException>(() => _resolver.Resolve("00000"));
        Assert.Contains("00000", ex.Message);
    }

    [Fact]
    public void Distance_haversine_sane()
    {
        var fc = new GeoPoint(40.5853, -105.0844, null);      // Fort Collins
        var denver = (39.7392, -104.9903);
        Assert.InRange(fc.DistanceKm(denver.Item1, denver.Item2), 90, 100);
    }

    [Fact]
    public void State_centroid_fallback_finds_colorado()
    {
        Assert.Equal("CO", StateCentroids.Nearest(new GeoPoint(39.0, -105.5, null)));
    }
}
