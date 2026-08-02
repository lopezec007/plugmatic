using System.Globalization;

namespace Plugmatic.Core.Model;

/// <summary>Exact RF frequency in Hz. Serialized to YAML as MHz string ("145.350000").</summary>
public readonly record struct Frequency(ulong Hz) : IComparable<Frequency>
{
    public static Frequency FromMHz(decimal mhz) => new((ulong)decimal.Round(mhz * 1_000_000m));
    public decimal MHz => Hz / 1_000_000m;

    public int CompareTo(Frequency other) => Hz.CompareTo(other.Hz);
    public override string ToString() => MHz.ToString("0.000000", CultureInfo.InvariantCulture);

    public static Frequency Parse(string s) =>
        FromMHz(decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture));

    public static Frequency operator +(Frequency a, long offsetHz) => new((ulong)((long)a.Hz + offsetHz));
}
