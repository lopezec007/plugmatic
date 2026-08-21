using Plugmatic.Core.Model;
using Plugmatic.Radios;
using Plugmatic.Radios.D878uv.Format;
using Plugmatic.Radios.D878uv.Protocol;

namespace Plugmatic.Tests;

/// <summary>
/// The write rules from docs/formats/d878uv-protocol.md §5.1/§5.3, as tests. These exist
/// because getting them wrong does not look like a failure — it looks like a codeplug that
/// was acknowledged and quietly did not change.
/// </summary>
public class D878WriteSemanticsTests
{
    private static D878uvProtocol Fast() => new() { HandshakeRetryDelayMs = 1, HandshakeRetries = 3 };

    private static byte[] Filled(byte value) => Enumerable.Repeat(value, 16).ToArray();

    [Fact]
    public async Task A_write_is_only_applied_when_END_commits_it()
    {
        var radio = new FakeAnytoneRadio();
        var proto = Fast();
        await proto.IdentifyAsync(radio, CancellationToken.None);

        await proto.WriteChunkAsync(radio, Layout.ChannelBanks, Filled(0x5A), CancellationToken.None);
        Assert.True(proto.HasStagedWrites);
        Assert.False(radio.Memory.ContainsKey(Layout.ChannelBanks));   // staged, not applied

        await proto.EndSessionAsync(radio, CancellationToken.None);
        Assert.False(proto.HasStagedWrites);
        Assert.Equal(0x5A, radio.Memory[Layout.ChannelBanks]);
    }

    [Fact]
    public async Task Reading_after_a_write_is_refused_before_the_radio_can_discard_the_codeplug()
    {
        var radio = new FakeAnytoneRadio();
        var proto = Fast();
        await proto.IdentifyAsync(radio, CancellationToken.None);
        await proto.WriteChunkAsync(radio, Layout.ChannelBanks, Filled(0x11), CancellationToken.None);

        var e = await Assert.ThrowsAsync<D878ProtocolException>(() =>
            proto.ReadRegionAsync(radio, Layout.ChannelBanks, 16, null, "read", CancellationToken.None));
        Assert.Contains("discards", e.Message);

        // The guard fired instead of the radio, so the staged write survived to be committed.
        Assert.Equal(0, radio.DiscardedWriteCount);
        await proto.EndSessionAsync(radio, CancellationToken.None);
        Assert.Equal(0x11, radio.Memory[Layout.ChannelBanks]);
    }

    [Fact]
    public async Task Reads_before_the_first_write_are_allowed()
    {
        var radio = new FakeAnytoneRadio();
        var proto = Fast();
        await proto.IdentifyAsync(radio, CancellationToken.None);
        await proto.ReadRegionAsync(radio, Layout.ChannelBanks, 16, null, "read", CancellationToken.None);
        await proto.WriteChunkAsync(radio, Layout.ChannelBanks, Filled(0x22), CancellationToken.None);
        await proto.EndSessionAsync(radio, CancellationToken.None);

        Assert.Equal(0x22, radio.Memory[Layout.ChannelBanks]);
    }

    [Fact]
    public async Task Staging_a_run_of_writes_never_issues_a_read_and_so_never_discards_itself()
    {
        var radio = new FakeAnytoneRadio();
        var proto = Fast();
        await proto.IdentifyAsync(radio, CancellationToken.None);

        for (uint n = 0; n < 0x100; n += 0x10)
            await proto.WriteChunkAsync(radio, Layout.ChannelBanks + n, Filled(0x33), CancellationToken.None);
        await proto.EndSessionAsync(radio, CancellationToken.None);

        Assert.DoesNotContain(radio.Log, entry => entry.StartsWith("R:"));
        Assert.Equal(0, radio.DiscardedWriteCount);
        Assert.Equal(0x33, radio.Memory[Layout.ChannelBanks + 0xF0]);
    }

    // ------------------------------------------------- banks, mirrors and safe writes

