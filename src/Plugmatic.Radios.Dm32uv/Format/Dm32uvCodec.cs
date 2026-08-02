using Plugmatic.Core.Model;
using Plugmatic.Radios;
using static Plugmatic.Radios.Dm32uv.Format.BitOps;

namespace Plugmatic.Radios.Dm32uv.Format;

/// <summary>
/// Native DM-32UV codeplug codec. Pure functions over byte images; every offset cites
/// docs/formats/dm32uv-format.md ("[format §N]"). No I/O.
/// </summary>
public sealed class Dm32uvCodec : IRadioCodec
{
    public static readonly Dm32uvCodec Instance = new();

    public RadioCapabilities Capabilities { get; } = new(
        Model: "dm32uv",
        MaxChannels: Layout.MaxChannels,
        MaxZones: Layout.MaxZones,
        MaxChannelsPerZone: Layout.MaxChannelsPerZone,
        MaxContacts: Layout.MaxContacts,
        MaxGroupLists: Layout.MaxGroupLists,
        MaxContactsPerGroupList: Layout.MaxContactsPerGroupList,
        MaxScanLists: Layout.MaxScanLists,
        MaxChannelsPerScanList: Layout.MaxChannelsPerScanList,
        MaxNameLength: 16,
        TxBands:
        [
            new BandRange(Frequency.FromMHz(136), Frequency.FromMHz(174)),   // VHF [format: qdmr limits]
            new BandRange(Frequency.FromMHz(400), Frequency.FromMHz(480)),   // UHF
        ]);

    /// <summary>Volatile virtual ranges masked by Compare(); populated at ladder step 2. [format §14]</summary>
    public static readonly List<(uint Start, uint End)> VolatileRanges = [];

    // ---------------------------------------------------------------- Decode

    public Codeplug Decode(ReadOnlyMemory<byte> imageBytes)
    {
        var img = new Dm32Image(imageBytes.ToArray());
        var ir = new Codeplug();

        // Preserve every present block verbatim for passthrough / re-encode. [format §2]
        for (int b = 0; b < Dm32Image.BlockCount; b++)
            if (img.BlockPresent(b))
                ir.RawBlocks[(uint)(b * Dm32Image.BlockSize)] =
                    img.Bytes.AsSpan(b * Dm32Image.BlockSize, Dm32Image.BlockSize).ToArray();

        DecodeContacts(img, ir);
        DecodeGroupLists(img, ir);
        DecodeRadioIds(img, ir);
        DecodeScanLists(img, ir, out var scanChannelNumbers);
        DecodeChannels(img, ir);
        DecodeZones(img, ir);

        // Scan lists reference channels by 1-based number; resolve after channels exist. [format §8]
        for (int i = 0; i < ir.ScanLists.Count; i++)
            foreach (var num in scanChannelNumbers[i])
                if (num >= 1 && num <= ir.Channels.Count)
                    ir.ScanLists[i].ChannelNames.Add(ir.Channels[num - 1].Name);

        return ir;
    }

    private static void DecodeContacts(Dm32Image img, Codeplug ir)
    {
        if (!img.BlockPresent(Layout.ContactIndexBlock)) return;
        int count = GetU16(img.ReadBlock(Layout.ContactIndexBlock), 0x00);   // [format §6.2]
        for (int i = 0; i < count; i++)
        {
            var (block, off) = Layout.ContactSlot(i);
            if (!img.BlockPresent(block))
                throw new Dm32FormatException($"Contact index says {count} contacts but block 0x{block:X2} is absent.");
            var rec = img.ReadBlock(block).Slice(off, Layout.ContactRecordSize);
            ir.Contacts.Add(new Contact
            {
                Name = AsciiField.Read(rec.Slice(0x02, 16)),                 // [format §6.1]
                DmrId = GetU24(rec, 0x13),
                Type = rec[0x16] switch { 3 => CallType.Private, 5 => CallType.All, _ => CallType.Group },
                RawRecord = rec.ToArray(),
            });
        }
    }

