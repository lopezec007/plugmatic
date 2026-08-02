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

/// <summary>Build profile (profiles/*.yaml). [spec §6.3.2]</summary>
public sealed class BuildProfile
{
    public string Name { get; set; } = "default";
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
