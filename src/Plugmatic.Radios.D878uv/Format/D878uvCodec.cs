using System.Buffers.Binary;
using Plugmatic.Core.Model;

namespace Plugmatic.Radios.D878uv.Format;

/// <summary>
/// AnyTone AT-D878UVII+ codec over the packed image of docs/formats/d878uv-format.md.
/// Decode-only for now: every field is still `verified: pending` in the format doc, and
/// nothing gets encoded onto a radio from unverified facts. Encode throws until the
/// hardware ladder confirms the layout.
/// </summary>
public sealed class D878uvCodec : IRadioCodec
{
    public static readonly D878uvCodec Instance = new();

    public RadioCapabilities Capabilities { get; } = new(
        Model: "d878uv",
        MaxChannels: Layout.MaxChannels,
        MaxZones: Layout.MaxZones,
        MaxChannelsPerZone: Layout.MaxChannelsPerZone,
        MaxContacts: Layout.MaxContacts,
        MaxGroupLists: Layout.MaxGroupLists,
        MaxContactsPerGroupList: 64,
        MaxScanLists: Layout.MaxScanLists,
        MaxChannelsPerScanList: 50,
        MaxNameLength: 16,
        TxBands:
        [
            new BandRange(Frequency.FromMHz(136), Frequency.FromMHz(174)),
            new BandRange(Frequency.FromMHz(400), Frequency.FromMHz(480)),
        ]);

    public byte[] Encode(Codeplug ir, ReadOnlyMemory<byte>? baseImage = null) =>
        throw new D878FormatException(
            "D878UV encoding is not implemented yet: every field in d878uv-format.md is still " +
            "'verified: pending'. Reading and archiving work; writing stays disabled until the " +
            "format doc is hardware-verified.");

    public Codeplug Decode(ReadOnlyMemory<byte> imageBytes)
    {
        var image = imageBytes.Span;
        if (image.Length != Layout.ImageSize)
            throw new D878FormatException(
                $"Image must be 0x{Layout.ImageSize:X} bytes (packed layout, format §1), got 0x{image.Length:X}.");

        var ir = new Codeplug();
        DecodeRadioIds(image, ir);
        var contactIndexToName = DecodeContacts(image, ir);
        // Scan and group lists first: channels reference them by index. [format §4]
        var scanListNames = DecodeScanLists(image, ir);
        var groupListNames = DecodeGroupLists(image, ir, contactIndexToName);
        DecodeChannels(image, ir, contactIndexToName, scanListNames, groupListNames);
        DecodeZones(image, ir);
        return ir;
    }

    private static void DecodeRadioIds(ReadOnlySpan<byte> image, Codeplug ir)
    {
        for (int i = 0; i < Layout.MaxRadioIds; i++)
        {
            if (!Layout.BitmapHas(image, Layout.RadioIdBitmap, i)) continue;
            var rec = image.Slice(Layout.RadioIdOffset(i), Layout.RadioIdSize);
            // [format §4, hw-verified] BCD DMR ID in bytes 0-3, name from byte 5.
            uint id = DecodeBcdId(rec);
            if (id == 0) continue;
            ir.Settings.RadioId = id;
            ir.Settings.Callsign = ReadName(rec[5..]);
            return;                                   // entry 0 is the radio's own ID
        }
    }

    private static Dictionary<uint, string> DecodeContacts(ReadOnlySpan<byte> image, Codeplug ir)
    {
        var byIndex = new Dictionary<uint, string>();
        for (int i = 0; i < Layout.MaxContacts; i++)
        {
            // Contact bitmap is INVERTED: a cleared bit means allocated. [format §4, hw-verified]
            if (Layout.BitmapHas(image, Layout.ContactBitmap, i)) continue;
            var (_, offset) = Layout.ContactSlot(i);
            var rec = image.Slice(offset, Layout.ContactRecordSize);
            // [format §4, hw-verified] type byte, 16-char name at 0x01, BCD DMR ID at 0x23.
            var name = ReadName(rec[1..17]);
            uint dmrId = DecodeBcdId(rec[0x23..]);
            if (name.Length == 0 && dmrId == 0) continue;
            var contact = new Contact
            {
                Name = name.Length > 0 ? name : $"TG{dmrId}",
                DmrId = dmrId,
                // hw: talkgroup contacts all carry type 1. 0/2 unconfirmed — **VERIFY**.
                Type = rec[0] switch { 0 => CallType.Private, 2 => CallType.All, _ => CallType.Group },
            };
            ir.Contacts.Add(contact);
            byIndex[(uint)i] = contact.Name;
        }
        return byIndex;
    }

