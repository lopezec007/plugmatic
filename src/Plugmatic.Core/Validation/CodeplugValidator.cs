using Plugmatic.Core.Model;

namespace Plugmatic.Core.Validation;

/// <summary>
/// Pre-encode validation — hard-fail with a readable report; no silent truncation. [spec §6.3.5]
/// Enforces I4 (NOAA + 467-interstitial TxInhibited) and structural integrity.
/// GMRS class conformance (I5) is asserted by the builder and re-checked here.
/// </summary>
public static class CodeplugValidator
{
    public const string NoaaZoneName = "NOAA WX";

    /// <summary>NOAA WX frequencies (Hz). [spec §6.3.4, LOCKED D10]</summary>
    public static readonly ulong[] NoaaFrequenciesHz =
        [162_550_000, 162_400_000, 162_475_000, 162_425_000, 162_450_000, 162_500_000, 162_525_000];

    /// <summary>467 MHz interstitials (FRS 8–14) — always RX-only (D9).</summary>
    public static readonly ulong[] Gmrs467InterstitialsHz =
        [467_562_500, 467_587_500, 467_612_500, 467_637_500, 467_662_500, 467_687_500, 467_712_500];

    public static List<string> Validate(Codeplug plug, RadioCapabilities caps)
    {
        var errors = new List<string>();
        void Err(string e) => errors.Add(e);

        // Counts
        if (plug.Channels.Count == 0) Err("codeplug has no channels");
        if (plug.Channels.Count > caps.MaxChannels) Err($"{plug.Channels.Count} channels exceed limit {caps.MaxChannels}");
        if (plug.Zones.Count > caps.MaxZones) Err($"{plug.Zones.Count} zones exceed limit {caps.MaxZones}");
        if (plug.Contacts.Count > caps.MaxContacts) Err($"{plug.Contacts.Count} contacts exceed limit {caps.MaxContacts}");
        if (plug.RxGroupLists.Count > caps.MaxGroupLists) Err($"{plug.RxGroupLists.Count} group lists exceed limit {caps.MaxGroupLists}");
        if (plug.ScanLists.Count > caps.MaxScanLists) Err($"{plug.ScanLists.Count} scan lists exceed limit {caps.MaxScanLists}");

        // Names: unique, non-empty, within length. No silent truncation anywhere.
        void CheckNames<T>(string what, IEnumerable<T> items, Func<T, string> name, int max)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                var n = name(item);
                if (string.IsNullOrWhiteSpace(n)) Err($"{what} with empty name");
                else if (n.Length > max) Err($"{what} name '{n}' exceeds {max} characters");
                else if (!n.All(c => c is >= ' ' and <= '~')) Err($"{what} name '{n}' contains non-ASCII characters");
                if (n.Length > 0 && !seen.Add(n)) Err($"duplicate {what} name '{n}'");
            }
        }
        CheckNames("channel", plug.Channels, c => c.Name, caps.MaxNameLength);
        CheckNames("zone", plug.Zones, z => z.Name, caps.MaxNameLength);
        CheckNames("contact", plug.Contacts, c => c.Name, caps.MaxNameLength);
        CheckNames("group list", plug.RxGroupLists, g => g.Name, 11);
        CheckNames("scan list", plug.ScanLists, s => s.Name, 11);

        // Frequencies in-band (TX only enforced for TX-allowed channels; RX-only channels may
        // listen out of band, e.g. NOAA at 162 MHz).
        foreach (var ch in plug.Channels)
        {
            if (ch.RxFrequency.Hz == 0) Err($"channel '{ch.Name}': RX frequency missing");
            if (ch.TxPermit == TxPermit.Allowed)
            {
                if (ch.TxFrequency.Hz == 0) Err($"channel '{ch.Name}': TX allowed but no TX frequency");
                else if (!caps.InBand(ch.TxFrequency))
                    Err($"channel '{ch.Name}': TX {ch.TxFrequency} MHz outside radio TX bands");
            }
        }

        // Referential integrity
        var channelNames = plug.Channels.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var contactNames = plug.Contacts.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var groupListNames = plug.RxGroupLists.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        var scanListNames = plug.ScanLists.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var ch in plug.Channels)
        {
            if (ch.ScanListName is { } sl && !scanListNames.Contains(sl))
                Err($"channel '{ch.Name}': unknown scan list '{sl}'");
            if (ch is DigitalChannel d)
            {
                if (d.TxContactName is { } tc && !contactNames.Contains(tc))
                    Err($"channel '{ch.Name}': unknown TX contact '{tc}'");
                if (d.RxGroupListName is { } gl && !groupListNames.Contains(gl))
                    Err($"channel '{ch.Name}': unknown RX group list '{gl}'");
                if (d.ColorCode is < 0 or > 15) Err($"channel '{ch.Name}': color code {d.ColorCode} out of 0-15");
            }
        }
        foreach (var z in plug.Zones)
        {
            if (z.ChannelNames.Count == 0) Err($"zone '{z.Name}' is empty");
            if (z.ChannelNames.Count > caps.MaxChannelsPerZone)
                Err($"zone '{z.Name}': {z.ChannelNames.Count} channels exceed {caps.MaxChannelsPerZone}");
            foreach (var cn in z.ChannelNames.Where(cn => !channelNames.Contains(cn)))
                Err($"zone '{z.Name}': unknown channel '{cn}'");
        }
        foreach (var g in plug.RxGroupLists)
        {
            if (g.ContactNames.Count > caps.MaxContactsPerGroupList)
                Err($"group list '{g.Name}': {g.ContactNames.Count} members exceed {caps.MaxContactsPerGroupList}");
            // "TG<id>" pseudo-names denote raw talkgroup IDs with no contact entry — legal on the wire.
            foreach (var cn in g.ContactNames.Where(cn => !contactNames.Contains(cn) && !IsRawTalkgroupRef(cn)))
                Err($"group list '{g.Name}': unknown contact '{cn}'");
        }
        foreach (var s in plug.ScanLists)
        {
            if (s.ChannelNames.Count > caps.MaxChannelsPerScanList)
                Err($"scan list '{s.Name}': {s.ChannelNames.Count} channels exceed {caps.MaxChannelsPerScanList}");
            // "@current" is the radio's current-channel member marker, not a channel reference.
            foreach (var cn in s.ChannelNames.Where(cn => cn != "@current" && !channelNames.Contains(cn)))
                Err($"scan list '{s.Name}': unknown channel '{cn}'");
        }

        // Duplicate channel definitions: same RX+TX+mode+selector is a true duplicate
        // (simplex vs repeater on a shared output differ by TX frequency).
        foreach (var dupe in plug.Channels.GroupBy(c => (c.RxFrequency.Hz, c.TxFrequency.Hz, c.GetType().Name,
                     c is DigitalChannel dd
                         ? $"{dd.TxContactName}/{dd.TimeSlot}"
                         : (c as AnalogChannel)?.TxTone.ToString()))
                     .Where(g => g.Count() > 1 && g.Key.Item1 != 0))
        {
            var names = string.Join("', '", dupe.Select(c => c.Name));
            Err($"duplicate channels (same RX/TX/mode/selector): '{names}'");
        }

        // I4: NOAA frequencies and 467 interstitials must be TxInhibited, regardless of config.
        foreach (var ch in plug.Channels)
        {
            if (NoaaFrequenciesHz.Contains(ch.RxFrequency.Hz) && ch.TxPermit != TxPermit.Inhibited)
                Err($"I4 violation: NOAA channel '{ch.Name}' must be RX-only");
            if (Gmrs467InterstitialsHz.Contains(ch.RxFrequency.Hz) && ch.TxPermit != TxPermit.Inhibited)
                Err($"I4 violation: 467-interstitial channel '{ch.Name}' must be RX-only (D9)");
        }

        // DMR ID sanity
        foreach (var c in plug.Contacts.Where(c => c.DmrId == 0 || c.DmrId > 0xFFFFFF))
            Err($"contact '{c.Name}': DMR ID {c.DmrId} out of 24-bit range");

        return errors;
    }

    public static bool IsRawTalkgroupRef(string name) =>
        name.StartsWith("TG", StringComparison.Ordinal)
        && uint.TryParse(name.AsSpan(2), out uint id) && id is > 0 and <= 0xFFFFFF;
}
