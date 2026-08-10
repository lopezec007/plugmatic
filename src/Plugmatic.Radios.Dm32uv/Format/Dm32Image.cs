namespace Plugmatic.Radios.Dm32uv.Format;

/// <summary>
/// The virtual codeplug image container. [format §1]
/// File offset == virtual address; 4 KiB blocks whose last byte holds the block's own
/// number when present; absent blocks are 0xFF-filled.
/// The image spans the whole virtual block space the metadata byte can address
/// (0x01..0xFF — 0x00 means "unallocated", so block 0 can never exist). Sizing it to
/// only the blocks we decode would silently drop the rest of the radio's codeplug from
/// reads, backups and writes. [format §1, hw-verified: this radio maps 0x01..0x7C]
/// </summary>
public sealed class Dm32Image
{
    public const int BlockSize = 0x1000;
    public const int BlockCount = 0x100;
    public const int Size = BlockCount * BlockSize;         // 0x100000
    public const uint WindowStart = 0x01000;                // meaningful window [0x01000, 0x100000)

    /// <summary>Image size written before the window covered the full block space.</summary>
    public const int LegacySize = 0x68 * BlockSize;

    private readonly byte[] _data;

    public Dm32Image() : this(CreateBlank()) { }

    public Dm32Image(byte[] data)
    {
        if (data.Length == Size)
        {
            _data = data;
            return;
        }
        // Accept short legacy images (archived backups) by 0xFF-padding the tail: the
        // blocks they lack are simply "absent" and stay untouched on write.
        if (data.Length == LegacySize)
        {
            _data = CreateBlank();
            data.CopyTo(_data, 0);
            return;
        }
        throw new Dm32FormatException(
            $"Image must be 0x{Size:X} bytes (or legacy 0x{LegacySize:X}), got 0x{data.Length:X}.");
    }

    private static byte[] CreateBlank()
    {
        var d = new byte[Size];
        Array.Fill(d, (byte)0xFF);
        return d;
    }

    public byte[] Bytes => _data;

    public bool BlockPresent(int block) => _data[block * BlockSize + BlockSize - 1] == (byte)block;

    /// <summary>Zero-fills the block and stamps its metadata byte, marking it present.</summary>
    public Span<byte> AllocateBlock(int block)
    {
        var span = _data.AsSpan(block * BlockSize, BlockSize);
        span.Clear();
        span[BlockSize - 1] = (byte)block;
        return span[..(BlockSize - 1)];
    }

    /// <summary>Payload span of a block (metadata byte excluded); allocates if absent.</summary>
    public Span<byte> Block(int block) =>
        BlockPresent(block)
            ? _data.AsSpan(block * BlockSize, BlockSize - 1)
            : AllocateBlock(block);

    public ReadOnlySpan<byte> ReadBlock(int block) => _data.AsSpan(block * BlockSize, BlockSize - 1);

    public void FreeBlock(int block) => _data.AsSpan(block * BlockSize, BlockSize).Fill(0xFF);

    /// <summary>Contiguous span across blocks for records that straddle nothing (span within one block).</summary>
    public Span<byte> Slice(uint virtAddr, int length) => _data.AsSpan((int)virtAddr, length);
}