    /// <summary>Scan list: name at 0x0F, u16 LE channel indices from 0x20. [format §4, hw-verified]</summary>
    private static Dictionary<int, string> DecodeScanLists(ReadOnlySpan<byte> image, Codeplug ir)
    {
        var names = new Dictionary<int, string>();
        for (int i = 0; i < Layout.MaxScanLists; i++)
        {
            if (!Layout.BitmapHas(image, Layout.ScanListBitmap, i)) continue;
            var (_, offset) = Layout.ScanListSlot(i);
            var rec = image.Slice(offset, Layout.ScanListSize);
            var name = ReadName(rec.Slice(0x0F, 16));
            var list = new ScanList { Name = name.Length > 0 ? name : $"Scan {i + 1}", RawRecord = rec.ToArray() };
            for (int n = 0x20; n + 1 < Layout.ScanListSize; n += 2)
            {
                ushort member = BinaryPrimitives.ReadUInt16LittleEndian(rec[n..]);
                if (member == 0xFFFF) break;
                list.ChannelNames.Add(ChannelRef(member));
            }
            names[i] = list.Name;
            ir.ScanLists.Add(list);
        }
        return names;
    }

    /// <summary>Group list: u32 LE contact indices from 0x00, name at 0x100. [format §4, hw-verified]</summary>
    private static Dictionary<int, string> DecodeGroupLists(
        ReadOnlySpan<byte> image, Codeplug ir, Dictionary<uint, string> contacts)
    {
        var names = new Dictionary<int, string>();
        for (int i = 0; i < Layout.MaxGroupLists; i++)
        {
            if (!Layout.BitmapHas(image, Layout.GroupListBitmap, i)) continue;
            var rec = image.Slice(Layout.GroupListOffset(i), Layout.GroupListSize);
            var name = ReadName(rec.Slice(0x100, 16));
            var list = new RxGroupList { Name = name.Length > 0 ? name : $"Group {i + 1}", RawRecord = rec.ToArray() };
            for (int n = 0; n < 0x100; n += 4)
            {
                uint member = BinaryPrimitives.ReadUInt32LittleEndian(rec[n..]);
                if (member == 0xFFFFFFFF) break;
                list.ContactNames.Add(contacts.TryGetValue(member, out var cn) ? cn : $"#{member}");
            }
            names[i] = list.Name;
            ir.RxGroupLists.Add(list);
        }
        return names;
    }

    /// <summary>Channel members are stored as 0-based indices; the IR references names.</summary>
    private static string ChannelRef(int index) => $"@{index}";

    private static void DecodeChannels(ReadOnlySpan<byte> image, Codeplug ir, Dictionary<uint, string> contacts,
        Dictionary<int, string> scanLists, Dictionary<int, string> groupLists)
    {
        var nameByIndex = new Dictionary<int, string>();
        for (int i = 0; i < Layout.MaxChannels; i++)
        {
            if (!Layout.BitmapHas(image, Layout.ChannelBitmap, i)) continue;
            var (_, offset) = Layout.ChannelSlot(i);
            var ch = DecodeChannel(image.Slice(offset, Layout.ChannelRecordSize), i, contacts);
            byte scanIdx = image[offset + 0x1B], groupIdx = image[offset + 0x1C];
            if (scanIdx != 0xFF && scanLists.TryGetValue(scanIdx, out var sl)) ch.ScanListName = sl;
            if (ch is DigitalChannel dch && groupIdx != 0xFF && groupLists.TryGetValue(groupIdx, out var gl))
                dch.RxGroupListName = gl;
            nameByIndex[i] = ch.Name;
            ir.Channels.Add(ch);
        }
        // Resolve the placeholder channel references now that every name is known.
        foreach (var list in ir.ScanLists)
            for (int n = 0; n < list.ChannelNames.Count; n++)
                if (list.ChannelNames[n].StartsWith('@')
                    && int.TryParse(list.ChannelNames[n][1..], out int idx)
                    && nameByIndex.TryGetValue(idx, out var chName))
                    list.ChannelNames[n] = chName;
    }

