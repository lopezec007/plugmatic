using Plugmatic.Core.Model;
using Plugmatic.Radios.Dm32uv.Format;

namespace Plugmatic.Tests;

public class CodecRoundTripTests
{
    public static Codeplug SampleIr()
    {
        var plug = new Codeplug
        {
            Settings = new GeneralSettings { RadioId = 3121234, Callsign = "W0XYZ" },
            Contacts =
            [
                new Contact { Name = "Colorado", Type = CallType.Group, DmrId = 3108 },
                new Contact { Name = "TAC 310", Type = CallType.Group, DmrId = 310 },
                new Contact { Name = "Parrot", Type = CallType.Private, DmrId = 9990 },
            ],
            RxGroupLists =
            [
                new RxGroupList { Name = "CO RX", ContactNames = ["Colorado", "TAC 310"] },
            ],
            ScanLists =
            [
                new ScanList { Name = "Local", ChannelNames = ["W0ABC VHF", "DMR CO 1"] },
            ],
        };
        plug.Channels.Add(new AnalogChannel
        {
            Name = "W0ABC VHF",
            RxFrequency = Frequency.FromMHz(145.115m),
            TxFrequency = Frequency.FromMHz(144.515m),
            Power = PowerLevel.High,
            SquelchLevel = 3,
            WideBandwidth = false,
            RxTone = SelectiveCall.None,
            TxTone = SelectiveCall.Ctcss(107.2m),
            Admit = AdmitCriterion.ChannelFree,
            ScanListName = "Local",
        });
        plug.Channels.Add(new DigitalChannel
        {
            Name = "DMR CO 1",
            RxFrequency = Frequency.FromMHz(448.6250m),
            TxFrequency = Frequency.FromMHz(443.6250m),
            Power = PowerLevel.High,
            ColorCode = 1,
            TimeSlot = TimeSlot.TS1,
            TxContactName = "Colorado",
            RxGroupListName = "CO RX",
            ScanListName = "Local",
        });
        plug.Channels.Add(new AnalogChannel
        {
            Name = "NOAA WX1",
            RxFrequency = Frequency.FromMHz(162.550m),
            TxFrequency = Frequency.FromMHz(162.550m),
            TxPermit = TxPermit.Inhibited,
            Power = PowerLevel.Low,
            WideBandwidth = false,
        });
        plug.Zones.Add(new Zone { Name = "Fort Collins", ChannelNames = ["W0ABC VHF", "DMR CO 1"] });
        plug.Zones.Add(new Zone { Name = "NOAA WX", ChannelNames = ["NOAA WX1"] });
        return plug;
    }

    [Fact]
    public void Encode_then_decode_preserves_structure()
    {
        var ir = SampleIr();
        var codec = Dm32uvCodec.Instance;
        var image = codec.Encode(ir);
        var decoded = codec.Decode(image);

        Assert.Equal(ir.Channels.Count, decoded.Channels.Count);
        for (int i = 0; i < ir.Channels.Count; i++)
        {
            Assert.Equal(ir.Channels[i].Name, decoded.Channels[i].Name);
            Assert.Equal(ir.Channels[i].RxFrequency, decoded.Channels[i].RxFrequency);
            Assert.Equal(ir.Channels[i].TxFrequency, decoded.Channels[i].TxFrequency);
            Assert.Equal(ir.Channels[i].TxPermit, decoded.Channels[i].TxPermit);
            Assert.Equal(ir.Channels[i].Power, decoded.Channels[i].Power);
            Assert.Equal(ir.Channels[i].SquelchLevel, decoded.Channels[i].SquelchLevel);
            Assert.Equal(ir.Channels[i].ScanListName, decoded.Channels[i].ScanListName);
            Assert.Equal(ir.Channels[i].GetType(), decoded.Channels[i].GetType());
        }
        var d0 = Assert.IsType<DigitalChannel>(decoded.Channels[1]);
        Assert.Equal("Colorado", d0.TxContactName);
        Assert.Equal("CO RX", d0.RxGroupListName);
        Assert.Equal(1, d0.ColorCode);
        Assert.Equal(TimeSlot.TS1, d0.TimeSlot);

        var a0 = Assert.IsType<AnalogChannel>(decoded.Channels[0]);
        Assert.Equal(SelectiveCall.Ctcss(107.2m), a0.TxTone);
        Assert.False(a0.WideBandwidth);
        Assert.Equal(AdmitCriterion.ChannelFree, a0.Admit);

        Assert.Equal(ir.Zones.Select(z => z.Name), decoded.Zones.Select(z => z.Name));
        Assert.Equal(ir.Zones[0].ChannelNames, decoded.Zones[0].ChannelNames);
        Assert.Equal(ir.Contacts.Select(c => (c.Name, c.Type, c.DmrId)),
                     decoded.Contacts.Select(c => (c.Name, c.Type, c.DmrId)));
        Assert.Equal(ir.RxGroupLists[0].ContactNames, decoded.RxGroupLists[0].ContactNames);
        Assert.Equal(ir.ScanLists[0].ChannelNames, decoded.ScanLists[0].ChannelNames);
        Assert.Equal(ir.Settings.RadioId, decoded.Settings.RadioId);
        Assert.Equal(ir.Settings.Callsign, decoded.Settings.Callsign);
    }