    [Fact]
    public void A_bank_is_a_0x40000_window_holding_0x20000_of_real_storage()
    {
        Assert.Equal(0x40000u, Layout.BankStride);
        Assert.Equal(0x20000u, Layout.BankStorageSize);
        Assert.Equal(0x00800000u, Layout.BankOf(0x00800030));
        Assert.Equal(0x00800000u, Layout.BankOf(0x0083FFFF));
        Assert.Equal(0x00840000u, Layout.BankOf(0x00840000));
    }

    [Fact]
    public void The_mirrored_half_of_a_bank_is_never_writable()
    {
        // Writing 0x00820000-0x0083FFF0 is what copied channel bank 0 over bank 1 on
        // hardware. The vendor CPS writes 0 of its 96,352 bytes above the half. [§5.5/§5.8]
        Assert.True(Layout.IsWritable(Layout.ChannelBanks, 16));
        Assert.True(Layout.IsWritable(Layout.ChannelBanks + 0x1FF00, 16));
        Assert.False(Layout.IsWritable(Layout.ChannelBanks + Layout.BankStorageSize, 16));
        Assert.False(Layout.IsWritable(Layout.ChannelBanks + 0x30000, 16));
    }

    [Fact]
    public void The_firmware_flash_signature_is_never_writable()
    {
        // Firmware-managed and restored after an erase; writing it disturbed the radio.
        Assert.True(Layout.IsFlashMarker(Layout.ChannelBanks + 0x1FFF0));
        Assert.False(Layout.IsWritable(Layout.ChannelBanks + 0x1FFF0, 16));
        Assert.True(Layout.IsWritable(Layout.ChannelBanks + 0x1FFE0, 16));
    }

    [Fact]
    public void Writes_are_bounded_to_the_banks_the_codeplug_occupies()
    {
        // Inside a codeplug bank but described by no region: legal, because rewriting the
        // bank is the only way to change anything in it.
        Assert.True(Layout.IsWritable(0x00802000, 16));
        // Outside every codeplug bank: refused. The callsign database lives here.
        Assert.False(Layout.IsWritable(0x04340000, 16));
    }

    [Fact]
    public void Only_banks_that_actually_change_are_named()
    {
        var baseline = new byte[Layout.ImageSize];
        var image = (byte[])baseline.Clone();
        Assert.Empty(D878uvProtocol.TouchedBanks(image, baseline));

        image[Layout.OffsetOf(Layout.ChannelBanks) + 0x30] ^= 0x5A;
        Assert.Equal([0x00800000u], D878uvProtocol.TouchedBanks(image, baseline));

        image[Layout.OffsetOf(Layout.ChannelBanks + Layout.BetweenChannelBanks)] ^= 0x01;
        Assert.Equal([0x00800000u, 0x00840000u], D878uvProtocol.TouchedBanks(image, baseline));
    }

    [Fact]
    public void Splicing_overwrites_modelled_bytes_and_leaves_everything_else_alone()
    {
        var unit = new byte[Layout.BankStorageSize];
        Array.Fill(unit, (byte)0x77);                    // stands in for what the radio holds
        var image = new byte[Layout.ImageSize];
        Array.Fill(image, (byte)0x11);

        D878uvProtocol.SpliceModelledBytes(unit, Layout.ChannelBanks, image);

        var region = Layout.Regions.First(r => r.Address == Layout.ChannelBanks);
        Assert.All(Enumerable.Range(0, region.Length), i => Assert.Equal(0x11, unit[i]));
        Assert.Equal(0x77, unit[region.Length]);         // past the region, radio's byte survives
        Assert.Equal(0x77, unit[^1]);
    }

    [Fact]
    public async Task A_write_preserves_bytes_the_region_table_does_not_model()
    {
        // 0x00802000 shares a bank with channels[0], holds real channel data on hardware, and
        // is described by no region. Changing one modelled byte must bring it back untouched.
        var radio = new FakeAnytoneRadio();
        var proto = Fast();
        await proto.IdentifyAsync(radio, CancellationToken.None);

        var baseline = new byte[Layout.ImageSize];
        radio.LoadImage(baseline);
        const uint unmodelled = 0x00802000;
        radio.Memory[unmodelled] = 0xC3;

        var image = (byte[])baseline.Clone();
        image[Layout.OffsetOf(Layout.ChannelBanks) + 0x30] = 0x5A;

        await proto.WriteImageAsync(radio, image, baseline, null, CancellationToken.None);
        await proto.EndSessionAsync(radio, CancellationToken.None);

        Assert.Equal([0x00800000u], radio.ErasedBlocks);
        Assert.Equal(0x5A, radio.Memory[Layout.ChannelBanks + 0x30]);
        Assert.Equal(0xC3, radio.Memory[unmodelled]);
    }

