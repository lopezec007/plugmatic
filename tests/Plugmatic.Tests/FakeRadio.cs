using Plugmatic.Radios;
using Plugmatic.Radios.Dm32uv.Format;

namespace Plugmatic.Tests;

/// <summary>
/// In-process ISerialLink implementing the RADIO side of docs/formats/dm32uv-protocol.md.
/// When a real capture contradicts this class, one of them is wrong and the doc gets fixed.
/// </summary>
public sealed class FakeRadio : ISerialLink
{
    public const uint CodeplugStart = 0x001000;
    public const uint CodeplugEnd = 0x0C8FFF;      // inclusive [protocol §4 0x0A]

    /// <summary>Reported 0x0A region (configurable for bounds tests).</summary>
    public uint RegionStart { get; init; } = CodeplugStart;
    public uint RegionEnd { get; init; } = CodeplugEnd;

    private enum State { Closed, Open, SysInfo, Program }
    private State _state = State.Closed;
    private readonly Queue<byte> _toHost = new();
    private readonly List<byte> _fromHost = [];

    /// <summary>Physical flash: 4 KiB blocks by physical address.</summary>
    public Dictionary<uint, byte[]> Flash { get; } = [];

    public string Model { get; init; } = "DP570UV";
    public string Firmware { get; init; } = "DM32.01.L01.048";
    public string BuildDate { get; init; } = "2022-06-27";

    // Fault injection
    public bool FailPsearchOnce { get; set; }
    public int TimeoutAfterWrites { get; set; } = -1;     // stop responding after N 'W' frames
    public bool NakWrites { get; set; }
    public bool ShortReadResponses { get; set; }
    private int _writeFrames;

    public List<string> Log { get; } = [];

    /// <summary>Scatter a virtual image into physical flash (simulates the radio's dynamic allocation).</summary>
    public void LoadVirtualImage(byte[] image, int physicalScatterSeed = 1234)
    {
        var img = new Dm32Image((byte[])image.Clone());
        var physicalSlots = new List<uint>();
        for (uint p = RegionStart & ~0xFFFu; p + 0xFFF <= RegionEnd; p += 0x1000) physicalSlots.Add(p);
        var rng = new Random(physicalScatterSeed);
        var shuffled = physicalSlots.OrderBy(_ => rng.Next()).ToList();
        int slot = 0;
        for (int blk = 0; blk < Dm32Image.BlockCount; blk++)
            if (img.BlockPresent(blk))
                Flash[shuffled[slot++]] = image.AsSpan(blk * Dm32Image.BlockSize, Dm32Image.BlockSize).ToArray();
    }

    /// <summary>Reassemble the virtual image from physical flash (what a subsequent read would see).</summary>
    public byte[] VirtualImage()
    {
        var img = new Dm32Image();
        foreach (var block in Flash.Values)
        {
            byte meta = block[Dm32Image.BlockSize - 1];
            if (meta is 0x00 or 0xFF || meta >= Dm32Image.BlockCount) continue;
            block.CopyTo(img.Bytes.AsSpan(meta * Dm32Image.BlockSize, Dm32Image.BlockSize));
        }
        return img.Bytes;
    }

    // ------------------------------------------------------------ ISerialLink (host side)

    public Task OpenAsync(SerialSettings settings, CancellationToken ct)
    {
        _state = State.Open;
        return Task.CompletedTask;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct)
    {
        int n = 0;
        while (n < buffer.Length && _toHost.Count > 0)
            buffer.Span[n++] = _toHost.Dequeue();
        return ValueTask.FromResult(n);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        _fromHost.AddRange(buffer.ToArray());
        ProcessPending();
        return ValueTask.CompletedTask;
    }

    public void DiscardInput() => _toHost.Clear();
    public void SetDtr(bool asserted) { if (!asserted) Log.Add("dtr-reset"); }
    public ValueTask DisposeAsync() { _state = State.Closed; return ValueTask.CompletedTask; }

    // ------------------------------------------------------------ radio state machine

    private void Respond(params byte[] bytes)
    {
        var data = ShortReadResponses && bytes.Length > 1 ? bytes[..^1] : bytes;
        foreach (var b in data) _toHost.Enqueue(b);
    }

    private void ProcessPending()
    {
        while (TryConsumeFrame()) { }
    }

    private bool StartsWith(ReadOnlySpan<byte> prefix) =>
        _fromHost.Count >= prefix.Length && _fromHost.Take(prefix.Length).SequenceEqual(prefix.ToArray());

    private void Consume(int n) => _fromHost.RemoveRange(0, n);

