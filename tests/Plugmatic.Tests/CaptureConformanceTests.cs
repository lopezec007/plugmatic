using System.Text.RegularExpressions;

namespace Plugmatic.Tests;

/// <summary>
/// Ground-truth checks: the vendored CPS capture (MIT, see NOTICE) must match the frames
/// documented in dm32uv-protocol.md — connecting the doc, the FakeRadio, and reality.
/// </summary>
public partial class CaptureConformanceTests
{
    private static readonly string CapturePath = Path.Combine(
        FindRepoRoot(), "tests", "fixtures", "captures", "cps_session_example.txt");

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null
               && !File.Exists(Path.Combine(dir, "Plugmatic.sln"))
               && !File.Exists(Path.Combine(dir, "Plugmatic.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private static List<(string dir, byte[] data)> ParseCapture()
    {
        var lines = File.ReadAllLines(CapturePath);
        var frames = new List<(string, byte[])>();
        string? dir = null;
        var bytes = new List<byte>();
        foreach (var line in lines)
        {
            if (line.Contains("Written data")) { Flush(); dir = "tx"; continue; }
            if (line.Contains("Read data")) { Flush(); dir = "rx"; continue; }
            if (line.StartsWith('[')) { Flush(); dir = null; continue; }
            if (dir is null) continue;
            var hex = HexArea().Match(line);
            if (!hex.Success) continue;
            foreach (var tok in hex.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                bytes.Add(Convert.ToByte(tok, 16));
        }
        Flush();
        return frames;

        void Flush()
        {
            if (dir is not null && bytes.Count > 0) frames.Add((dir, bytes.ToArray()));
            bytes.Clear();
        }
    }

    [GeneratedRegex(@"^\s{4}((?:[0-9A-Fa-f]{2} )+)")]
    private static partial Regex HexArea();

    [Fact]
    public void Capture_opens_with_psearch_and_dp570uv()
    {
        var frames = ParseCapture();
        Assert.Equal("tx", frames[0].dir);
        Assert.Equal("PSEARCH"u8.ToArray(), frames[0].data);
        Assert.Equal("rx", frames[1].dir);
        Assert.Equal((byte)0x06, frames[1].data[0]);
        Assert.Equal("DP570UV", System.Text.Encoding.ASCII.GetString(frames[1].data, 1, 7));
    }

    [Fact]
    public void Capture_handshake_order_matches_protocol_doc()
    {
        var frames = ParseCapture();
        var txAscii = frames.Where(f => f.dir == "tx")
            .Select(f => System.Text.Encoding.ASCII.GetString(f.data.TakeWhile(b => b is >= 0x20 and < 0x7F).ToArray()))
            .ToList();
        int psearch = txAscii.FindIndex(s => s.StartsWith("PSEARCH"));
        int passsta = txAscii.FindIndex(s => s.StartsWith("PASSSTA"));
        int sysinfo = txAscii.FindIndex(s => s.StartsWith("SYSINFO"));
        Assert.True(psearch >= 0 && passsta > psearch && sysinfo > passsta,
            $"handshake order wrong: {psearch}/{passsta}/{sysinfo}");
    }

    [Fact]
    public void Capture_vframe_0x0a_parses_as_memory_range()
    {
        var frames = ParseCapture();
        // find tx 56 00 00 00 0A and its rx
        for (int i = 0; i < frames.Count - 1; i++)
        {
            if (frames[i].dir == "tx" && frames[i].data.SequenceEqual(new byte[] { 0x56, 0x00, 0x00, 0x00, 0x0A }))
            {
                var rx = frames[i + 1];
                Assert.Equal("rx", rx.dir);
                Assert.Equal(0x56, rx.data[0]);
                Assert.Equal(0x0A, rx.data[1]);
                Assert.Equal(8, rx.data[2]);
                uint start = BitConverter.ToUInt32(rx.data, 3);
                uint end = BitConverter.ToUInt32(rx.data, 7);
                Assert.True(start < end, $"range {start:X}..{end:X}");
                Assert.Equal(0u, start & 0xFFF);          // block aligned
                Assert.Equal(0xFFFu, end & 0xFFF);        // inclusive end
                return;
            }
        }
        Assert.Fail("V-frame 0x0A not found in capture");
    }

    [Fact]
    public void Capture_read_frames_echo_address_with_w_type()
    {
        var frames = ParseCapture();
        int checkedFrames = 0;
        for (int i = 0; i < frames.Count - 1 && checkedFrames < 5; i++)
        {
            var (dir, data) = frames[i];
            if (dir != "tx" || data.Length != 6 || data[0] != (byte)'R') continue;
            var rx = frames[i + 1];
            if (rx.dir != "rx" || rx.data.Length < 6) continue;
            Assert.Equal((byte)'W', rx.data[0]);
            Assert.Equal(data[1], rx.data[1]);   // address echoed LE
            Assert.Equal(data[2], rx.data[2]);
            Assert.Equal(data[3], rx.data[3]);
            checkedFrames++;
        }
        Assert.True(checkedFrames > 0, "no R/W exchanges found in capture");
    }
}
