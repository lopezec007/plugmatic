using System.CommandLine;
using Plugmatic.Cli.Services;
using Plugmatic.Core.Model;
using Plugmatic.Core.Runs;
using Plugmatic.Radios;

namespace Plugmatic.Cli.Commands;

/// <summary>The D7 write flow. [spec §7.5]</summary>
public static class WriteCommand
{
    public static Command Build()
    {
        var radio = Common.RadioOption();
        var port = new Option<string?>("--port") { Description = "Serial port" };
        var plug = new Option<string?>("--plug") { Description = "generated.yaml (or a run dir containing one)" };
        var image = new Option<string?>("--image") { Description = "Raw image (.bin) to restore" };
        var yes = new Option<bool>("--yes", "-y") { Description = "Skip the confirmation prompt" };

        var cmd = new Command("write", "Program the radio (always reads and archives the current codeplug first)");
        foreach (var o in new Option[] { radio, port, plug, image, yes }) cmd.Options.Add(o);
        cmd.SetAction(async (pr, ct) =>
        {
            var def = Common.Resolve(pr.GetValue(radio));
            if (!def.SupportsWrite)
                throw new CliError($"{def.DisplayName} support is read-only in this build; writing is not implemented yet.", 1);
            var source = LoadSource(def, pr.GetValue(plug), pr.GetValue(image));
            return await WriteFlow.RunAsync(def, new RunManager(), pr.GetValue(port), source, pr.GetValue(yes), ct);
        });
        return cmd;
    }

    public static WriteFlow.Source LoadSource(IRadioDefinition def, string? plug, string? image)
    {
        if ((plug is null) == (image is null))
            throw new CliError("Exactly one of --plug or --image is required.", 1);

        if (plug is not null)
        {
            var path = plug;
            if (Directory.Exists(path))
            {
                var candidate = Path.Combine(path, "generated.yaml");
                if (!File.Exists(candidate))
                    throw new CliError($"No generated.yaml in run dir {path}.", 1);
                path = candidate;
            }
            if (!File.Exists(path)) throw new CliError($"Not found: {path}", 1);
            Codeplug ir;
            try { ir = IrYaml.Deserialize(File.ReadAllText(path)); }
            catch (Exception e) { throw new CliError($"Cannot parse {path}: {e.Message}", 1); }
            var validation = Plugmatic.Core.Validation.CodeplugValidator.Validate(ir, def.Codec.Capabilities);
            if (validation.Count > 0)
                throw new CliError("Codeplug validation failed:\n  " + string.Join("\n  ", validation), 1);
            return new WriteFlow.Source(ir, null, Path.GetFileName(path));
        }

        if (!File.Exists(image)) throw new CliError($"Not found: {image}", 1);
        return new WriteFlow.Source(null, File.ReadAllBytes(image!), Path.GetFileName(image)!);
    }
}