    private static Channel DecodeChannel(ReadOnlySpan<byte> rec, int index, Dictionary<uint, string> contacts)
    {
        // [format §3, hw-verified] BCD frequencies in 10 Hz units. 0x04 is a TX *offset*
        // whose sign comes from the repeater-mode bits; on simplex channels the field
        // holds the TX frequency instead and is ignored.
        var rx = DecodeBcdFrequency(rec);
        var offset = DecodeBcdFrequency(rec[4..]);
        var tx = (rec[0x08] >> 6 & 0x3) switch
        {
            1 => new Frequency(rx.Hz + offset.Hz),
            2 => new Frequency(rx.Hz >= offset.Hz ? rx.Hz - offset.Hz : 0),
            _ => rx,
        };

        bool digital = (rec[0x08] & 0x3) != 0;
        Channel ch = digital
            ? new DigitalChannel
            {
                ColorCode = rec[0x20],
                TimeSlot = (rec[0x21] & 1) != 0 ? TimeSlot.TS2 : TimeSlot.TS1,
                TxContactName = contacts.GetValueOrDefault(BinaryPrimitives.ReadUInt32LittleEndian(rec[0x14..])),
            }
            : new AnalogChannel
            {
                WideBandwidth = (rec[0x08] >> 4 & 0x3) != 0,
                // Signalling mode selects which tone field applies; without it, unused
                // CTCSS/DCS bytes decode as bogus tones. [format §3]
                TxTone = ToneCodec.Decode(rec[0x09] >> 2 & 0x3, rec[0x0A],
                                          BinaryPrimitives.ReadUInt16LittleEndian(rec[0x0C..])),
                RxTone = ToneCodec.Decode(rec[0x09] & 0x3, rec[0x0B],
                                          BinaryPrimitives.ReadUInt16LittleEndian(rec[0x0E..])),
            };

        ch.Name = ReadName(rec[0x23..]);
        if (ch.Name.Length == 0) ch.Name = $"CH{index + 1}";
        ch.RxFrequency = rx;
        ch.TxFrequency = tx;
        ch.TxPermit = (rec[0x09] >> 5 & 1) != 0 ? TxPermit.Inhibited : TxPermit.Allowed;
        ch.Power = (rec[0x08] >> 2 & 0x3) switch { 0 => PowerLevel.Low, 1 => PowerLevel.Medium, _ => PowerLevel.High };
        ch.RawRecord = rec.ToArray();
        return ch;
    }

    private static void DecodeZones(ReadOnlySpan<byte> image, Codeplug ir)
    {
        for (int i = 0; i < Layout.MaxZones; i++)
        {
            if (!Layout.BitmapHas(image, Layout.ZoneBitmap, i)) continue;
            var name = ReadName(image.Slice(Layout.ZoneNameOffset(i), Layout.ZoneNameSize));
            var zone = new Zone { Name = name.Length > 0 ? name : $"Zone {i + 1}" };
            var list = image.Slice(Layout.ZoneChannelsOffset(i), Layout.ZoneChannelListSize);
            for (int n = 0; n < Layout.MaxChannelsPerZone; n++)
            {
                ushort chIndex = BinaryPrimitives.ReadUInt16LittleEndian(list[(n * 2)..]);
                if (chIndex == 0xFFFF) break;
                if (chIndex < ir.Channels.Count) zone.ChannelNames.Add(ir.Channels[chIndex].Name);
            }
            if (zone.ChannelNames.Count > 0) ir.Zones.Add(zone);
        }
    }

