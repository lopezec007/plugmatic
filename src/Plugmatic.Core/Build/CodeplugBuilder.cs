using Plugmatic.Core.Model;
using Plugmatic.Core.Validation;

namespace Plugmatic.Core.Build;

public interface ICodeplugBuilder
{
    BuildResult Build(IReadOnlyList<Repeater> repeaters, BuildProfile profile, GmrsPolicy gmrs);
}

/// <summary>Repeaters + profile + policy -> IR. Enforces I4/I5 by construction. [spec §6.3]</summary>
public sealed class CodeplugBuilder(RadioCapabilities caps) : ICodeplugBuilder
{
    /// <summary>GMRS main channels 15-22 (repeater outputs / simplex). [spec §6.3.3]</summary>
    private static readonly (int Num, decimal MHz)[] GmrsMain =
        [(15, 462.550m), (16, 462.575m), (17, 462.600m), (18, 462.625m),
         (19, 462.650m), (20, 462.675m), (21, 462.700m), (22, 462.725m)];

    /// <summary>GMRS 462 interstitials 1-7 (5 W ERP class => Medium power).</summary>
    private static readonly (int Num, decimal MHz)[] Gmrs462Interstitial =
        [(1, 462.5625m), (2, 462.5875m), (3, 462.6125m), (4, 462.6375m),
         (5, 462.6625m), (6, 462.6875m), (7, 462.7125m)];

    /// <summary>467 interstitials FRS 8-14 — always RX-only (D9).</summary>
    private static readonly (int Num, decimal MHz)[] Gmrs467Interstitial =
        [(8, 467.5625m), (9, 467.5875m), (10, 467.6125m), (11, 467.6375m),
         (12, 467.6625m), (13, 467.6875m), (14, 467.7125m)];

    private static readonly (string Name, decimal MHz)[] NoaaChannels =
        [("NOAA WX1", 162.550m), ("NOAA WX2", 162.400m), ("NOAA WX3", 162.475m),
         ("NOAA WX4", 162.425m), ("NOAA WX5", 162.450m), ("NOAA WX6", 162.500m),
         ("NOAA WX7", 162.525m)];

