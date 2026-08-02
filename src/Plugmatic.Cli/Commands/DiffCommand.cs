using System.CommandLine;
using Plugmatic.Cli.Services;
using Plugmatic.Core.Model;

namespace Plugmatic.Cli.Commands;

/// <summary>Human-readable IR diff. [spec §7.6]</summary>
public static class DiffCommand
{
    public static Command Build()
    {
        var oldOpt = new Option<string>("--old") { Required = true, Description = "Run dir or IR yaml" };
        var newOpt = new Option<string>("--new") { Required = true, Description = "Run dir or IR yaml" };
        var cmd = new Command("diff", "Compare two codeplugs (runs or yaml files)");
        cmd.Options.Add(oldOpt); cmd.Options.Add(newOpt);
        cmd.SetAction((pr, _) =>
        {
            var a = LoadIr(pr.GetValue(oldOpt)!);
            var b = LoadIr(pr.GetValue(newOpt)!);
            Console.Write(Render(a, b));
            return Task.FromResult(0);
        });
        return cmd;
    }

    public static Codeplug LoadIr(string path)
    {
        if (Directory.Exists(path))
        {
            var candidate = new[] { "generated.yaml", "read.yaml", "pre-write.yaml" }
                .Select(f => Path.Combine(path, f)).FirstOrDefault(File.Exists)
                ?? throw new CliError($"No IR yaml found in run dir {path}.", 1);
            path = candidate;
        }
        if (!File.Exists(path)) throw new CliError($"Not found: {path}", 1);
        return IrYaml.Deserialize(File.ReadAllText(path));
    }

    public static string Render(Codeplug a, Codeplug b)
    {
        var sb = new System.Text.StringBuilder();

        void Section<T>(string name, List<T> olds, List<T> news, Func<T, string> key, Func<T, string> describe)
        {
            var oldBy = olds.ToDictionary(key);
            var newBy = news.ToDictionary(key);
            var removed = oldBy.Keys.Except(newBy.Keys).ToList();
            var added = newBy.Keys.Except(oldBy.Keys).ToList();
            var changed = oldBy.Keys.Intersect(newBy.Keys)
                .Where(k => describe(oldBy[k]) != describe(newBy[k])).ToList();
            if (removed.Count + added.Count + changed.Count == 0) return;
            sb.AppendLine($"{name}:");
            foreach (var k in removed) sb.AppendLine($"  - {describe(oldBy[k])}");
            foreach (var k in added) sb.AppendLine($"  + {describe(newBy[k])}");
            foreach (var k in changed)
            {
                sb.AppendLine($"  ~ {describe(oldBy[k])}");
                sb.AppendLine($"    -> {describe(newBy[k])}");
            }
        }

        static string DescribeChannel(Channel c)
        {
            var extra = c switch
            {
                DigitalChannel d => $"DMR cc{d.ColorCode} {d.TimeSlot} tg={d.TxContactName ?? "-"} gl={d.RxGroupListName ?? "-"}",
                AnalogChannel an => $"FM {(an.WideBandwidth ? "25k" : "12.5k")} rxTone={an.RxTone} txTone={an.TxTone}",
                _ => "",
            };
            return $"{c.Name} [rx {c.RxFrequency} tx {c.TxFrequency} {c.Power}" +
                   $"{(c.TxPermit == TxPermit.Inhibited ? " RX-only" : "")} {extra}]";
        }

        Section("channels", a.Channels, b.Channels, c => c.Name, DescribeChannel);
        Section("zones", a.Zones, b.Zones, z => z.Name, z => $"{z.Name} [{string.Join(", ", z.ChannelNames)}]");
        Section("contacts", a.Contacts, b.Contacts, c => c.Name, c => $"{c.Name} [{c.Type} {c.DmrId}]");
        Section("group lists", a.RxGroupLists, b.RxGroupLists, g => g.Name, g => $"{g.Name} [{string.Join(", ", g.ContactNames)}]");
        Section("scan lists", a.ScanLists, b.ScanLists, s => s.Name, s => $"{s.Name} [{string.Join(", ", s.ChannelNames)}]");
        if (a.Settings.RadioId != b.Settings.RadioId || a.Settings.Callsign != b.Settings.Callsign)
            sb.AppendLine($"settings:\n  ~ radioId {a.Settings.RadioId} '{a.Settings.Callsign}' -> {b.Settings.RadioId} '{b.Settings.Callsign}'");

        return sb.Length == 0 ? "No differences.\n" : sb.ToString();
    }
}