    private static void DecodeGroupLists(Dm32Image img, Codeplug ir)
    {
        if (!img.BlockPresent(Layout.GroupListBlock)) return;
        var block = img.ReadBlock(Layout.GroupListBlock);
        uint bitmap = GetU32(block, Layout.GroupListBitmapOffset);           // [format §7]
        for (int i = 0; i < Layout.MaxGroupLists; i++)
        {
            if ((bitmap & 1u << i) == 0) continue;
            var rec = block.Slice(Layout.GroupListsOffset + i * Layout.GroupListRecordSize, Layout.GroupListRecordSize);
            var gl = new RxGroupList { Name = AsciiField.Read(rec[..0x0B]), RawRecord = rec.ToArray() };
            for (int n = 0; n < Layout.MaxContactsPerGroupList; n++)
            {
                uint id = GetU24(rec, 0x0B + n * 3);
                if (id == 0) continue;
                gl.ContactNames.Add(ResolveOrCreateGroupContact(ir, id));
            }
            ir.RxGroupLists.Add(gl);
        }
    }

    /// <summary>Group lists store DMR IDs, not indices; map to contact names, creating a contact if the radio lacks one.</summary>
    private static string ResolveOrCreateGroupContact(Codeplug ir, uint dmrId)
    {
        var existing = ir.Contacts.FirstOrDefault(c => c.DmrId == dmrId && c.Type == CallType.Group);
        if (existing is not null) return existing.Name;
        var created = new Contact { Name = $"TG{dmrId}", Type = CallType.Group, DmrId = dmrId };
        ir.Contacts.Add(created);
        return created.Name;
    }

    private static void DecodeRadioIds(Dm32Image img, Codeplug ir)
    {
        if (!img.BlockPresent(Layout.RadioIdBlock)) return;
        var block = img.ReadBlock(Layout.RadioIdBlock);
        int count = block[0x00];                                            // [format §10, C9]
        if (count < 1) return;
        var rec = block.Slice(Layout.RadioIdsOffset, Layout.RadioIdRecordSize);
        ir.Settings.RadioId = GetU24(rec, 0x00);
        ir.Settings.Callsign = AsciiField.Read(rec.Slice(0x03, 12));
    }

    private static void DecodeScanLists(Dm32Image img, Codeplug ir, out List<List<int>> channelNumbers)
    {
        channelNumbers = [];
        if (!img.BlockPresent(Layout.ScanListBlock)) return;
        var block = img.ReadBlock(Layout.ScanListBlock);
        int count = Math.Min((int)block[0x00], Layout.MaxScanLists);         // [format §8]
        for (int i = 0; i < count; i++)
        {
            var rec = block.Slice(Layout.ScanListsOffset + i * Layout.ScanListRecordSize, Layout.ScanListRecordSize);
            var sl = new ScanList { Name = AsciiField.Read(rec[..0x0B]), RawRecord = rec.ToArray() };
            var numbers = new List<int>();
            int chCount = Math.Min((int)rec[0x0B], Layout.MaxChannelsPerScanList);
            for (int n = 0; n < chCount; n++)
                numbers.Add(GetU16(rec, 0x18 + n * 2));                      // 0 = "current channel" marker
            ir.ScanLists.Add(sl);
            channelNumbers.Add(numbers.Where(x => x != 0).ToList());
        }
    }

    private static void DecodeChannels(Dm32Image img, Codeplug ir)
    {
        if (!img.BlockPresent(Layout.FirstChannelBlock)) return;
        int count = GetU16(img.ReadBlock(Layout.FirstChannelBlock), 0x00);   // [format §4]
        if (count > Layout.MaxChannels)
            throw new Dm32FormatException($"Channel count {count} exceeds radio maximum {Layout.MaxChannels}.");

        for (int i = 0; i < count; i++)
        {
            var (block, off) = Layout.ChannelSlot(i);
            if (!img.BlockPresent(block))
                throw new Dm32FormatException($"Channel {i + 1} expected in absent block 0x{block:X2}.");
            var rec = img.ReadBlock(block).Slice(off, Layout.ChannelRecordSize);
            ir.Channels.Add(DecodeChannelRecord(rec, i, img, ir));
        }
    }