    public BuildResult Build(IReadOnlyList<Repeater> repeaters, BuildProfile profile, GmrsPolicy gmrs)
    {
        var notes = new List<string>();
        var plug = new Codeplug();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var zones = new Dictionary<string, Zone>(StringComparer.Ordinal);

        // Settings from config (radio keeps its own ID when unset — encode-over-base behaviour).
        var cfg = ConfigStore.Load();
        if (uint.TryParse(cfg.GetValueOrDefault("dmr.id"), out var dmrId)) plug.Settings.RadioId = dmrId;
        plug.Settings.Callsign = cfg.GetValueOrDefault("dmr.callsign") ?? "";
        if (plug.Settings.RadioId == 0)
            notes.Add("dmr.id not configured — the radio's existing DMR ID is kept on write " +
                      "(plugmatic config set dmr.id <id>).");

        // Contacts from profile talkgroups (in profile order: TX-contact slots stay <= 255).
        foreach (var tg in profile.Talkgroups)
            plug.Contacts.Add(new Contact
            {
                Name = Fit(tg.Name, 16),
                Type = tg.Private ? CallType.Private : CallType.Group,
                DmrId = tg.Id,
            });

        var groupTgs = profile.Talkgroups.Where(t => !t.Private).ToList();
        if (groupTgs.Count > 0)
            plug.RxGroupLists.Add(new RxGroupList
            {
                Name = Fit("RX All", 11),
                ContactNames = groupTgs.Select(t => Fit(t.Name, 16)).Take(caps.MaxContactsPerGroupList).ToList(),
            });

        var ordered = repeaters.OrderBy(r => r.DistanceKm).ToList();

        // Provider data sometimes carries corrupt offsets (e.g. +600 MHz); a repeater whose
        // input we cannot transmit on is skipped with a note — never silently.
        bool UsableInput(Repeater r)
        {
            if (caps.InBand(r.Input)) return true;
            notes.Add($"{r.Callsign} {r.Output}: input {r.Input} outside radio TX bands (bad source offset?); skipped.");
            return false;
        }

        // ---- Ham analog ----
        if (profile.AnalogHam)
            foreach (var r in ordered.Where(r => r.Service == RepeaterService.Ham && r.Mode != RepeaterMode.Dmr && UsableInput(r)))
            {
                var ch = new AnalogChannel
                {
                    Name = ChannelName(profile.NamingTemplate, r, names),
                    RxFrequency = r.Output,
                    TxFrequency = r.Input,
                    Power = PowerLevel.High,
                    WideBandwidth = true,
                    TxTone = r.UplinkTone,
                    RxTone = SelectiveCall.None,   // carrier squelch RX by default
                    Admit = AdmitCriterion.ChannelFree,
                };
                plug.Channels.Add(ch);
                ZoneFor(zones, profile, r, digital: false).ChannelNames.Add(ch.Name);
            }

        // ---- Ham DMR: one channel per (repeater x talkgroup) ----
        if (profile.DigitalHam)
            foreach (var r in ordered.Where(r => r.Service == RepeaterService.Ham && r.Mode != RepeaterMode.Fm && UsableInput(r)))
            {
                var slots = TalkgroupsFor(r, profile);
                foreach (var (tg, slot) in slots.Take(profile.MaxTalkgroupsPerRepeater))
                {
                    var contactName = EnsureContact(plug, tg, notes);
                    if (contactName is null) continue;
                    var ch = new DigitalChannel
                    {
                        Name = DigitalName(r, contactName, names),
                        RxFrequency = r.Output,
                        TxFrequency = r.Input,
                        Power = PowerLevel.High,
                        ColorCode = r.ColorCode ?? 1,
                        TimeSlot = slot == 2 ? TimeSlot.TS2 : TimeSlot.TS1,
                        TxContactName = contactName,
                        RxGroupListName = plug.RxGroupLists.FirstOrDefault()?.Name,
                    };
                    plug.Channels.Add(ch);
                    ZoneFor(zones, profile, r, digital: true).ChannelNames.Add(ch.Name);
                }
            }

        // ---- GMRS (D8/D9, I5 by construction) ----
        if (profile.Gmrs)
        {
            var gmrsZone = GetZone(zones, "GMRS");
            foreach (var (num, mhz) in Gmrs462Interstitial)
                AddGmrs(plug, gmrsZone, names, $"GMRS {num}", mhz, mhz,
                    gmrs.TxEnabled ? TxPermit.Allowed : TxPermit.Inhibited,
                    PowerLevel.Medium, wide: true);            // 5 W ERP class: never High
            foreach (var (num, mhz) in Gmrs467Interstitial)
                AddGmrs(plug, gmrsZone, names, $"GMRS {num}", mhz, mhz,
                    TxPermit.Inhibited,                        // D9: always RX-only
                    PowerLevel.Low, wide: false);              // narrow 12.5 kHz
            foreach (var (num, mhz) in GmrsMain)
                AddGmrs(plug, gmrsZone, names, $"GMRS {num}", mhz, mhz,
                    gmrs.TxEnabled ? TxPermit.Allowed : TxPermit.Inhibited,
                    PowerLevel.High, wide: true);              // 50 W class -> radio max

            var gmrsRepeaters = ordered.Where(r => r.Service == RepeaterService.Gmrs).ToList();
            foreach (var r in gmrsRepeaters)
            {
                var main = GmrsMain.FirstOrDefault(m => Frequency.FromMHz(m.MHz).Hz == r.Output.Hz);
                if (main.Num == 0)
                {
                    notes.Add($"GMRS repeater {r.Callsign} output {r.Output} not a GMRS main channel; skipped.");
                    continue;
                }
                var ch = new AnalogChannel
                {
                    Name = ChannelName("GMRS R{num} {call}".Replace("{num}", main.Num.ToString()), r, names),
                    RxFrequency = r.Output,
                    TxFrequency = r.Output + 5_000_000,        // +5.000 MHz repeater input [spec §6.3.3]
                    TxPermit = gmrs.TxEnabled ? TxPermit.Allowed : TxPermit.Inhibited,
                    Power = PowerLevel.High,
                    WideBandwidth = true,
                    TxTone = r.UplinkTone,
                    Admit = AdmitCriterion.ChannelFree,
                };
                plug.Channels.Add(ch);
                gmrsZone.ChannelNames.Add(ch.Name);
            }
            if (!gmrs.TxEnabled)
                notes.Add("GMRS channels are RX-only (enable with: plugmatic config gmrs-tx enable).");
        }

        // ---- NOAA (D10, I4 by construction) ----
        if (profile.Noaa)
        {
            var wxZone = GetZone(zones, CodeplugValidator.NoaaZoneName);
            foreach (var (name, mhz) in NoaaChannels)
            {
                var ch = new AnalogChannel
                {
                    Name = name,
                    RxFrequency = Frequency.FromMHz(mhz),
                    TxFrequency = Frequency.FromMHz(mhz),
                    TxPermit = TxPermit.Inhibited,             // I4: no acknowledgment path exists
                    Power = PowerLevel.Low,
                    WideBandwidth = false,
                };
                names.Add(ch.Name);
                plug.Channels.Add(ch);
                wxZone.ChannelNames.Add(ch.Name);
            }
        }

        // ---- assemble zones (split at 64, drop empties) ----
        foreach (var zone in zones.Values.Where(z => z.ChannelNames.Count > 0))
        {
            if (zone.ChannelNames.Count <= caps.MaxChannelsPerZone)
            {
                plug.Zones.Add(zone);
                continue;
            }
            int part = 1;
            foreach (var chunk in zone.ChannelNames.Chunk(caps.MaxChannelsPerZone))
                plug.Zones.Add(new Zone { Name = Fit($"{zone.Name} {part++}", 16), ChannelNames = [.. chunk] });
        }

        if (plug.Channels.Count > profile.MaxChannels)
            notes.Add($"NOTE: {plug.Channels.Count} channels exceed profile maxChannels {profile.MaxChannels}; " +
                      "reduce radius or talkgroup fan-out (channels are distance-sorted, nothing was cut silently).");

        return new BuildResult(plug, notes);
    }

