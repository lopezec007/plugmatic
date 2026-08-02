using Plugmatic.Core.Model;
using Plugmatic.Radios.Dm32uv.Format;

namespace Plugmatic.Tests;

/// <summary>Byte-exact vectors from docs/formats/dm32uv-format.md §11 (cross-validated examples).</summary>
public class PrimitiveTests
{
    [Fact]
    public void Bcd_encodes_145_350_as_documented()
    {
        // 145.350 MHz -> word 0x14535000 stored LE as 00 50 53 14 [format §11.1]
        uint word = Bcd.EncodeFrequency(Frequency.FromMHz(145.350m));
        var bytes = BitConverter.GetBytes(word);   // little-endian on all supported platforms
        Assert.Equal(new byte[] { 0x00, 0x50, 0x53, 0x14 }, bytes);
    }

    [Theory]
    [InlineData(145.350)]
    [InlineData(446.00625)]
    [InlineData(162.550)]
    [InlineData(462.7125)]
    public void Bcd_round_trips(decimal mhz)
    {
        var f = Frequency.FromMHz(mhz);
        Assert.Equal(f, Bcd.DecodeFrequency(Bcd.EncodeFrequency(f)));
    }

    [Fact]
    public void Ctcss_127_3_encodes_as_documented()
    {
        // 127.3 Hz -> word 0x1273 -> bytes 73 12 [format §11.2]
        ushort word = ToneCodec.Encode(SelectiveCall.Ctcss(127.3m));
        Assert.Equal(0x1273, word);
    }

    [Fact]
    public void Dcs_023_normal_encodes_as_documented()
    {
        // D023N -> 0x8023 -> bytes 23 80 [format §11.2]
        ushort word = ToneCodec.Encode(SelectiveCall.Parse("D023N"));
        Assert.Equal(0x8023, word);
    }

    [Fact]
    public void Dcs_inverted_uses_type_3()
    {
        Assert.Equal(0xC023, ToneCodec.Encode(SelectiveCall.Parse("D023I")));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("67.0")]
    [InlineData("127.3")]
    [InlineData("254.1")]
    [InlineData("D023N")]
    [InlineData("D754I")]
    public void Tones_round_trip(string text)
    {
        var tone = SelectiveCall.Parse(text);
        Assert.Equal(tone, ToneCodec.Decode(ToneCodec.Encode(tone)));
        Assert.Equal(text, tone.ToString());
    }

    [Fact]
    public void Ascii_decoder_stops_at_ff_padding()
    {
        // CPS-written radios pad with 0xFF after the terminator [format §12 C2]
        byte[] field = [(byte)'A', (byte)'B', 0x00, 0xFF, 0xFF, 0xFF];
        Assert.Equal("AB", AsciiField.Read(field));
        byte[] noTerm = [(byte)'A', (byte)'B', 0xFF, 0xFF, 0xFF, 0xFF];
        Assert.Equal("AB", AsciiField.Read(noTerm));
    }

    [Fact]
    public void Frequency_yaml_format_is_mhz()
    {
        Assert.Equal("145.350000", Frequency.FromMHz(145.35m).ToString());
        Assert.Equal(Frequency.FromMHz(145.35m), Frequency.Parse("145.35"));
    }
}
