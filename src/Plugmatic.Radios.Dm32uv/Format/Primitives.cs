using Plugmatic.Core.Model;

namespace Plugmatic.Radios.Dm32uv.Format;

public sealed class Dm32FormatException(string message) : Exception(message);

/// <summary>Frequency codec: BCD8 little-endian, units of 10 Hz. [format §11.1]</summary>
public static class Bcd
{
    public const uint NoTxFrequency = 0xFFFFFFFF;

    public static uint EncodeFrequency(Frequency f)
    {
        ulong tens = f.Hz / 10;
        if (f.Hz % 10 != 0 || tens > 99_999_999)
            throw new Dm32FormatException($"Frequency {f} not representable in BCD8 x10Hz.");
        uint bcd = 0;
        for (int digit = 0; digit <= 7; digit++)
        {
            bcd |= (uint)(tens % 10) << (digit * 4);   // least significant decimal digit -> lowest nibble
            tens /= 10;
        }
        return bcd; // caller stores little-endian
    }

    public static Frequency DecodeFrequency(uint bcd)
    {
        ulong value = 0;
        for (int digit = 7; digit >= 0; digit--)
        {
            uint nibble = (bcd >> (digit * 4)) & 0xF;
            if (nibble > 9) throw new Dm32FormatException($"Invalid BCD nibble in frequency word 0x{bcd:X8}.");
            value = value * 10 + nibble;
        }
        return new Frequency(value * 10);
    }
}

/// <summary>Tone codec: u16 word, type in bits 15–14, BCD/octal nibbles below. [format §11.2]</summary>
public static class ToneCodec
{
    public const ushort None = 0xFFFF;

    public static ushort Encode(SelectiveCall tone) => tone.Kind switch
    {
        ToneKind.None => None,
        ToneKind.Ctcss => EncodeCtcss(tone.Value),
        ToneKind.Dcs => (ushort)(((tone.Inverted ? 3 : 2) << 14)
                                 | ((tone.Value >> 6) & 0x7) << 8
                                 | ((tone.Value >> 3) & 0x7) << 4
                                 | (tone.Value & 0x7)),
        _ => None,
    };

    private static ushort EncodeCtcss(int tenthsHz)
    {
        if (tenthsHz is < 0 or > 9999)
            throw new Dm32FormatException($"CTCSS {tenthsHz / 10.0} Hz out of BCD range.");
        int d3 = tenthsHz / 1000 % 10, d2 = tenthsHz / 100 % 10, d1 = tenthsHz / 10 % 10, d0 = tenthsHz % 10;
        return (ushort)((d3 << 12) | (d2 << 8) | (d1 << 4) | d0);
    }

    public static SelectiveCall Decode(ushort word)
    {
        if (word == None) return SelectiveCall.None;
        int type = word >> 14;
        int payload = word & 0x3FFF;
        switch (type)
        {
            case 0:
            {
                int tenths = ((payload >> 12) & 0xF) * 1000 + ((payload >> 8) & 0xF) * 100
                           + ((payload >> 4) & 0xF) * 10 + (payload & 0xF);
                return new SelectiveCall(ToneKind.Ctcss, tenths, false);
            }
            case 2 or 3:
            {
                int code = (((payload >> 8) & 0x7) << 6) | (((payload >> 4) & 0x7) << 3) | (payload & 0x7);
                return SelectiveCall.Dcs(code, type == 3);
            }
            default:
                return SelectiveCall.None; // type 1 unused; tolerate
        }
    }
}

/// <summary>ASCII name fields: 0x00-terminated; decoder also stops at 0xFF. [format §11.3, C2]</summary>
public static class AsciiField
{
    public static string Read(ReadOnlySpan<byte> field)
    {
        int end = 0;
        while (end < field.Length && field[end] != 0x00 && field[end] != 0xFF) end++;
        return System.Text.Encoding.ASCII.GetString(field[..end]);
    }

    public static void Write(Span<byte> field, string value)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(value);
        if (bytes.Length > field.Length)
            throw new Dm32FormatException($"String '{value}' exceeds {field.Length}-byte field.");
        field.Clear();
        bytes.CopyTo(field);
    }
}
