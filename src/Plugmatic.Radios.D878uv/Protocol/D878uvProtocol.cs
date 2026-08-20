using System.Buffers.Binary;
using Plugmatic.Radios.D878uv.Format;

namespace Plugmatic.Radios.D878uv.Protocol;

public sealed class D878ProtocolException(string message) : Exception(message);

/// <summary>
/// AnyTone AT-D878UVII+ serial protocol, implemented from docs/formats/d878uv-protocol.md.
/// Contains no bootloader/DFU sequences (D14); writes are bounds-checked against the
/// format doc's region table (I8).
/// </summary>
public sealed class D878uvProtocol : IRadioProtocol
{
    private const byte Ack = 0x06;
    private const byte CmdRead = (byte)'R';
    private const byte CmdWrite = (byte)'W';

    /// <summary>Read payload size; 64 is dmrconfig-proven, 16 is the fallback. [protocol §4]</summary>
    public const int ReadChunk = 0x40;
    /// <summary>Write payload size — 16 in both references, never varied. [protocol §5]</summary>
    public const int WriteChunk = 0x10;

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMilliseconds(2000);

    public int InterCommandDelayMs { get; init; } = 0;
    public int HandshakeRetries { get; init; } = 10;
    public int HandshakeRetryDelayMs { get; init; } = 500;

    private bool _inProgramMode;
    private int _readChunk = ReadChunk;

    /// <summary>
    /// True once a write has been staged in this session. The radio discards every staged
    /// write the moment it sees a read command, so this flips the session into write-only
    /// mode until <see cref="EndSessionAsync"/> commits. [protocol §5.1 W3]
    /// </summary>
    private bool _writesStaged;

    /// <summary>Writes are staged and not yet committed by `END`. [protocol §5.1]</summary>
    public bool HasStagedWrites => _writesStaged;

    // ------------------------------------------------------------ identify

    public async Task<RadioIdentity> IdentifyAsync(ISerialLink link, CancellationToken ct)
    {
        await EnterProgramModeAsync(link, ct);

        // 0x02 -> 16-byte radio-info record. [protocol §3]
        var info = new byte[16];
        for (int attempt = 0; ; attempt++)
        {
            link.DiscardInput();
            await link.WriteAsync(new byte[] { 0x02 }, ct);
            if (await TryReadExactAsync(link, info, CommandTimeout, ct) && info[0] == 'I' && info[15] == Ack)
                break;
            if (attempt + 1 >= HandshakeRetries)
                throw new D878ProtocolException(
                    $"Bad identify response: {Convert.ToHexString(info)} (expected 'I' … 0x06).");
            await Task.Delay(HandshakeRetryDelayMs, ct);
        }

        string model = System.Text.Encoding.ASCII.GetString(info, 1, 7).TrimEnd('\0', ' ');
        string version = System.Text.Encoding.ASCII.GetString(info, 9, 6).TrimEnd('\0', ' ');
        return new RadioIdentity(
            Model: model,
            FirmwareVersion: version,
            BuildDate: null,
            CodeplugMemoryStart: Layout.RegionsStart,
            CodeplugMemoryEnd: Layout.RegionsEnd,
            RawIdentifyHex: Convert.ToHexString(info));
    }

    private async Task EnterProgramModeAsync(ISerialLink link, CancellationToken ct)
    {
        if (_inProgramMode) return;
        var ack = new byte[3];
        for (int attempt = 0; ; attempt++)
        {
            link.DiscardInput();
            await link.WriteAsync("PROGRAM"u8.ToArray(), ct);            // [protocol §2]
            if (await TryReadExactAsync(link, ack, CommandTimeout, ct)
                && ack[0] == 'Q' && ack[1] == 'X' && ack[2] == Ack)
            {
                _inProgramMode = true;
                return;
            }
            if (attempt + 1 >= HandshakeRetries)
                throw new D878ProtocolException(
                    $"No PROGRAM acknowledge (got {Convert.ToHexString(ack)}, expected 515806). " +
                    "Is the radio powered on with the programming cable seated?");
            await Task.Delay(HandshakeRetryDelayMs, ct);
        }
    }

