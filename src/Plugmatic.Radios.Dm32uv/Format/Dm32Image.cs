namespace Plugmatic.Radios.Dm32uv.Format;

/// <summary>
/// The virtual codeplug image container. [format §1]
/// 0x68000 bytes; file offset == virtual address; 4 KiB blocks whose last byte holds
/// the block's own number when present; absent blocks are 0xFF-filled.
/// </summary>
public sealed class Dm32Image
{
    public const int BlockSize = 0x1000;
    public const int BlockCount = 0x68;
    public const int Size = BlockCount * BlockSize;         // 0x68000
    public const uint WindowStart = 0x03000;                // meaningful window [0x03000, 0x68000)

    private readonly byte[] _data;

    public Dm32Image() : this(CreateBlank()) { }

    public Dm32Image(byte[] data)
    {
        if (data.Length != Size)
            throw new Dm32FormatException($"Image must be 0x{Size:X} bytes, got 0x{data.Length:X}.");
        _data = data;
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
