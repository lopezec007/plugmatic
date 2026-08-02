using System.Globalization;

namespace Plugmatic.Core.Model;

public enum ToneKind { None, Ctcss, Dcs }

/// <summary>
/// CTCSS tone or DCS code. CTCSS magnitude is stored in tenths of Hz (127.3 Hz => 1273).
/// DCS code is the conventional octal number (D023N => 0o023 => 19 decimal), plus polarity.
/// Serialized as "none", "127.3", "D023N", "D023I".
/// </summary>
public readonly record struct SelectiveCall(ToneKind Kind, int Value, bool Inverted)
{
    public static readonly SelectiveCall None = new(ToneKind.None, 0, false);
    public static SelectiveCall Ctcss(decimal hz) => new(ToneKind.Ctcss, (int)decimal.Round(hz * 10), false);
    public static SelectiveCall Dcs(int octalCode, bool inverted) => new(ToneKind.Dcs, octalCode, inverted);

    public override string ToString() => Kind switch
    {
        ToneKind.None => "none",
        ToneKind.Ctcss => (Value / 10m).ToString("0.0", CultureInfo.InvariantCulture),
        ToneKind.Dcs => $"D{Convert.ToString(Value, 8).PadLeft(3, '0')}{(Inverted ? "I" : "N")}",
        _ => "none",
    };

    public static SelectiveCall Parse(string s)
    {
        s = s.Trim();
        if (s.Length == 0 || s.Equals("none", StringComparison.OrdinalIgnoreCase))
            return None;
        if (s.StartsWith('D') || s.StartsWith('d'))
        {
            bool inverted = char.ToUpperInvariant(s[^1]) == 'I';
            if (char.ToUpperInvariant(s[^1]) is not ('I' or 'N'))
                throw new FormatException($"DCS code must end in N or I: '{s}'");
            int code = Convert.ToInt32(s[1..^1], 8);
            return Dcs(code, inverted);
        }
        return Ctcss(decimal.Parse(s, CultureInfo.InvariantCulture));
    }
}
