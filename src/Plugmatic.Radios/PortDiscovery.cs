using System.IO.Ports;

namespace Plugmatic.Radios;

public sealed record PortCandidate(string Name, string? VidPid, string? Description, bool KnownCable);

/// <summary>Serial port enumeration + known programming-cable identification. [spec §6.5]</summary>
public static class PortDiscovery
{
    /// <summary>Known DM-32UV programming cable bridge chips. Ours is CH340 (verified by lsusb).</summary>
    private static readonly Dictionary<string, string> KnownCables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1a86:7523"] = "CH340 (QinHeng) — verified plugmatic cable",
        ["0403:6001"] = "FTDI FT232",
        ["067b:23a3"] = "Prolific PL23A3",
    };

    public static IReadOnlyList<PortCandidate> List()
    {
        var result = new List<PortCandidate>();
        foreach (var name in SerialPort.GetPortNames().Distinct().OrderBy(n => n))
        {
            string? vidpid = null, desc = null;
            if (OperatingSystem.IsLinux())
                (vidpid, desc) = LinuxUsbInfo(name);
            bool known = vidpid is not null && KnownCables.ContainsKey(vidpid);
            if (known) desc = KnownCables[vidpid!];
            result.Add(new PortCandidate(name, vidpid, desc, known));
        }
        return result;
    }

    private static (string? vidpid, string? desc) LinuxUsbInfo(string portName)
    {
        try
        {
            var dev = Path.GetFileName(portName);
            // /sys/class/tty/ttyUSB0/device -> .../3-5/3-5:1.0/ttyUSB0 ; the USB device dir
            // (which holds idVendor/idProduct) is two levels above the resolved target.
            // .NET normalizes ".." lexically before the kernel sees the symlink, so a
            // component-wise realpath is required.
            var deviceLink = $"/sys/class/tty/{dev}/device";
            if (!Directory.Exists(deviceLink)) return (null, null);
            var resolved = RealPath(deviceLink);
            var usbDir = Path.GetDirectoryName(Path.GetDirectoryName(resolved));
            if (usbDir is null) return (null, null);
            string? Read(string f)
            {
                var p = Path.Combine(usbDir, f);
                return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
            }
            var vid = Read("idVendor"); var pid = Read("idProduct");
            var product = Read("product") ?? Read("manufacturer");
            return (vid is null || pid is null ? null : $"{vid}:{pid}", product);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>realpath(3): resolve every symlink component so ".." applies to real directories.</summary>
    private static string RealPath(string path)
    {
        string cur = "/";
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            cur = cur == "/" ? "/" + part : cur + "/" + part;
            for (int hops = 0; hops < 40; hops++)
            {
                FileSystemInfo info = Directory.Exists(cur) ? new DirectoryInfo(cur) : new FileInfo(cur);
                var target = info.LinkTarget;
                if (target is null) break;
                cur = Path.IsPathRooted(target)
                    ? target
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cur)!, target));
            }
        }
        return cur;
    }
}