    /// <summary>
    /// 8-digit BCD frequency, most-significant digit pair in the first byte, unit 10 Hz.
    /// `44 63 25 00` → 44632500 → 446.325 MHz. [format §3, hw-verified]
    /// </summary>
    internal static Frequency DecodeBcdFrequency(ReadOnlySpan<byte> field)
    {
        ulong value = 0;
        for (int i = 0; i < 4; i++)
        {
            int hi = field[i] >> 4, lo = field[i] & 0xF;
            if (hi > 9 || lo > 9) return new Frequency(0);      // filler (0xFF…) is not a frequency
            value = value * 100 + (ulong)(hi * 10 + lo);
        }
        return new Frequency(value * 10);
    }

    /// <summary>8-digit BCD DMR ID: `03 21 76 32` → 3217632. [format §4, hw-verified]</summary>
    internal static uint DecodeBcdId(ReadOnlySpan<byte> field)
    {
        uint value = 0;
        for (int i = 0; i < 4; i++)
        {
            int hi = field[i] >> 4, lo = field[i] & 0xF;
            if (hi > 9 || lo > 9) return 0;
            value = value * 100 + (uint)(hi * 10 + lo);
        }
        return value;
    }

    private static string ReadName(ReadOnlySpan<byte> field)
    {
        int end = 0;
        while (end < field.Length && field[end] is not 0x00 and not 0xFF) end++;
        return System.Text.Encoding.ASCII.GetString(field[..end]).TrimEnd();
    }

    public ImageComparison Compare(ReadOnlyMemory<byte> aBytes, ReadOnlyMemory<byte> bBytes)
    {
        var a = aBytes.Span;
        var b = bBytes.Span;
        if (a.Length != b.Length)
            return new ImageComparison(false, [$"sizes differ: 0x{a.Length:X} vs 0x{b.Length:X}"]);

        var diffs = new List<string>();
        int? runStart = null;
        for (int i = 0; i <= a.Length; i++)
        {
            bool differ = i < a.Length && a[i] != b[i];
            if (differ) runStart ??= i;
            else if (runStart is { } s)
            {
                diffs.Add($"0x{Layout.AddressOf(s):X8}..0x{Layout.AddressOf(i - 1):X8} ({i - s} bytes)");
                runStart = null;
                if (diffs.Count >= 200) { diffs.Add("… truncated"); break; }
            }
        }
        return diffs.Count == 0 ? ImageComparison.Same : new ImageComparison(false, diffs);
    }
}

/// <summary>CTCSS/DCS decoding for AnyTone channel records. [format §3]</summary>
internal static class ToneCodec
{
    /// <summary>Standard CTCSS table, tenths of Hz — index order used by the channel record.</summary>
    private static readonly int[] CtcssTenths =
    [
        670, 693, 719, 744, 770, 797, 825, 854, 885, 915, 948, 974, 1000, 1035, 1072, 1109,
        1148, 1188, 1230, 1273, 1318, 1365, 1413, 1462, 1514, 1567, 1598, 1622, 1655, 1679,
        1713, 1738, 1773, 1799, 1835, 1862, 1899, 1928, 1966, 1995, 2035, 2065, 2107, 2181,
        2257, 2291, 2336, 2418, 2503, 2541,
    ];

    /// <summary>
    /// signallingMode: 0 = none, 1 = CTCSS (index), 2 = DCS (code). Anything else is
    /// treated as none. **VERIFY** — no channel in the reference codeplug uses a tone,
    /// so the CTCSS index table and the DCS bit layout are still unconfirmed.
    /// </summary>
    public static SelectiveCall Decode(int signallingMode, byte ctcssIndex, ushort dcsCode) => signallingMode switch
    {
        1 when ctcssIndex < CtcssTenths.Length => SelectiveCall.Ctcss(CtcssTenths[ctcssIndex] / 10m),
        2 when dcsCode is not 0xFFFF => SelectiveCall.Parse(
            $"D{Convert.ToString(dcsCode & 0x01FF, 8).PadLeft(3, '0')}{((dcsCode & 0x8000) != 0 ? "I" : "N")}"),
        _ => SelectiveCall.None,
    };
}