    [Fact]
    public async Task A_write_never_addresses_the_mirror_or_the_flash_signature()
    {
        var radio = new FakeAnytoneRadio();
        var proto = Fast();
        await proto.IdentifyAsync(radio, CancellationToken.None);

        var baseline = new byte[Layout.ImageSize];
        radio.LoadImage(baseline);
        // Something non-0xFF right up against the end of the storage half.
        radio.Memory[Layout.ChannelBanks + 0x1FFF4] = 0x99;
        var image = (byte[])baseline.Clone();
        image[Layout.OffsetOf(Layout.ChannelBanks) + 0x30] = 0x5A;

        await proto.WriteImageAsync(radio, image, baseline, null, CancellationToken.None);

        foreach (var entry in radio.Log.Where(e => e.StartsWith("W:")))
        {
            uint at = Convert.ToUInt32(entry[2..entry.IndexOf('+')], 16);
            Assert.True(at - Layout.BankOf(at) < Layout.BankStorageSize,
                        $"write at 0x{at:X8} landed in the mirrored half");
            Assert.False(Layout.IsFlashMarker(at), $"write at 0x{at:X8} hit the flash signature");
        }
    }

    [Fact]
    public async Task An_unchanged_image_erases_nothing_and_writes_nothing()
    {
        var radio = new FakeAnytoneRadio();
        var proto = Fast();
        await proto.IdentifyAsync(radio, CancellationToken.None);

        var image = new byte[Layout.ImageSize];
        radio.LoadImage(image);
        await proto.WriteImageAsync(radio, image, image, null, CancellationToken.None);
        await proto.EndSessionAsync(radio, CancellationToken.None);

        Assert.Empty(radio.ErasedBlocks);
        Assert.DoesNotContain(radio.Log, e => e.StartsWith("W:"));
    }

    [Fact]
    public async Task A_write_without_a_baseline_is_refused()
    {
        var radio = new FakeAnytoneRadio();
        var proto = Fast();
        await proto.IdentifyAsync(radio, CancellationToken.None);

        var e = await Assert.ThrowsAsync<D878ProtocolException>(() =>
            proto.WriteImageAsync(radio, new byte[Layout.ImageSize], null, null, CancellationToken.None));
        Assert.Contains("baseline", e.Message);
        Assert.DoesNotContain(radio.Log, entry => entry.StartsWith("W:"));
    }

    [Fact]
    public async Task Every_bank_is_read_before_the_first_write_goes_out()
    {
        var radio = new FakeAnytoneRadio();
        var proto = Fast();
        await proto.IdentifyAsync(radio, CancellationToken.None);

        var baseline = new byte[Layout.ImageSize];
        radio.LoadImage(baseline);
        var image = (byte[])baseline.Clone();
        image[Layout.OffsetOf(Layout.ChannelBanks) + 0x30] = 0x5A;
        await proto.WriteImageAsync(radio, image, baseline, null, CancellationToken.None);

        int firstWrite = radio.Log.FindIndex(e => e.StartsWith("W:"));
        int lastRead = radio.Log.FindLastIndex(e => e.StartsWith("R:"));
        Assert.True(lastRead < firstWrite, "a read landed after a write — the radio would discard everything");
        Assert.Equal(0, radio.DiscardedWriteCount);

        long readBytes = radio.Log.Where(e => e.StartsWith("R:")).Sum(e => long.Parse(e.Split('+')[1]));
        Assert.True(readBytes >= Layout.BankStorageSize,
                    $"only {readBytes} bytes read; a bank's storage is {Layout.BankStorageSize}");
    }
}

