using Plugmatic.Core.Model;
using Plugmatic.Radios.D878uv.Format;

namespace Plugmatic.Tests;

public class D878CodecTests
{
    /// <summary>The real radio image, when present, is the strongest available fixture.</summary>
    private static byte[]? FactoryImage()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                               "Plugmatic", "radios", "d878uv");
        if (!Directory.Exists(dir)) return null;
        var image = Directory.EnumerateFiles(dir, "*.bin", SearchOption.AllDirectories)
            .Where(f => new FileInfo(f).Length == Layout.ImageSize)
            .OrderBy(f => f).FirstOrDefault();
        return image is null ? null : File.ReadAllBytes(image);
    }

    /// <summary>
    /// Encoding is idempotent on a real image: whatever the first pass settles, a second
    /// pass leaves alone. Stated this way rather than as strict byte-exactness because the
    /// encoder deliberately repairs out-of-range zone selections — a value that stops the
    /// radio resolving a channel at all — and this radio's own factory image contains one.
    /// Any change beyond that region still fails the test below. [format §4]
    /// </summary>
    [Fact]
    public void Round_trip_of_the_real_radio_image_is_idempotent()
    {
        if (FactoryImage() is not { } image) return;    // skipped where no radio image is archived
        var codec = D878uvCodec.Instance;
        var once = codec.Encode(codec.Decode(image), image);
        var twice = codec.Encode(codec.Decode(once), once);
        var cmp = codec.Compare(once, twice);
        Assert.True(cmp.Equal, "round trip is not idempotent:\n  " + string.Join("\n  ", cmp.Differences.Take(20)));
    }

    /// <summary>The first pass may only touch the zone-selection table, and nothing else.</summary>
    [Fact]
    public void Round_trip_of_the_real_radio_image_changes_nothing_outside_zone_selections()
    {
        if (FactoryImage() is not { } image) return;
        var codec = D878uvCodec.Instance;
        var once = codec.Encode(codec.Decode(image), image);

        int from = Layout.OffsetOf(Layout.ZoneCurrentChannel);
        int to = from + 0x400;
        for (int i = 0; i < image.Length; i++)
            if (i < from || i >= to)
                Assert.True(image[i] == once[i],
                    $"round trip changed 0x{Layout.AddressOf(i):X8}, outside the zone-selection table");
    }

    [Fact]
    public void Real_image_decodes_to_the_counts_its_bitmaps_declare()
    {
        if (FactoryImage() is not { } image) return;
        var ir = D878uvCodec.Instance.Decode(image);
        int BitCount(uint bitmap, int max, bool inverted) =>
            Enumerable.Range(0, max).Count(i => Layout.BitmapHas(image, bitmap, i) != inverted);

        Assert.Equal(BitCount(Layout.ChannelBitmap, Layout.MaxChannels, false), ir.Channels.Count);
        Assert.Equal(BitCount(Layout.ZoneBitmap, Layout.MaxZones, false), ir.Zones.Count);
        Assert.Equal(BitCount(Layout.ScanListBitmap, Layout.MaxScanLists, false), ir.ScanLists.Count);
        Assert.Equal(BitCount(Layout.GroupListBitmap, Layout.MaxGroupLists, false), ir.RxGroupLists.Count);
        Assert.Equal(BitCount(Layout.ContactBitmap, Layout.MaxContacts, true), ir.Contacts.Count);
    }

    [Fact]
    public void Encode_requires_a_base_image()
    {
        var ex = Assert.Throws<D878FormatException>(() => D878uvCodec.Instance.Encode(new Codeplug()));
        Assert.Contains("base image", ex.Message);
    }

    [Fact]
    public void Bcd_frequency_and_id_round_trip()
    {
        Span<byte> buf = stackalloc byte[4];
        foreach (var mhz in new[] { 446.325m, 145.115m, 462.5625m, 439.800m })
        {
            var f = Frequency.FromMHz(mhz);
            D878uvCodec.EncodeBcdFrequency(buf, f);
            Assert.Equal(f, D878uvCodec.DecodeBcdFrequency(buf));
        }
        // The documented hardware sample: 446.325 MHz is stored as 44 63 25 00.
        D878uvCodec.EncodeBcdFrequency(buf, Frequency.FromMHz(446.325m));
        Assert.Equal("44632500", Convert.ToHexString(buf));

        foreach (uint id in new uint[] { 3217632, 3108, 310831, 1 })
        {
            D878uvCodec.EncodeBcdId(buf, id);
            Assert.Equal(id, D878uvCodec.DecodeBcdId(buf));
        }
    }

    [Fact]
    public void Editing_one_field_rewrites_exactly_one_byte()
    {
        if (FactoryImage() is not { } factory) return;
        var codec = D878uvCodec.Instance;
        // Settle any zone-selection repair first, so this measures the edit and nothing else.
        var image = codec.Encode(codec.Decode(factory), factory);
        var ir = codec.Decode(image);
        var target = ir.Channels.OfType<DigitalChannel>().First();
        int slot = ir.Channels.IndexOf(target);
        target.ColorCode = target.ColorCode == 1 ? 2 : 1;

        var written = codec.Encode(ir, image);
        var cmp = codec.Compare(image, written);
        Assert.Single(cmp.Differences);                       // the colour-code byte, nothing else
        Assert.Equal(target.ColorCode, ((DigitalChannel)codec.Decode(written).Channels[slot]).ColorCode);
    }

    [Fact]
    public void Renaming_a_channel_also_moves_its_list_memberships()
    {
        // The IR references channels by name, so a rename must be applied to the
        // referencing lists too; otherwise the member is silently dropped on encode.
        if (FactoryImage() is not { } image) return;
        var codec = D878uvCodec.Instance;
        var ir = codec.Decode(image);
        var old = ir.Channels[0].Name;
        ir.Channels[0].Name = "RENAMED";
        foreach (var list in ir.ScanLists)
            for (int i = 0; i < list.ChannelNames.Count; i++)
                if (list.ChannelNames[i] == old) list.ChannelNames[i] = "RENAMED";
        foreach (var zone in ir.Zones)
            for (int i = 0; i < zone.ChannelNames.Count; i++)
                if (zone.ChannelNames[i] == old) zone.ChannelNames[i] = "RENAMED";

        var back = codec.Decode(codec.Encode(ir, image));
        Assert.Equal("RENAMED", back.Channels[0].Name);
        Assert.Equal(ir.ScanLists.Select(s => s.ChannelNames.Count),
                     back.ScanLists.Select(s => s.ChannelNames.Count));   // no member lost
    }
}