    private bool TryConsumeFrame()
    {
        if (_fromHost.Count == 0) return false;

        // Handshake commands [protocol §3]
        if (StartsWith("PSEARCH"u8))
        {
            Consume(7);
            Log.Add("PSEARCH");
            if (FailPsearchOnce) { FailPsearchOnce = false; return true; }   // swallow: simulates sleepy CH340
            Respond([0x06, .. System.Text.Encoding.ASCII.GetBytes(Model)]);
            return true;
        }
        if (StartsWith("PASSSTA"u8))
        {
            Consume(7); Log.Add("PASSSTA");
            Respond(0x50, 0x00, 0x00);
            return true;
        }
        if (StartsWith("SYSINFO"u8))
        {
            Consume(7); Log.Add("SYSINFO");
            _state = State.SysInfo;
            Respond(0x06);
            return true;
        }

        // V-frames [protocol §4]
        if (_fromHost[0] == 0x56)
        {
            if (_fromHost.Count < 5) return false;
            byte id = _fromHost[4];
            Consume(5);
            Log.Add($"V{id:X2}");
            switch (id)
            {
                case 0x01: RespondValue(id, System.Text.Encoding.ASCII.GetBytes(Firmware)); break;
                case 0x03: RespondValue(id, System.Text.Encoding.ASCII.GetBytes(BuildDate)); break;
                case 0x0A: RespondValue(id, [.. BitConverter.GetBytes(RegionStart), .. BitConverter.GetBytes(RegionEnd)]); break;
                default: RespondValue(id, []); break;
            }
            return true;
        }

        // PROGRAM entry / END [protocol §5/§7]
        if (_fromHost[0] == 0xFF)
        {
            if (_fromHost.Count < 12) return false;
            var frame = _fromHost.Take(12).ToArray();
            Consume(12);
            if (frame.AsSpan(5, 7).SequenceEqual("PROGRAM"u8))
            {
                Log.Add("PROGRAM");
                _state = State.Program;
                Respond(0x06);
            }
            else if (frame.AsSpan(5, 3).SequenceEqual("END"u8))
            {
                Log.Add("END");
                _state = State.SysInfo;
                Respond(0x06);
            }
            return true;
        }
        if (_fromHost[0] == 0x02 && _state == State.Program)
        {
            Consume(1); Log.Add("mode02");
            Respond(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
            return true;
        }
        if (_fromHost[0] == 0x06 && _state == State.Program)
        {
            Consume(1); Log.Add("ping");
            Respond(0x06);
            return true;
        }

        // Read 'R' [protocol §6.1]
        if (_fromHost[0] == (byte)'R')
        {
            if (_state != State.Program) { Consume(1); return true; }        // ignored outside program mode
            if (_fromHost.Count < 6) return false;
            uint addr = (uint)(_fromHost[1] | _fromHost[2] << 8 | _fromHost[3] << 16);
            ushort len = (ushort)(_fromHost[4] | _fromHost[5] << 8);
            Consume(6);
            var payload = ReadFlash(addr, len);
            Respond([(byte)'W', (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16), (byte)len, (byte)(len >> 8), .. payload]);
            return true;
        }

        // Write 'W' [protocol §6.2]
        if (_fromHost[0] == (byte)'W')
        {
            if (_fromHost.Count < 6) return false;
            uint addr = (uint)(_fromHost[1] | _fromHost[2] << 8 | _fromHost[3] << 16);
            ushort len = (ushort)(_fromHost[4] | _fromHost[5] << 8);
            if (_fromHost.Count < 6 + len) return false;
            var payload = _fromHost.Skip(6).Take(len).ToArray();
            Consume(6 + len);
            _writeFrames++;
            Log.Add($"W:{addr:X6}+{len}");
            if (_state != State.Program) return true;
            if (TimeoutAfterWrites >= 0 && _writeFrames > TimeoutAfterWrites) return true;   // dead air
            if (NakWrites) { Respond(0x15); return true; }
            if (addr % 0x1000 != 0 || len != 0x1000) { Respond(0x15); return true; }
            WriteFlash(addr, payload);
            Respond(0x06);
            return true;
        }

        // Unknown byte: drop it (defensive)
        Consume(1);
        return true;
    }

    private void RespondValue(byte id, byte[] payload) =>
        Respond([0x56, id, (byte)payload.Length, .. payload]);

    private byte[] ReadFlash(uint addr, ushort len)
    {
        var result = new byte[len];
        for (int i = 0; i < len; i++)
        {
            uint a = addr + (uint)i;
            uint block = a & ~0xFFFu;
            result[i] = Flash.TryGetValue(block, out var data) ? data[a & 0xFFF] : (byte)0xFF;
        }
        return result;
    }

    private void WriteFlash(uint addr, byte[] block)
    {
        // The 0xFF000 adoption address: the radio re-homes the block by its metadata byte,
        // choosing a free physical slot. [protocol §6.5]
        if (addr == 0xFF000 && !Flash.ContainsKey(addr))
        {
            for (uint p = RegionStart & ~0xFFFu; p + 0xFFF <= RegionEnd; p += 0x1000)
            {
                if (!Flash.ContainsKey(p)) { Flash[p] = block; return; }
            }
        }
        Flash[addr] = block;
    }
}
