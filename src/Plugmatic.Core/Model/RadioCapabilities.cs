namespace Plugmatic.Core.Model;

public sealed record BandRange(Frequency Lower, Frequency Upper)
{
    public bool Contains(Frequency f) => f.Hz >= Lower.Hz && f.Hz <= Upper.Hz;
}

/// <summary>Per-radio limits consumed by the builder and validator. [spec §6.4]</summary>
public sealed record RadioCapabilities(
    string Model,
    int MaxChannels, int MaxZones, int MaxChannelsPerZone,
    int MaxContacts, int MaxGroupLists, int MaxContactsPerGroupList,
    int MaxScanLists, int MaxChannelsPerScanList,
    int MaxNameLength,
    IReadOnlyList<BandRange> TxBands)
{
    public bool InBand(Frequency f) => TxBands.Any(b => b.Contains(f));
}
