using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using CoordinateSharp;

namespace Plugmatic.Location;

public enum LocationFormat { Zip, LatLong, Utm }

public sealed record GeoPoint(double Lat, double Lon, string? Description)
{
    public override string ToString() =>
        $"{Lat.ToString("0.0000", CultureInfo.InvariantCulture)}, {Lon.ToString("0.0000", CultureInfo.InvariantCulture)}"
        + (Description is null ? "" : $" ({Description})");

    /// <summary>Great-circle distance in km (haversine).</summary>
    public double DistanceKm(double lat, double lon)
    {
        const double R = 6371.0;
        double dLat = (lat - Lat) * Math.PI / 180, dLon = (lon - Lon) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                   + Math.Cos(Lat * Math.PI / 180) * Math.Cos(lat * Math.PI / 180)
                     * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Sqrt(a));
    }
}

public interface ILocationResolver
{
    GeoPoint Resolve(string input, LocationFormat? forced = null);
}

/// <summary>ZIP (bundled GeoNames table) / decimal or DMS lat-long / UTM. [spec §6.1]</summary>
public sealed partial class LocationResolver : ILocationResolver
{
    public GeoPoint Resolve(string input, LocationFormat? forced = null)
    {
        input = input.Trim();
        var format = forced ?? Detect(input);
        return format switch
        {
            LocationFormat.Zip => ResolveZip(input),
            LocationFormat.Utm => ResolveUtm(input),
            _ => ResolveLatLong(input),
        };
    }

    private static LocationFormat Detect(string input)
    {
        if (ZipPattern().IsMatch(input)) return LocationFormat.Zip;
        if (UtmPattern().IsMatch(input)) return LocationFormat.Utm;
        return LocationFormat.LatLong;
    }

    [GeneratedRegex(@"^\d{5}(-\d{4})?$")]
    private static partial Regex ZipPattern();

    // "13T 493000 4493000"
    [GeneratedRegex(@"^\d{1,2}\s*[C-Xc-x]\s+\d{1,7}(\.\d+)?\s+\d{1,7}(\.\d+)?$")]
    private static partial Regex UtmPattern();

    // ---------------------------------------------------------------- ZIP

    private static Dictionary<string, (double lat, double lon, string place)>? _zips;

    private static Dictionary<string, (double, double, string)> LoadZips()
    {
        if (_zips is not null) return _zips;
        using var stream = typeof(LocationResolver).Assembly
            .GetManifestResourceStream("Plugmatic.Location.Resources.us-zips.tsv.gz")
            ?? throw new InvalidOperationException("Bundled ZIP table missing from assembly.");
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        var map = new Dictionary<string, (double, double, string)>(42000);
        while (reader.ReadLine() is { } line)
        {
            var p = line.Split('\t');
            if (p.Length < 5) continue;
            map[p[0]] = (double.Parse(p[1], CultureInfo.InvariantCulture),
                         double.Parse(p[2], CultureInfo.InvariantCulture),
                         $"{p[3]}, {p[4]}");
        }
        return _zips = map;
    }

    private static GeoPoint ResolveZip(string input)
    {
        var zip5 = input[..5];
        if (!LoadZips().TryGetValue(zip5, out var hit))
            throw new FormatException($"Unknown US ZIP code '{zip5}'.");
        return new GeoPoint(hit.Item1, hit.Item2, hit.Item3);
    }

    /// <summary>US state abbreviation for a resolved ZIP (drives RepeaterBook state queries).</summary>
    public static string? StateForZip(string zip5) =>
        LoadZips().TryGetValue(zip5, out var hit) ? hit.Item3.Split(", ")[^1] : null;

    // ---------------------------------------------------------------- lat/long

    private static GeoPoint ResolveLatLong(string input)
    {
        // decimal degrees: "40.5384, -105.0512" (comma or space separated)
        var parts = input.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
        {
            Validate(lat, lon);
            return new GeoPoint(lat, lon, null);
        }

        // DMS via CoordinateSharp ("40 32 18 N 105 03 04 W", "N40°32'18\" W105°3'4\"")
        if (Coordinate.TryParse(input, out var c))
        {
            Validate(c.Latitude.ToDouble(), c.Longitude.ToDouble());
            return new GeoPoint(c.Latitude.ToDouble(), c.Longitude.ToDouble(), null);
        }
        throw new FormatException($"Cannot parse location '{input}' as lat/long.");
    }

    private static void Validate(double lat, double lon)
    {
        if (lat is < -90 or > 90 || lon is < -180 or > 180)
            throw new FormatException($"Coordinates out of range: {lat}, {lon}.");
    }

    // ---------------------------------------------------------------- UTM

    private static GeoPoint ResolveUtm(string input)
    {
        var m = Regex.Match(input, @"^(\d{1,2})\s*([C-Xc-x])\s+(\d+(?:\.\d+)?)\s+(\d+(?:\.\d+)?)$");
        if (!m.Success) throw new FormatException($"Cannot parse UTM '{input}' (expected e.g. '13T 493000 4493000').");
        var utm = new UniversalTransverseMercator(
            m.Groups[2].Value.ToUpperInvariant(),
            int.Parse(m.Groups[1].Value),
            double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
            double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture));
        var c = UniversalTransverseMercator.ConvertUTMtoLatLong(utm);
        return new GeoPoint(c.Latitude.ToDouble(), c.Longitude.ToDouble(), null);
    }
}
