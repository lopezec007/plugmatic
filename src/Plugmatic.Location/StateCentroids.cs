using System.Globalization;
using System.IO.Compression;

namespace Plugmatic.Location;

/// <summary>Mean ZIP-centroid per state, derived from the bundled GeoNames table at first use.</summary>
public static class StateCentroids
{
    private static Dictionary<string, (double lat, double lon)>? _centroids;

    private static Dictionary<string, (double, double)> Load()
    {
        if (_centroids is not null) return _centroids;
        using var stream = typeof(StateCentroids).Assembly
            .GetManifestResourceStream("Plugmatic.Location.Resources.us-zips.tsv.gz")!;
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        var sums = new Dictionary<string, (double lat, double lon, int n)>();
        while (reader.ReadLine() is { } line)
        {
            var p = line.Split('\t');
            if (p.Length < 5) continue;
            var state = p[4];
            var cur = sums.GetValueOrDefault(state);
            sums[state] = (cur.lat + double.Parse(p[1], CultureInfo.InvariantCulture),
                           cur.lon + double.Parse(p[2], CultureInfo.InvariantCulture),
                           cur.n + 1);
        }
        return _centroids = sums.ToDictionary(kv => kv.Key, kv => (kv.Value.lat / kv.Value.n, kv.Value.lon / kv.Value.n));
    }

    public static string? Nearest(GeoPoint point)
    {
        string? best = null;
        double bestKm = double.MaxValue;
        foreach (var (state, (lat, lon)) in Load())
        {
            var d = point.DistanceKm(lat, lon);
            if (d < bestKm) { bestKm = d; best = state; }
        }
        return best;
    }
}
