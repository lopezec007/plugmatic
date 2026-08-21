using Plugmatic.Radios;
using Plugmatic.Radios.D878uv.Format;
using Plugmatic.Radios.D878uv.Protocol;

namespace Plugmatic.Tests;

/// <summary>
/// In-process AnyTone radio: the executable form of docs/formats/d878uv-protocol.md.
/// When a hardware capture contradicts this class, one of them is wrong and the doc
/// gets fixed first.
/// </summary>
public sealed class FakeAnytoneRadio : ISerialLink
{
    private readonly Queue<byte> _toHost = new();
    private readonly List<byte> _fromHost = [];
    private bool _program;

    /// <summary>
    /// Writes land here, not in <see cref="Memory"/>: this radio stages writes, commits
    /// them on `END`, and throws the whole staging area away the moment it sees a read.
    /// [d878uv-protocol.md §5.1]
    /// </summary>
    private readonly Dictionary<uint, byte> _staged = [];

    /// <summary>Counts writes a read discarded — the §5.1 W3 trap, made visible to tests.</summary>
    public int DiscardedWriteCount { get; private set; }

    /// <summary>Erase blocks this radio has erased, in order. [§5.4]</summary>
    public List<uint> ErasedBlocks { get; } = [];

    /// <summary>
    /// `END` commits — and committing means erasing every block the session staged a byte
    /// into, then programming only the staged bytes. Anything else that was in those blocks
    /// is gone. This is the behaviour that cost real channel data on hardware, so the fake
    /// models it: a test that writes carelessly fails here instead of on a radio.
    /// [d878uv-protocol.md §5.4]
    /// </summary>
    private void Commit()
    {
        foreach (var block in _staged.Keys.Select(Layout.EraseBlockOf).Distinct().OrderBy(b => b))
        {
            ErasedBlocks.Add(block);
            foreach (var addr in Memory.Keys
                         .Where(a => a >= block && a < block + Layout.EraseBlockSize).ToList())
                Memory.Remove(addr);                       // erased to 0xFF
        }
        foreach (var (addr, value) in _staged) Memory[addr] = value;
        _staged.Clear();
    }

    public string Model { get; init; } = "D878UV2";
    public string Version { get; init; } = "V300";
    public Dictionary<uint, byte> Memory { get; } = [];
    public List<string> Log { get; } = [];
    public bool CorruptNextChecksum { get; set; }

    public void LoadImage(byte[] packed)
    {
        foreach (var region in Layout.Regions)
            for (int i = 0; i < region.Length; i++)
                Memory[region.Address + (uint)i] = packed[Layout.OffsetOf(region.Address) + i];
    }

    public byte[] PackedImage()
    {
        var img = new byte[Layout.ImageSize];
        Array.Fill(img, (byte)0xFF);
        foreach (var (addr, value) in Memory)
            if (Layout.TryOffsetOf(addr, out int off)) img[off] = value;
        return img;
    }

    public Task OpenAsync(SerialSettings settings, CancellationToken ct) => Task.CompletedTask;

    public ValueTask<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct)
    {
        int n = 0;
        while (n < buffer.Length && _toHost.Count > 0) buffer.Span[n++] = _toHost.Dequeue();
        return ValueTask.FromResult(n);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        _fromHost.AddRange(buffer.ToArray());
        while (Consume()) { }
        return ValueTask.CompletedTask;
    }

    public void DiscardInput() => _toHost.Clear();
    public void SetDtr(bool asserted) { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void Send(params byte[] bytes) { foreach (var b in bytes) _toHost.Enqueue(b); }

    private bool Consume()
    {
        if (_fromHost.Count == 0) return false;

        if (Starts("PROGRAM"u8)) { Take(7); Log.Add("PROGRAM"); _program = true; Send(0x51, 0x58, 0x06); return true; }
        if (Starts("END"u8))
        {
            Take(3); Log.Add("END");
            Commit();
            _program = false;
            Send(0x06);
            return true;
        }
        if (_fromHost[0] == 0x02)
        {
            Take(1); Log.Add("identify");
            var info = new byte[16];
            info[0] = (byte)'I';
            System.Text.Encoding.ASCII.GetBytes(Model).CopyTo(info, 1);
            System.Text.Encoding.ASCII.GetBytes(Version).CopyTo(info, 9);
            info[15] = 0x06;
            Send(info);
            return true;
        }
        if (_fromHost[0] == (byte)'R')
        {
            if (_fromHost.Count < 6) return false;
            var req = Take(6);
            uint addr = (uint)(req[1] << 24 | req[2] << 16 | req[3] << 8 | req[4]);
            int len = req[5];
            Log.Add($"R:{addr:X8}+{len}");
            if (!_program) return true;
            // A read discards everything staged, whatever address it targets. [§5.1 W3]
            DiscardedWriteCount += _staged.Count;
            _staged.Clear();
            var frame = new byte[len + 8];
            frame[0] = (byte)'W';
            frame[1] = req[1]; frame[2] = req[2]; frame[3] = req[3]; frame[4] = req[4];
            frame[5] = (byte)len;
            for (int i = 0; i < len; i++) frame[6 + i] = Memory.GetValueOrDefault(addr + (uint)i, (byte)0xFF);
            frame[6 + len] = D878uvProtocol.Checksum(frame, len);
            if (CorruptNextChecksum) { frame[6 + len] ^= 0xFF; CorruptNextChecksum = false; }
            frame[7 + len] = 0x06;
            Send(frame);
            return true;
        }
        if (_fromHost[0] == (byte)'W')
        {
            if (_fromHost.Count < 6) return false;
            int len = _fromHost[5];
            if (_fromHost.Count < len + 8) return false;
            var frame = Take(len + 8);
            uint addr = (uint)(frame[1] << 24 | frame[2] << 16 | frame[3] << 8 | frame[4]);
            Log.Add($"W:{addr:X8}+{len}");
            if (frame[6 + len] != D878uvProtocol.Checksum(frame, len)) { Send(0x15); return true; }
            // Staged, not applied: only END makes these real. [§5.1 W1]
            for (int i = 0; i < len; i++) _staged[addr + (uint)i] = frame[6 + i];
            Send(0x06);
            return true;
        }
        _fromHost.RemoveAt(0);
        return true;
    }

    private bool Starts(ReadOnlySpan<byte> prefix) =>
        _fromHost.Count >= prefix.Length && _fromHost.Take(prefix.Length).SequenceEqual(prefix.ToArray());

    private byte[] Take(int n)
    {
        var taken = _fromHost.Take(n).ToArray();
        _fromHost.RemoveRange(0, n);
        return taken;
    }
}