    private static Channel DecodeChannelRecord(ReadOnlySpan<byte> rec, int index, Dm32Image img, Codeplug ir)
    {
        int type = GetBits(rec, 0x18, 4, 4);                                 // [format §4] 0/2 FM, 1/3 DMR
        bool digital = type is 1 or 3;

        Channel ch = digital
            ? new DigitalChannel
            {
                ColorCode = GetBits(rec, 0x1D, 0, 4),
                TimeSlot = GetBit(rec, 0x1D, 4) ? TimeSlot.TS2 : TimeSlot.TS1,
                Admit = (AdmitCriterion)Math.Min(GetBits(rec, 0x1A, 4, 2), 2),
                RxGroupListName = GetBits(rec, 0x1F, 0, 5) is int gl and > 0 && gl <= ir.RxGroupLists.Count
                    ? ir.RxGroupLists[gl - 1].Name : null,
                TxContactName = DecodeTxContact(img, ir, index),
            }
            : new AnalogChannel
            {
                WideBandwidth = GetBit(rec, 0x19, 7),
                RxTone = ToneCodec.Decode(GetU16(rec, 0x21)),
                TxTone = ToneCodec.Decode(GetU16(rec, 0x23)),
                Admit = (AdmitCriterion)Math.Min(GetBits(rec, 0x1A, 4, 2), 2),
            };

        ch.Name = AsciiField.Read(rec[..0x10]);
        ch.RxFrequency = Bcd.DecodeFrequency(GetU32(rec, 0x10));
        uint txWord = GetU32(rec, 0x14);
        // 0 Hz is the IR sentinel for "no TX frequency" (wire: 0xFFFFFFFF). [format §4]
        ch.TxFrequency = txWord == Bcd.NoTxFrequency ? new Frequency(0) : Bcd.DecodeFrequency(txWord);
        ch.TxPermit = GetBit(rec, 0x18, 3) ? TxPermit.Inhibited : TxPermit.Allowed;
        ch.Power = (PowerLevel)Math.Min(GetBits(rec, 0x18, 1, 2), 2);
        ch.SquelchLevel = GetBits(rec, 0x1C, 4, 4);
        int sli = GetBits(rec, 0x19, 2, 4);                                  // 1-based nibble
        if (sli > 0 && sli <= ir.ScanLists.Count) ch.ScanListName = ir.ScanLists[sli - 1].Name;
        ch.RawRecord = rec.ToArray();
        return ch;
    }

    private static string? DecodeTxContact(Dm32Image img, Codeplug ir, int channelIndex)
    {
        var (block, off) = Layout.ExtensionSlot(channelIndex);               // [format §5]
        if (!img.BlockPresent(block)) return null;
        var rec = img.ReadBlock(block).Slice(off, Layout.ExtensionRecordSize);
        int idx = GetBits(rec, 0, 0, 4) << 8 | rec[1];                       // 12-bit, 1-based
        if (idx == 0 || idx > ir.Contacts.Count) return null;
        return ir.Contacts[idx - 1].Name;
    }

    private static void DecodeZones(Dm32Image img, Codeplug ir)
    {
        if (!img.BlockPresent(Layout.FirstZoneBlock)) return;
        int count = img.ReadBlock(Layout.FirstZoneBlock)[0x00];              // [format §3]
        if (count > Layout.MaxZones)
            throw new Dm32FormatException($"Zone count {count} exceeds radio maximum {Layout.MaxZones}.");
        for (int i = 0; i < count; i++)
        {
            var (block, off) = Layout.ZoneSlot(i);
            if (!img.BlockPresent(block))
                throw new Dm32FormatException($"Zone {i + 1} expected in absent block 0x{block:X2}.");
            var rec = img.ReadBlock(block).Slice(off, Layout.ZoneRecordSize);
            var zone = new Zone { Name = AsciiField.Read(rec[..0x10]), RawRecord = rec.ToArray() };
            int chCount = Math.Min((int)rec[0x10], Layout.MaxChannelsPerZone);
            for (int n = 0; n < chCount; n++)
            {
                int num = GetU16(rec, 0x11 + n * 2);                         // 1-based, 0 = empty
                if (num >= 1 && num <= ir.Channels.Count)
                    zone.ChannelNames.Add(ir.Channels[num - 1].Name);
            }
            ir.Zones.Add(zone);
        }
    }

