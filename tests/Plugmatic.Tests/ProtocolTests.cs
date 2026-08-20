using Plugmatic.Radios;
using Plugmatic.Radios.Dm32uv.Format;
using Plugmatic.Radios.Dm32uv.Protocol;

namespace Plugmatic.Tests;

/// <summary>Protocol layer against the FakeRadio (no hardware, no delays).</summary>
public class ProtocolTests
{
    private static Dm32uvProtocol FastProtocol() => new()
    { CommandDelayMs = 0, MetadataProbeDelayMs = 0, BlockReadDelayMs = 0, BlockWriteDelayMs = 0 };

    private static FakeRadio LoadedRadio()
    {
        var radio = new FakeRadio();
        radio.LoadVirtualImage(Dm32uvCodec.Instance.Encode(CodecRoundTripTests.SampleIr()));
        return radio;
    }

    [Fact]
    public async Task Identify_returns_model_firmware_and_memory_range()
    {
        var radio = new FakeRadio();
        var proto = FastProtocol();
        var id = await proto.IdentifyAsync(radio, CancellationToken.None);
        Assert.Equal("DP570UV", id.Model);
        Assert.Equal("DM32.01.L01.048", id.FirmwareVersion);
        Assert.Equal("2022-06-27", id.BuildDate);
        Assert.Equal(FakeRadio.CodeplugStart, id.CodeplugMemoryStart);
        Assert.Equal(FakeRadio.CodeplugEnd, id.CodeplugMemoryEnd);
    }

    [Fact]
    public async Task Identify_retries_after_swallowed_psearch()
    {
        var radio = new FakeRadio { FailPsearchOnce = true };
        var id = await FastProtocol().IdentifyAsync(radio, CancellationToken.None);
        Assert.Equal("DP570UV", id.Model);
        Assert.Equal(2, radio.Log.Count(l => l == "PSEARCH"));
    }

    [Fact]
    public async Task Read_image_reassembles_scattered_blocks()
    {
        var image = Dm32uvCodec.Instance.Encode(CodecRoundTripTests.SampleIr());
        var radio = new FakeRadio();
        radio.LoadVirtualImage(image, physicalScatterSeed: 99);
        var proto = FastProtocol();
        await proto.IdentifyAsync(radio, CancellationToken.None);
        var read = await proto.ReadImageAsync(radio, null, CancellationToken.None);
        var cmp = Dm32uvCodec.Instance.Compare(image, read);
        Assert.True(cmp.Equal, string.Join("\n", cmp.Differences));
    }

    [Fact]
    public async Task Write_then_read_round_trips_through_fake_flash()
    {
        var image = Dm32uvCodec.Instance.Encode(CodecRoundTripTests.SampleIr());
        var radio = LoadedRadio();
        var proto = FastProtocol();
        await proto.IdentifyAsync(radio, CancellationToken.None);

        // Mutate: rename a zone, add a channel — writes must land on mapped physical blocks.
        var ir = Dm32uvCodec.Instance.Decode(image);
        ir.Zones[0].Name = "Loveland";
        var newImage = Dm32uvCodec.Instance.Encode(ir, image);

        await proto.WriteImageAsync(radio, newImage, null, null, CancellationToken.None);
        var cmp = Dm32uvCodec.Instance.Compare(newImage, radio.VirtualImage());
        Assert.True(cmp.Equal, string.Join("\n", cmp.Differences));
    }

    [Fact]
    public async Task Write_of_unmapped_block_uses_adoption_address()
    {
        var radio = LoadedRadio();
        var proto = FastProtocol();
        await proto.IdentifyAsync(radio, CancellationToken.None);
        var baseImage = await proto.ReadImageAsync(radio, null, CancellationToken.None);

        // Add a group-list-free extra block: the sample plug has no 0x03 block; hand-craft one
        // so a previously-absent virtual block must be written via 0xFF000 adoption.
        var img = new Dm32Image((byte[])baseImage.Clone());
        Assert.False(img.BlockPresent(0x03));
        img.AllocateBlock(0x03).Fill(0x11);
        var newImage = img.Bytes;

        await proto.WriteImageAsync(radio, newImage, null, null, CancellationToken.None);
        var readBack = radio.VirtualImage();
        Assert.True(new Dm32Image(readBack).BlockPresent(0x03));
        var cmp = Dm32uvCodec.Instance.Compare(newImage, readBack);
        Assert.True(cmp.Equal, string.Join("\n", cmp.Differences));
        Assert.Contains(radio.Log, l => l == $"W:0FF000+{Dm32Image.BlockSize}");
    }

