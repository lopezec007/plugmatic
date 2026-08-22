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

    /// <summary>
    /// Encodes over a base image (the same-run pre-write read, which D7 always provides).
    /// Records are written **only where a field's value actually differs** from what the
    /// base already holds, so lossy encodings — power levels, the offset field that
    /// simplex channels ignore — survive untouched and `Encode(Decode(img), img) == img`.
    /// </summary>
    public byte[] Encode(Codeplug ir, ReadOnlyMemory<byte>? baseImage = null)
    {
        if (baseImage is not { } b)
            throw new D878FormatException(
                "D878UV encoding requires a base image: the codeplug is a sparse region map with " +
                "many undecoded areas that must be carried forward. The hardware write path always " +
                "supplies the same-run pre-write read (D7).");
        var image = b.ToArray();
        if (image.Length != Layout.ImageSize)
            throw new D878FormatException(
                $"Base image must be 0x{Layout.ImageSize:X} bytes, got 0x{image.Length:X}.");

        var span = image.AsSpan();
        var contactIndex = EncodeContacts(span, ir);
        var channelIndex = AssignChannelSlots(span, ir);
        var scanIndex = EncodeScanLists(span, ir, channelIndex);
        var groupIndex = EncodeGroupLists(span, ir, contactIndex);
        EncodeChannels(span, ir, channelIndex, contactIndex, scanIndex, groupIndex);
        EncodeZones(span, ir, channelIndex);
        EncodeRadioId(span, ir);
        return image;
    }

    // ---------------------------------------------------------------- slot allocation

    private static bool IsAllocated(ReadOnlySpan<byte> image, uint bitmap, int index, bool inverted) =>
        Layout.BitmapHas(image, bitmap, index) != inverted;

    private static void SetAllocated(Span<byte> image, uint bitmap, int index, bool allocated, bool inverted)
    {
        int at = Layout.OffsetOf(bitmap) + index / 8;
        int mask = 1 << (index % 8);
        bool bit = allocated != inverted;                 // inverted tables store a cleared bit
        image[at] = (byte)(bit ? image[at] | mask : image[at] & ~mask);
    }

    /// <summary>
    /// Maps IR items onto record slots: reuse the base image's allocated slots in ascending
    /// order first (so a decode/encode round trip is the identity), then take free slots, then
    /// release any left over. Returns the slot for each IR item.
    /// </summary>
    private static int[] AssignSlots(Span<byte> image, uint bitmap, int maxItems, int needed, bool inverted)
    {
        if (needed > maxItems)
            throw new D878FormatException($"{needed} items exceed the radio's limit of {maxItems}.");
        var allocated = new List<int>();
        for (int i = 0; i < maxItems; i++)
            if (IsAllocated(image, bitmap, i, inverted)) allocated.Add(i);

        var slots = new List<int>(allocated.Take(needed));
        var used = new HashSet<int>(slots);
        for (int i = 0; slots.Count < needed && i < maxItems; i++)
            if (!IsAllocated(image, bitmap, i, inverted)) { slots.Add(i); used.Add(i); }

        foreach (var slot in slots) SetAllocated(image, bitmap, slot, true, inverted);
        foreach (var slot in allocated) if (!used.Contains(slot)) SetAllocated(image, bitmap, slot, false, inverted);

        // Ascending, and this is load-bearing. Slots are chosen by reusing what the base image
        // already had allocated and then filling the gaps, which produces a set like
        // [0..51, 100..131, 52..99, ...]. Decode enumerates the bitmap in index order, so
        // handing back the selection order would silently reorder every item past the first
        // gap — item 53 encoded into slot 100 and read back as whatever sits in slot 52.
        // Caught by the I3 round-trip gate when the first plug larger than the radio's
        // existing 84 channels was built. [format §4]
        slots.Sort();
        return [.. slots];
    }

    private static int[] AssignChannelSlots(Span<byte> image, Codeplug ir) =>
        AssignSlots(image, Layout.ChannelBitmap, Layout.MaxChannels, ir.Channels.Count, inverted: false);

    // ---------------------------------------------------------------- table encoders

    private static Dictionary<string, int> EncodeContacts(Span<byte> image, Codeplug ir)
    {
        // Contact bitmap is inverted (cleared bit = allocated). [format §4, hw-verified]
        var slots = AssignSlots(image, Layout.ContactBitmap, Layout.MaxContacts, ir.Contacts.Count, inverted: true);
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < ir.Contacts.Count; i++)
        {
            var contact = ir.Contacts[i];
            var (_, offset) = Layout.ContactSlot(slots[i]);
            var rec = image.Slice(offset, Layout.ContactRecordSize);
            byte type = contact.Type switch { CallType.Private => 0, CallType.All => 2, _ => 1 };
            if (rec[0] != type) rec[0] = type;
            WriteNameIfChanged(rec.Slice(1, 16), contact.Name);
            if (DecodeBcdId(rec[0x23..]) != contact.DmrId) EncodeBcdId(rec[0x23..], contact.DmrId);
            byName.TryAdd(contact.Name, slots[i]);
        }
        return byName;
    }

    private static Dictionary<string, int> EncodeScanLists(Span<byte> image, Codeplug ir, int[] channelSlots)
    {
        var slots = AssignSlots(image, Layout.ScanListBitmap, Layout.MaxScanLists, ir.ScanLists.Count, inverted: false);
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        var channelSlotByName = ChannelSlotByName(ir, channelSlots);
        for (int i = 0; i < ir.ScanLists.Count; i++)
        {
            var list = ir.ScanLists[i];
            var (_, offset) = Layout.ScanListSlot(slots[i]);
            var rec = image.Slice(offset, Layout.ScanListSize);
            WriteNameIfChanged(rec.Slice(0x0F, 16), list.Name);
            int at = 0x20;
            foreach (var member in list.ChannelNames)
            {
                if (!channelSlotByName.TryGetValue(member, out int ch)) continue;
                if (at + 1 >= Layout.ScanListSize) break;
                SetU16IfChanged(rec, at, (ushort)ch);
                at += 2;
            }
            if (at + 1 < Layout.ScanListSize) SetU16IfChanged(rec, at, 0xFFFF);   // terminator; tail untouched
            byName.TryAdd(list.Name, slots[i]);
        }
        return byName;
    }

    private static Dictionary<string, int> EncodeGroupLists(Span<byte> image, Codeplug ir, Dictionary<string, int> contacts)
    {
        var slots = AssignSlots(image, Layout.GroupListBitmap, Layout.MaxGroupLists, ir.RxGroupLists.Count, inverted: false);
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < ir.RxGroupLists.Count; i++)
        {
            var list = ir.RxGroupLists[i];
            var rec = image.Slice(Layout.GroupListOffset(slots[i]), Layout.GroupListSize);
            WriteNameIfChanged(rec.Slice(0x100, 16), list.Name);
            int at = 0;
            foreach (var member in list.ContactNames)
            {
                if (!contacts.TryGetValue(member, out int ci)) continue;
                if (at + 4 > 0x100) break;
                SetU32IfChanged(rec, at, (uint)ci);
                at += 4;
            }
            if (at + 4 <= 0x100) SetU32IfChanged(rec, at, 0xFFFFFFFF);
            byName.TryAdd(list.Name, slots[i]);
        }
        return byName;
    }

    /// <summary>
    /// True when a channel record is erased flash rather than a channel.
    ///
    /// Only the scan-list and group-list indices legitimately hold 0xFF — their "none"
    /// sentinel. Every other byte of every one of the 84 records in this radio's own
    /// codeplug is something else, so 0xFF outside those two positions means the slot has
    /// never been written. [format §3, hw-verified 2026-08-22]
    /// </summary>
    internal static bool IsUninitialisedRecord(ReadOnlySpan<byte> rec)
    {
        for (int at = 0; at < rec.Length; at++)
            if (rec[at] == 0xFF && at is not (0x1B or 0x1C)) return true;
        return false;
    }

    private static void EncodeChannels(Span<byte> image, Codeplug ir, int[] slots,
        Dictionary<string, int> contacts, Dictionary<string, int> scanLists, Dictionary<string, int> groupLists)
    {
        for (int i = 0; i < ir.Channels.Count; i++)
        {
            var ch = ir.Channels[i];
            var (_, offset) = Layout.ChannelSlot(slots[i]);
            var rec = image.Slice(offset, Layout.ChannelRecordSize);

            // A slot that has never held a channel is erased flash, not a record: every field
            // this codec does not model reads 0xFF, including flags the radio acts on. That is
            // how generated channels ended up starting a scan the moment they were selected —
            // the whole reserved area was 0xFF. Reset to the radio's own defaults first, so
            // what the IR does not set is what the radio itself would have written.
            if (IsUninitialisedRecord(rec))
            {
                rec.Clear();
                rec[0x1B] = 0xFF;                    // scan list: none
                rec[0x1C] = 0xFF;                    // RX group list: none
            }

            if (DecodeBcdFrequency(rec).Hz != ch.RxFrequency.Hz) EncodeBcdFrequency(rec, ch.RxFrequency);

            // TX is stored as a magnitude plus a repeater-mode selector; leave both alone when
            // the pair already yields the right frequency (simplex records keep their filler).
            int mode = rec[0x08] >> 6 & 0x3;
            var current = mode switch
            {
                1 => new Frequency(DecodeBcdFrequency(rec).Hz + DecodeBcdFrequency(rec[4..]).Hz),
                2 => new Frequency(DecodeBcdFrequency(rec).Hz - Math.Min(DecodeBcdFrequency(rec[4..]).Hz, DecodeBcdFrequency(rec).Hz)),
                _ => DecodeBcdFrequency(rec),
            };
            if (current.Hz != ch.TxFrequency.Hz)
            {
                ulong rx = ch.RxFrequency.Hz, tx = ch.TxFrequency.Hz;
                int newMode = tx == rx ? 0 : tx > rx ? 1 : 2;
                EncodeBcdFrequency(rec[4..], new Frequency(tx == rx ? tx : tx > rx ? tx - rx : rx - tx));
                rec[0x08] = (byte)(rec[0x08] & 0x3F | newMode << 6);
            }

            // These three fields decode many-to-one (modes 1-3 are all "digital", powers 2-3
            // are both "high"). Compare the DECODED value and leave the record alone when it
            // already means the right thing, or a round trip silently rewrites the radio's
            // choice — e.g. turning every mode-3 channel into mode 1.
            bool digitalNow = (rec[0x08] & 0x3) != 0;
            if (digitalNow != ch is DigitalChannel)
                SetBitsIfChanged(rec, 0x08, 0, 2, ch is DigitalChannel ? 1 : 0);

            var powerNow = (rec[0x08] >> 2 & 0x3) switch
            {
                0 => PowerLevel.Low,
                1 => PowerLevel.Medium,
                _ => PowerLevel.High,
            };
            if (powerNow != ch.Power)
                SetBitsIfChanged(rec, 0x08, 2, 2, ch.Power switch
                {
                    PowerLevel.Low => 0, PowerLevel.Medium => 1, _ => 2,
                });

            SetBitIfChanged(rec, 0x09, 5, ch.TxPermit == TxPermit.Inhibited);
            if (ch is AnalogChannel analog)
            {
                if ((rec[0x08] >> 4 & 0x3) != 0 != analog.WideBandwidth)
                    SetBitsIfChanged(rec, 0x08, 4, 2, analog.WideBandwidth ? 1 : 0);

                // 0x09 bits 0-1 select the RX signalling mode and bits 2-3 the TX one; the
                // CTCSS index and DCS code live in separate fields per direction and are only
                // read when their mode selects them. Bit 5 of 0x09 is TX inhibit, set above,
                // so only the mode bits are touched here. [format §3]
                // Compare decoded-to-decoded, not byte-to-byte. A record whose mode bits say
                // "none" can still carry stale CTCSS/DCS bytes that Decode ignores; rewriting
                // them would churn bytes the radio does not read and break the byte-exact
                // round trip over a factory image.
                var currentTx = ToneCodec.Decode(rec[0x09] >> 2 & 0x3, rec[0x0A],
                                                 BinaryPrimitives.ReadUInt16LittleEndian(rec[0x0C..]));
                if (currentTx != analog.TxTone)
                {
                    var (txMode, txCtcss, txDcs) = ToneCodec.Encode(analog.TxTone);
                    SetBitsIfChanged(rec, 0x09, 2, 2, txMode);
                    if (rec[0x0A] != txCtcss) rec[0x0A] = txCtcss;
                    SetU16IfChanged(rec, 0x0C, txDcs);
                }

                var currentRx = ToneCodec.Decode(rec[0x09] & 0x3, rec[0x0B],
                                                 BinaryPrimitives.ReadUInt16LittleEndian(rec[0x0E..]));
                if (currentRx != analog.RxTone)
                {
                    var (rxMode, rxCtcss, rxDcs) = ToneCodec.Encode(analog.RxTone);
                    SetBitsIfChanged(rec, 0x09, 0, 2, rxMode);
                    if (rec[0x0B] != rxCtcss) rec[0x0B] = rxCtcss;
                    SetU16IfChanged(rec, 0x0E, rxDcs);
                }
            }
            if (ch is DigitalChannel digital)
            {
                if (rec[0x20] != digital.ColorCode) rec[0x20] = (byte)digital.ColorCode;
                SetBitIfChanged(rec, 0x21, 0, digital.TimeSlot == TimeSlot.TS2);
                uint contactIdx = digital.TxContactName is { } tc && contacts.TryGetValue(tc, out int ci)
                    ? (uint)ci : BinaryPrimitives.ReadUInt32LittleEndian(rec[0x14..]);
                SetU32IfChanged(rec, 0x14, contactIdx);
                byte gl = digital.RxGroupListName is { } g && groupLists.TryGetValue(g, out int gi)
                    ? (byte)gi : (byte)0xFF;
                if (rec[0x1C] != gl) rec[0x1C] = gl;
            }
            byte sl = ch.ScanListName is { } s && scanLists.TryGetValue(s, out int si) ? (byte)si : (byte)0xFF;
            if (rec[0x1B] != sl) rec[0x1B] = sl;
            WriteNameIfChanged(rec.Slice(0x23, 16), ch.Name);
        }
    }

    private static void EncodeZones(Span<byte> image, Codeplug ir, int[] channelSlots)
    {
        var slots = AssignSlots(image, Layout.ZoneBitmap, Layout.MaxZones, ir.Zones.Count, inverted: false);
        var channelSlotByName = ChannelSlotByName(ir, channelSlots);
        for (int i = 0; i < ir.Zones.Count; i++)
        {
            var zone = ir.Zones[i];
            WriteNameIfChanged(image.Slice(Layout.ZoneNameOffset(slots[i]), Layout.ZoneNameSize), zone.Name);
            var list = image.Slice(Layout.ZoneChannelsOffset(slots[i]), Layout.ZoneChannelListSize);
            int at = 0;
            foreach (var member in zone.ChannelNames)
            {
                if (!channelSlotByName.TryGetValue(member, out int ch)) continue;
                if (at + 1 >= Layout.ZoneChannelListSize) break;
                SetU16IfChanged(list, at, (ushort)ch);
                at += 2;
            }
            if (at + 1 < Layout.ZoneChannelListSize) SetU16IfChanged(list, at, 0xFFFF);

            // Zone-adjacent state that outlives a codeplug replacement. An out-of-range
            // position is repaired unconditionally, including on a restore: it is not a
            // preference to preserve, it is a value that stops the radio resolving a channel
            // at all. Doing this only when a zone's members changed is not enough — a slot
            // can keep identical members while carrying a position stale from a plug two
            // generations back, which is exactly how Firestone DMR and NOAA WX survived the
            // first attempt at this fix.
            ClampZoneSelection(image, slots[i], at / 2);
            SetAllocated(image, Layout.HiddenZoneBitmap, slots[i], zone.Hidden, inverted: false);
        }
    }

    /// <summary>
    /// Keep a zone's selected-channel position inside its member list, for both VFOs.
    ///
    /// The position survives a codeplug replacement, so a slot that used to hold a 40-channel
    /// zone leaves "channel 31" behind for whatever zone lands there next. Entering a zone
    /// whose position is past the end gives "No Valid Chan!" and locks the menu and zone
    /// controls — observed on hardware, 2026-08-22. A still-valid position is left alone so
    /// the operator keeps their place. [format §4]
    /// </summary>
    private static void ClampZoneSelection(Span<byte> image, int slot, int memberCount)
    {
        int baseOffset = Layout.OffsetOf(Layout.ZoneCurrentChannel);
        foreach (int half in new[] { 0, Layout.ZoneCurrentChannelVfoB })
        {
            int at = baseOffset + half + slot * 2;
            ushort current = BinaryPrimitives.ReadUInt16LittleEndian(image[at..]);
            if (current >= memberCount)
                BinaryPrimitives.WriteUInt16LittleEndian(image[at..], 0);
        }
    }

    private static void EncodeRadioId(Span<byte> image, Codeplug ir)
    {
        if (ir.Settings.RadioId == 0) return;                 // never zero the radio's own ID
        for (int i = 0; i < Layout.MaxRadioIds; i++)
        {
            if (!Layout.BitmapHas(image, Layout.RadioIdBitmap, i)) continue;
            var rec = image.Slice(Layout.RadioIdOffset(i), Layout.RadioIdSize);
            if (DecodeBcdId(rec) != ir.Settings.RadioId) EncodeBcdId(rec, ir.Settings.RadioId);
            if (ir.Settings.Callsign.Length > 0) WriteNameIfChanged(rec.Slice(5, 16), ir.Settings.Callsign);
            return;
        }
    }

    private static Dictionary<string, int> ChannelSlotByName(Codeplug ir, int[] slots)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < ir.Channels.Count && i < slots.Length; i++) map.TryAdd(ir.Channels[i].Name, slots[i]);
        return map;
    }

    // ---------------------------------------------------------------- write-if-changed helpers

    private static void WriteNameIfChanged(Span<byte> field, string name)
    {
        if (ReadName(field) == name) return;
        field.Clear();
        System.Text.Encoding.ASCII.GetBytes(name.Length > field.Length ? name[..field.Length] : name).CopyTo(field);
    }

    private static void SetU16IfChanged(Span<byte> rec, int offset, ushort value)
    {
        if (BinaryPrimitives.ReadUInt16LittleEndian(rec[offset..]) != value)
            BinaryPrimitives.WriteUInt16LittleEndian(rec[offset..], value);
    }

    private static void SetU32IfChanged(Span<byte> rec, int offset, uint value)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(rec[offset..]) != value)
            BinaryPrimitives.WriteUInt32LittleEndian(rec[offset..], value);
    }

    private static void SetBitsIfChanged(Span<byte> rec, int offset, int lowBit, int width, int value)
    {
        int mask = ((1 << width) - 1) << lowBit;
        int updated = rec[offset] & ~mask | (value << lowBit) & mask;
        if (updated != rec[offset]) rec[offset] = (byte)updated;
    }

    private static void SetBitIfChanged(Span<byte> rec, int offset, int bit, bool value) =>
        SetBitsIfChanged(rec, offset, bit, 1, value ? 1 : 0);

    /// <summary>Inverse of <see cref="DecodeBcdFrequency"/>. [format §3]</summary>
    internal static void EncodeBcdFrequency(Span<byte> field, Frequency frequency)
    {
        ulong value = frequency.Hz / 10;
        for (int i = 3; i >= 0; i--)
        {
            int pair = (int)(value % 100);
            field[i] = (byte)(pair / 10 << 4 | pair % 10);
            value /= 100;
        }
    }

    /// <summary>Inverse of <see cref="DecodeBcdId"/>. [format §4]</summary>
    internal static void EncodeBcdId(Span<byte> field, uint id)
    {
        for (int i = 3; i >= 0; i--)
        {
            int pair = (int)(id % 100);
            field[i] = (byte)(pair / 10 << 4 | pair % 10);
            id /= 100;
        }
    }

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
        var channelNameBySlot = DecodeChannels(image, ir, contactIndexToName, scanListNames, groupListNames);
        DecodeZones(image, ir, channelNameBySlot);
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

    private static Dictionary<int, string> DecodeChannels(ReadOnlySpan<byte> image, Codeplug ir,
        Dictionary<uint, string> contacts, Dictionary<int, string> scanLists, Dictionary<int, string> groupLists)
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
        return nameByIndex;
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

    private static void DecodeZones(ReadOnlySpan<byte> image, Codeplug ir, Dictionary<int, string> channelNameBySlot)
    {
        for (int i = 0; i < Layout.MaxZones; i++)
        {
            if (!Layout.BitmapHas(image, Layout.ZoneBitmap, i)) continue;
            var name = ReadName(image.Slice(Layout.ZoneNameOffset(i), Layout.ZoneNameSize));
            var zone = new Zone
            {
                Name = name.Length > 0 ? name : $"Zone {i + 1}",
                Hidden = Layout.BitmapHas(image, Layout.HiddenZoneBitmap, i),
            };
            var list = image.Slice(Layout.ZoneChannelsOffset(i), Layout.ZoneChannelListSize);
            for (int n = 0; n < Layout.MaxChannelsPerZone; n++)
            {
                ushort chIndex = BinaryPrimitives.ReadUInt16LittleEndian(list[(n * 2)..]);
                if (chIndex == 0xFFFF) break;
                if (channelNameBySlot.TryGetValue(chIndex, out var chName)) zone.ChannelNames.Add(chName);
            }
            // Allocated-but-empty zones are kept so slot assignment round-trips.
            ir.Zones.Add(zone);
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
    /// <summary>
    /// The radio's CTCSS table, tenths of Hz, in the index order the channel record uses.
    ///
    /// **Starts at 62.5 Hz**, not 67.0. The list is otherwise the standard 50-tone extended
    /// set, and reading it as that set — index 12 = 100.0 — put every generated analog
    /// channel one tone low: the radio displayed 97.4 Hz on a channel written as 100.0.
    /// With 62.5 leading, index 12 is 97.4 and 100.0 is index 13, which matches the radio.
    /// (hw-verified 2026-08-22, W0QEY on this unit; supersedes the qdmr-derived table.)
    /// </summary>
    private static readonly int[] CtcssTenths =
    [
        625,
        670, 693, 719, 744, 770, 797, 825, 854, 885, 915, 948, 974, 1000, 1035, 1072, 1109,
        1148, 1188, 1230, 1273, 1318, 1365, 1413, 1462, 1514, 1567, 1598, 1622, 1655, 1679,
        1713, 1738, 1773, 1799, 1835, 1862, 1899, 1928, 1966, 1995, 2035, 2065, 2107, 2181,
        2257, 2291, 2336, 2418, 2503, 2541,
    ];

    /// <summary>
    /// signallingMode: 0 = none, 1 = CTCSS (index), 2 = DCS (code). Anything else is
    /// treated as none. The CTCSS index table is hardware-verified (see above); the **DCS
    /// bit layout is still VERIFY** — no channel in the reference codeplug uses DCS, so the
    /// 9-bit octal code and the 0x8000 inverted flag remain unconfirmed against a display.
    /// </summary>
    public static SelectiveCall Decode(int signallingMode, byte ctcssIndex, ushort dcsCode) => signallingMode switch
    {
        1 when ctcssIndex < CtcssTenths.Length => SelectiveCall.Ctcss(CtcssTenths[ctcssIndex] / 10m),
        2 when dcsCode is not 0xFFFF => SelectiveCall.Parse(
            $"D{Convert.ToString(dcsCode & 0x01FF, 8).PadLeft(3, '0')}{((dcsCode & 0x8000) != 0 ? "I" : "N")}"),
        _ => SelectiveCall.None,
    };

    /// <summary>
    /// Inverse of <see cref="Decode"/>. A tone-less channel is written the way the radio's
    /// own records carry it — mode 0 with both tone fields zeroed, as the factory codeplug
    /// does — rather than with an 0xFFFF filler.
    /// </summary>
    public static (int Mode, byte CtcssIndex, ushort Dcs) Encode(SelectiveCall call)
    {
        switch (call.Kind)
        {
            case ToneKind.Ctcss:
                int index = Array.IndexOf(CtcssTenths, call.Value);
                if (index < 0)
                    throw new D878FormatException(
                        $"CTCSS {call} is not one of the radio's {CtcssTenths.Length} standard tones.");
                return (1, (byte)index, 0);
            case ToneKind.Dcs:
                if (call.Value is < 0 or > 0x01FF)
                    throw new D878FormatException($"DCS code {call} does not fit the radio's 9-bit field.");
                return (2, 0, (ushort)(call.Value | (call.Inverted ? 0x8000 : 0)));
            default:
                return (0, 0, 0);
        }
    }
}
