using System.CommandLine;
using Plugmatic.Cli.Services;
using Plugmatic.Core.Model;
using Plugmatic.Core.Runs;
using Plugmatic.Radios.Dm32uv.Format;

namespace Plugmatic.Cli.Commands;

/// <summary>read = the backup mechanism; a backup is just a read run. [spec §7.4]</summary>
public static class ReadCommand
{
    public static Command Build()
    {
        var radio = new Option<string>("--radio") { Description = "Radio model", DefaultValueFactory = _ => "dm32uv" };
        var port = new Option<string?>("--port") { Description = "Serial port (auto-detected when omitted)" };
        var cmd = new Command("read", "Read and archive the radio's codeplug");
        cmd.Options.Add(radio);
        cmd.Options.Add(port);
        cmd.SetAction(async (pr, ct) =>
        {
            Common.RequireDm32uv(pr.GetValue(radio));
            return await RunAsync(pr.GetValue(port), ct);
        });
        return cmd;
    }

    public static async Task<int> RunAsync(string? port, CancellationToken ct, string? tag = null)
    {
        var runs = new RunManager();
        var run = runs.CreateRun("dm32uv", "read");
        if (tag is not null) run.Tags.Add(tag);
        Console.WriteLine($"Run: {run.Directory}");
        var outcome = RunOutcome.Failed;
        try
        {
            await using var session = await RadioSession.OpenAsync(port, run, ct);
            var id = await session.IdentifyAsync(ct);
            run.Extra["radio"] = new System.Text.Json.Nodes.JsonObject
            {
                ["model"] = "dm32uv", ["reportedId"] = id.Model, ["firmware"] = id.FirmwareVersion,
            };
            run.Extra["port"] = session.PortName;
            Console.WriteLine($"Radio: {id.Model}, firmware {id.FirmwareVersion}, build {id.BuildDate}");

            var image = await session.Protocol.ReadImageAsync(session.Link, WriteFlow.ConsoleProgress(), ct);
            run.WriteArtifact("read.bin", image);
            var ir = Dm32uvCodec.Instance.Decode(image);
            run.WriteArtifact("read.yaml", IrYaml.Serialize(ir));

            Console.WriteLine($"Read complete: {ir.Channels.Count} channels, {ir.Zones.Count} zones, " +
                              $"{ir.Contacts.Count} contacts, {ir.RxGroupLists.Count} group lists.");
            outcome = RunOutcome.Success;
            return 0;
        }
        catch (CliError e)
        {
            Console.Error.WriteLine(e.Message);
            return e.ExitCode;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Read failed: {e.Message}");
            return 3;
        }
        finally
        {
            runs.Finalize(run, outcome);
        }
    }
}

public static class Common
{
    public static void RequireDm32uv(string? radio)
    {
        if (!string.Equals(radio, "dm32uv", StringComparison.OrdinalIgnoreCase))
            throw new CliError($"Unsupported radio '{radio}'. Supported: dm32uv.", 1);
    }
}
