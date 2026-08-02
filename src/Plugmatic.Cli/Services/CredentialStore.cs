using System.Diagnostics;
using System.Text.Json;
using Plugmatic.Core;

namespace Plugmatic.Cli.Services;

/// <summary>
/// Secret storage (repeaterbook.token, brandmeister.apikey). [spec §5/§7.1, I7]
/// Windows: DPAPI-encrypted file. Linux: libsecret via secret-tool when available,
/// else credentials.dat with 0600 permissions. Values never appear in logs or errors.
/// </summary>
public static class CredentialStore
{
    public static readonly string[] KnownSecretKeys = ["repeaterbook.token", "brandmeister.apikey"];

    public static bool IsSecretKey(string key) => KnownSecretKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    public static void Set(string key, string value)
    {
        if (OperatingSystem.IsLinux() && SecretToolAvailable())
        {
            var psi = new ProcessStartInfo("secret-tool", $"store --label=plugmatic:{key} service plugmatic key {key}")
            { RedirectStandardInput = true };
            using var p = Process.Start(psi)!;
            p.StandardInput.Write(value);
            p.StandardInput.Close();
            p.WaitForExit();
            if (p.ExitCode == 0) return;
            // fall through to file store on failure
        }
        var all = LoadFile();
        all[key] = value;
        SaveFile(all);
    }

    public static bool TryGet(string key, out string value)
    {
        value = "";
        if (OperatingSystem.IsLinux() && SecretToolAvailable())
        {
            var psi = new ProcessStartInfo("secret-tool", $"lookup service plugmatic key {key}")
            { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode == 0 && output.Length > 0)
            {
                value = output;
                return true;
            }
        }
        if (LoadFile().TryGetValue(key, out var v))
        {
            value = v;
            return true;
        }
        return false;
    }

    private static bool? _secretTool;
    private static bool SecretToolAvailable()
    {
        if (_secretTool is { } cached) return cached;
        try
        {
            var p = Process.Start(new ProcessStartInfo("secret-tool", "--help")
            { RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit(2000);
            _secretTool = p.ExitCode == 0;
        }
        catch { _secretTool = false; }
        return _secretTool.Value;
    }

    private static Dictionary<string, string> LoadFile()
    {
        var path = PlugmaticPaths.CredentialsFile;
        if (!File.Exists(path)) return [];
        var bytes = File.ReadAllBytes(path);
        if (OperatingSystem.IsWindows())
            bytes = System.Security.Cryptography.ProtectedData.Unprotect(
                bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(bytes) ?? [];
    }

    private static void SaveFile(Dictionary<string, string> all)
    {
        PlugmaticPaths.EnsureCreated();
        var path = PlugmaticPaths.CredentialsFile;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(all);
        if (OperatingSystem.IsWindows())
            bytes = System.Security.Cryptography.ProtectedData.Protect(
                bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // 0600
        File.Move(tmp, path, overwrite: true);
    }
}
