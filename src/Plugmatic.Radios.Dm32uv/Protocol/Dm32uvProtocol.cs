using System.Buffers.Binary;
using Plugmatic.Radios;
using Plugmatic.Radios.Dm32uv.Format;

namespace Plugmatic.Radios.Dm32uv.Protocol;

public sealed class Dm32ProtocolException(string message) : Exception(message);

/// <summary>
/// DM-32UV serial programming protocol. Every frame cites docs/formats/dm32uv-protocol.md.
/// The protocol layer contains no bootloader/DFU/firmware sequences (I8/D14); writes are
/// bounds-checked against the radio-reported codeplug region.
/// </summary>
public sealed class Dm32uvProtocol : IRadioProtocol
{
    public const string ExpectedModel = "DP570UV";           // [protocol §3.1]
    private const byte Ack = 0x06;
    private const byte Nak = 0x15;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromMilliseconds(5000);

    /// <summary>Inter-command pacing [protocol §3/§6]. Overridable so FakeRadio tests run fast.</summary>
    public int CommandDelayMs { get; init; } = 20;
    public int MetadataProbeDelayMs { get; init; } = 5;
    public int BlockReadDelayMs { get; init; } = 25;
    public int BlockWriteDelayMs { get; init; } = 30;

    private RadioIdentity? _identity;
    private Dictionary<uint, uint>? _physByVirt;             // session address map [protocol §6.3]

    // ------------------------------------------------------------ identify

    public async Task<RadioIdentity> IdentifyAsync(ISerialLink link, CancellationToken ct)
    {
        // PSEARCH with up to 3 attempts, 500 ms apart, input cleared each try. [protocol §3.1]
        byte[] resp = new byte[8];
        bool got = false;
        for (int attempt = 0; attempt < 3 && !got; attempt++)
        {
            await Task.Delay(500, ct);
            link.DiscardInput();
            await link.WriteAsync("PSEARCH"u8.ToArray(), ct);
            got = await TryReadExactAsync(link, resp, DefaultTimeout, ct);
        }
        if (!got) throw new Dm32ProtocolException("No response to PSEARCH — radio off, wrong port, or cable issue.");
        if (resp[0] != Ack)
            throw new Dm32ProtocolException($"PSEARCH rejected (0x{resp[0]:X2}).");
        string model = System.Text.Encoding.ASCII.GetString(resp, 1, 7);
        string rawIdentify = Convert.ToHexString(resp);

        await Task.Delay(CommandDelayMs, ct);
        await link.WriteAsync("PASSSTA"u8.ToArray(), ct);    // [protocol §3.2]
        byte[] pass = new byte[3];
        await ReadExactAsync(link, pass, DefaultTimeout, ct, "PASSSTA response");
        if (pass[0] != (byte)'P')
            throw new Dm32ProtocolException($"PASSSTA unexpected response 0x{pass[0]:X2}.");

        await Task.Delay(CommandDelayMs, ct);
        await link.WriteAsync("SYSINFO"u8.ToArray(), ct);    // [protocol §3.3]
        await ExpectAckAsync(link, ct, "SYSINFO");

        string? firmware = await ReadStringValueAsync(link, 0x01, ct);   // [protocol §4]
        string? buildDate = await ReadStringValueAsync(link, 0x03, ct);
        var (start, end) = await ReadMemoryRangeAsync(link, 0x0A, ct);

        _identity = new RadioIdentity(model, firmware, buildDate, start, end, rawIdentify);
        return _identity;
    }

    private async Task<string?> ReadStringValueAsync(ISerialLink link, byte valueId, CancellationToken ct)
    {
        var payload = await ValueRequestAsync(link, valueId, ct);
        return System.Text.Encoding.ASCII.GetString(payload).TrimEnd('\0');
    }

