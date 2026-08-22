using System.Diagnostics;

namespace Plugmatic.Providers;

/// <summary>
/// Runs the approved `repeaterbook` Python client (tools/repeaterbook_fetch.py) and returns
/// its export JSON.
///
/// Since 2026-03-03 RepeaterBook's export API is closed to unapproved clients, and a
/// per-user `rbuapp_` token is bound to *one approved application*. Plugmatic is not that
/// application: a request from our own HttpClient is refused with `auth_missing` whichever
/// header carries the token — verified against the live endpoint. The PyPI `repeaterbook`
/// package is an approved distributed client, so the sanctioned route is to let it make the
/// call under the user's own token. That is why this is a subprocess and not more C#.
/// </summary>
public static class RepeaterBookBridge
{
    /// <summary>Where the helper and its virtualenv live, or null when unavailable.</summary>
    public sealed record Tooling(string Python, string Script, string WorkingDirectory);

    /// <summary>
    /// Walk up from the working directory and the binary looking for the helper, then prefer
    /// the project's own virtualenv over a bare `python3` — the package must be importable.
    /// </summary>
    public static Tooling? Discover(string? pythonOverride = null, string? scriptOverride = null)
    {
        string? script = scriptOverride;
        if (script is null)
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                {
                    var candidate = Path.Combine(dir.FullName, "tools", "repeaterbook_fetch.py");
                    if (File.Exists(candidate)) { script = candidate; break; }
                }
                if (script is not null) break;
            }
        }
        if (script is null || !File.Exists(script)) return null;

        var root = Directory.GetParent(script)!.Parent!.FullName;
        string python = pythonOverride ?? Path.Combine(root, ".venv", "bin", "python");
        if (!File.Exists(python)) python = pythonOverride ?? "python3";
        return new Tooling(python, script, root);
    }

    /// <summary>Fetch one state's export as RepeaterBook's own JSON. Throws on failure.</summary>
    public static async Task<string> FetchAsync(Tooling tooling, string state, bool gmrs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(tooling.Python)
        {
            WorkingDirectory = tooling.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(tooling.Script);
        psi.ArgumentList.Add("--state");
        psi.ArgumentList.Add(state);
        if (gmrs) psi.ArgumentList.Add("--gmrs");

        using var proc = Process.Start(psi)
            ?? throw new ProviderException($"repeaterbook: could not start {tooling.Python}");
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        string body = await stdout, log = await stderr;

        if (proc.ExitCode != 0 || body.Length == 0)
            throw new ProviderException(
                $"repeaterbook: helper exited {proc.ExitCode}. {Tail(log)}\n" +
                $"Helper: {tooling.Script}\nPython: {tooling.Python}\n" +
                "It needs the `repeaterbook` package and a token (TOKEN=rbuapp_… in .env, or $REPEATERBOOK).");
        return body;
    }

    private static string Tail(string log)
    {
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? "" : lines[^1].Trim();
    }
}
