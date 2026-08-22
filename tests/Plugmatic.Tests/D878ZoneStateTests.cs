using Plugmatic.Core.Model;
using Plugmatic.Radios.D878uv.Format;

namespace Plugmatic.Tests;

/// <summary>
/// Zone-adjacent state that outlives a codeplug replacement. Both of these locked up a real
/// radio on 2026-08-22: a stale selected-channel position gave "No Valid Chan!" with the
/// menu and zone controls dead, and stale hidden bits made two zones vanish. [format §4]
/// </summary>
public class D878ZoneStateTests
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
        image.AsSpan(Layout.OffsetOf(Layout.ZoneCurrentChannel), 0x400).Clear();
        return image;
    }

    private static Codeplug Plug(int zoneChannels, string zoneName = "Z1")
    {
        var plug = new Codeplug();
        var zone = new Zone { Name = zoneName };
        for (int i = 0; i < zoneChannels; i++)
        {
            var ch = new AnalogChannel
            {
                Name = $"{zoneName}CH{i}",
                RxFrequency = Frequency.FromMHz(146.0m + i * 0.01m),
                TxFrequency = Frequency.FromMHz(146.0m + i * 0.01m),
            };
            plug.Channels.Add(ch);
            zone.ChannelNames.Add(ch.Name);
        }
        plug.Zones.Add(zone);
        return plug;
    }

    private static ushort Selection(byte[] image, int slot, int half) =>
        BitConverter.ToUInt16(image, Layout.OffsetOf(Layout.ZoneCurrentChannel) + half + slot * 2);

    private static void SetSelection(byte[] image, int slot, int half, ushort value) =>
        BitConverter.GetBytes(value)
            .CopyTo(image, Layout.OffsetOf(Layout.ZoneCurrentChannel) + half + slot * 2);

    [Fact]
    public void A_selected_position_past_the_end_of_a_replaced_zone_is_reset()
    {
        // Slot 0 previously held a 40-channel zone sitting on its 31st channel; the new plug
        // gives that slot a one-channel zone. Left alone, the radio cannot resolve a channel.
        var basis = Codec.Encode(Plug(40, "OLD"), BlankBase());
        SetSelection(basis, 0, 0, 30);
        SetSelection(basis, 0, Layout.ZoneCurrentChannelVfoB, 30);

        var image = Codec.Encode(Plug(1, "NEW"), basis);

        Assert.Equal(0, Selection(image, 0, 0));
        Assert.Equal(0, Selection(image, 0, Layout.ZoneCurrentChannelVfoB));
    }

    [Fact]
    public void A_still_valid_position_is_left_alone_so_the_operator_keeps_their_place()
    {
        var basis = Codec.Encode(Plug(40, "OLD"), BlankBase());
        SetSelection(basis, 0, 0, 5);

        var image = Codec.Encode(Plug(20, "NEW"), basis);   // 5 is still in range

        Assert.Equal(5, Selection(image, 0, 0));
    }

    [Fact]
    public void An_out_of_range_selection_is_repaired_even_on_a_restore()
    {
        // Deliberately not preserved. It is not a preference, it is a value that stops the
        // radio resolving a channel — and a slot can carry one while its members are
        // untouched, which is how two zones survived the first version of this fix.
        var basis = Codec.Encode(Plug(4, "SAME"), BlankBase());
        SetSelection(basis, 0, Layout.ZoneCurrentChannelVfoB, 4);   // out of range for 4 members

        var image = Codec.Encode(Codec.Decode(basis), basis);

        Assert.Equal(0, Selection(image, 0, Layout.ZoneCurrentChannelVfoB));
        // …and settling it is a one-time repair, not a moving target.
        Assert.True(Codec.Compare(image, Codec.Encode(Codec.Decode(image), image)).Equal);
    }

    [Fact]
    public void A_generated_zone_is_never_left_hidden_by_the_slot_it_inherited()
    {
        var basis = Codec.Encode(Plug(4, "OLD"), BlankBase());
        SetAllocatedHidden(basis, 0, true);                 // previous occupant was hidden

        var image = Codec.Encode(Plug(4, "NEW"), basis);

        Assert.False(Layout.BitmapHas(image, Layout.HiddenZoneBitmap, 0));
        Assert.False(Codec.Decode(image).Zones[0].Hidden);
    }

    [Fact]
    public void A_hidden_zone_survives_a_round_trip()
    {
        var basis = Codec.Encode(Plug(4, "Z"), BlankBase());
        SetAllocatedHidden(basis, 0, true);

        var decoded = Codec.Decode(basis);
        Assert.True(decoded.Zones[0].Hidden);
        Assert.True(Codec.Decode(Codec.Encode(decoded, basis)).Zones[0].Hidden);
    }

    private static void SetAllocatedHidden(byte[] image, int index, bool hidden)
    {
        int at = Layout.OffsetOf(Layout.HiddenZoneBitmap) + index / 8;
        int mask = 1 << (index % 8);
        image[at] = (byte)(hidden ? image[at] | mask : image[at] & ~mask);
    }
}
