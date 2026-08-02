using Plugmatic.Core.Model;
using Plugmatic.Location;

namespace Plugmatic.Providers;

/// <summary>Orchestrates providers: state query -> merge -> distance filter -> sort. [spec §6.2]</summary>
public sealed class RepeaterDirectory(ProviderFetcher fetcher)
{
    /// <summary>US state name lookup for RepeaterBook (which queries by full state name).</summary>
    private static readonly Dictionary<string, string> StateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AL"] = "Alabama", ["AK"] = "Alaska", ["AZ"] = "Arizona", ["AR"] = "Arkansas",
        ["CA"] = "California", ["CO"] = "Colorado", ["CT"] = "Connecticut", ["DE"] = "Delaware",
        ["FL"] = "Florida", ["GA"] = "Georgia", ["HI"] = "Hawaii", ["ID"] = "Idaho",
        ["IL"] = "Illinois", ["IN"] = "Indiana", ["IA"] = "Iowa", ["KS"] = "Kansas",
        ["KY"] = "Kentucky", ["LA"] = "Louisiana", ["ME"] = "Maine", ["MD"] = "Maryland",
        ["MA"] = "Massachusetts", ["MI"] = "Michigan", ["MN"] = "Minnesota", ["MS"] = "Mississippi",
        ["MO"] = "Missouri", ["MT"] = "Montana", ["NE"] = "Nebraska", ["NV"] = "Nevada",
        ["NH"] = "New Hampshire", ["NJ"] = "New Jersey", ["NM"] = "New Mexico", ["NY"] = "New York",
        ["NC"] = "North Carolina", ["ND"] = "North Dakota", ["OH"] = "Ohio", ["OK"] = "Oklahoma",
        ["OR"] = "Oregon", ["PA"] = "Pennsylvania", ["RI"] = "Rhode Island", ["SC"] = "South Carolina",
        ["SD"] = "South Dakota", ["TN"] = "Tennessee", ["TX"] = "Texas", ["UT"] = "Utah",
        ["VT"] = "Vermont", ["VA"] = "Virginia", ["WA"] = "Washington", ["WV"] = "West Virginia",
        ["WI"] = "Wisconsin", ["WY"] = "Wyoming", ["DC"] = "District of Columbia",
    };

    public static string StateName(string abbrOrName) =>
        StateNames.TryGetValue(abbrOrName, out var name) ? name : abbrOrName;

    public static string StateAbbr(string abbrOrName)
    {
        if (abbrOrName.Length == 2) return abbrOrName.ToUpperInvariant();
        var hit = StateNames.FirstOrDefault(kv => kv.Value.Equals(abbrOrName, StringComparison.OrdinalIgnoreCase));
        return hit.Key ?? abbrOrName;
    }

    /// <summary>Non-fatal provider problems from the last query (shown to the user).</summary>
    public List<string> Warnings { get; } = [];

    public async Task<List<Repeater>> QueryAsync(
        GeoPoint center, double radiusKm, string state, RepeaterQueryOptions opts,
        string? repeaterBookToken, string? brandMeisterKey, CancellationToken ct)
    {
        Warnings.Clear();
        var stateName = StateName(state);

        // RepeaterBook is primary but requires an approved token for live fetches; without
        // one, degrade to RadioID (DMR-complete) per the spec's risk plan.
        List<Repeater> ham = [];
        try
        {
            ham = await RepeaterBook.QueryAsync(fetcher, stateName, gmrs: false, repeaterBookToken, ct);
        }
        catch (ProviderException e)
        {
            Warnings.Add($"RepeaterBook unavailable ({e.Message}); continuing with RadioID DMR data only. " +
                         "Analog ham coverage needs a RepeaterBook token (plugmatic config set repeaterbook.token ...).");
        }
        var dmr = await RadioIdNet.QueryAsync(fetcher, stateName, ct);
        var merged = RepeaterMerge.Merge(ham, dmr);

        if (opts.IncludeGmrs)
            try
            {
                merged.AddRange(await RepeaterBook.QueryAsync(fetcher, stateName, gmrs: true, repeaterBookToken, ct));
            }
            catch (ProviderException e)
            {
                Warnings.Add($"GMRS repeater list unavailable ({e.Message}); fixed GMRS channels are still generated.");
            }

        // Geocode coordinate-less entries (RadioID reports city/state only) via the bundled
        // GeoNames city centroids, then distance-filter.
        foreach (var r in merged.Where(r => r is { Lat: 0, Lon: 0, City.Length: > 0 }))
        {
            var abbr = StateAbbr(r.State ?? state);
            if (CityCentroids.Find(r.City!, abbr) is { } hit)
            {
                r.Lat = hit.Lat;
                r.Lon = hit.Lon;
            }
        }
        foreach (var r in merged)
            r.DistanceKm = r.Lat == 0 && r.Lon == 0 ? double.NaN : center.DistanceKm(r.Lat, r.Lon);
        var inRange = merged
            .Where(r => !double.IsNaN(r.DistanceKm) && r.DistanceKm <= radiusKm)
            .OrderBy(r => r.DistanceKm)
            .ToList();

        // BrandMeister static talkgroups for in-range DMR repeaters with IDs.
        foreach (var r in inRange.Where(r => r.Mode != RepeaterMode.Fm && r.DmrId is not null))
        {
            var tgs = await BrandMeister.StaticTalkgroupsAsync(fetcher, r.DmrId!.Value, brandMeisterKey, ct);
            foreach (var tg in tgs) if (!r.StaticTalkgroups.Contains(tg)) r.StaticTalkgroups.Add(tg);
        }
        return inRange;
    }
}
