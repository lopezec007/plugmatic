using System.CommandLine;
using Plugmatic.Cli.Commands;
using Plugmatic.Cli.Services;

var root = new RootCommand("plugmatic — DMR radio auto-programmer (Baofeng DM-32UV)");

root.Subcommands.Add(DoctorCommand.Build());
root.Subcommands.Add(PortsCommand.Build());
root.Subcommands.Add(ReadCommand.Build());
root.Subcommands.Add(WriteCommand.Build());
root.Subcommands.Add(DiffCommand.Build());
root.Subcommands.Add(DevCommands.Build());
root.Subcommands.Add(ConfigCommand.Build());
root.Subcommands.Add(FetchCommand.Build());
root.Subcommands.Add(BuildCommand.Build());

try
{
    return await root.Parse(args).InvokeAsync();
}
catch (CliError e)
{
    Console.Error.WriteLine(e.Message);
    return e.ExitCode;
}
