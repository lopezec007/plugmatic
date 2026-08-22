using Plugmatic.Core.Model;
using Plugmatic.Radios.D878uv.Format;

namespace Plugmatic.Tests;

/// <summary>
/// Analog CTCSS/DCS round-trips. The encoder did not exist until 2026-08-21 — every analog
/// channel was written tone-less — which went unnoticed while the only FM channels in a
/// generated plug were tone-less GMRS and NOAA. The I3 gate caught it on the first plug
/// built with real analog repeaters. [format §3]
/// </summary>
public class D878ToneTests
{
    private static readonly Plugmatic.Radios.IRadioCodec Codec = D878uvCodec.Instance;

    private static byte[] BlankBase()
    {
        var image = new byte[Layout.ImageSize];
        Array.Fill(image, (byte)0xFF);
        foreach (var name in new[] { "channelBitmap", "zoneBitmap", "scanListBitmap",
                                     "radioIdBitmap", "hiddenZoneBitmap", "groupListBitmap" })
        {
            var region = Layout.Regions.First(r => r.Name == name);
            image.AsSpan(Layout.OffsetOf(region.Address), region.Length).Clear();
        }
        return image;
    }

    private static Codeplug OneChannel(SelectiveCall tx, SelectiveCall rx) => new()
    {
        Channels =
        {
            new AnalogChannel
            {
                Name = "TONE",
                RxFrequency = Frequency.FromMHz(146.94m),
                TxFrequency = Frequency.FromMHz(146.34m),
                TxTone = tx,
                RxTone = rx,
            },
        },
    };

    /// <summary>Every tone the radio's table holds must survive a write and a read.</summary>
    [Fact]
    public void Every_standard_CTCSS_tone_round_trips()
    {
        decimal[] tones =
        [
            62.5m, 67.0m, 69.3m, 71.9m, 74.4m, 77.0m, 79.7m, 82.5m, 85.4m, 88.5m, 91.5m, 94.8m,
            97.4m, 100.0m, 103.5m, 107.2m, 110.9m, 114.8m, 118.8m, 123.0m, 127.3m, 131.8m,
            136.5m, 141.3m, 146.2m, 151.4m, 156.7m, 159.8m, 162.2m, 165.5m, 167.9m, 171.3m,
            173.8m, 177.3m, 179.9m, 183.5m, 186.2m, 189.9m, 192.8m, 196.6m, 199.5m, 203.5m,
            206.5m, 210.7m, 218.1m, 225.7m, 229.1m, 233.6m, 241.8m, 250.3m, 254.1m,
        ];
        foreach (var hz in tones)
        {
            var ir = OneChannel(SelectiveCall.Ctcss(hz), SelectiveCall.Ctcss(hz));
            var back = (AnalogChannel)Codec.Decode(Codec.Encode(ir, BlankBase())).Channels[0];
            Assert.Equal(SelectiveCall.Ctcss(hz), back.TxTone);
            Assert.Equal(SelectiveCall.Ctcss(hz), back.RxTone);
        }
    }

    /// <summary>
    /// The exact mapping the radio showed us. Reading the table as the standard 50-tone set
    /// (index 12 = 100.0) put every analog channel one tone low on hardware. [format §3]
    /// </summary>
    [Theory]
    [InlineData(12, "97.4")]
    [InlineData(13, "100.0")]
    [InlineData(0, "62.5")]
    public void The_ctcss_index_table_matches_what_the_radio_displays(int index, string expected)
    {
        var ir = OneChannel(SelectiveCall.Parse(expected), SelectiveCall.None);
        var image = Codec.Encode(ir, BlankBase());
        var rec = image.AsSpan(Layout.ChannelSlot(0).Offset, Layout.ChannelRecordSize);
        Assert.Equal(index, rec[0x0A]);                       // TX CTCSS index byte
        var back = (AnalogChannel)Codec.Decode(image).Channels[0];
        Assert.Equal(expected, back.TxTone.ToString());
    }

    [Theory]
    [InlineData("D023N")]
    [InlineData("D023I")]
    [InlineData("D754N")]
    [InlineData("D754I")]
    public void DCS_codes_round_trip_in_both_polarities(string code)
    {
        var call = SelectiveCall.Parse(code);
        var ir = OneChannel(call, call);
        var back = (AnalogChannel)Codec.Decode(Codec.Encode(ir, BlankBase())).Channels[0];
        Assert.Equal(call, back.TxTone);
        Assert.Equal(call, back.RxTone);
        Assert.Equal(code, back.TxTone.ToString());
    }

    [Fact]
    public void Tx_and_rx_tones_are_independent()
    {
        var ir = OneChannel(SelectiveCall.Ctcss(100.0m), SelectiveCall.Parse("D023I"));
        var back = (AnalogChannel)Codec.Decode(Codec.Encode(ir, BlankBase())).Channels[0];
        Assert.Equal(SelectiveCall.Ctcss(100.0m), back.TxTone);
        Assert.Equal(SelectiveCall.Parse("D023I"), back.RxTone);
    }

    [Fact]
    public void A_tone_less_channel_stays_tone_less_and_keeps_tx_inhibit()
    {
        var ir = OneChannel(SelectiveCall.None, SelectiveCall.None);
        ir.Channels[0].TxPermit = TxPermit.Inhibited;          // shares byte 0x09 with the mode bits
        var back = (AnalogChannel)Codec.Decode(Codec.Encode(ir, BlankBase())).Channels[0];
        Assert.Equal(SelectiveCall.None, back.TxTone);
        Assert.Equal(SelectiveCall.None, back.RxTone);
        Assert.Equal(TxPermit.Inhibited, back.TxPermit);
    }

    [Fact]
    public void A_tone_outside_the_radios_table_is_refused_rather_than_silently_dropped()
    {
        var ir = OneChannel(SelectiveCall.Ctcss(123.4m), SelectiveCall.None);
        Assert.Throws<D878FormatException>(() => Codec.Encode(ir, BlankBase()));
    }
}
