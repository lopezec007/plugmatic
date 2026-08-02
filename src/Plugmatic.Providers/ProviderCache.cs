using Microsoft.Data.Sqlite;
using Plugmatic.Core;

namespace Plugmatic.Providers;

/// <summary>Raw provider responses + timestamps; TTL default 7 days. [spec §5/§6.2]</summary>
public sealed class ProviderCache : IDisposable
{
    private readonly SqliteConnection _db;
    public TimeSpan Ttl { get; init; } = TimeSpan.FromDays(7);

    public ProviderCache(string? path = null)
    {
        PlugmaticPaths.EnsureCreated();
        _db = new SqliteConnection($"Data Source={path ?? PlugmaticPaths.RepeaterCacheDb}");
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS responses(
                provider TEXT NOT NULL, key TEXT NOT NULL, fetchedUtc TEXT NOT NULL,
                body TEXT NOT NULL, PRIMARY KEY(provider, key));
            """;
        cmd.ExecuteNonQuery();
    }

    public (string Body, DateTime FetchedUtc)? Get(string provider, string key, bool ignoreTtl)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT body, fetchedUtc FROM responses WHERE provider=@p AND key=@k";
        cmd.Parameters.AddWithValue("@p", provider);
        cmd.Parameters.AddWithValue("@k", key);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var fetched = DateTime.Parse(r.GetString(1), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        if (!ignoreTtl && DateTime.UtcNow - fetched > Ttl) return null;
        return (r.GetString(0), fetched);
    }

    public void Put(string provider, string key, string body)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO responses(provider, key, fetchedUtc, body) VALUES(@p, @k, @t, @b)
            ON CONFLICT(provider, key) DO UPDATE SET fetchedUtc=@t, body=@b;
            """;
        cmd.Parameters.AddWithValue("@p", provider);
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@t", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
        cmd.Parameters.AddWithValue("@b", body);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
