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

    // ------------------------------------------------- erase blocks and block-safe writes

    [Fact]
    public void An_erase_block_is_256KB_and_aligned()
    {
        Assert.Equal(0x40000u, Layout.EraseBlockSize);
        Assert.Equal(0x00800000u, Layout.EraseBlockOf(0x00800030));
        Assert.Equal(0x00800000u, Layout.EraseBlockOf(0x0083FFFF));
        Assert.Equal(0x00840000u, Layout.EraseBlockOf(0x00840000));
    }

    [Fact]
    public void Writes_are_bounded_to_the_erase_blocks_the_codeplug_occupies()
    {
        // Inside a codeplug block but described by no region: legal, because rewriting the
        // block is the only way to change anything in it. [protocol §5.4]
        Assert.True(Layout.IsWritable(0x00802000, 16));
        Assert.True(Layout.IsWritable(Layout.ChannelBanks, 16));

        // Outside every codeplug block: still refused. The callsign database lives here.
        Assert.False(Layout.IsWritable(0x04340000, 16));
        // A chunk may not straddle two blocks.
        Assert.False(Layout.IsWritable(Layout.ChannelBanks + Layout.EraseBlockSize - 8, 16));
    }

    [Fact]
    public void Only_blocks_that_actually_change_are_named()
    {
        var baseline = new byte[Layout.ImageSize];
        var image = (byte[])baseline.Clone();
        Assert.Empty(D878uvProtocol.TouchedBlocks(image, baseline));

        image[Layout.OffsetOf(Layout.ChannelBanks) + 0x30] ^= 0x5A;
        Assert.Equal([0x00800000u], D878uvProtocol.TouchedBlocks(image, baseline));

        image[Layout.OffsetOf(Layout.ChannelBanks + Layout.BetweenChannelBanks)] ^= 0x01;
        Assert.Equal([0x00800000u, 0x00840000u], D878uvProtocol.TouchedBlocks(image, baseline));
    }

    [Fact]
    public async Task Codeplug_writes_are_refused_until_the_writable_address_set_is_known()
    {
        // Not a placeholder: rewriting a whole erase window sends addresses the vendor CPS
        // never writes, and doing so copied channel bank 0 over bank 1 on hardware. [§5.7]
        var radio = new FakeAnytoneRadio();
        var proto = Fast();
        await proto.IdentifyAsync(radio, CancellationToken.None);

        var baseline = new byte[Layout.ImageSize];
        var image = (byte[])baseline.Clone();
        image[Layout.OffsetOf(Layout.ChannelBanks) + 0x30] = 0x5A;

        var e = await Assert.ThrowsAsync<D878ProtocolException>(() =>
            proto.WriteImageAsync(radio, image, baseline, null, CancellationToken.None));
        Assert.Contains("Refusing to write", e.Message);
        Assert.DoesNotContain(radio.Log, entry => entry.StartsWith("W:"));
        Assert.Empty(radio.ErasedBlocks);
    }

    [Fact]
    public void The_blast_radius_of_a_change_is_reported_in_erase_windows()
    {
        var baseline = new byte[Layout.ImageSize];
        var image = (byte[])baseline.Clone();
        Assert.Empty(D878uvProtocol.TouchedBlocks(image, baseline));

        image[Layout.OffsetOf(Layout.ChannelBanks) + 0x30] ^= 0x5A;
        Assert.Equal([0x00800000u], D878uvProtocol.TouchedBlocks(image, baseline));

        image[Layout.OffsetOf(Layout.ChannelBanks + Layout.BetweenChannelBanks)] ^= 0x01;
        Assert.Equal([0x00800000u, 0x00840000u], D878uvProtocol.TouchedBlocks(image, baseline));
    }
}
