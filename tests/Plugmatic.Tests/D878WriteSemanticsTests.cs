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

    // ------------------------------------------------- erase blocks and the write plan

    [Fact]
    public void An_erase_block_is_256KB_and_aligned()
    {
        Assert.Equal(0x40000u, Layout.EraseBlockSize);
        Assert.Equal(0x00800000u, Layout.EraseBlockOf(0x00800030));
        Assert.Equal(0x00800000u, Layout.EraseBlockOf(0x0083FFFF));
        Assert.Equal(0x00840000u, Layout.EraseBlockOf(0x00840000));
    }

    [Fact]
    public void An_unchanged_image_touches_no_block_and_needs_no_writes()
    {
        var image = new byte[Layout.ImageSize];
        Assert.Empty(D878uvProtocol.BuildWritePlan(image, image));
    }

    [Fact]
    public void Changing_one_byte_is_refused_because_its_erase_block_is_only_partly_described()
    {
        // Writing 16 bytes erases 256 KB. The region table describes 0x2000 of the block at
        // 0x00800000 and nothing of the rest, and the CPS demonstrably keeps real channel
        // data at 0x00802000-0x00804000 — so this write would destroy it. [§5.4]
        var baseline = new byte[Layout.ImageSize];
        var image = (byte[])baseline.Clone();
        image[Layout.OffsetOf(Layout.ChannelBanks) + 0x30] ^= 0x5A;

        var e = Assert.Throws<D878ProtocolException>(() => D878uvProtocol.BuildWritePlan(image, baseline));
        Assert.Contains("00800000", e.Message);
        Assert.Contains("erase block", e.Message.ToLowerInvariant());
    }

    [Fact]
    public async Task A_whole_image_write_is_refused_rather_than_wiping_what_it_cannot_restore()
    {
        var radio = new FakeAnytoneRadio();
        var proto = Fast();
        await proto.IdentifyAsync(radio, CancellationToken.None);

        await Assert.ThrowsAsync<D878ProtocolException>(() =>
            proto.WriteImageAsync(radio, new byte[Layout.ImageSize], null, null, CancellationToken.None));

        // Refused before a single frame went out.
        Assert.DoesNotContain(radio.Log, entry => entry.StartsWith("W:"));
    }

    [Fact]
    public void Every_region_lives_in_a_block_the_table_cannot_yet_fully_cover()
    {
        // Documents the exact gap that blocks write support: if this ever starts failing,
        // the region table has grown to cover whole erase blocks and writes can be enabled.
        var blocks = Layout.Regions.Select(r => Layout.EraseBlockOf(r.Address)).Distinct();
        Assert.All(blocks, block =>
            Assert.True(Layout.CoveredBytesInBlock(block) < Layout.EraseBlockSize,
                        $"block 0x{block:X8} is now fully covered — revisit SupportsWrite"));
    }
}