    [Fact]
    public void Reencode_of_decoded_image_is_compare_equal()
    {
        // Encode(Decode(img)) must be Compare-equal to img. [spec §6.4 round-trip property]
        var codec = Dm32uvCodec.Instance;
        var original = codec.Encode(SampleIr());
        var reEncoded = codec.Encode(codec.Decode(original));
        var cmp = codec.Compare(original, reEncoded);
        Assert.True(cmp.Equal, string.Join("\n", cmp.Differences));
    }

    [Fact]
    public void Reencode_survives_foreign_padding_and_unknown_bits()
    {
        // Simulate a CPS-written radio: 0xFF name padding + junk in unmodeled bits.
        var codec = Dm32uvCodec.Instance;
        var image = codec.Encode(SampleIr());
        var img = new Dm32Image((byte[])image.Clone());
        var ch0 = img.Block(Layout.FirstChannelBlock).Slice(Layout.ChannelBank0Header, Layout.ChannelRecordSize);
        // Re-pad the name with 0xFF after the terminator (C2 style).
        for (int i = "W0ABC VHF".Length + 1; i < 0x10; i++) ch0[i] = 0xFF;
        ch0[0x2A] = 0x5A;                     // unknown byte [format §4]
        ch0[0x25] = 0x10;                     // VOX-ish unknown bits
        var mutated = img.Bytes;

        var reEncoded = codec.Encode(codec.Decode(mutated));
        var cmp = codec.Compare(mutated, reEncoded);
        Assert.True(cmp.Equal, string.Join("\n", cmp.Differences));
    }

    [Fact]
    public void Yaml_round_trips_ir()
    {
        var ir = SampleIr();
        var yaml = IrYaml.Serialize(ir);
        var back = IrYaml.Deserialize(yaml);
        Assert.Equal(ir.Channels.Count, back.Channels.Count);
        Assert.Equal(ir.Channels.Select(c => c.Name), back.Channels.Select(c => c.Name));
        Assert.IsType<DigitalChannel>(back.Channels[1]);
        Assert.Equal("Colorado", ((DigitalChannel)back.Channels[1]).TxContactName);
        Assert.Equal(SelectiveCall.Ctcss(107.2m), ((AnalogChannel)back.Channels[0]).TxTone);
        Assert.Equal(ir.Settings.RadioId, back.Settings.RadioId);
        Assert.Equal(TxPermit.Inhibited, back.Channels[2].TxPermit);
    }

    [Fact]
    public void Encode_stamps_metadata_bytes()
    {
        var image = Dm32uvCodec.Instance.Encode(SampleIr());
        var img = new Dm32Image(image);
        Assert.True(img.BlockPresent(Layout.FirstChannelBlock));
        Assert.True(img.BlockPresent(Layout.FirstZoneBlock));
        Assert.True(img.BlockPresent(Layout.ContactIndexBlock));
        Assert.True(img.BlockPresent(Layout.RadioIdBlock));
        Assert.Equal((byte)Layout.FirstChannelBlock,
                     image[Layout.FirstChannelBlock * Dm32Image.BlockSize + Dm32Image.BlockSize - 1]);
    }

    [Fact]
    public void Many_channels_span_multiple_banks()
    {
        var ir = new Codeplug { Settings = new GeneralSettings { RadioId = 1, Callsign = "T" } };
        for (int i = 0; i < 300; i++)
            ir.Channels.Add(new AnalogChannel
            {
                Name = $"CH{i + 1}",
                RxFrequency = Frequency.FromMHz(146.520m),
                TxFrequency = Frequency.FromMHz(146.520m),
            });
        var codec = Dm32uvCodec.Instance;
        var decoded = codec.Decode(codec.Encode(ir));
        Assert.Equal(300, decoded.Channels.Count);
        Assert.Equal("CH300", decoded.Channels[299].Name);
        // 84 in bank 0, 85 in banks 1-2, remainder in bank 3 [format §4]
        var img = new Dm32Image(codec.Encode(ir));
        Assert.True(img.BlockPresent(Layout.FirstChannelBlock + 3));
        Assert.False(img.BlockPresent(Layout.FirstChannelBlock + 4));
    }
}
