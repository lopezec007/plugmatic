using YamlDotNet.Serialization;

namespace Plugmatic.Core.Model;

public enum TxPermit { Allowed, Inhibited }
/// <summary>
/// Transmit power. `Turbo` is the AnyTone's fourth step above High; radios without it clamp
/// to their maximum. Modelled rather than folded into High because folding made them
/// indistinguishable on decode, so the encoder could not tell a Turbo record from a correct
/// one and left 25 generated channels on Turbo. [d878uv-format.md §3]
/// </summary>
public enum PowerLevel { Low, Medium, High, Turbo }
public enum CallType { Private, Group, All }
public enum TimeSlot { TS1 = 1, TS2 = 2 }
public enum AdmitCriterion { Always, ChannelFree, ToneOrColorCode }

/// <summary>Radio-neutral intermediate representation. YAML-serialized as generated.yaml / read.yaml.</summary>
public sealed class Codeplug
{
    public GeneralSettings Settings { get; set; } = new();
    public List<Contact> Contacts { get; set; } = [];
    public List<RxGroupList> RxGroupLists { get; set; } = [];
    public List<Channel> Channels { get; set; } = [];
    public List<Zone> Zones { get; set; } = [];
    public List<ScanList> ScanLists { get; set; } = [];

    /// <summary>
    /// Opaque regions captured at decode time (virtual block address -> raw 4 KiB block),
    /// carried back verbatim on encode. Never serialized to YAML. [format §2 "passthrough"]
    /// </summary>
    [YamlIgnore]
    public Dictionary<uint, byte[]> RawBlocks { get; set; } = [];

    public Channel? FindChannel(string name) => Channels.FirstOrDefault(c => c.Name == name);
    public Contact? FindContact(string name) => Contacts.FirstOrDefault(c => c.Name == name);
}

public sealed class GeneralSettings
{
    /// <summary>The radio's own DMR ID (radio-ID list entry 0).</summary>
    public uint RadioId { get; set; }
    /// <summary>Operator callsign; used as the radio-ID entry name (12 chars max).</summary>
    public string Callsign { get; set; } = "";

    /// <summary>Radio-wide group-call match. Default off — the DM-32UV ships this way.</summary>
    public bool GroupCallMatch { get; set; }
    /// <summary>Radio-wide private-call match. Default off.</summary>
    public bool PrivateCallMatch { get; set; }
}

public sealed class Contact
{
    public string Name { get; set; } = "";
    public CallType Type { get; set; } = CallType.Group;
    public uint DmrId { get; set; }
    /// <summary>See Channel.RawRecord.</summary>
    [YamlIgnore]
    public byte[]? RawRecord { get; set; }
}

public sealed class RxGroupList
{
    public string Name { get; set; } = "";
    /// <summary>Member talkgroup contact names (resolved to DMR IDs at encode).</summary>
    public List<string> ContactNames { get; set; } = [];
    /// <summary>See Channel.RawRecord.</summary>
    [YamlIgnore]
    public byte[]? RawRecord { get; set; }
}

public abstract class Channel
{
    public string Name { get; set; } = "";
    public Frequency RxFrequency { get; set; }
    public Frequency TxFrequency { get; set; }
    public TxPermit TxPermit { get; set; } = TxPermit.Allowed;
    public PowerLevel Power { get; set; } = PowerLevel.High;
    public int SquelchLevel { get; set; } = 3;   // 0-15
    public string? ScanListName { get; set; }
    /// <summary>Original binary record captured at decode; preserves unmodeled bits on re-encode. Not serialized.</summary>
    [YamlIgnore]
    public byte[]? RawRecord { get; set; }
}

public sealed class AnalogChannel : Channel
{
    public bool WideBandwidth { get; set; } = true;   // true = 25 kHz
    public SelectiveCall RxTone { get; set; } = SelectiveCall.None;
    public SelectiveCall TxTone { get; set; } = SelectiveCall.None;
    public AdmitCriterion Admit { get; set; } = AdmitCriterion.Always;
}

public sealed class DigitalChannel : Channel
{
    public int ColorCode { get; set; }                // 0-15
    public TimeSlot TimeSlot { get; set; } = TimeSlot.TS1;
    /// <summary>TX talkgroup (contact name); null = none.</summary>
    public string? TxContactName { get; set; }
    public string? RxGroupListName { get; set; }
    public AdmitCriterion Admit { get; set; } = AdmitCriterion.ToneOrColorCode;
}

public sealed class Zone
{
    public string Name { get; set; } = "";
    public List<string> ChannelNames { get; set; } = [];

    /// <summary>
    /// Zone is present but not offered in the radio's zone selector. Modelled so a restore
    /// preserves it and a generated plug shows every zone it defines — the flag lives in a
    /// per-slot bitmap that outlives a codeplug replacement, so leaving it alone would hide
    /// whichever new zone inherited a hidden slot. [d878uv-format.md §4]
    /// </summary>
    public bool Hidden { get; set; }
    /// <summary>See Channel.RawRecord.</summary>
    [YamlIgnore]
    public byte[]? RawRecord { get; set; }
}

public sealed class ScanList
{
    /// <summary>Member marker for the radio's "current channel" slot.</summary>
    public const string CurrentChannelMarker = "@current";

    public string Name { get; set; } = "";
    public List<string> ChannelNames { get; set; } = [];
    /// <summary>See Channel.RawRecord.</summary>
    [YamlIgnore]
    public byte[]? RawRecord { get; set; }
}