public class D878ProtocolTests
{
    private static D878uvProtocol Fast() => new() { HandshakeRetryDelayMs = 1, HandshakeRetries = 3 };

    [Fact]
    public async Task Identify_parses_model_and_version()
    {
        var radio = new FakeAnytoneRadio { Model = "D878UV2", Version = "V300" };
        var id = await Fast().IdentifyAsync(radio, CancellationToken.None);
        Assert.Equal("D878UV2", id.Model);
        Assert.Equal("V300", id.FirmwareVersion);
        Assert.Equal("PROGRAM", radio.Log[0]);
        Assert.Equal("identify", radio.Log[1]);
    }

    [Fact]
    public async Task Read_uses_big_endian_addresses_and_verifies_checksum()
    {
        var radio = new FakeAnytoneRadio();
        radio.Memory[Layout.ChannelBanks] = 0xAB;
        var proto = Fast();
        await proto.IdentifyAsync(radio, CancellationToken.None);

        var data = await proto.ReadRegionAsync(radio, Layout.ChannelBanks, 0x40, null, "read", CancellationToken.None);
        Assert.Equal(0xAB, data[0]);
        Assert.Contains(radio.Log, l => l == $"R:{Layout.ChannelBanks:X8}+64");
    }

    [Fact]
    public async Task Bad_checksum_is_retried_then_reported()
    {
        var radio = new FakeAnytoneRadio { CorruptNextChecksum = true };
        var proto = Fast();
        await proto.IdentifyAsync(radio, CancellationToken.None);
        // The retry drops to the conservative chunk size and succeeds.
        var data = await proto.ReadRegionAsync(radio, Layout.ChannelBanks, 0x10, null, "read", CancellationToken.None);
        Assert.Equal(0x10, data.Length);
    }

    [Fact]
    public async Task Full_image_round_trips_through_the_fake_radio()
    {
        var image = new byte[Layout.ImageSize];
        Random.Shared.NextBytes(image);
        var radio = new FakeAnytoneRadio();
        radio.LoadImage(image);

        var proto = Fast();
        await proto.IdentifyAsync(radio, CancellationToken.None);
        var read = await proto.ReadImageAsync(radio, null, CancellationToken.None);
        Assert.Equal(image, read);
    }

    [Fact]
    public async Task Writes_outside_the_region_table_are_refused_before_sending()
    {
        var radio = new FakeAnytoneRadio();
        var proto = Fast();
        await proto.IdentifyAsync(radio, CancellationToken.None);
        var chunk = new byte[16];

        var ex = await Assert.ThrowsAsync<D878ProtocolException>(
            () => proto.WriteChunkAsync(radio, 0x04340000, chunk, CancellationToken.None));   // callsign DB
        Assert.Contains("I8", ex.Message);
        Assert.DoesNotContain(radio.Log, l => l.StartsWith("W:"));

        await proto.WriteChunkAsync(radio, Layout.ChannelBanks, chunk, CancellationToken.None);
        Assert.Contains(radio.Log, l => l.StartsWith("W:00800000"));
    }

    [Fact]
    public async Task End_session_leaves_program_mode()
    {
        var radio = new FakeAnytoneRadio();
        var proto = Fast();
        await proto.IdentifyAsync(radio, CancellationToken.None);
        await proto.EndSessionAsync(radio, CancellationToken.None);
        Assert.Contains("END", radio.Log);
    }

    [Fact]
    public void Region_table_is_ordered_gapless_in_the_packed_image_and_maps_both_ways()
    {
        Assert.NotEmpty(Layout.Regions);
        for (int i = 1; i < Layout.Regions.Count; i++)
            Assert.True(Layout.Regions[i].Address >= Layout.Regions[i - 1].End,
                        $"{Layout.Regions[i - 1].Name} overlaps {Layout.Regions[i].Name}");
        foreach (var region in Layout.Regions)
        {
            int off = Layout.OffsetOf(region.Address);
            Assert.Equal(region.Address, Layout.AddressOf(off));
            Assert.Equal(region.Address + (uint)region.Length - 1, Layout.AddressOf(off + region.Length - 1));
        }
        Assert.False(Layout.IsWritable(0x04340000, 16));    // callsign DB excluded by design
        Assert.True(Layout.IsWritable(Layout.ChannelBanks, 16));
    }

    [Fact]
    public void Writing_stays_disabled_until_the_writable_address_set_is_known()
    {
        // The protocol is solved; the addressing is not. See d878uv-protocol.md §5.7.
        var radio = Plugmatic.Radios.D878uv.D878uvRadio.Instance;
        Assert.False(radio.SupportsWrite);
        Assert.True(radio.SeparateReadWriteSessions);
    }
}
