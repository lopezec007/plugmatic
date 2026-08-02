using System.Globalization;
using System.IO.Compression;

namespace Plugmatic.Location;

/// <summary>Mean ZIP-centroid per (city, state) from the bundled GeoNames table — geocodes
/// providers that report city names without coordinates (e.g. RadioID).</summary>
public static class CityCentroids
{
    private static Dictionary<(string City, string State), (double lat, double lon)>? _map;

    private static Dictionary<(string, string), (double, double)> Load()
    {
        if (_map is not null) return _map;
        using var stream = typeof(CityCentroids).Assembly
            .GetManifestResourceStream("Plugmatic.Location.Resources.us-zips.tsv.gz")!;
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        var sums = new Dictionary<(string, string), (double lat, double lon, int n)>();
        while (reader.ReadLine() is { } line)
        {
            var p = line.Split('\t');
            if (p.Length < 5) continue;
            var key = (p[3].ToLowerInvariant(), p[4]);
            var cur = sums.GetValueOrDefault(key);
            sums[key] = (cur.lat + double.Parse(p[1], CultureInfo.InvariantCulture),
                         cur.lon + double.Parse(p[2], CultureInfo.InvariantCulture),
                         cur.n + 1);
        }
        return _map = sums.ToDictionary(kv => kv.Key, kv => (kv.Value.lat / kv.Value.n, kv.Value.lon / kv.Value.n));
    }

    public static (double Lat, double Lon)? Find(string city, string stateAbbr) =>
        Load().TryGetValue((city.Trim().ToLowerInvariant(), stateAbbr), out var hit) ? hit : null;
}
