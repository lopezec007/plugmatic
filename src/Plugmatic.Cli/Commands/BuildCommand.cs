using System.CommandLine;
using Plugmatic.Cli.Services;
using Plugmatic.Core;
using Plugmatic.Core.Build;
using Plugmatic.Core.Model;
using Plugmatic.Core.Runs;
using Plugmatic.Core.Validation;
using Plugmatic.Radios.Dm32uv.Format;

namespace Plugmatic.Cli.Commands;

/// <summary>fetch (cached) -> build -> validate -> generated.yaml + manifest. [spec §7.3]</summary>
public static class BuildCommand
{
    public static Command Build()
    {
        var location = new Option<string>("--location") { Required = true };
        var format = new Option<string?>("--format");
        var radio = new Option<string>("--radio") { DefaultValueFactory = _ => "dm32uv" };
        var profileOpt = new Option<string?>("--profile") { Description = "Profile name (in ~/Plugmatic/config/profiles or ./profiles) or a yaml path" };
        var radius = new Option<string?>("--radius") { Description = "Overrides the profile radius (60mi, 100km)" };
        var noNoaa = new Option<bool>("--no-noaa");
        var offline = new Option<bool>("--offline");
        var outOpt = new Option<string?>("--out") { Description = "Also write generated.yaml here" };

        var cmd = new Command("build", "Build a codeplug for a location (no hardware)");
        foreach (var o in new Option[] { location, format, radio, profileOpt, radius, noNoaa, offline, outOpt })
            cmd.Options.Add(o);
        cmd.SetAction(async (pr, ct) =>
        {
            Common.RequireDm32uv(pr.GetValue(radio));
            var profile = LoadProfile(pr.GetValue(profileOpt));
            if (pr.GetValue(noNoaa)) profile.Noaa = false;
            var radiusText = pr.GetValue(radius) ?? $"{profile.RadiusMi}mi";

            var runs = new RunManager();
            var run = runs.CreateRun("dm32uv", "build");
            var outcome = RunOutcome.Failed;
            try
            {
                var repeatersTask = FetchCommand.FetchAsync(
                    pr.GetValue(location)!, pr.GetValue(format), radiusText,
                    pr.GetValue(offline), profile.Gmrs, ct, out var fetchCtx);
                var repeaters = await repeatersTask;
                Console.WriteLine($"{repeaters.Count} repeaters in range.");

                var (gmrsEnabled, ack) = GmrsPolicyStore.Get();
                var builder = new CodeplugBuilder(Dm32uvCodec.Instance.Capabilities);   // settings read from config
                var result = builder.Build(repeaters, profile, new GmrsPolicy(gmrsEnabled, ack));

                var errors = CodeplugValidator.Validate(result.Codeplug, Dm32uvCodec.Instance.Capabilities);
                if (errors.Count > 0)
                {
                    Console.Error.WriteLine("Validation failed:\n  " + string.Join("\n  ", errors));
                    return 1;
                }

                var yaml = IrYaml.Serialize(result.Codeplug);
                var path = run.WriteArtifact("generated.yaml", yaml);
                if (pr.GetValue(outOpt) is { } outPath) File.Copy(path, outPath, overwrite: true);

                run.Extra["inputs"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["locationRaw"] = pr.GetValue(location),
                    ["resolvedLat"] = fetchCtx.Center.Lat,
                    ["resolvedLon"] = fetchCtx.Center.Lon,
                    ["radiusKm"] = fetchCtx.RadiusKm,
                    ["profile"] = profile.Name,
                    ["providerFetchTimestamps"] = new System.Text.Json.Nodes.JsonObject(
                        fetchCtx.Fetcher.FetchTimestamps.Select(kv =>
                            new KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>(
                                kv.Key, kv.Value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")))),
                };
                run.Extra["gmrs"] = new System.Text.Json.Nodes.JsonObject
                { ["txEnabled"] = gmrsEnabled, ["acknowledgedUtc"] = ack };

                var p = result.Codeplug;
                Console.WriteLine($"\nGenerated: {p.Channels.Count} channels " +
                    $"({p.Channels.Count(c => c is DigitalChannel)} DMR, {p.Channels.Count(c => c is AnalogChannel)} FM), " +
                    $"{p.Zones.Count} zones, {p.Contacts.Count} contacts, {p.RxGroupLists.Count} group lists.");
                Console.WriteLine($"GMRS TX: {(gmrsEnabled ? $"ENABLED (acknowledged {ack})" : "disabled - GMRS channels RX-only")}");
                foreach (var note in result.Notes) Console.WriteLine($"note: {note}");
                Console.WriteLine($"\n{path}");
                Console.WriteLine($"Write it with: plugmatic write --radio dm32uv --plug {run.Directory}");
                outcome = RunOutcome.Success;
                return 0;
            }
            catch (CliError e)
            {
                Console.Error.WriteLine(e.Message);
                return e.ExitCode;
            }
            finally
            {
                runs.Finalize(run, outcome);
            }
        });
        return cmd;
    }

    public static BuildProfile LoadProfile(string? nameOrPath)
    {
        if (nameOrPath is null) return BuildProfile.ColoradoDefault();
        if (File.Exists(nameOrPath)) return BuildProfile.Load(nameOrPath);
        foreach (var dir in new[] { PlugmaticPaths.ProfilesDir, Path.Combine(AppContext.BaseDirectory, "profiles"), "profiles" })
        {
            var candidate = Path.Combine(dir, nameOrPath + ".yaml");
            if (File.Exists(candidate)) return BuildProfile.Load(candidate);
        }
        if (nameOrPath is "colorado-default") return BuildProfile.ColoradoDefault();
        throw new CliError($"Profile '{nameOrPath}' not found (looked in {PlugmaticPaths.ProfilesDir}).", 1);
    }
}