    [Fact]
    public async Task Read_covers_blocks_beyond_the_decoded_area()
    {
        // Regression: an image window that stopped at 0x68 silently dropped 11 of this
        // radio's 107 mapped blocks from reads, backups and writes.
        var img = new Dm32Image(Dm32uvCodec.Instance.Encode(CodecRoundTripTests.SampleIr()));
        img.AllocateBlock(0x7C).Fill(0x5A);
        img.AllocateBlock(0x01).Fill(0x11);
        var radio = new FakeRadio();
        radio.LoadVirtualImage(img.Bytes);

        var proto = FastProtocol();
        await proto.IdentifyAsync(radio, CancellationToken.None);
        var read = await proto.ReadImageAsync(radio, null, CancellationToken.None);

        var back = new Dm32Image(read);
        Assert.True(back.BlockPresent(0x7C));
        Assert.True(back.BlockPresent(0x01));
        Assert.Equal(0x5A, read[0x7C * Dm32Image.BlockSize]);
        var cmp = Dm32uvCodec.Instance.Compare(img.Bytes, read);
        Assert.True(cmp.Equal, string.Join("\n", cmp.Differences));
    }

    [Fact]
    public async Task Nak_on_write_aborts_with_readable_error()
    {
        var radio = LoadedRadio();
        radio.NakWrites = true;
        var proto = FastProtocol();
        await proto.IdentifyAsync(radio, CancellationToken.None);
        var image = await proto.ReadImageAsync(radio, null, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<Dm32ProtocolException>(
            () => proto.WriteImageAsync(radio, image, null, null, CancellationToken.None));
        Assert.Contains("NAK", ex.Message);
    }

    [Fact]
    public async Task Mid_write_timeout_aborts_without_retrying_writes()
    {
        var radio = LoadedRadio();
        radio.TimeoutAfterWrites = 2;
        var proto = FastProtocol();
        await proto.IdentifyAsync(radio, CancellationToken.None);
        var image = await proto.ReadImageAsync(radio, null, CancellationToken.None);
        int writesBefore = radio.Log.Count(l => l.StartsWith("W:"));
        await Assert.ThrowsAsync<Dm32ProtocolException>(
            () => proto.WriteImageAsync(radio, image, null, null, CancellationToken.None));
        int writes = radio.Log.Count(l => l.StartsWith("W:")) - writesBefore;
        Assert.Equal(3, writes);   // 2 acked + the one that timed out; NO resend [protocol §8]
    }

    [Fact]
    public async Task I8_only_in_region_and_adoption_addresses_are_writable()
    {
        var radio = LoadedRadio();
        var proto = FastProtocol();
        await proto.IdentifyAsync(radio, CancellationToken.None);
        var block = new byte[Dm32Image.BlockSize];
        block[^1] = 0x03;

        // Outside the region and not the adoption block: refused before any byte is sent.
        int writesBefore = radio.Log.Count(l => l.StartsWith("W:"));
        var ex = await Assert.ThrowsAsync<Dm32ProtocolException>(
            () => proto.WritePhysicalBlockAsync(radio, 0x0D0000, block, CancellationToken.None));
        Assert.Contains("I8", ex.Message);
        var ex2 = await Assert.ThrowsAsync<Dm32ProtocolException>(
            () => proto.WritePhysicalBlockAsync(radio, 0x000000, block, CancellationToken.None));
        Assert.Contains("I8", ex2.Message);
        Assert.Equal(writesBefore, radio.Log.Count(l => l.StartsWith("W:")));

        // The two legal targets go through.
        await proto.WritePhysicalBlockAsync(radio, 0x001000, block, CancellationToken.None);
        await proto.WritePhysicalBlockAsync(radio, Dm32uvProtocol.AdoptionAddress, block, CancellationToken.None);
    }

    [Fact]
    public async Task End_session_sends_end_frame_and_cycles_dtr()
    {
        var radio = LoadedRadio();
        var proto = FastProtocol();
        await proto.IdentifyAsync(radio, CancellationToken.None);
        await proto.ReadImageAsync(radio, null, CancellationToken.None);
        await proto.EndSessionAsync(radio, CancellationToken.None);
        Assert.Contains("END", radio.Log);
        Assert.Contains("dtr-reset", radio.Log);
    }
}
