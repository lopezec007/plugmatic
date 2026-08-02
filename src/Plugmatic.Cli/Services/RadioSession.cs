using Plugmatic.Core.Runs;
using Plugmatic.Radios;
using Plugmatic.Radios.Dm32uv.Protocol;

namespace Plugmatic.Cli.Services;

public sealed class CliError(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;   // 1 user, 2 environment, 3 hardware
}

/// <summary>
/// One serial session against the (single supported) radio: port resolution, transfer
/// logging into the run dir, identify preflight with model check (I2), teardown.
/// </summary>
public sealed class RadioSession : IAsyncDisposable
{
    public required Dm32uvProtocol Protocol { get; init; }
    public required ISerialLink Link { get; init; }
    public required string PortName { get; init; }
    public RadioIdentity? Identity { get; private set; }
    private StreamWriter? _log;

    public static async Task<RadioSession> OpenAsync(string? portOption, RunContext run, CancellationToken ct)
    {
        var port = ResolvePort(portOption);
        var log = run.OpenLog("transfer.log");
        ISerialLink link = new TransferLogLink(new SerialPortLink(), log);
        var session = new RadioSession
        {
            Protocol = new Dm32uvProtocol(),
            Link = link,
            PortName = port,
        };
        session._log = log;
        try
        {
            await link.OpenAsync(new SerialSettings(port), ct);
        }
        catch (Exception e)
        {
            throw new CliError($"Cannot open {port}: {e.Message}" + LinuxPermissionHint(e), 2);
        }
        return session;
    }

    /// <summary>Identify + model preflight. Mismatch = abort, no override (I2).</summary>
    public async Task<RadioIdentity> IdentifyAsync(CancellationToken ct)
    {
        Identity = await Protocol.IdentifyAsync(Link, ct);
        if (Identity.Model != Dm32uvProtocol.ExpectedModel)
            throw new CliError(
                $"Connected radio identifies as '{Identity.Model}', expected '{Dm32uvProtocol.ExpectedModel}' (DM-32UV). " +
                "Aborting — no override exists (I2).", 3);
        return Identity;
    }

    private static string ResolvePort(string? portOption)
    {
        if (!string.IsNullOrEmpty(portOption)) return portOption;
        var candidates = PortDiscovery.List();
        var known = candidates.Where(c => c.KnownCable).ToList();
        if (known.Count == 1) return known[0].Name;
        if (candidates.Count == 1) return candidates[0].Name;
        if (candidates.Count == 0)
            throw new CliError("No serial ports found. Is the programming cable plugged in? (plugmatic ports)", 2);
        throw new CliError(
            "Multiple serial ports found; pass --port explicitly or use 'plugmatic ports' to disambiguate:\n  " +
            string.Join("\n  ", candidates.Select(c => $"{c.Name} {c.VidPid} {c.Description}")), 1);
    }

    private static string LinuxPermissionHint(Exception e) =>
        OperatingSystem.IsLinux() && e is UnauthorizedAccessException
            ? "\nHint: add yourself to the dialout group: sudo usermod -aG dialout $USER (then log out/in)."
            : "";

    public async ValueTask DisposeAsync()
    {
        try { await Protocol.EndSessionAsync(Link, CancellationToken.None); } catch { }
        await Link.DisposeAsync();
        _log?.Dispose();
    }
}
