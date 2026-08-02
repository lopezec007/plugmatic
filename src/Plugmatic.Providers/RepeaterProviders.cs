using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Plugmatic.Core.Model;

namespace Plugmatic.Providers;

public sealed class ProviderException(string message) : Exception(message);

/// <summary>
/// Fetches raw provider payloads through the cache. `--offline` never constructs an
/// HttpClient — cached bytes (any age) or failure. [spec §6.2]
/// </summary>
public sealed class ProviderFetcher(ProviderCache cache, bool offline, Func<HttpMessageHandler>? handlerFactory = null)
{
    public string UserAgent { get; init; } =
        $"plugmatic/{typeof(ProviderFetcher).Assembly.GetName().Version?.ToString(3)} (+https://github.com/lopezec/plugmatic; claude.ai.baggy455@passmail.net)";

    public Dictionary<string, DateTime> FetchTimestamps { get; } = [];

    public async Task<string> GetAsync(string provider, string key, string url,
        (string Name, string Value)? header, CancellationToken ct)
    {
        var cached = cache.Get(provider, key, ignoreTtl: offline);
        if (cached is { } hit)
        {
            FetchTimestamps[provider] = hit.FetchedUtc;
            return hit.Body;
        }
        if (offline)
            throw new ProviderException($"--offline and no cached {provider} data for '{key}'. Run once online first.");

        using var http = new HttpClient(handlerFactory?.Invoke() ?? new HttpClientHandler(), disposeHandler: true);
        http.Timeout = TimeSpan.FromSeconds(60);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (header is { } h) req.Headers.TryAddWithoutValidation(h.Name, h.Value);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new ProviderException($"{provider}: HTTP {(int)resp.StatusCode} for {url}");
        var body = await resp.Content.ReadAsStringAsync(ct);
        cache.Put(provider, key, body);
        FetchTimestamps[provider] = DateTime.UtcNow;
        return body;
    }
}

/// <summary>RepeaterBook state export (primary; token required for live fetches). Never scrapes HTML.</summary>
public static class RepeaterBook
{
    public static async Task<List<Repeater>> QueryAsync(
        ProviderFetcher fetcher, string state, bool gmrs, string? token, CancellationToken ct)
    {
        string kind = gmrs ? "gmrs" : "ham";
        string url = gmrs
            ? $"https://www.repeaterbook.com/api/export.php?state={Uri.EscapeDataString(state)}&stype=gmrs"
            : $"https://www.repeaterbook.com/api/export.php?state={Uri.EscapeDataString(state)}";
        var body = await fetcher.GetAsync("repeaterbook", $"{kind}:{state}", url,
            token is null ? null : ("Authorization", $"Bearer {token}"), ct);
        return Parse(body, gmrs);
    }

    public static List<Repeater> Parse(string json, bool gmrs)
    {
        var result = new List<Repeater>();
        var root = JsonNode.Parse(json);
        if (root?["results"] is not JsonArray rows) return result;
        foreach (var row in rows)
        {
            if (row is null) continue;
            string? s(string k) => row[k]?.GetValue<JsonElement>() is { } el
                ? el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString()
                : null;

            if (!TryMHz(s("Frequency"), out var output)) continue;
            if (!TryMHz(s("Input Freq"), out var input)) input = output;
            // Operational only
            var status = s("Operational Status");
            if (status is not null && !status.Equals("On-air", StringComparison.OrdinalIgnoreCase)
                                   && !status.Equals("On-Air", StringComparison.OrdinalIgnoreCase)) continue;

            var rep = new Repeater
            {
                Callsign = (s("Callsign") ?? "?").Trim(),
                Output = output,
                Input = input,
                Service = gmrs ? RepeaterService.Gmrs : RepeaterService.Ham,
                City = s("Nearest City"),
                State = s("State"),
            };
            if (double.TryParse(s("Lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) rep.Lat = lat;
            if (double.TryParse(s("Long"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) rep.Lon = lon;

            bool dmr = s("DMR") is "Yes" or "1";
            bool fm = s("FM Analog") is "Yes" or "1" || !dmr;
            rep.Mode = dmr && fm ? RepeaterMode.FmAndDmr : dmr ? RepeaterMode.Dmr : RepeaterMode.Fm;
            if (dmr && int.TryParse(s("DMR Color Code"), out var cc)) rep.ColorCode = cc;
            if (dmr && uint.TryParse(s("DMR ID"), out var did)) rep.DmrId = did;

            // Uplink tone ("PL"): CTCSS Hz or DCS like "D023"
            rep.UplinkTone = ParseTone(s("PL"));
            rep.DownlinkTone = ParseTone(s("TSQ"));
            rep.Sources.Add("repeaterbook");
            result.Add(rep);
        }
        return result;
    }

    private static bool TryMHz(string? text, out Frequency f)
    {
        f = default;
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz) || mhz <= 0)
            return false;
        f = Frequency.FromMHz(mhz);
        return true;
    }

    public static SelectiveCall ParseTone(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return SelectiveCall.None;
        text = text.Trim();
        if (text.StartsWith('D') && text.Length >= 4 && char.IsDigit(text[1]))
        {
            var code = text.TrimEnd('N', 'I', 'n', 'i');
            bool inverted = text.EndsWith('I') || text.EndsWith('i');
            if (int.TryParse(code.AsSpan(1), out _))
                return SelectiveCall.Parse($"{code}{(inverted ? "I" : "N")}");
        }
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz) && hz > 0)
            return SelectiveCall.Ctcss(hz);
        return SelectiveCall.None;   // "CSQ", "Restricted", etc.
    }
}

/// <summary>RadioID.net DMR repeater enrichment (no auth). [spec §6.2]</summary>
public static class RadioIdNet
{
    public static async Task<List<Repeater>> QueryAsync(ProviderFetcher fetcher, string state, CancellationToken ct)
    {
        var body = await fetcher.GetAsync("radioid", $"state:{state}",
            $"https://radioid.net/api/dmr/repeater/?state={Uri.EscapeDataString(state)}", null, ct);
        return Parse(body);
    }

