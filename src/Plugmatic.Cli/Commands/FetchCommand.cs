using System.CommandLine;
using Plugmatic.Cli.Services;
using Plugmatic.Core.Model;
using Plugmatic.Location;
using Plugmatic.Providers;


namespace Plugmatic.Cli.Commands;

/// <summary>fetch --location ... — summary table, cache only, no run dir. [spec §7.2]</summary>
public static class FetchCommand
{
    public static Command Build()
    {
        var location = new Option<string>("--location") { Required = true, Description = "ZIP, lat/long, or UTM" };
        var format = new Option<string?>("--format") { Description = "Force location format: zip|latlong|utm" };
        var radius = new Option<string>("--radius") { DefaultValueFactory = _ => "60mi", Description = "e.g. 60mi or 100km" };
        var offline = new Option<bool>("--offline") { Description = "Cache only; never touch the network" };
        var gmrs = new Option<bool>("--gmrs") { Description = "Include GMRS repeaters" };

        var cmd = new Command("fetch", "Fetch repeaters for a location (summary table)");
        foreach (var o in new Option[] { location, format, radius, offline, gmrs }) cmd.Options.Add(o);
        cmd.SetAction(async (pr, ct) =>
        {
            var repeaters = await FetchAsync(
                pr.GetValue(location)!, pr.GetValue(format), pr.GetValue(radius)!,
                pr.GetValue(offline), pr.GetValue(gmrs), ct, out _);
            Print(repeaters);
            return 0;
        });
        return cmd;
    }

    public static Task<List<Repeater>> FetchAsync(
        string locationInput, string? formatText, string radiusText, bool offline, bool gmrs,
        CancellationToken ct, out FetchContext context)
    {
        LocationFormat? forced = formatText?.ToLowerInvariant() switch
        {
            null => null, "zip" => LocationFormat.Zip, "latlong" => LocationFormat.LatLong,
            "utm" => LocationFormat.Utm,
            _ => throw new CliError($"Unknown --format '{formatText}' (zip|latlong|utm)", 1),
        };

        GeoPoint center;
        try { center = new LocationResolver().Resolve(locationInput, forced); }
        catch (FormatException e) { throw new CliError(e.Message, 1); }
        Console.WriteLine($"Resolved {locationInput} -> {center}");

        double radiusKm = ParseRadiusKm(radiusText);

        // State: from ZIP metadata, or reverse from description; RepeaterBook queries by state.
        string? state = null;
        if (LocationFormat.Zip == (forced ?? (locationInput.Trim().Length == 5 && locationInput.Trim().All(char.IsDigit) ? LocationFormat.Zip : LocationFormat.LatLong)))
            state = LocationResolver.StateForZip(locationInput.Trim()[..5]);
        state ??= NearestStateByZipTable(center);
        if (state is null) throw new CliError("Cannot determine US state for this location.", 1);

        CredentialStore.TryGet("repeaterbook.token", out var token);
        CredentialStore.TryGet("brandmeister.apikey", out var bmKey);

        var cache = new ProviderCache();
        var fetcher = new ProviderFetcher(cache, offline);
        var service = new RepeaterDirectory(fetcher);
        context = new FetchContext(center, radiusKm, state, fetcher, cache, service);
        return QueryAndWarnAsync(service, center, radiusKm, state, gmrs, offline, token, bmKey, ct);
    }

    private static async Task<List<Repeater>> QueryAndWarnAsync(RepeaterDirectory service,
        GeoPoint center, double radiusKm, string state, bool gmrs, bool offline,
        string? token, string? bmKey, CancellationToken ct)
    {
        var result = await service.QueryAsync(center, radiusKm, state,
            new RepeaterQueryOptions(gmrs, offline),
            string.IsNullOrEmpty(token) ? null : token,
            string.IsNullOrEmpty(bmKey) ? null : bmKey, ct);
        foreach (var w in service.Warnings) Console.Error.WriteLine($"warning: {w}");
        return result;
    }

    public sealed record FetchContext(GeoPoint Center, double RadiusKm, string State,
        ProviderFetcher Fetcher, ProviderCache Cache, RepeaterDirectory Service);

    public static double ParseRadiusKm(string text)
    {
        text = text.Trim().ToLowerInvariant();
        double factor = 1.0;
        if (text.EndsWith("mi")) { factor = 1.609344; text = text[..^2]; }
        else if (text.EndsWith("km")) { text = text[..^2]; }
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0)
            throw new CliError($"Cannot parse radius '{text}' (e.g. 60mi, 100km).", 1);
        return value * factor;
    }

    /// <summary>Fallback state detection: nearest ZIP centroid's state.</summary>
    private static string? NearestStateByZipTable(GeoPoint center)
    {
        // Cheap approach: probe the bundled table via LocationResolver by scanning a grid is
        // overkill; instead use the closest of the 50 state centroids derived from the table.
        return StateCentroids.Nearest(center);
    }

    private static void Print(List<Repeater> repeaters)
    {
        if (repeaters.Count == 0)
        {
            Console.WriteLine("No repeaters in range.");
            return;
        }
        Console.WriteLine();
        Console.WriteLine($"{"Callsign",-10} {"Output",10} {"Input",10} {"Mode",-6} {"CC",2} {"Tone",-7} {"Dist",7}  City (sources)");
        foreach (var r in repeaters)
            Console.WriteLine($"{r.Callsign,-10} {r.Output,10} {r.Input,10} {ModeText(r),-6} " +
                              $"{r.ColorCode?.ToString() ?? "-",2} {r.UplinkTone,-7} {r.DistanceKm,6:0.0}km" +
                              $"  {r.City} ({string.Join("+", r.Sources)})");
        Console.WriteLine($"\n{repeaters.Count} repeaters.");
    }

    private static string ModeText(Repeater r) => r.Mode switch
    {
        RepeaterMode.Dmr => "DMR",
        RepeaterMode.FmAndDmr => "FM+DMR",
        _ => r.Service == Core.Model.RepeaterService.Gmrs ? "GMRS" : "FM",
    };
}