    private async Task<(uint start, uint end)> ReadMemoryRangeAsync(ISerialLink link, byte valueId, CancellationToken ct)
    {
        var payload = await ValueRequestAsync(link, valueId, ct);
        if (payload.Length != 8)
            throw new Dm32ProtocolException($"V-frame 0x{valueId:X2}: expected 8-byte range, got {payload.Length}.");
        return (BinaryPrimitives.ReadUInt32LittleEndian(payload),
                BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4)));
    }

    /// <summary>V-frame: 56 00 00 00 id → 56 id len payload. [protocol §4]</summary>
    private async Task<byte[]> ValueRequestAsync(ISerialLink link, byte valueId, CancellationToken ct)
    {
        await Task.Delay(CommandDelayMs, ct);
        await link.WriteAsync(new byte[] { 0x56, 0x00, 0x00, 0x00, valueId }, ct);
        byte[] header = new byte[3];
        await ReadExactAsync(link, header, DefaultTimeout, ct, $"V-frame 0x{valueId:X2} header");
        if (header[0] != 0x56 || header[1] != valueId)
            throw new Dm32ProtocolException(
                $"V-frame 0x{valueId:X2}: bad header {Convert.ToHexString(header)}.");
        byte[] payload = new byte[header[2]];
        await ReadExactAsync(link, payload, DefaultTimeout, ct, $"V-frame 0x{valueId:X2} payload");
        return payload;
    }

    // ------------------------------------------------------------ program mode

    private bool _inProgramMode;

    private async Task EnterProgramModeAsync(ISerialLink link, CancellationToken ct)
    {
        if (_inProgramMode) return;
        if (_identity is null) throw new InvalidOperationException("IdentifyAsync must run first (I2 preflight).");

        await Task.Delay(CommandDelayMs, ct);                // [protocol §5]
        await link.WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0C, (byte)'P', (byte)'R', (byte)'O', (byte)'G', (byte)'R', (byte)'A', (byte)'M' }, ct);
        await ExpectAckAsync(link, ct, "PROGRAM");

        await Task.Delay(CommandDelayMs, ct);
        await link.WriteAsync(new byte[] { 0x02 }, ct);
        byte[] eight = new byte[8];                          // opaque 8-byte reply [protocol §5, C1]
        await ReadExactAsync(link, eight, DefaultTimeout, ct, "mode-02 response");

        await Task.Delay(CommandDelayMs, ct);
        await link.WriteAsync(new byte[] { 0x06 }, ct);
        await ExpectAckAsync(link, ct, "program-mode ping");
        _inProgramMode = true;
    }

    // ------------------------------------------------------------ raw block access

    /// <summary>Read `length` (≤ 0x1000) bytes at physical address. [protocol §6.1]</summary>
    public async Task<byte[]> ReadPhysicalAsync(ISerialLink link, uint address, ushort length, CancellationToken ct)
    {
        await EnterProgramModeAsync(link, ct);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var req = new byte[6];
                req[0] = (byte)'R';
                req[1] = (byte)address; req[2] = (byte)(address >> 8); req[3] = (byte)(address >> 16);
                BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(4), length);
                await link.WriteAsync(req, ct);

                var header = new byte[6];
                await ReadExactAsync(link, header, DefaultTimeout, ct, "read response header");
                if (header[0] != (byte)'W')
                    throw new Dm32ProtocolException($"Read: expected 'W' response, got 0x{header[0]:X2}.");
                uint echoAddr = (uint)(header[1] | header[2] << 8 | header[3] << 16);
                ushort echoLen = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
                if (echoAddr != address || echoLen != length)
                    throw new Dm32ProtocolException($"Read: echo mismatch (asked 0x{address:X6}+{length}, got 0x{echoAddr:X6}+{echoLen}).");
                var payload = new byte[length];
                await ReadExactAsync(link, payload, DefaultTimeout, ct, "read payload");
                return payload;
            }
            catch (Dm32ProtocolException) when (attempt < 2)
            {
                // Read frames are idempotent — bounded retry. [protocol §8]
                link.DiscardInput();
                await Task.Delay(100, ct);
            }
        }
    }

    /// <summary>The documented adoption address for not-yet-mapped virtual blocks. [protocol §6.5]</summary>
    public const uint AdoptionAddress = 0xFF000;

    /// <summary>Write one aligned 4 KiB block at physical address. Bounds-checked (I8). No retries. [protocol §6.2/§8]</summary>
    internal async Task WritePhysicalBlockAsync(ISerialLink link, uint address, ReadOnlyMemory<byte> block, CancellationToken ct)
    {
        if (_identity is null) throw new InvalidOperationException("Identify first.");
        if (block.Length != Dm32Image.BlockSize || (address & 0xFFF) != 0)
            throw new Dm32ProtocolException("Writes must be aligned whole 4 KiB blocks.");
        // I8: only two legal targets exist — fully inside the radio-reported codeplug
        // region, or the exact adoption block. Everything else is refused. [protocol §6.2]
        bool insideRegion = address >= _identity.CodeplugMemoryStart
                            && address + Dm32Image.BlockSize - 1 <= _identity.CodeplugMemoryEnd;
        if (!insideRegion && address != AdoptionAddress)
            throw new Dm32ProtocolException(
                $"I8 bounds violation: write 0x{address:X6} outside codeplug region " +
                $"0x{_identity.CodeplugMemoryStart:X6}-0x{_identity.CodeplugMemoryEnd:X6}. Refusing.");

        await EnterProgramModeAsync(link, ct);
        var frame = new byte[6 + Dm32Image.BlockSize];
        frame[0] = (byte)'W';
        frame[1] = (byte)address; frame[2] = (byte)(address >> 8); frame[3] = (byte)(address >> 16);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), Dm32Image.BlockSize);
        block.Span.CopyTo(frame.AsSpan(6));
        await link.WriteAsync(frame, ct);

        byte[] one = new byte[1];
        if (!await TryReadExactAsync(link, one, WriteTimeout, ct))
            throw new Dm32ProtocolException($"Write 0x{address:X6}: no ACK within 5 s.");
        if (one[0] == Nak) throw new Dm32ProtocolException($"Write 0x{address:X6}: radio NAK.");
        if (one[0] != Ack) throw new Dm32ProtocolException($"Write 0x{address:X6}: unexpected 0x{one[0]:X2}.");
    }

    // ------------------------------------------------------------ address map

    private async Task<Dictionary<uint, uint>> GetAddressMapAsync(ISerialLink link, CancellationToken ct)
    {
        if (_physByVirt is not null) return _physByVirt;
        if (_identity is null) throw new InvalidOperationException("Identify first.");
        await EnterProgramModeAsync(link, ct);

        var map = new Dictionary<uint, uint>();              // virt block addr -> phys block addr
        for (uint phys = _identity.CodeplugMemoryStart & ~0xFFFu; phys < _identity.CodeplugMemoryEnd; phys += 0x1000)
        {
            await Task.Delay(MetadataProbeDelayMs, ct);
            var meta = await ReadPhysicalAsync(link, phys + 0xFFF, 1, ct);   // [protocol §6.3]
            byte v = meta[0];
            if (v is 0x00 or 0xFF) continue;
            uint virt = (uint)v << 12;
            if (map.TryGetValue(virt, out var existing))
                throw new Dm32ProtocolException(
                    $"Duplicate virtual block 0x{v:X2} at physical 0x{existing:X6} and 0x{phys:X6} — " +
                    "protocol doc §6.3 assumption violated; capture and update the doc.");
            map[virt] = phys;
        }
        _physByVirt = map;
        return map;
    }

    // ------------------------------------------------------------ image read/write

    public async Task<byte[]> ReadImageAsync(ISerialLink link, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var map = await GetAddressMapAsync(link, ct);
        var img = new Dm32Image();                           // absent blocks 0xFF [format §1]
        // Every mapped block is read: a backup that skips blocks is not a backup.
        var inWindow = map.Where(kv => kv.Key >= Dm32Image.WindowStart && kv.Key < Dm32Image.Size)
                          .OrderBy(kv => kv.Value).ToList(); // read in physical order [protocol §6.4]
        if (inWindow.Count != map.Count)
            throw new Dm32ProtocolException(
                $"Radio maps {map.Count} blocks but only {inWindow.Count} fall inside the image window " +
                $"[0x{Dm32Image.WindowStart:X}, 0x{Dm32Image.Size:X}); refusing a partial read. " +
                "Update Dm32Image/format doc §1.");
        int done = 0;
        foreach (var (virt, phys) in inWindow)
        {
            await Task.Delay(BlockReadDelayMs, ct);
            var block = await ReadPhysicalAsync(link, phys, Dm32Image.BlockSize, ct);
            block.CopyTo(img.Bytes.AsSpan((int)virt, Dm32Image.BlockSize));
            progress?.Report(new TransferProgress("read", ++done, inWindow.Count));
        }
        return img.Bytes;
    }

    public async Task WriteImageAsync(ISerialLink link, ReadOnlyMemory<byte> image, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var img = new Dm32Image(image.ToArray());
        var map = await GetAddressMapAsync(link, ct);

        var blocks = new List<int>();
        for (int blk = (int)(Dm32Image.WindowStart / Dm32Image.BlockSize); blk < Dm32Image.BlockCount; blk++)
            if (img.BlockPresent(blk)) blocks.Add(blk);

        int done = 0;
        foreach (int blk in blocks)
        {
            uint virt = (uint)blk * Dm32Image.BlockSize;
            // Mapped virtual blocks overwrite their physical home; new blocks go to the
            // 0xFF000 adoption address and the radio re-homes them via the stamped
            // metadata byte. [protocol §6.5]
            uint phys = map.TryGetValue(virt, out var p) ? p : AdoptionAddress;
            var payload = image.Slice(blk * Dm32Image.BlockSize, Dm32Image.BlockSize);
            if (payload.Span[Dm32Image.BlockSize - 1] != (byte)blk)
                throw new Dm32ProtocolException($"Block 0x{blk:X2} not stamped with its virtual number; codec bug.");
            await Task.Delay(BlockWriteDelayMs, ct);
            await WritePhysicalBlockAsync(link, phys, payload, ct);
            progress?.Report(new TransferProgress("write", ++done, blocks.Count));
        }
    }

    // ------------------------------------------------------------ teardown

    public async Task EndSessionAsync(ISerialLink link, CancellationToken ct)
    {
        // Belt and braces: END frame (dm32-spec) then DTR reset (qdmr). [protocol §7]
        try
        {
            if (_inProgramMode)
            {
                await Task.Delay(CommandDelayMs, ct);
                await link.WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0C, (byte)'E', (byte)'N', (byte)'D', 0x00, 0x00, 0x00, 0x00 }, ct);
                byte[] one = new byte[1];
                await TryReadExactAsync(link, one, DefaultTimeout, ct);      // tolerate silence
            }
        }
        catch { /* teardown is best-effort */ }
        finally
        {
            try
            {
                link.SetDtr(false);
                await Task.Delay(500, CancellationToken.None);
                link.SetDtr(true);
            }
            catch { /* port may already be gone */ }
            _inProgramMode = false;
            _physByVirt = null;
        }
    }

    // ------------------------------------------------------------ link helpers

    private static async Task ExpectAckAsync(ISerialLink link, CancellationToken ct, string what)
    {
        byte[] one = new byte[1];
        await ReadExactAsync(link, one, DefaultTimeout, ct, $"{what} ACK");
        if (one[0] == Nak) throw new Dm32ProtocolException($"{what}: radio NAK (0x15).");
        if (one[0] != Ack) throw new Dm32ProtocolException($"{what}: expected ACK 0x06, got 0x{one[0]:X2}.");
    }

    private static async Task ReadExactAsync(ISerialLink link, Memory<byte> buffer, TimeSpan timeout, CancellationToken ct, string what)
    {
        if (!await TryReadExactAsync(link, buffer, timeout, ct))
            throw new Dm32ProtocolException($"Timeout reading {what}.");
    }

    private static async Task<bool> TryReadExactAsync(ISerialLink link, Memory<byte> buffer, TimeSpan timeout, CancellationToken ct)
    {
        int got = 0;
        var deadline = DateTime.UtcNow + timeout;
        while (got < buffer.Length)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return false;
            int n = await link.ReadAsync(buffer[got..], remaining, ct);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }
}
