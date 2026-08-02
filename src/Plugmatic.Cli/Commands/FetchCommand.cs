using System.CommandLine;

namespace Plugmatic.Cli.Commands;

public static class FetchCommand
{
    public static Command Build()
    {
        var cmd = new Command("fetch", "Fetch repeaters for a location (summary table)");
        cmd.SetAction((_, _) =>
        {
            Console.Error.WriteLine("fetch: not implemented yet (P3).");
            return Task.FromResult(2);
        });
        return cmd;
    }
}