    public static List<Repeater> Parse(string json)
    {
        var result = new List<Repeater>();
        var root = JsonNode.Parse(json);
        if (root?["results"] is not JsonArray rows) return result;
        foreach (var row in rows)
        {
            if (row is null) continue;
            if (!decimal.TryParse(row["frequency"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz))
                continue;
            var status = row["status"]?.ToString();
            if (status is not null && !status.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                continue;
            var output = Frequency.FromMHz(mhz);
            decimal.TryParse(row["offset"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var offset);
            var rep = new Repeater
            {
                Callsign = row["callsign"]?.ToString().Trim() ?? "?",
                Output = output,
                Input = Frequency.FromMHz(mhz + offset),
                Mode = RepeaterMode.Dmr,
                City = row["city"]?.ToString(),
                State = row["state"]?.ToString(),
                Network = row["ipsc_network"]?.ToString(),
            };
            if (int.TryParse(row["color_code"]?.ToString(), out var cc)) rep.ColorCode = cc;
            // The DMR repeater ID is "locator" in RadioID's schema (verified live 2026-08).
            if (uint.TryParse(row["locator"]?.ToString(), out var id)) rep.DmrId = id;
            // Static talkgroups come embedded — no BrandMeister round-trip needed for these.
            if (row["talkgroups"] is JsonArray tgs)
                foreach (var tg in tgs)
                {
                    if (!uint.TryParse(tg?["talkgroup"]?.ToString(), out var tgId)) continue;
                    int slot = int.TryParse(tg?["timeslot"]?.ToString(), out var s) ? s
                             : int.TryParse(tg?["slot"]?.ToString(), out s) ? s : 1;
                    if (!rep.StaticTalkgroups.Contains((tgId, slot))) rep.StaticTalkgroups.Add((tgId, slot));
                }
            rep.Sources.Add("radioid");
            result.Add(rep);
        }
        return result;
    }
}

/// <summary>BrandMeister static-talkgroup enrichment (optional API key). [spec §6.2]</summary>
public static class BrandMeister
{
    public static async Task<List<(uint Talkgroup, int Slot)>> StaticTalkgroupsAsync(
        ProviderFetcher fetcher, uint repeaterId, string? apiKey, CancellationToken ct)
    {
        try
        {
            var body = await fetcher.GetAsync("brandmeister", $"tg:{repeaterId}",
                $"https://api.brandmeister.network/v2/device/{repeaterId}/talkgroup",
                apiKey is null ? null : ("Authorization", $"Bearer {apiKey}"), ct);
            return Parse(body);
        }
        catch (ProviderException)
        {
            return [];   // enrichment is best-effort
        }
    }

    public static List<(uint, int)> Parse(string json)
    {
        var result = new List<(uint, int)>();
        if (JsonNode.Parse(json) is not JsonArray rows) return result;
        foreach (var row in rows)
        {
            if (uint.TryParse(row?["talkgroup"]?.ToString(), out var tg)
                && int.TryParse(row?["slot"]?.ToString(), out var slot))
                result.Add((tg, slot));
        }
        return result;
    }
}

/// <summary>Merge + dedupe: normalized callsign + output frequency, 2 km sanity check. [spec §6.2]</summary>
public static class RepeaterMerge
{
    public static List<Repeater> Merge(IEnumerable<Repeater> primary, IEnumerable<Repeater> enrichment)
    {
        var merged = new List<Repeater>(primary);
        foreach (var extra in enrichment)
        {
            var match = merged.FirstOrDefault(r =>
                Normalize(r.Callsign) == Normalize(extra.Callsign)
                && r.Output.Hz == extra.Output.Hz
                && (r.Lat == 0 || extra.Lat == 0
                    || new Plugmatic.Location.GeoPoint(r.Lat, r.Lon, null).DistanceKm(extra.Lat, extra.Lon) <= 2.0));
            if (match is null)
            {
                merged.Add(extra);
                continue;
            }
            match.ColorCode ??= extra.ColorCode;
            match.DmrId ??= extra.DmrId;
            if (match.Mode == RepeaterMode.Fm && extra.Mode != RepeaterMode.Fm) match.Mode = RepeaterMode.FmAndDmr;
            if (match.Lat == 0) { match.Lat = extra.Lat; match.Lon = extra.Lon; }
            match.City ??= extra.City;
            foreach (var s in extra.Sources) if (!match.Sources.Contains(s)) match.Sources.Add(s);
            foreach (var tg in extra.StaticTalkgroups) if (!match.StaticTalkgroups.Contains(tg)) match.StaticTalkgroups.Add(tg);
        }
        return merged;
    }

    public static string Normalize(string callsign) =>
        new([.. callsign.ToUpperInvariant().Where(char.IsLetterOrDigit)]);
}