    // ---------------------------------------------------------------- Encode

    public byte[] Encode(Codeplug ir, ReadOnlyMemory<byte>? baseImage = null)
    {
        GuardLimits(ir);

        Dm32Image img;
        if (baseImage is { } b)
        {
            img = new Dm32Image(b.ToArray());
        }
        else
        {
            img = new Dm32Image();
            foreach (var (addr, block) in ir.RawBlocks)                      // passthrough canvas [format §2]
                block.CopyTo(img.Bytes.AsSpan((int)addr, Dm32Image.BlockSize));
        }

        var channelIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < ir.Channels.Count; i++) channelIndexByName.TryAdd(ir.Channels[i].Name, i);
        var contactIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < ir.Contacts.Count; i++) contactIndexByName.TryAdd(ir.Contacts[i].Name, i);

        EncodeContacts(img, ir);
        EncodeContactIndex(img, ir);
        EncodeGroupLists(img, ir, contactIndexByName);
        EncodeRadioIds(img, ir);
        EncodeScanLists(img, ir, channelIndexByName);
        EncodeChannels(img, ir, contactIndexByName);
        EncodeZones(img, ir, channelIndexByName);

        return img.Bytes;
    }

    private static void GuardLimits(Codeplug ir)
    {
        if (ir.Channels.Count > Layout.MaxChannels) throw new Dm32FormatException($"{ir.Channels.Count} channels > {Layout.MaxChannels}.");
        if (ir.Contacts.Count > Layout.MaxContacts) throw new Dm32FormatException($"{ir.Contacts.Count} contacts > {Layout.MaxContacts}.");
        if (ir.Zones.Count > Layout.MaxZones) throw new Dm32FormatException($"{ir.Zones.Count} zones > {Layout.MaxZones}.");
        if (ir.RxGroupLists.Count > Layout.MaxGroupLists) throw new Dm32FormatException($"{ir.RxGroupLists.Count} group lists > {Layout.MaxGroupLists}.");
        if (ir.ScanLists.Count > Layout.MaxScanLists) throw new Dm32FormatException($"{ir.ScanLists.Count} scan lists > {Layout.MaxScanLists}.");
    }

    /// <summary>Record slot writer: starts from RawRecord when available so unmodeled bits survive.</summary>
    private static Span<byte> PrepareSlot(Span<byte> slot, byte[]? raw)
    {
        if (raw is not null && raw.Length == slot.Length) raw.CopyTo(slot);
        else slot.Clear();
        return slot;
    }

    private static void EncodeContacts(Dm32Image img, Codeplug ir)
    {
        for (int blockIdx = 0; blockIdx < Layout.ContactBlockCount; blockIdx++)
        {
            int block = Layout.FirstContactBlock + blockIdx;
            bool needed = ir.Contacts.Count > blockIdx * Layout.ContactsPerBlock;
            if (!needed && !img.BlockPresent(block)) continue;               // never allocate empty banks
            var span = img.Block(block);
            span.Clear();
        }
        for (int i = 0; i < ir.Contacts.Count; i++)
        {
            var (block, off) = Layout.ContactSlot(i);
            var rec = PrepareSlot(img.Block(block).Slice(off, Layout.ContactRecordSize), ir.Contacts[i].RawRecord);
            SetNameIfChanged(rec.Slice(0x02, 16), ir.Contacts[i].Name);      // [format §6.1]
            SetU24(rec, 0x13, ir.Contacts[i].DmrId);
            rec[0x16] = ir.Contacts[i].Type switch { CallType.Private => 3, CallType.All => 5, _ => 4 };
        }
    }

    private static void EncodeContactIndex(Dm32Image img, Codeplug ir)
    {
        if (ir.Contacts.Count == 0 && !img.BlockPresent(Layout.ContactIndexBlock)) return;
        var block = img.Block(Layout.ContactIndexBlock);
        block.Clear();                                                       // rebuilt wholesale [format §6.2]
        SetU16(block, 0x00, (ushort)ir.Contacts.Count);
        SetU16(block, 0x02, (ushort)ir.Contacts.Count(c => c.Type == CallType.Group));
        SetU16(block, 0x04, (ushort)ir.Contacts.Count(c => c.Type == CallType.Private));

        var bitmap = block.Slice(Layout.ContactIndexBitmapOffset, Layout.ContactIndexBitmapSize);
        bitmap.Fill(0xFF);                                                   // inverted polarity: cleared bit = allocated
        for (int i = 0; i < ir.Contacts.Count; i++)
            bitmap[i / 8] &= (byte)~(1 << i % 8);

        static ushort Entry(Contact c, int slot) => (ushort)(
            (c.Type switch { CallType.Private => 3, CallType.All => 5, _ => 4 }) << 12 | slot + 1 & 0x0FFF);

        var table = block[Layout.ContactIndexTableOffset..];
        var sorted = block[Layout.ContactIndexSortedOffset..];
        var order = Enumerable.Range(0, ir.Contacts.Count).OrderBy(i => ir.Contacts[i].DmrId).ToArray();
        for (int i = 0; i < ir.Contacts.Count; i++)
        {
            SetU16(table, i * 2, Entry(ir.Contacts[i], i));
            SetU16(sorted, i * 2, Entry(ir.Contacts[order[i]], order[i]));
        }
    }

    private static void EncodeGroupLists(Dm32Image img, Codeplug ir, Dictionary<string, int> contactIndexByName)
    {
        if (ir.RxGroupLists.Count == 0 && !img.BlockPresent(Layout.GroupListBlock)) return;
        var block = img.Block(Layout.GroupListBlock);
        block.Clear();
        uint bitmap = 0;
        for (int i = 0; i < ir.RxGroupLists.Count; i++)
        {
            bitmap |= 1u << i;
            var gl = ir.RxGroupLists[i];
            var rec = PrepareSlot(block.Slice(Layout.GroupListsOffset + i * Layout.GroupListRecordSize,
                                              Layout.GroupListRecordSize), gl.RawRecord);
            SetNameIfChanged(rec[..0x0B], gl.Name);
            for (int n = 0; n < Layout.MaxContactsPerGroupList; n++)
            {
                uint id = 0;
                if (n < gl.ContactNames.Count && contactIndexByName.TryGetValue(gl.ContactNames[n], out int ci))
                    id = ir.Contacts[ci].DmrId;                              // stores DMR IDs [format §7]
                SetU24(rec, 0x0B + n * 3, id);
            }
        }
        SetU32(block, Layout.GroupListBitmapOffset, bitmap);
    }

    private static void EncodeRadioIds(Dm32Image img, Codeplug ir)
    {
        if (ir.Settings.RadioId == 0 && !img.BlockPresent(Layout.RadioIdBlock)) return;
        var block = img.Block(Layout.RadioIdBlock);                          // [format §10]
        if (block[0x00] < 1) block[0x00] = 1;                                // count; extra entries preserved from canvas
        var rec = block.Slice(Layout.RadioIdsOffset, Layout.RadioIdRecordSize);
        SetU24(rec, 0x00, ir.Settings.RadioId);
        SetNameIfChanged(rec.Slice(0x03, 12), ir.Settings.Callsign);
    }

    private static void EncodeScanLists(Dm32Image img, Codeplug ir, Dictionary<string, int> channelIndexByName)
    {
        if (ir.ScanLists.Count == 0 && !img.BlockPresent(Layout.ScanListBlock)) return;
        var block = img.Block(Layout.ScanListBlock);
        // entry area rewritten; bank tail (mode/ranges @0xE00) preserved from canvas [format §8, C7]
        block.Slice(Layout.ScanListsOffset, Layout.MaxScanLists * Layout.ScanListRecordSize).Clear();
        block[0x00] = (byte)ir.ScanLists.Count;
        for (int i = 0; i < ir.ScanLists.Count; i++)
        {
            var sl = ir.ScanLists[i];
            var rec = PrepareSlot(block.Slice(Layout.ScanListsOffset + i * Layout.ScanListRecordSize,
                                              Layout.ScanListRecordSize), sl.RawRecord);
            SetNameIfChanged(rec[..0x0B], sl.Name);
            var members = sl.ChannelNames.Where(channelIndexByName.ContainsKey)
                                         .Take(Layout.MaxChannelsPerScanList).ToList();
            rec[0x0B] = (byte)members.Count;
            for (int n = 0; n < Layout.MaxChannelsPerScanList; n++)
                SetU16(rec, 0x18 + n * 2,
                    (ushort)(n < members.Count ? channelIndexByName[members[n]] + 1 : 0));
        }
    }

    private static void EncodeChannels(Dm32Image img, Codeplug ir, Dictionary<string, int> contactIndexByName)
    {
        int neededChannelBlocks = ir.Channels.Count <= Layout.ChannelsInBank0
            ? 1
            : 1 + (ir.Channels.Count - Layout.ChannelsInBank0 + Layout.ChannelsPerBank - 1) / Layout.ChannelsPerBank;

        for (int bi = 0; bi < Layout.ChannelBlockCount; bi++)
        {
            int block = Layout.FirstChannelBlock + bi;
            if (bi >= neededChannelBlocks && !img.BlockPresent(block)) continue;
            img.Block(block).Clear();
        }
        for (int bi = 0; bi < Layout.ExtensionBlockCount; bi++)
        {
            int block = Layout.FirstExtensionBlock + bi;
            bool needed = ir.Channels.Count > bi * Layout.ExtensionsPerBlock;
            if (!needed && !img.BlockPresent(block)) continue;
            img.Block(block).Clear();
        }

        SetU16(img.Block(Layout.FirstChannelBlock), 0x00, (ushort)ir.Channels.Count);   // [format §4]

        var scanIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < ir.ScanLists.Count; i++) scanIndexByName.TryAdd(ir.ScanLists[i].Name, i);

        for (int i = 0; i < ir.Channels.Count; i++)
        {
            var ch = ir.Channels[i];
            var (block, off) = Layout.ChannelSlot(i);
            var rec = PrepareSlot(img.Block(block).Slice(off, Layout.ChannelRecordSize), ch.RawRecord);
            EncodeChannelRecord(rec, ch, scanIndexByName, ir);

            if (ch is DigitalChannel d)
            {
                var (eb, eo) = Layout.ExtensionSlot(i);                      // [format §5]
                var ext = img.Block(eb).Slice(eo, Layout.ExtensionRecordSize);
                int idx = d.TxContactName is not null && contactIndexByName.TryGetValue(d.TxContactName, out int ci)
                    ? ci + 1 : 0;
                SetBits(ext, 0, 0, 4, idx >> 8);
                ext[1] = (byte)idx;
            }
        }
    }

    private static void EncodeChannelRecord(Span<byte> rec, Channel ch, Dictionary<string, int> scanIndexByName, Codeplug ir)
    {
        SetNameIfChanged(rec[..0x10], ch.Name);
        SetU32(rec, 0x10, Bcd.EncodeFrequency(ch.RxFrequency));
        SetU32(rec, 0x14, ch.TxFrequency.Hz == 0 ? Bcd.NoTxFrequency : Bcd.EncodeFrequency(ch.TxFrequency));

        bool digital = ch is DigitalChannel;
        SetBits(rec, 0x18, 4, 4, digital ? 1 : 0);                           // type [format §4]
        SetBit(rec, 0x18, 3, ch.TxPermit == TxPermit.Inhibited);             // RX-only — the D-mapping
        SetBits(rec, 0x18, 1, 2, (int)ch.Power);
        SetBits(rec, 0x1C, 4, 4, Math.Clamp(ch.SquelchLevel, 0, 15));

        int sli = 0;
        if (ch.ScanListName is not null && scanIndexByName.TryGetValue(ch.ScanListName, out int si)
            && si <= Layout.MaxChannelReferencableScanList)
            sli = si + 1;
        SetBits(rec, 0x19, 2, 4, sli);

        switch (ch)
        {
            case AnalogChannel a:
                SetBit(rec, 0x19, 7, a.WideBandwidth);
                SetBits(rec, 0x1A, 4, 2, (int)a.Admit);
                SetU16(rec, 0x21, ToneCodec.Encode(a.RxTone));
                SetU16(rec, 0x23, ToneCodec.Encode(a.TxTone));
                break;
            case DigitalChannel d:
                SetBits(rec, 0x1A, 4, 2, (int)d.Admit);
                SetBit(rec, 0x1D, 4, d.TimeSlot == TimeSlot.TS2);            // [format §4, C3]
                SetBits(rec, 0x1D, 0, 4, Math.Clamp(d.ColorCode, 0, 15));
                int gli = 0;
                if (d.RxGroupListName is not null)
                {
                    int idx = ir.RxGroupLists.FindIndex(g => g.Name == d.RxGroupListName);
                    if (idx >= 0 && idx < 31) gli = idx + 1;
                }
                SetBits(rec, 0x1F, 0, 5, gli);
                break;
        }
    }

    private static void EncodeZones(Dm32Image img, Codeplug ir, Dictionary<string, int> channelIndexByName)
    {
        int neededBanks = ir.Zones.Count == 0 ? 1 : (ir.Zones.Count + Layout.ZonesPerBank - 1) / Layout.ZonesPerBank;
        if (ir.Zones.Count == 0 && !img.BlockPresent(Layout.FirstZoneBlock)) return;

        for (int bi = 0; bi < Layout.ZoneBlockCount; bi++)
        {
            int block = Layout.FirstZoneBlock + bi;
            if (bi >= neededBanks && !img.BlockPresent(block)) continue;
            var span = img.Block(block);
            // Bank 0 header carries VFO state at 0x01-0x08 — preserve it, rewrite records area. [format §3]
            if (bi == 0) span[Layout.ZoneBankHeader..].Clear();
            else span.Clear();
        }

        var bank0 = img.Block(Layout.FirstZoneBlock);
        bank0[0x00] = (byte)ir.Zones.Count;

        for (int i = 0; i < ir.Zones.Count; i++)
        {
            var zone = ir.Zones[i];
            var (block, off) = Layout.ZoneSlot(i);
            var rec = PrepareSlot(img.Block(block).Slice(off, Layout.ZoneRecordSize), zone.RawRecord);
            SetNameIfChanged(rec[..0x10], zone.Name);
            var members = zone.ChannelNames.Where(channelIndexByName.ContainsKey)
                                           .Take(Layout.MaxChannelsPerZone).ToList();
            rec[0x10] = (byte)members.Count;
            for (int n = 0; n < Layout.MaxChannelsPerZone; n++)
                SetU16(rec, 0x11 + n * 2,
                    (ushort)(n < members.Count ? channelIndexByName[members[n]] + 1 : 0));
        }
    }

    // ---------------------------------------------------------------- Compare

    public ImageComparison Compare(ReadOnlyMemory<byte> aBytes, ReadOnlyMemory<byte> bBytes)
    {
        var a = new Dm32Image(aBytes.ToArray());
        var b = new Dm32Image(bBytes.ToArray());
        var diffs = new List<string>();

        for (int block = 0; block < Dm32Image.BlockCount; block++)
        {
            bool pa = a.BlockPresent(block), pb = b.BlockPresent(block);
            if (!pa && !pb) continue;
            if (pa != pb)
            {
                diffs.Add($"block 0x{block:X2}: present in {(pa ? "A only" : "B only")}");
                continue;
            }
            var sa = a.Bytes.AsSpan(block * Dm32Image.BlockSize, Dm32Image.BlockSize);
            var sb = b.Bytes.AsSpan(block * Dm32Image.BlockSize, Dm32Image.BlockSize);
            int? runStart = null;
            for (int i = 0; i <= Dm32Image.BlockSize; i++)
            {
                bool differ = i < Dm32Image.BlockSize && sa[i] != sb[i]
                              && !IsMasked((uint)(block * Dm32Image.BlockSize + i));
                if (differ) runStart ??= i;
                else if (runStart is { } s)
                {
                    diffs.Add($"0x{block * Dm32Image.BlockSize + s:X5}..0x{block * Dm32Image.BlockSize + i - 1:X5}"
                              + $" ({i - s} bytes)");
                    runStart = null;
                }
            }
        }
        return diffs.Count == 0 ? ImageComparison.Same : new ImageComparison(false, diffs);
    }

    private static bool IsMasked(uint virtAddr) =>
        VolatileRanges.Any(r => virtAddr >= r.Start && virtAddr < r.End);
}
