using Plugmatic.Core.Model;
using Plugmatic.Core.Validation;

namespace Plugmatic.Core.Build;

public interface ICodeplugBuilder
{
    BuildResult Build(IReadOnlyList<Repeater> repeaters, BuildProfile profile, GmrsPolicy gmrs);
}

/// <summary>Repeaters + profile + policy -> IR. Enforces I4/I5 by construction. [spec §6.3]</summary>
public sealed class CodeplugBuilder(RadioCapabilities caps, GeneralSettings? settings = null) : ICodeplugBuilder
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

        // Operator identity: from injected settings, else config. Without a DMR ID, ALL DMR
        // channels are forced RX-only — an unidentified radio must not transmit digital.
        plug.Settings = settings ?? LoadSettingsFromConfig();
        if (plug.Settings.RadioId != 0 && plug.Settings.Callsign.Length == 0)
            plug.Settings.Callsign = plug.Settings.RadioId.ToString();   // radio shows the ID as its name
        bool dmrTxAllowed = plug.Settings.RadioId != 0;
        if (!dmrTxAllowed)
            notes.Add("No DMR ID configured — every DMR channel is RX-only. " +
                      "Enable DMR transmit with: plugmatic config set dmr.id <your id>");

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
                    Name = AnalogName(profile, r, names),
                    RxFrequency = r.Output,
                    TxFrequency = r.Input,
                    Power = PowerLevel.High,
                    WideBandwidth = true,
                    TxTone = r.UplinkTone,
                    RxTone = RxToneFor(profile, r),
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
                foreach (var (tg, slot) in slots)
                {
                    var contactName = EnsureContact(plug, tg, notes);
                    if (contactName is null) continue;
                    var ch = new DigitalChannel
                    {
                        Name = DigitalName(profile, r, contactName, names),
                        RxFrequency = r.Output,
                        TxFrequency = r.Input,
                        TxPermit = dmrTxAllowed ? TxPermit.Allowed : TxPermit.Inhibited,
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
            // Two GMRS repeaters on the same channel with the same uplink tone are the same
            // channel as far as the radio is concerned — identical RX, TX and selector. The
            // list is distance-ordered, so the first one wins and the rest are noted. Without
            // this the validator rejects the whole plug for duplicate channels, which is what
            // RepeaterBook's Colorado GMRS data (several tone-less entries on R20/R21, some
            // with blank callsigns) actually produces.
            var gmrsSeen = new HashSet<(ulong Hz, string Tone)>();
            foreach (var r in gmrsRepeaters)
            {
                var main = GmrsMain.FirstOrDefault(m => Frequency.FromMHz(m.MHz).Hz == r.Output.Hz);
                if (main.Num == 0)
                {
                    notes.Add($"GMRS repeater {r.Callsign} output {r.Output} not a GMRS main channel; skipped.");
                    continue;
                }
                if (!gmrsSeen.Add((r.Output.Hz, r.UplinkTone.ToString())))
                {
                    notes.Add($"GMRS R{main.Num} {r.Callsign}: another repeater already uses that " +
                              "channel and tone; skipped as a duplicate.");
                    continue;
                }
                var ch = new AnalogChannel
                {
                    Name = Unique($"GMRS R{main.Num} {r.Callsign}".TrimEnd(), names, r),
                    RxFrequency = r.Output,
                    TxFrequency = r.Output + 5_000_000,        // +5.000 MHz repeater input [spec §6.3.3]
                    TxPermit = gmrs.TxEnabled ? TxPermit.Allowed : TxPermit.Inhibited,
                    Power = PowerLevel.High,
                    WideBandwidth = true,
                    TxTone = r.UplinkTone,
                    RxTone = RxToneFor(profile, r),
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
                    WideBandwidth = true,                      // NOAA deviation is wide-FM (user-verified on air)
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

        // ---- zone order: GMRS, places A-Z, NOAA last; channels A-Z within each ----
        int Rank(Zone z) =>
            z.Name.StartsWith("GMRS", StringComparison.Ordinal) ? 0
            : z.Name.StartsWith(CodeplugValidator.NoaaZoneName, StringComparison.Ordinal) ? 2
            : 1;
        plug.Zones.Sort((a, b) =>
        {
            int byRank = Rank(a).CompareTo(Rank(b));
            return byRank != 0 ? byRank
                 : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        // GMRS and NOAA keep their channels in service order (GMRS 1..22, WX1..WX7), which is
        // the order anyone reads them in; everywhere else alphabetical is what makes a mixed
        // analog/DMR zone navigable.
        foreach (var zone in plug.Zones.Where(z => Rank(z) == 1))
            zone.ChannelNames.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));

        BuildScanLists(plug, notes);

        if (plug.Channels.Count > profile.MaxChannels)
            notes.Add($"NOTE: {plug.Channels.Count} channels exceed profile maxChannels {profile.MaxChannels}; " +
                      "reduce radius or talkgroup fan-out (channels are distance-sorted, nothing was cut silently).");

        return new BuildResult(plug, notes);
    }

    /// <summary>
    /// One scan list per zone, mirroring the factory CPS convention: the current-channel
    /// marker first, then the zone's channels. The channel record's scan-list field is a
    /// 4-bit 1-based index, so only the first 15 lists are referencable — zones beyond
    /// that get no list (noted, not silent).
    /// </summary>
    private void BuildScanLists(Codeplug plug, List<string> notes)
    {
        int maxReferencable = caps.MaxScanLists;
        var listNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var zone in plug.Zones)
        {
            if (plug.ScanLists.Count >= maxReferencable)
            {
                notes.Add($"Zone '{zone.Name}' has no scan list (channel records can only reference {maxReferencable}).");
                continue;
            }
            var name = UniqueListName(zone.Name, listNames);
            var sl = new ScanList { Name = name };
            sl.ChannelNames.Add(ScanList.CurrentChannelMarker);
            foreach (var ch in zone.ChannelNames.Take(caps.MaxChannelsPerScanList - 1))
                sl.ChannelNames.Add(ch);
            if (zone.ChannelNames.Count > caps.MaxChannelsPerScanList - 1)
                notes.Add($"Scan list '{name}' covers the first {caps.MaxChannelsPerScanList - 1} of " +
                          $"{zone.ChannelNames.Count} channels in zone '{zone.Name}'.");
            plug.ScanLists.Add(sl);
            foreach (var chName in zone.ChannelNames)
                if (plug.FindChannel(chName) is { ScanListName: null } ch)
                    ch.ScanListName = name;
        }
    }

    private static string UniqueListName(string zoneName, HashSet<string> taken)
    {
        // Keep a " DMR" suffix visible when the 11-char budget would truncate it away
        // (zones "Fort Collins" / "Fort Collins DMR" must not collapse to the same list name).
        var name = zoneName.Length > 11 && zoneName.EndsWith(" DMR", StringComparison.Ordinal)
            ? Fit(zoneName[..^4], 7) + " DMR"
            : Fit(zoneName, 11);
        if (taken.Add(name)) return name;
        for (int i = 2; ; i++)
        {
            var candidate = Fit(zoneName, 11 - (i.ToString().Length + 1)) + "~" + i;
            if (taken.Add(candidate)) return candidate;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static GeneralSettings LoadSettingsFromConfig()
    {
        var cfg = ConfigStore.Load();
        var s = new GeneralSettings { Callsign = cfg.GetValueOrDefault("dmr.callsign") ?? "" };
        if (uint.TryParse(cfg.GetValueOrDefault("dmr.id"), out var dmrId)) s.RadioId = dmrId;
        return s;
    }

    private static List<(uint Tg, int Slot)> TalkgroupsFor(Repeater r, BuildProfile profile)
    {
        // Static talkgroups first (authoritative slots), then group profile talkgroups, with
        // the fan-out cap applied to those; private profile talkgroups (Parrot) are appended
        // AFTER the cap so every repeater keeps its TX/RX self-test channel (user requirement).
        var result = new List<(uint, int)>();
        foreach (var (tg, slot) in r.StaticTalkgroups)
            result.Add((tg, slot));
        foreach (var tg in profile.Talkgroups.Where(t => !t.Private))
            if (!result.Any(x => x.Item1 == tg.Id))
                result.Add((tg.Id, tg.Slot));
        var capped = result.Take(profile.MaxTalkgroupsPerRepeater).ToList();
        foreach (var tg in profile.Talkgroups.Where(t => t.Private))
            if (!capped.Any(x => x.Item1 == tg.Id))
                capped.Add((tg.Id, tg.Slot));
        return capped;
    }

    private static string? EnsureContact(Codeplug plug, uint tgId, List<string> notes)
    {
        var existing = plug.Contacts.FirstOrDefault(c => c.DmrId == tgId);
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

    /// <summary>
    /// RX tone squelch for one repeater. `downlink` uses the published downlink tone and
    /// falls back to carrier squelch where the source has none — never the uplink tone,
    /// which is a different tone on 35 of the 182 Colorado repeaters that publish both, and
    /// guessing it wrong makes the channel deaf. [profile §6.3.2]
    /// </summary>
    private static SelectiveCall RxToneFor(BuildProfile profile, Repeater r) =>
        profile.RxTone == RxTonePolicy.Downlink ? r.DownlinkTone : SelectiveCall.None;

    private Zone ZoneFor(Dictionary<string, Zone> zones, BuildProfile profile, Repeater r, bool digital)
    {
        var key = profile.ZoneStrategy switch
        {
            "by-repeater" => Fit(r.Callsign, 16),
            "by-network" => digital ? "DMR" : "Analog",
            // One zone per place, holding both its analog and its DMR channels. Splitting
            // them produced two entries per town in the zone selector — "Estes Park" and
            // "Estes Park DMR" — with the pair separated by the whole alphabet.
            _ => Fit(TitleCase(r.City ?? r.Callsign), 16),
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

    /// <summary>kHz fragment of the output frequency, e.g. 448.775 MHz -> "775".</summary>
    private static string KhzFrag(Repeater r) => (r.Output.Hz % 1_000_000 / 1000).ToString("D3");

    private static string AnalogName(BuildProfile profile, Repeater r, HashSet<string> taken)
    {
        switch (profile.NameStyle)
        {
            case ChannelNameStyle.Frequency:
                var khz = (r.Output.Hz / 1000 % 1000000).ToString("D6").TrimStart('0');
                var name = profile.NamingTemplate
                    .Replace("{call}", r.Callsign)
                    .Replace("{city}", TitleCase(r.City ?? ""))
                    .Replace("{mhz}", r.Output.MHz.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                    .Replace("{khz}", khz)
                    .Trim();
                return Unique(name, taken, r);
            default:   // Callsign: bare callsign; collisions disambiguated by kHz fragment
                return Unique(r.Callsign, taken, r);
        }
    }

    private static string DigitalName(BuildProfile profile, Repeater r, string contactName, HashSet<string> taken)
    {
        // "TG" is dropped from raw-talkgroup pseudo-names to save screen space.
        var tg = contactName.StartsWith("TG", StringComparison.Ordinal)
                 && contactName.Length > 2 && char.IsDigit(contactName[2])
            ? contactName[2..] : contactName;
        return profile.NameStyle switch
        {
            ChannelNameStyle.Frequency => Unique($"{KhzFrag(r)} {contactName}", taken, r),
            _ => Unique($"{r.Callsign} {tg}", taken, r),   // Callsign (default)
        };
    }

    /// <summary>Reserve a unique 16-char name: as-is, then with the kHz fragment, then ~n.</summary>
    private static string Unique(string name, HashSet<string> taken, Repeater? r = null)
    {
        name = Fit(name, 16);
        if (taken.Add(name)) return name;
        if (r is not null)
        {
            var frag = KhzFrag(r);
            var withFrag = Fit(name, 16 - frag.Length - 1) + " " + frag;
            if (taken.Add(withFrag)) return withFrag;
        }
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
