using System.CommandLine;
using Plugmatic.Core;
using Plugmatic.Radios;

namespace Plugmatic.Cli.Commands;

/// <summary>Environment checks with per-OS fix-it hints. [spec §7.8]</summary>
public static class DoctorCommand
{
    public static Command Build()
    {
        var cmd = new Command("doctor", "Check the environment and explain how to fix problems");
        cmd.SetAction(_ => Run());
        return cmd;
    }

    private static int Run()
    {
        int failures = 0;
        void Check(string name, bool ok, string? hint = null, bool warnOnly = false)
        {
            var mark = ok ? "ok " : warnOnly ? "warn" : "FAIL";
            Console.WriteLine($"[{mark,-4}] {name}");
            if (!ok && hint is not null) Console.WriteLine($"        {hint}");
            if (!ok && !warnOnly) failures++;
        }

        // Plugmatic dir writable
        bool dirOk;
        try { PlugmaticPaths.EnsureCreated(); dirOk = true; }
        catch { dirOk = false; }
        Check($"data directory writable ({PlugmaticPaths.Root})", dirOk,
              "Check permissions on your home directory.");

        // Serial access
        if (OperatingSystem.IsLinux())
        {
            bool inDialout = false;
            try
            {
                var groups = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("id", "-nG") { RedirectStandardOutput = true });
                inDialout = groups!.StandardOutput.ReadToEnd().Split(' ', '\n').Contains("dialout");
                groups.WaitForExit();
            }
            catch { }
            Check("user in 'dialout' group (serial access)", inDialout,
                  "Run: sudo usermod -aG dialout $USER   then log out and back in.");
        }

        // Candidate port present
        var ports = PortDiscovery.List();
        var known = ports.Where(p => p.KnownCable).ToList();
        Check($"programming cable present ({known.Count} known, {ports.Count} total ports)",
              ports.Count > 0,
              "Plug in the USB programming cable; 'plugmatic ports' to inspect.");
        foreach (var p in known)
            Console.WriteLine($"        {p.Name}  {p.VidPid}  {p.Description}");

        // RepeaterBook token (warn only)
        bool hasToken = Services.CredentialStore.TryGet("repeaterbook.token", out _);
        Check("RepeaterBook token configured", hasToken,
              "plugmatic config set repeaterbook.token <token>   (fetch/build need it; read/write don't)",
              warnOnly: true);

        // Cache healthy
        bool cacheOk = true;
        try
        {
            if (File.Exists(PlugmaticPaths.RepeaterCacheDb))
                using (File.Open(PlugmaticPaths.RepeaterCacheDb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }
        }
        catch { cacheOk = false; }
        Check("repeater cache healthy", cacheOk,
              $"Delete {PlugmaticPaths.RepeaterCacheDb} to rebuild the cache.");

        Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) failed.");
        return failures == 0 ? 0 : 2;
    }
}
