using System.Buffers.Binary;

namespace Plugmatic.Radios.Dm32uv.Format;

/// <summary>Masked read-modify-write helpers so unmodeled bits survive re-encode.</summary>
internal static class BitOps
{
    public static int GetBits(ReadOnlySpan<byte> rec, int offset, int lowBit, int width) =>
        (rec[offset] >> lowBit) & ((1 << width) - 1);

    public static void SetBits(Span<byte> rec, int offset, int lowBit, int width, int value)
    {
        int mask = ((1 << width) - 1) << lowBit;
        rec[offset] = (byte)((rec[offset] & ~mask) | ((value << lowBit) & mask));
    }

    public static bool GetBit(ReadOnlySpan<byte> rec, int offset, int bit) => GetBits(rec, offset, bit, 1) != 0;
    public static void SetBit(Span<byte> rec, int offset, int bit, bool value) => SetBits(rec, offset, bit, 1, value ? 1 : 0);

    public static ushort GetU16(ReadOnlySpan<byte> rec, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(rec[offset..]);
    public static void SetU16(Span<byte> rec, int offset, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(rec[offset..], v);
    public static uint GetU32(ReadOnlySpan<byte> rec, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(rec[offset..]);
    public static void SetU32(Span<byte> rec, int offset, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(rec[offset..], v);

    public static uint GetU24(ReadOnlySpan<byte> rec, int offset) =>
        (uint)(rec[offset] | rec[offset + 1] << 8 | rec[offset + 2] << 16);

    public static void SetU24(Span<byte> rec, int offset, uint v)
    {
        rec[offset] = (byte)v;
        rec[offset + 1] = (byte)(v >> 8);
        rec[offset + 2] = (byte)(v >> 16);
    }

    /// <summary>Writes a name field only when its decoded value differs — preserves original padding style. [format §12 C2]</summary>
    public static void SetNameIfChanged(Span<byte> field, string name)
    {
        if (AsciiField.Read(field) != name)
            AsciiField.Write(field, name);
    }
}
