using System.CommandLine;
using Plugmatic.Radios;

namespace Plugmatic.Cli.Commands;

/// <summary>Port listing + interactive unplug/replug detector. [spec §7.7 / §6.5]</summary>
public static class PortsCommand
{
    public static Command Build()
    {
        var detect = new Option<bool>("--detect") { Description = "Interactively identify the cable by unplug/replug" };
        var cmd = new Command("ports", "List candidate serial ports");
        cmd.Options.Add(detect);
        cmd.SetAction(pr => pr.GetValue(detect) ? RunDetect() : RunList());
        return cmd;
    }

    private static int RunList()
    {
        var ports = PortDiscovery.List();
        if (ports.Count == 0)
        {
            Console.WriteLine("No serial ports found.");
            return 2;
        }
        foreach (var p in ports)
            Console.WriteLine($"{p.Name,-16} {p.VidPid ?? "-",-10} {(p.KnownCable ? "[known cable] " : "")}{p.Description ?? ""}");
        return 0;
    }

    private static int RunDetect()
    {
        Console.WriteLine("Unplug the programming cable, then press Enter.");
        Console.ReadLine();
        var before = PortDiscovery.List().Select(p => p.Name).ToHashSet();
        Console.WriteLine("Replug the cable, wait two seconds, then press Enter.");
        Console.ReadLine();
        var after = PortDiscovery.List();
        var appeared = after.Where(p => !before.Contains(p.Name)).ToList();
        switch (appeared.Count)
        {
            case 1:
                Console.WriteLine($"Cable is: {appeared[0].Name} ({appeared[0].VidPid} {appeared[0].Description})");
                return 0;
            case 0:
                Console.WriteLine("No new port appeared. Check the cable and try again.");
                return 2;
            default:
                Console.WriteLine("Multiple new ports appeared:");
                foreach (var p in appeared) Console.WriteLine($"  {p.Name} {p.VidPid}");
                return 1;
        }
    }
}
