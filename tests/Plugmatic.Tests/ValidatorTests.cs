using Plugmatic.Core.Model;
using Plugmatic.Core.Validation;
using Plugmatic.Radios.Dm32uv.Format;

namespace Plugmatic.Tests;

public class ValidatorTests
{
    private static readonly RadioCapabilities Caps = Dm32uvCodec.Instance.Capabilities;

    [Fact]
    public void Sample_plug_validates_clean()
    {
        var errors = CodeplugValidator.Validate(CodecRoundTripTests.SampleIr(), Caps);
        Assert.Empty(errors);
    }

    [Fact]
    public void I4_noaa_channel_must_be_rx_only()
    {
        var plug = CodecRoundTripTests.SampleIr();
        plug.Channels.First(c => c.Name == "NOAA WX1").TxPermit = TxPermit.Allowed;
        var errors = CodeplugValidator.Validate(plug, Caps);
        Assert.Contains(errors, e => e.Contains("I4") && e.Contains("NOAA"));
    }

    [Fact]
    public void I4_467_interstitial_must_be_rx_only()
    {
        var plug = CodecRoundTripTests.SampleIr();
        plug.Channels.Add(new AnalogChannel
        {
            Name = "GMRS 8",
            RxFrequency = Frequency.FromMHz(467.5625m),
            TxFrequency = Frequency.FromMHz(467.5625m),
            TxPermit = TxPermit.Allowed,     // must be flagged regardless of GMRS ack (D9)
        });
        var errors = CodeplugValidator.Validate(plug, Caps);
        Assert.Contains(errors, e => e.Contains("I4") && e.Contains("467"));
    }

    [Fact]
    public void Out_of_band_tx_rejected_but_rx_only_oob_allowed()
    {
        var plug = CodecRoundTripTests.SampleIr();
        plug.Channels.Add(new AnalogChannel
        {
            Name = "Bad TX",
            RxFrequency = Frequency.FromMHz(30m),
            TxFrequency = Frequency.FromMHz(30m),
            TxPermit = TxPermit.Allowed,
        });
        var errors = CodeplugValidator.Validate(plug, Caps);
        Assert.Contains(errors, e => e.Contains("Bad TX") && e.Contains("outside"));
        // NOAA 162 MHz RX-only channel in the sample must NOT be flagged.
        Assert.DoesNotContain(errors, e => e.Contains("NOAA WX1"));
    }

    [Fact]
    public void Long_names_fail_no_silent_truncation()
    {
        var plug = CodecRoundTripTests.SampleIr();
        plug.Channels[0].Name = "THIS NAME IS WAY TOO LONG FOR 16";
        plug.Zones[0].ChannelNames[0] = plug.Channels[0].Name;
        var errors = CodeplugValidator.Validate(plug, Caps);
        Assert.Contains(errors, e => e.Contains("exceeds 16"));
    }

    [Fact]
    public void Dangling_references_reported()
    {
        var plug = CodecRoundTripTests.SampleIr();
        ((DigitalChannel)plug.Channels[1]).TxContactName = "Nope";
        plug.Zones[0].ChannelNames.Add("Ghost");
        var errors = CodeplugValidator.Validate(plug, Caps);
        Assert.Contains(errors, e => e.Contains("unknown TX contact 'Nope'"));
        Assert.Contains(errors, e => e.Contains("unknown channel 'Ghost'"));
    }

    [Fact]
    public void Duplicate_names_reported()
    {
        var plug = CodecRoundTripTests.SampleIr();
        plug.Channels[1].Name = plug.Channels[0].Name;
        var errors = CodeplugValidator.Validate(plug, Caps);
        Assert.Contains(errors, e => e.Contains("duplicate channel name"));
    }
}