    // ------------------------------------------------------------ region read/write

    /// <summary>Read `length` bytes from `address`, chunked per §4.</summary>
    public async Task<byte[]> ReadRegionAsync(ISerialLink link, uint address, int length,
        IProgress<TransferProgress>? progress, string phase, CancellationToken ct)
    {
        GuardReadAfterWrite();
        await EnterProgramModeAsync(link, ct);
        var result = new byte[length];
        int done = 0;
        while (done < length)
        {
            int want = Math.Min(_readChunk, length - done);
            var chunk = await ReadChunkAsync(link, address + (uint)done, want, ct);
            chunk.CopyTo(result.AsSpan(done));
            done += want;
            progress?.Report(new TransferProgress(phase, done, length));
        }
        return result;
    }

    /// <summary>
    /// A read after a write does not merely return stale bytes — it makes the radio throw
    /// away every write staged in this session. Failing loudly here is the difference
    /// between a caught bug and a codeplug that silently did not change. [protocol §5.1 W3]
    /// </summary>
    private void GuardReadAfterWrite()
    {
        if (!_writesStaged) return;
        throw new D878ProtocolException(
            "Read attempted after a write in the same session. This radio discards every " +
            "staged write when it receives a read command, so this would silently throw the " +
            "codeplug away. End the session (which commits), then reopen to read back. " +
            "[d878uv-protocol.md §5.1]");
    }

