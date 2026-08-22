using Plugmatic.Core.Model;
using Plugmatic.Radios.D878uv.Format;

namespace Plugmatic.Tests;

/// <summary>
/// A channel written into a never-used slot must come out looking like a record the radio
/// itself wrote, not like erased flash with a few fields filled in. Generated channels
/// inherited 0xFF across the whole reserved area and started scanning the moment they were
/// selected. [format §3]
/// </summary>
public class D878FreshRecordTests
{
    private static readonly Plugmatic.Radios.IRadioCodec Codec = D878uvCodec.Instance;

    /// <summary>Erased flash: every slot 0xFF, with the allocation bitmaps cleared.</summary>
    private static byte[] ErasedBase()
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

    private static Codeplug TwoChannels() => new()
    {
        Channels =
        {
            new AnalogChannel
            {
                Name = "GMRS 1", RxFrequency = Frequency.FromMHz(462.5625m),
                TxFrequency = Frequency.FromMHz(462.5625m),
            },
            new DigitalChannel
            {
                Name = "DMR 1", RxFrequency = Frequency.FromMHz(446.9m),
                TxFrequency = Frequency.FromMHz(441.9m), ColorCode = 2, TimeSlot = TimeSlot.TS1,
            },
        },
    };

    private static ReadOnlySpan<byte> Record(byte[] image, int slot) =>
        image.AsSpan(Layout.ChannelSlot(slot).Offset, Layout.ChannelRecordSize);

    [Fact]
    public void A_channel_written_into_erased_flash_keeps_no_0xFF_reserved_bytes()
    {
        var image = Codec.Encode(TwoChannels(), ErasedBase());
        for (int slot = 0; slot < 2; slot++)
        {
            var rec = Record(image, slot);
            for (int at = 0; at < rec.Length; at++)
                Assert.True(rec[at] != 0xFF || at is 0x1B or 0x1C,
                    $"slot {slot} byte 0x{at:X2} left at 0xFF — erased flash, not a channel record");
        }
    }

    [Fact]
    public void The_same_holds_when_a_polluted_record_is_rewritten()
    {
        // The state a previous build left on the radio: a few fields set, the rest erased.
        var basis = Codec.Encode(TwoChannels(), ErasedBase());
        var polluted = (byte[])basis.Clone();
        int at0 = Layout.ChannelSlot(0).Offset;
        foreach (int at in new[] { 0x19, 0x1A, 0x1D, 0x1E, 0x1F, 0x22 }) polluted[at0 + at] = 0xFF;

        var image = Codec.Encode(TwoChannels(), polluted);

        var rec = Record(image, 0);
        foreach (int at in new[] { 0x19, 0x1A, 0x1D, 0x1E, 0x1F, 0x22 })
            Assert.Equal(0x00, rec[at]);
    }

    [Fact]
    public void A_real_record_is_never_mistaken_for_erased_flash()
    {
        // The sentinel rule this rests on, checked against the radio's own codeplug: only the
        // scan-list and group-list indices ever hold 0xFF.
        if (D878CodecTests.FactoryImage() is not { } factory) return;
        // Allocation is sparse — this radio's own plug uses slots 0-51 and 100-131 — so walk
        // the bitmap rather than assuming list position equals slot.
        int checkedSlots = 0;
        for (int slot = 0; slot < Layout.MaxChannels; slot++)
        {
            if (!Layout.BitmapHas(factory, Layout.ChannelBitmap, slot)) continue;
            checkedSlots++;
            var rec = factory.AsSpan(Layout.ChannelSlot(slot).Offset, Layout.ChannelRecordSize);
            Assert.False(D878uvCodec.IsUninitialisedRecord(rec),
                $"factory slot {slot} looks uninitialised — the 0xFF sentinel rule is wrong");
        }
        Assert.True(checkedSlots > 0, "no allocated channels in the fixture image");
    }
}
