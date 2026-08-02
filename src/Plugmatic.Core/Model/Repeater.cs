namespace Plugmatic.Core.Model;

public enum RepeaterMode { Fm, Dmr, FmAndDmr }
public enum RepeaterService { Ham, Gmrs }

/// <summary>Merged repeater record from the providers. [spec §6.2]</summary>
public sealed class Repeater
{
    public required string Callsign { get; set; }
    public required Frequency Output { get; set; }
    public required Frequency Input { get; set; }
    public RepeaterMode Mode { get; set; } = RepeaterMode.Fm;
    public RepeaterService Service { get; set; } = RepeaterService.Ham;
    public SelectiveCall UplinkTone { get; set; } = SelectiveCall.None;
    public SelectiveCall DownlinkTone { get; set; } = SelectiveCall.None;
    public int? ColorCode { get; set; }
    public uint? DmrId { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    /// <summary>DMR network name when known (BM, RMHAM, ...).</summary>
    public string? Network { get; set; }
    public double DistanceKm { get; set; }
    public List<string> Sources { get; } = [];
    /// <summary>Static talkgroups (BrandMeister): (talkgroup id, timeslot).</summary>
    public List<(uint Talkgroup, int Slot)> StaticTalkgroups { get; } = [];
}

public sealed record RepeaterQueryOptions(bool IncludeGmrs, bool Offline);