    private async Task<byte[]> ReadChunkAsync(ISerialLink link, uint address, int length, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var req = new byte[6];
                req[0] = CmdRead;
                BinaryPrimitives.WriteUInt32BigEndian(req.AsSpan(1), address);   // BIG-endian [protocol §4]
                req[5] = (byte)length;
                if (InterCommandDelayMs > 0) await Task.Delay(InterCommandDelayMs, ct);
                await link.WriteAsync(req, ct);

                var resp = new byte[length + 8];
                if (!await TryReadExactAsync(link, resp, CommandTimeout, ct))
                    throw new D878ProtocolException($"Timeout reading 0x{address:X8}.");
                ValidateFrame(resp, address, length, "read response");
                return resp[6..(6 + length)];
            }
            catch (D878ProtocolException) when (attempt < 2)
            {
                // Reads are idempotent: resync, and drop to the conservative chunk size.
                link.DiscardInput();
                _readChunk = 0x10;
                await Task.Delay(100, ct);
                if (length > _readChunk) throw;   // caller re-chunks on the next pass
            }
        }
    }

    /// <summary>Shared frame check for read responses and write requests. [protocol §4/§5]</summary>
    private static void ValidateFrame(ReadOnlySpan<byte> frame, uint address, int length, string what)
    {
        if (frame[0] != CmdWrite)
            throw new D878ProtocolException($"{what}: expected 'W', got 0x{frame[0]:X2}.");
        uint echo = BinaryPrimitives.ReadUInt32BigEndian(frame[1..]);
        if (echo != address)
            throw new D878ProtocolException($"{what}: address echo 0x{echo:X8} != 0x{address:X8}.");
        if (frame[5] != length)
            throw new D878ProtocolException($"{what}: length echo {frame[5]} != {length}.");
        byte sum = Checksum(frame, length);
        if (frame[6 + length] != sum)
            throw new D878ProtocolException(
                $"{what}: checksum 0x{frame[6 + length]:X2} != computed 0x{sum:X2} at 0x{address:X8}.");
        if (frame[7 + length] != Ack)
            throw new D878ProtocolException($"{what}: missing trailing ACK at 0x{address:X8}.");
    }

    /// <summary>8-bit sum over address + length + payload, i.e. frame[1 .. 5+len]. [protocol §4]</summary>
    internal static byte Checksum(ReadOnlySpan<byte> frame, int length)
    {
        byte sum = 0;
        for (int i = 1; i < 6 + length; i++) sum += frame[i];
        return sum;
    }

    /// <summary>Write one 16-byte chunk. Bounds-checked (I8). Never retried. [protocol §5/§6]</summary>
    public async Task WriteChunkAsync(ISerialLink link, uint address, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (data.Length != WriteChunk)
            throw new D878ProtocolException($"Writes must be {WriteChunk} bytes, got {data.Length}.");
        if (!Layout.IsWritable(address, data.Length))
            throw new D878ProtocolException(
                $"I8 bounds violation: 0x{address:X8}+{data.Length} is outside the codeplug regions " +
                "in d878uv-format.md. Refusing.");

        await EnterProgramModeAsync(link, ct);
        var frame = new byte[WriteChunk + 8];
        frame[0] = CmdWrite;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), address);
        frame[5] = WriteChunk;
        data.Span.CopyTo(frame.AsSpan(6));
        frame[6 + WriteChunk] = Checksum(frame, WriteChunk);
        frame[7 + WriteChunk] = Ack;

        if (InterCommandDelayMs > 0) await Task.Delay(InterCommandDelayMs, ct);
        await link.WriteAsync(frame, ct);

        var ack = new byte[1];
        if (!await TryReadExactAsync(link, ack, CommandTimeout, ct))
            throw new D878ProtocolException($"Write 0x{address:X8}: no acknowledge.");
        if (ack[0] != Ack)
            throw new D878ProtocolException($"Write 0x{address:X8}: got 0x{ack[0]:X2}, expected ACK.");

        // The ACK means "staged", not "applied": only `END` makes it real. [protocol §5.1]
        _writesStaged = true;
    }

    // ------------------------------------------------------------ image read/write

    public async Task<byte[]> ReadImageAsync(ISerialLink link, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var image = new byte[Layout.ImageSize];
        int total = Layout.Regions.Sum(r => r.Length), done = 0;
        foreach (var region in Layout.Regions)
        {
            var bytes = await ReadRegionAsync(link, region.Address, region.Length, null, "read", ct);
            bytes.CopyTo(image.AsSpan(Layout.OffsetOf(region.Address)));
            done += region.Length;
            progress?.Report(new TransferProgress("read", done, total));
        }
        return image;
    }

    /// <summary>
    /// Write `image`, sending only the 16-byte chunks that differ from `baseline` when one
    /// is supplied. Nothing is committed until <see cref="EndSessionAsync"/> sends `END`,
    /// and no read may happen in between. [protocol §5.1/§5.3]
    /// </summary>
    public async Task WriteImageAsync(ISerialLink link, ReadOnlyMemory<byte> image,
        ReadOnlyMemory<byte>? baseline, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        if (image.Length != Layout.ImageSize)
            throw new D878ProtocolException($"Image must be 0x{Layout.ImageSize:X} bytes, got 0x{image.Length:X}.");
        if (baseline is { } b && b.Length != Layout.ImageSize)
            throw new D878ProtocolException($"Baseline must be 0x{Layout.ImageSize:X} bytes, got 0x{b.Length:X}.");

        var plan = BuildWritePlan(image, baseline);
        int total = plan.Count * WriteChunk, done = 0;
        foreach (var (address, offset) in plan)
        {
            await WriteChunkAsync(link, address, image.Slice(offset, WriteChunk), ct);
            done += WriteChunk;
            progress?.Report(new TransferProgress("write", done, total));
        }
    }

    /// <summary>
    /// The 16-byte chunks to send, ascending by address.
    ///
    /// A write erases the whole 256 KB block around it (Layout.EraseBlockSize), so a plan
    /// cannot be "just the chunks that changed": every byte of every block it touches has
    /// to be rewritten in the same session or it comes back 0xFF. That makes the region
    /// table the limiting factor — it describes 1.63 MB scattered across blocks that hold
    /// considerably more, so writing a block would destroy codeplug bytes Plugmatic has
    /// never read. Blocks like that are refused, by address, rather than silently wiped.
    /// [protocol §5.3/§5.4]
    /// </summary>
    internal static List<(uint Address, int Offset)> BuildWritePlan(
        ReadOnlyMemory<byte> image, ReadOnlyMemory<byte>? baseline)
    {
        // 1. Which erase blocks hold a byte that has to change?
        var touched = new SortedSet<uint>();
        foreach (var region in Layout.Regions)
        {
            int start = Layout.OffsetOf(region.Address);
            for (int n = 0; n < region.Length; n += WriteChunk)
            {
                int len = Math.Min(WriteChunk, region.Length - n);
                if (Unchanged(image, baseline, start + n, len)) continue;
                touched.Add(Layout.EraseBlockOf(region.Address + (uint)n));
            }
        }

        // 2. Refuse any block the region table does not fully describe.
        var underCovered = touched
            .Select(block => (Block: block, Covered: Layout.CoveredBytesInBlock(block)))
            .Where(x => x.Covered < Layout.EraseBlockSize)
            .ToList();
        if (underCovered.Count > 0)
        {
            var first = underCovered[0];
            throw new D878ProtocolException(
                $"Refusing to write: {underCovered.Count} erase block(s) would be wiped but are " +
                "only partly described by the region table. Writing 16 bytes erases the whole " +
                $"0x{Layout.EraseBlockSize:X} block, so every byte in it must be rewritten from a " +
                "known value. Block 0x" + $"{first.Block:X8} is covered for only 0x{first.Covered:X} " +
                $"of 0x{Layout.EraseBlockSize:X} bytes; the rest is codeplug data Plugmatic has " +
                "never read and could not put back. [d878uv-protocol.md §5.4]");
        }

        // 3. Rewrite every touched block in full. Regions inside a fully covered block are
        //    contiguous in the packed image, so the block maps to one contiguous slice.
        var plan = new List<(uint, int)>();
        foreach (var block in touched)
        {
            int start = Layout.OffsetOf(block);
            for (uint n = 0; n < Layout.EraseBlockSize; n += WriteChunk)
                plan.Add((block + n, start + (int)n));
        }
        return plan;
    }

    /// <summary>True when `len` bytes at `at` are identical in image and baseline.</summary>
    private static bool Unchanged(ReadOnlyMemory<byte> image, ReadOnlyMemory<byte>? baseline, int at, int len)
        => baseline is { } b && b.Span.Slice(at, len).SequenceEqual(image.Span.Slice(at, len));

    /// <summary>
    /// Leave program mode. After writes this is the **commit point**, not a teardown
    /// courtesy: `END` is what makes staged writes real, so when writes are pending a
    /// failure here means the codeplug was discarded and must be reported rather than
    /// swallowed. [protocol §2/§5.1]
    /// </summary>
    public async Task EndSessionAsync(ISerialLink link, CancellationToken ct)
    {
        if (!_inProgramMode) return;
        bool committing = _writesStaged;
        try
        {
            await link.WriteAsync("END"u8.ToArray(), ct);                 // [protocol §2]
            var ack = new byte[1];
            bool acked = await TryReadExactAsync(link, ack, CommandTimeout, ct) && ack[0] == Ack;
            if (committing && !acked)
                throw new D878ProtocolException(
                    "END was not acknowledged after staging writes. This radio commits on END, " +
                    "so the codeplug has NOT been changed and the radio still holds its previous " +
                    "contents. [d878uv-protocol.md §5.1]");
        }
        catch (Exception) when (!committing)
        {
            // Teardown of a read-only session is best-effort.
        }
        finally
        {
            _inProgramMode = false;
            _writesStaged = false;
        }
    }

    // ------------------------------------------------------------ link helper

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
