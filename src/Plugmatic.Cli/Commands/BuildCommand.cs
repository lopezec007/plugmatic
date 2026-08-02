using System.CommandLine;

namespace Plugmatic.Cli.Commands;

public static class BuildCommand
{
    public static Command Build()
    {
        var cmd = new Command("build", "Build a codeplug for a location (no hardware)");
        cmd.SetAction((_, _) =>
        {
            Console.Error.WriteLine("build: not implemented yet (P4).");
            return Task.FromResult(2);
        });
        return cmd;
    }
}
