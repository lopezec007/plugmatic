using Plugmatic.Core.Model;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Plugmatic.Core.Build;

public sealed class ProfileTalkgroup
{
    public string Name { get; set; } = "";
    public uint Id { get; set; }
    public bool Private { get; set; }
    /// <summary>Preferred timeslot when the repeater has no static-TG hint (1 or 2).</summary>
    public int Slot { get; set; } = 1;
}

/// <summary>How generated channels are named. Extensible — add a member + a case in the builder.</summary>
public enum ChannelNameStyle
{
    /// <summary>"WA0ABC Colorado" / "WA0ABC 310815": repeater callsign + talkgroup (no kHz, no "TG"). Default.</summary>
    Callsign,
    /// <summary>"145115 WA0ABC" / "775 Colorado": kHz fragment first (the original style).</summary>
    Frequency,
}

/// <summary>What, if anything, a generated analog channel decodes on receive.</summary>
public enum RxTonePolicy
{
    /// <summary>
    /// Carrier squelch: the channel opens on any signal. Default, and the safe one — an RX
    /// tone that is wrong or stale fails *silently*, leaving a channel that looks fine and
    /// hears nothing, whereas a wrong TX tone fails obviously by not opening the repeater.
    /// It also keeps simplex traffic and stations the repeater is not encoding audible.
    /// </summary>
    None,

    /// <summary>
    /// Decode the repeater's published downlink tone (RepeaterBook "TSQ") where one exists;
    /// channels without a published downlink stay on carrier squelch. Quieter, at the cost
    /// of trusting third-party data for whether you hear anything at all.
    /// </summary>
    Downlink,
}

/// <summary>Build profile (profiles/*.yaml). [spec §6.3.2]</summary>
public sealed class BuildProfile
{
    public string Name { get; set; } = "default";
    /// <summary>Channel naming style: callsign (default) | frequency.</summary>
    public ChannelNameStyle NameStyle { get; set; } = ChannelNameStyle.Callsign;
    public double RadiusMi { get; set; } = 60;
    public int MaxChannels { get; set; } = 1000;
    /// <summary>by-town | by-repeater | by-network</summary>
    public string ZoneStrategy { get; set; } = "by-town";
    public bool AnalogHam { get; set; } = true;
    public bool DigitalHam { get; set; } = true;
    public bool Gmrs { get; set; } = true;
    public bool Noaa { get; set; } = true;
    /// <summary>Tokens: {call} {city} {mhz} {khz}. Result must fit 16 chars after substitution.</summary>
    public string NamingTemplate { get; set; } = "{khz} {call}";
    public List<ProfileTalkgroup> Talkgroups { get; set; } = [];
    /// <summary>Cap DMR channels per repeater (talkgroup fan-out control).</summary>
    public int MaxTalkgroupsPerRepeater { get; set; } = 8;
    /// <summary>RX tone squelch on analog channels: none (default) | downlink.</summary>
    public RxTonePolicy RxTone { get; set; } = RxTonePolicy.None;

    public static BuildProfile Load(string path) =>
        new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<BuildProfile>(File.ReadAllText(path))
        ?? throw new FormatException($"Empty profile: {path}");

    public static BuildProfile ColoradoDefault() => new()
    {
        Name = "colorado-default",
        Talkgroups =
        [
            new ProfileTalkgroup { Name = "Local 2", Id = 2, Slot = 2 },
            new ProfileTalkgroup { Name = "Colorado", Id = 3108, Slot = 1 },
            new ProfileTalkgroup { Name = "North Colorado", Id = 3171, Slot = 1 },
            new ProfileTalkgroup { Name = "TAC 310", Id = 310, Slot = 1 },
            new ProfileTalkgroup { Name = "TAC 311", Id = 311, Slot = 1 },
            new ProfileTalkgroup { Name = "TAC 312", Id = 312, Slot = 1 },
            new ProfileTalkgroup { Name = "Parrot", Id = 9990, Private = true, Slot = 2 },
        ],
    };
}

public sealed record GmrsPolicy(bool TxEnabled, string? AcknowledgedUtc);

public sealed record BuildResult(Codeplug Codeplug, List<string> Notes);
