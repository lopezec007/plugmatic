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

    private static BuildResult BuildWith(bool gmrsTx, uint dmrId = 3217632,
        ChannelNameStyle style = ChannelNameStyle.Callsign)
    {
        var builder = new CodeplugBuilder(Caps, new GeneralSettings { RadioId = dmrId, Callsign = "TEST" });
        var profile = BuildProfile.ColoradoDefault();
        profile.NameStyle = style;
        return builder.Build(SampleRepeaters(), profile,
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
    public void Noaa_zone_present_rx_only_and_wideband()
    {
        var plug = BuildWith(gmrsTx: true).Codeplug;   // even with GMRS ack, NOAA stays inhibited (I4)
        var zone = plug.Zones.Single(z => z.Name == "NOAA WX");
        Assert.Equal(7, zone.ChannelNames.Count);
        foreach (var name in zone.ChannelNames)
        {
            var ch = (AnalogChannel)plug.FindChannel(name)!;
            Assert.Equal(TxPermit.Inhibited, ch.TxPermit);
            Assert.True(ch.WideBandwidth);             // NOAA is wide-FM (user-verified on air)
        }
    }

    [Fact]
    public void Every_dmr_repeater_gets_a_parrot_channel()
    {
        var plug = BuildWith(gmrsTx: false).Codeplug;
        var parrot = plug.Channels.OfType<DigitalChannel>()
            .Where(c => c.TxContactName == "Parrot").ToList();
        Assert.Single(parrot);                          // one DMR repeater in the sample
        Assert.Equal(TimeSlot.TS2, parrot[0].TimeSlot);
        Assert.Equal(CallType.Private, plug.FindContact("Parrot")!.Type);
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
    public void Frequency_style_uses_khz_call_template()
    {
        var plug = BuildWith(gmrsTx: false, style: ChannelNameStyle.Frequency).Codeplug;
        Assert.NotNull(plug.FindChannel("145115 W0UPS"));
        Assert.NotNull(plug.FindChannel("775 Colorado"));
    }

    [Fact]
    public void Callsign_style_is_default_drops_khz_and_tg()
    {
        var plug = BuildWith(gmrsTx: false).Codeplug;
        Assert.NotNull(plug.FindChannel("W0UPS"));                 // analog: bare callsign
        Assert.NotNull(plug.FindChannel("N0DMR Colorado"));        // digital: call + talkgroup
        Assert.NotNull(plug.FindChannel("N0DMR 310815"));          // pseudo-TG: "TG" stripped
        Assert.Null(plug.FindChannel("N0DMR TG310815"));
        Assert.Equal(ChannelNameStyle.Callsign, new BuildProfile().NameStyle);
    }

    [Fact]
    public void Callsign_style_collisions_disambiguate_with_khz()
    {
        var repeaters = SampleRepeaters();
        repeaters.Add(new Repeater
        {
            Callsign = "W0UPS", Output = Frequency.FromMHz(447.700m), Input = Frequency.FromMHz(442.700m),
            Mode = RepeaterMode.Fm, City = "Fort Collins", Lat = 40.57, Lon = -105.09, DistanceKm = 6,
        });
        var builder = new CodeplugBuilder(Caps, new GeneralSettings { RadioId = 1, Callsign = "T" });
        var plug = builder.Build(repeaters, BuildProfile.ColoradoDefault(), new GmrsPolicy(false, null)).Codeplug;
        Assert.NotNull(plug.FindChannel("W0UPS"));
        Assert.NotNull(plug.FindChannel("W0UPS 700"));
        Assert.Empty(CodeplugValidator.Validate(plug, Caps));
    }

    [Fact]
    public void Scan_lists_generated_per_zone_and_referenced()
    {
        var plug = BuildWith(gmrsTx: false).Codeplug;
        Assert.Equal(plug.Zones.Count, plug.ScanLists.Count);
        foreach (var sl in plug.ScanLists)
        {
            Assert.True(sl.Name.Length <= 11, sl.Name);
            Assert.Equal(ScanList.CurrentChannelMarker, sl.ChannelNames[0]);
            Assert.True(sl.ChannelNames.Count <= Caps.MaxChannelsPerScanList);
        }
        // Every channel that lives in a zone points at that zone's scan list.
        var zone = plug.Zones.First(z => z.Name == "NOAA WX");
        var list = plug.ScanLists.Single(s => s.Name == "NOAA WX");
        foreach (var chName in zone.ChannelNames)
            Assert.Equal(list.Name, plug.FindChannel(chName)!.ScanListName);
        Assert.Empty(CodeplugValidator.Validate(plug, Caps));
    }

    [Fact]
    public void Blank_callsign_defaults_to_the_dmr_id_digits()
    {
        var builder = new CodeplugBuilder(Caps, new GeneralSettings { RadioId = 3217632, Callsign = "" });
        var plug = builder.Build(SampleRepeaters(), BuildProfile.ColoradoDefault(),
            new GmrsPolicy(false, null)).Codeplug;
        Assert.Equal("3217632", plug.Settings.Callsign);
    }

    [Fact]
    public void Without_dmr_id_every_dmr_channel_is_rx_only()
    {
        var result = BuildWith(gmrsTx: false, dmrId: 0);
        var dmr = result.Codeplug.Channels.OfType<DigitalChannel>().ToList();
        Assert.NotEmpty(dmr);
        Assert.All(dmr, c => Assert.Equal(TxPermit.Inhibited, c.TxPermit));
        Assert.Contains(result.Notes, n => n.Contains("dmr.id"));
        Assert.Empty(CodeplugValidator.Validate(result.Codeplug, Caps));
    }

    [Fact]
    public void Validator_rejects_dmr_tx_without_radio_id()
    {
        var plug = BuildWith(gmrsTx: false).Codeplug;
        plug.Settings.RadioId = 0;   // TX-enabled DMR channels but no ID
        var errors = CodeplugValidator.Validate(plug, Caps);
        Assert.Contains(errors, e => e.Contains("dmr.id"));
    }

    [Fact]
    public void Encode_without_radio_id_leaves_radio_id_block_untouched()
    {
        // The ladder-step-6 regression: a plug without an ID must never zero the radio's own.
        var codec = Dm32uvCodec.Instance;
        var baseImage = codec.Encode(CodecRoundTripTests.SampleIr());   // has ID 3121234 'W0XYZ'
        var idBlockBefore = baseImage.AsSpan(0x67000, 0x1000).ToArray();

        var newPlug = BuildWith(gmrsTx: false, dmrId: 0).Codeplug;
        var written = codec.Encode(newPlug, baseImage);
        Assert.Equal(idBlockBefore, written.AsSpan(0x67000, 0x1000).ToArray());
        Assert.Equal(3121234u, codec.Decode(written).Settings.RadioId);
    }
}
