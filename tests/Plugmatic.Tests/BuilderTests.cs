using Plugmatic.Core.Build;
using Plugmatic.Core.Model;
using Plugmatic.Core.Validation;
using Plugmatic.Radios.Dm32uv.Format;

namespace Plugmatic.Tests;

public class BuilderTests
{
    private static readonly RadioCapabilities Caps = Dm32uvCodec.Instance.Capabilities;

    private static List<Repeater> SampleRepeaters() =>
    [
        new Repeater
        {
            Callsign = "W0UPS", Output = Frequency.FromMHz(145.115m), Input = Frequency.FromMHz(144.515m),
            Mode = RepeaterMode.Fm, UplinkTone = SelectiveCall.Ctcss(100.0m),
            City = "Fort Collins", Lat = 40.57, Lon = -105.09, DistanceKm = 5,
        },
        new Repeater
        {
            Callsign = "N0DMR", Output = Frequency.FromMHz(448.775m), Input = Frequency.FromMHz(443.775m),
            Mode = RepeaterMode.Dmr, ColorCode = 2, DmrId = 310999,
            City = "Loveland", Lat = 40.4, Lon = -105.07, DistanceKm = 15,
            StaticTalkgroups = { (310815, 2) },
        },
        new Repeater
        {
            Callsign = "WQAB123", Output = Frequency.FromMHz(462.600m), Input = Frequency.FromMHz(462.600m),
            Mode = RepeaterMode.Fm, Service = RepeaterService.Gmrs,
            UplinkTone = SelectiveCall.Ctcss(141.3m),
            City = "Fort Collins", Lat = 40.58, Lon = -105.08, DistanceKm = 6,
        },
    ];

    private static BuildResult BuildWith(bool gmrsTx)
    {
        var builder = new CodeplugBuilder(Caps);
        return builder.Build(SampleRepeaters(), BuildProfile.ColoradoDefault(),
            new GmrsPolicy(gmrsTx, gmrsTx ? "2026-08-01T00:00:00Z" : null));
    }

    [Fact]
    public void Generated_plug_validates_clean()
    {
        var result = BuildWith(gmrsTx: false);
        var errors = CodeplugValidator.Validate(result.Codeplug, Caps);
        Assert.Empty(errors);
    }

    [Fact]
    public void Noaa_zone_present_and_rx_only()
    {
        var plug = BuildWith(gmrsTx: true).Codeplug;   // even with GMRS ack, NOAA stays inhibited (I4)
        var zone = plug.Zones.Single(z => z.Name == "NOAA WX");
        Assert.Equal(7, zone.ChannelNames.Count);
        foreach (var name in zone.ChannelNames)
            Assert.Equal(TxPermit.Inhibited, plug.FindChannel(name)!.TxPermit);
    }

    [Fact]
    public void Gmrs_rx_only_without_acknowledgment()
    {
        var plug = BuildWith(gmrsTx: false).Codeplug;
        foreach (var ch in plug.Channels.Where(c => c.Name.StartsWith("GMRS")))
            Assert.Equal(TxPermit.Inhibited, ch.TxPermit);
    }

    [Fact]
    public void Gmrs_class_rules_with_acknowledgment()
    {
        var plug = BuildWith(gmrsTx: true).Codeplug;

        // 462 interstitials (1-7): TX allowed, Medium power (5 W class), never High. (I5)
        var g1 = (AnalogChannel)plug.FindChannel("GMRS 1")!;
        Assert.Equal(TxPermit.Allowed, g1.TxPermit);
        Assert.Equal(PowerLevel.Medium, g1.Power);

        // 467 interstitials (8-14): always RX-only (D9), narrow.
        var g8 = (AnalogChannel)plug.FindChannel("GMRS 8")!;
        Assert.Equal(TxPermit.Inhibited, g8.TxPermit);
        Assert.False(g8.WideBandwidth);

        // Main channels (15-22): TX allowed at High.
        var g15 = (AnalogChannel)plug.FindChannel("GMRS 15")!;
        Assert.Equal(TxPermit.Allowed, g15.TxPermit);
        Assert.Equal(PowerLevel.High, g15.Power);

        // Repeater: +5 MHz input, tone carried over.
        var rpt = (AnalogChannel)plug.Channels.Single(c => c.Name.Contains("WQAB123"));
        Assert.Equal(Frequency.FromMHz(467.600m), rpt.TxFrequency);
        Assert.Equal(SelectiveCall.Ctcss(141.3m), rpt.TxTone);

        var errors = CodeplugValidator.Validate(plug, Caps);
        Assert.Empty(errors);
    }

    [Fact]
    public void Dmr_channels_fan_out_over_talkgroups_with_slots()
    {
        var plug = BuildWith(gmrsTx: false).Codeplug;
        var dmr = plug.Channels.OfType<DigitalChannel>().ToList();
        Assert.NotEmpty(dmr);
        // BrandMeister static TG 310815 slot 2 comes first and wins its slot.
        var stat = dmr.Single(c => c.TxContactName == "TG310815");
        Assert.Equal(TimeSlot.TS2, stat.TimeSlot);
        Assert.Equal(2, stat.ColorCode);
        // Profile talkgroup Colorado on TS1.
        var co = dmr.Single(c => c.TxContactName == "Colorado");
        Assert.Equal(TimeSlot.TS1, co.TimeSlot);
        // All reference the RX group list.
        Assert.All(dmr, c => Assert.Equal("RX All", c.RxGroupListName));
    }

    [Fact]
    public void Built_plug_encodes_and_round_trips_through_codec()
    {
        var plug = BuildWith(gmrsTx: true).Codeplug;
        var codec = Dm32uvCodec.Instance;
        var image = codec.Encode(plug);
        var decoded = codec.Decode(image);
        Assert.Equal(plug.Channels.Count, decoded.Channels.Count);
        Assert.Equal(plug.Zones.Count, decoded.Zones.Count);
        var cmp = codec.Compare(image, codec.Encode(decoded));
        Assert.True(cmp.Equal, string.Join("\n", cmp.Differences));
    }

    [Fact]
    public void Analog_names_use_khz_call_template()
    {
        var plug = BuildWith(gmrsTx: false).Codeplug;
        Assert.NotNull(plug.FindChannel("145115 W0UPS"));
    }
}