/// <summary>
/// Slot assignment has to keep IR order, because Decode enumerates bitmaps in index order.
/// Regression test for the reordering the I3 gate caught on 2026-08-21: with a base image
/// whose allocation has a gap, every item past the gap was encoded into one slot and read
/// back from another. [format §4]
/// </summary>
public class D878SlotOrderTests
{
    private static readonly Plugmatic.Radios.IRadioCodec Codec = D878uvCodec.Instance;

    private static readonly string[] PlainBitmaps =
        ["channelBitmap", "zoneBitmap", "scanListBitmap", "radioIdBitmap", "hiddenZoneBitmap", "groupListBitmap"];

    /// <summary>An erased radio: 0xFF everywhere, with the set-means-allocated bitmaps cleared.</summary>
    private static byte[] BlankBase()
    {
        var image = new byte[Layout.ImageSize];
        Array.Fill(image, (byte)0xFF);
        foreach (var name in PlainBitmaps)
        {
            var region = Layout.Regions.First(r => r.Name == name);
            image.AsSpan(Layout.OffsetOf(region.Address), region.Length).Clear();
        }
        return image;                       // contactBitmap is inverted, so 0xFF = none allocated
    }

    /// <summary>Bitmaps are LSB-first; the contact bitmap is inverted. [format §3/§4]</summary>
    private static void SetAllocated(byte[] image, uint bitmap, int index, bool allocated, bool inverted)
    {
        int at = Layout.OffsetOf(bitmap) + index / 8;
        int mask = 1 << (index % 8);
        bool bit = inverted ? !allocated : allocated;
        image[at] = (byte)(bit ? image[at] | mask : image[at] & ~mask);
    }

    /// <summary>Leave 0-9 and 40-49 allocated, 10-39 free — a gap right after slot 9.</summary>
    private static byte[] WithGap(byte[] image, uint bitmap, bool inverted)
    {
        for (int i = 10; i < 40; i++) SetAllocated(image, bitmap, i, false, inverted);
        for (int i = 40; i < 50; i++) SetAllocated(image, bitmap, i, true, inverted);
        return image;
    }

    private static AnalogChannel Ch(int i) => new()
    {
        Name = $"CH{i:D3}",
        RxFrequency = Frequency.FromMHz(430.0m + i * 0.0125m),
        TxFrequency = Frequency.FromMHz(430.0m + i * 0.0125m),
    };

    [Fact]
    public void Channels_keep_their_order_when_the_base_allocation_has_gaps()
    {
        var seed = new Codeplug();
        for (int i = 0; i < 20; i++) seed.Channels.Add(Ch(i));
        var basis = WithGap(Codec.Encode(seed, BlankBase()), Layout.ChannelBitmap, inverted: false);

        var ir = new Codeplug();
        for (int i = 0; i < 120; i++) ir.Channels.Add(Ch(i));

        var decoded = Codec.Decode(Codec.Encode(ir, basis));
        Assert.Equal(ir.Channels.Count, decoded.Channels.Count);
        Assert.Equal(ir.Channels.Select(c => c.Name), decoded.Channels.Select(c => c.Name));
    }

    [Fact]
    public void Contacts_keep_their_order_when_the_base_allocation_has_gaps()
    {
        var seed = new Codeplug();
        for (int i = 0; i < 20; i++)
            seed.Contacts.Add(new Contact { Name = $"TG{i:D3}", Type = CallType.Group, DmrId = (uint)(1000 + i) });
        var basis = WithGap(Codec.Encode(seed, BlankBase()), Layout.ContactBitmap, inverted: true);

        var ir = new Codeplug();
        for (int i = 0; i < 60; i++)
            ir.Contacts.Add(new Contact { Name = $"TG{i:D3}", Type = CallType.Group, DmrId = (uint)(1000 + i) });

        var decoded = Codec.Decode(Codec.Encode(ir, basis));
        Assert.Equal(ir.Contacts.Select(c => c.Name), decoded.Contacts.Select(c => c.Name));
    }
}
