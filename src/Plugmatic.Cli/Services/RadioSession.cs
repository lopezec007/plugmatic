using Plugmatic.Core.Runs;
using Plugmatic.Radios;

namespace Plugmatic.Cli.Services;

public sealed class CliError(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;   // 1 user, 2 environment, 3 hardware
}

/// <summary>
/// One serial session against a radio: port resolution, transfer logging into the run
/// dir, identify preflight with model check (I2), teardown. Radio-agnostic — the
/// concrete protocol comes from the radio definition.
/// </summary>
public sealed class RadioSession : IAsyncDisposable
{
    public required IRadioDefinition Radio { get; init; }
    public required IRadioProtocol Protocol { get; init; }
    public required ISerialLink Link { get; init; }
    public required string PortName { get; init; }
    public RadioIdentity? Identity { get; private set; }
    private StreamWriter? _log;

    public static async Task<RadioSession> OpenAsync(
        IRadioDefinition radio, string? portOption, RunContext run, CancellationToken ct)
    {
        // Radios that own their USB stack are briefly absent after each session; give the
        // device node a chance to come back before declaring the radio missing. [§2]
        if (radio.ReEnumeratesAfterSession) await WaitForPortAsync(radio, portOption, ct);
        var port = ResolvePort(radio, portOption);
        var log = run.OpenLog("transfer.log");
        ISerialLink link = new TransferLogLink(new SerialPortLink(), log);
        var session = new RadioSession
        {
            Radio = radio,
            Protocol = radio.CreateProtocol(),
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
            throw new CliError($"Cannot open {port}: {e.Message}" + PermissionHint(port, e), 2);
        }
        return session;
    }

    /// <summary>
    /// Open a session, waiting for the port to come back first. Radios that own their USB
    /// stack (the AnyTone) drop and re-create their device node at the end of every
    /// session, so a second session in the same run has to tolerate the node being absent
    /// for a second or so rather than reporting "no serial ports found".
    /// [d878uv-protocol.md §2]
    /// </summary>
    public static async Task<RadioSession> ReopenAsync(
        IRadioDefinition radio, string? portOption, RunContext run, TimeSpan timeout, CancellationToken ct)
    {
        // The device node can linger for a moment after the radio drops it, so an open that
        // succeeds immediately may be holding a stale node that vanishes on first use. The
        // handshake is the real liveness check: retry open *and* handshake together.
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        await Task.Delay(SettleDelay, ct);
        while (true)
        {
            RadioSession? session = null;
            try
            {
                session = await OpenAsync(radio, portOption, run, ct);
                await session.Protocol.IdentifyAsync(session.Link, ct);
                return session;
            }
            catch (Exception e)
            {
                last = e;
                if (session is not null) await session.DisposeAsync();
                if (DateTime.UtcNow >= deadline) break;
                await Task.Delay(500, ct);
            }
        }
        throw new CliError(
            $"The radio did not answer within {timeout.TotalSeconds:0}s of the previous session ending. " +
            "It re-enumerates its USB device after every session; if it stays away, power-cycle it.\n" +
            $"Last error: {last!.Message}",
            last is CliError c ? c.ExitCode : 3);
    }

    /// <summary>Grace period for the radio's USB device to come back. [d878uv-protocol.md §2]</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(1200);

    /// <summary>Poll until a usable port shows up, or give up and let ResolvePort report it.</summary>
    private static async Task WaitForPortAsync(IRadioDefinition radio, string? portOption, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            try { ResolvePort(radio, portOption); return; }
            catch (CliError) { await Task.Delay(400, ct); }
        }
    }

    /// <summary>Identify + model preflight. Mismatch = abort, no override (I2).</summary>
    public async Task<RadioIdentity> IdentifyAsync(CancellationToken ct)
    {
        Identity = await Protocol.IdentifyAsync(Link, ct);
        if (!Radio.IdentifiesAs.Contains(Identity.Model, StringComparer.Ordinal))
        {
            var other = RadioRegistry.All.FirstOrDefault(r => r.IdentifiesAs.Contains(Identity.Model, StringComparer.Ordinal));
            throw new CliError(
                $"Connected radio identifies as '{Identity.Model}', but --radio {Radio.Model} expects " +
                $"{string.Join(" or ", Radio.IdentifiesAs)}. Aborting — no override exists (I2)." +
                (other is null ? "" : $"\nThat radio looks like '--radio {other.Model}' ({other.DisplayName})."), 3);
        }
        return Identity;
    }

    private static string ResolvePort(IRadioDefinition radio, string? portOption)
    {
        if (!string.IsNullOrEmpty(portOption)) return portOption;
        var candidates = PortDiscovery.List();

        // Prefer a port whose USB ID belongs to the radio we were asked for.
        var forThisRadio = candidates
            .Where(c => c.VidPid is not null && radio.KnownUsbIds.Contains(c.VidPid, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (forThisRadio.Count == 1) return forThisRadio[0].Name;
        if (forThisRadio.Count > 1)
            throw new CliError(
                $"Multiple ports match {radio.DisplayName}; pass --port explicitly:\n  " +
                string.Join("\n  ", forThisRadio.Select(c => $"{c.Name} {c.VidPid}")), 1);

        var known = candidates.Where(c => c.KnownCable).ToList();
        if (known.Count == 1) return known[0].Name;
        if (candidates.Count == 1) return candidates[0].Name;
        if (candidates.Count == 0)
            throw new CliError("No serial ports found. Is the radio connected and powered on? (plugmatic ports)", 2);
        throw new CliError(
            "Multiple serial ports found; pass --port explicitly or use 'plugmatic ports':\n  " +
            string.Join("\n  ", candidates.Select(c => $"{c.Name} {c.VidPid} {c.Description}")), 1);
    }

    private static string PermissionHint(string port, Exception e) =>
        OperatingSystem.IsLinux() && e is UnauthorizedAccessException
            ? $"\nHint: add yourself to the dialout group (sudo usermod -aG dialout $USER, then log out/in), " +
              $"or for this session: sudo chmod a+rw {port}"
            : "";

    public async ValueTask DisposeAsync()
    {
        try { await Protocol.EndSessionAsync(Link, CancellationToken.None); } catch { }
        await Link.DisposeAsync();
        _log?.Dispose();
    }
}
