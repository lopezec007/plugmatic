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
            // A radio whose own USB stack provides the port is just as "known" as a cable.
            if (RadioRegistry.ByUsbId(vidpid) is { } radio)
            {
                known = true;
                desc = $"{radio.DisplayName} (--radio {radio.Model})";
            }
            result.Add(new PortCandidate(name, vidpid, desc, known));
        }
        return result;
    }

    private static (string? vidpid, string? desc) LinuxUsbInfo(string portName)
    {
        try
        {
            var dev = Path.GetFileName(portName);
            // The depth from the tty to the USB device node differs per driver:
            //   ttyUSB0 (usb-serial): device -> .../3-5/3-5:1.0/ttyUSB0   (2 levels up)
            //   ttyACM0 (CDC-ACM):    device -> .../3-6/3-6:1.0          (1 level up)
            // So walk upward to the first directory carrying idVendor+idProduct instead of
            // assuming a depth. .NET normalizes ".." lexically before the kernel sees the
            // symlink, hence the component-wise realpath first.
            var deviceLink = $"/sys/class/tty/{dev}/device";
            if (!Directory.Exists(deviceLink)) return (null, null);
            var dir = RealPath(deviceLink);
            for (int up = 0; up < 6 && dir is not null && dir != "/"; up++)
            {
                string? Read(string f)
                {
                    var p = Path.Combine(dir!, f);
                    return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
                }
                var vid = Read("idVendor");
                var pid = Read("idProduct");
                if (vid is not null && pid is not null)
                    return ($"{vid}:{pid}", Read("product") ?? Read("manufacturer"));
                dir = Path.GetDirectoryName(dir);
            }
            return (null, null);
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