    // ---------------------------------------------------------------- helpers

    private List<(uint Tg, int Slot)> TalkgroupsFor(Repeater r, BuildProfile profile)
    {
        // BrandMeister static talkgroups first (authoritative slots), then profile defaults.
        var result = new List<(uint, int)>();
        foreach (var (tg, slot) in r.StaticTalkgroups)
            result.Add((tg, slot));
        foreach (var tg in profile.Talkgroups.Where(t => !t.Private))
            if (!result.Any(x => x.Item1 == tg.Id))
                result.Add((tg.Id, tg.Slot));
        return result;
    }

    private static string? EnsureContact(Codeplug plug, uint tgId, List<string> notes)
    {
        var existing = plug.Contacts.FirstOrDefault(c => c.DmrId == tgId && c.Type == CallType.Group);
        if (existing is not null) return existing.Name;
        var name = Fit($"TG{tgId}", 16);
        if (plug.Contacts.Count >= 250)
        {
            notes.Add($"Contact table budget reached; talkgroup {tgId} skipped.");
            return null;
        }
        if (plug.Contacts.Any(c => c.Name == name)) return name;
        plug.Contacts.Add(new Contact { Name = name, Type = CallType.Group, DmrId = tgId });
        return name;
    }

    private Zone ZoneFor(Dictionary<string, Zone> zones, BuildProfile profile, Repeater r, bool digital)
    {
        var key = profile.ZoneStrategy switch
        {
            "by-repeater" => Fit(r.Callsign, 16),
            "by-network" => digital ? "DMR" : "Analog",
            _ => Fit(TitleCase(r.City ?? r.Callsign), digital ? 12 : 16) + (digital ? " DMR" : ""),
        };
        return GetZone(zones, key);
    }

    private static Zone GetZone(Dictionary<string, Zone> zones, string name)
    {
        if (!zones.TryGetValue(name, out var zone))
            zones[name] = zone = new Zone { Name = Fit(name, 16) };
        return zone;
    }

    private static void AddGmrs(Codeplug plug, Zone zone, HashSet<string> names, string name,
        decimal rxMHz, decimal txMHz, TxPermit permit, PowerLevel power, bool wide)
    {
        var ch = new AnalogChannel
        {
            Name = name,
            RxFrequency = Frequency.FromMHz(rxMHz),
            TxFrequency = Frequency.FromMHz(txMHz),
            TxPermit = permit,
            Power = power,
            WideBandwidth = wide,
        };
        names.Add(name);
        plug.Channels.Add(ch);
        zone.ChannelNames.Add(ch.Name);
    }

    private static string ChannelName(string template, Repeater r, HashSet<string> taken)
    {
        var khz = (r.Output.Hz / 1000 % 1000000).ToString("D6").TrimStart('0');
        var name = template
            .Replace("{call}", r.Callsign)
            .Replace("{city}", TitleCase(r.City ?? ""))
            .Replace("{mhz}", r.Output.MHz.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{khz}", khz)
            .Trim();
        return Unique(Fit(name, 16), taken);
    }

    private static string DigitalName(Repeater r, string contactName, HashSet<string> taken)
    {
        // "775 Colorado" style: kHz-within-MHz fragment + talkgroup name (e.g. 448.775 -> 775).
        var frag = (r.Output.Hz % 1_000_000 / 1000).ToString("D3");
        return Unique(Fit($"{frag} {contactName}", 16), taken);
    }

    private static string Unique(string name, HashSet<string> taken)
    {
        if (taken.Add(name)) return name;
        for (int i = 2; ; i++)
        {
            var candidate = Fit(name, 16 - (i.ToString().Length + 1)) + "~" + i;
            if (taken.Add(candidate)) return candidate;
        }
    }

    private static string Fit(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd();

    private static string TitleCase(string s) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
}
