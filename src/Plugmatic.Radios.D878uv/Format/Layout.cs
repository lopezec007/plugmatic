namespace Plugmatic.Radios.D878uv.Format;

public sealed class D878FormatException(string message) : Exception(message);

/// <summary>One contiguous run of radio memory. [format §2]</summary>
public readonly record struct Region(string Name, uint Address, int Length)
{
    public uint End => Address + (uint)Length;   // exclusive
}

/// <summary>
/// The region table and the packed-image mapping, per docs/formats/d878uv-format.md §1-§2.
/// Every address here is cited in that document; nothing may be added without a doc entry.
/// </summary>
public static class Layout
{
    // Counts and strides [format §2] (qdmr Limit/Offset structs).
    public const int MaxChannels = 4000;
    public const int ChannelsPerBank = 128;
    public const int ChannelRecordSize = 0x40;
    public const int MaxZones = 250;
    public const int MaxChannelsPerZone = 250;
    public const int ZoneNameSize = 0x20;
    public const int ZoneChannelListSize = 0x200;
    public const int MaxScanLists = 250;
    public const int ScanListsPerBank = 16;
    public const int ScanListSize = 0x200;
    public const int MaxGroupLists = 250;
    public const int GroupListSize = 0x200;
    public const int MaxContacts = 10000;
    public const int ContactsPerBank = 1000;
    public const int ContactRecordSize = 0x64;
    public const int MaxRadioIds = 250;
    public const int RadioIdSize = 0x20;

    // Bank bases [format §2].
    public const uint ChannelBanks = 0x00800000;
    public const uint BetweenChannelBanks = 0x00040000;
    public const uint ChannelBitmap = 0x024C1500;
    public const uint ZoneBitmap = 0x024C1300;
    public const uint HiddenZoneBitmap = 0x024C1360;
    public const uint ZoneNames = 0x02540000;
    public const uint ZoneChannels = 0x01000000;
    public const uint ScanListBitmap = 0x024C1340;
    public const uint ScanListBanks = 0x01080000;
    public const uint BetweenScanListBanks = 0x00040000;
    public const uint GroupListBitmap = 0x025C0B10;
    public const uint GroupLists = 0x02980000;
    public const uint ContactBitmap = 0x02640000;
    public const uint ContactBanks = 0x02680000;
    public const uint BetweenContactBanks = 0x00040000;
    public const uint RadioIdBitmap = 0x024C1320;
    public const uint RadioIds = 0x02580000;
    public const uint Settings = 0x02500000;
    public const uint SettingsExtension = 0x02501400;

    /// <summary>
    /// Logical spacing between banks. A write anywhere in a bank affects everything visible
    /// in this whole window, so it is the span to check for damage. [d878uv-protocol.md §5.4]
    /// </summary>
    public const uint BankStride = 0x00040000;

    /// <summary>
    /// Real storage per bank. The upper half of a bank is the same 0x20000 read a second
    /// time — verified: a bank's two halves are byte-identical, and the vendor CPS never
    /// writes a single byte at `bank + 0x20000` or above (0 of 96,352). Writing the mirror
    /// is what put channel bank 0's records into bank 1 on 2026-08-21, so the writable
    /// window is `[bank, bank + BankStorageSize)` and nothing else.
    /// [d878uv-protocol.md §5.5/§5.6]
    /// </summary>
    public const uint BankStorageSize = 0x00020000;

    /// <summary>Base address of the bank containing `address`.</summary>
    public static uint BankOf(uint address) => address & ~(BankStride - 1);

    /// <summary>
    /// The radio keeps a 16-byte flash signature at the end of every 0x20000 half-block
    /// (`FF FF FF FF 22 33 44 55 FF FF FF FF 55 55 AA AA`). It is firmware-managed, not
    /// codeplug data: it is present in blocks Plugmatic has never written, and it is back
    /// in place after an erase. Tools that ask "is this block empty?" must ignore it.
    /// [d878uv-protocol.md §5.4]
    /// </summary>
    public const uint FlashMarkerStride = 0x20000;

    /// <summary>True if `address` falls in a firmware flash signature. [see FlashMarkerStride]</summary>
    public static bool IsFlashMarker(uint address) =>
        (address & (FlashMarkerStride - 1)) >= FlashMarkerStride - 16;

    /// <summary>Region table in ascending address order — the canonical image layout. [format §2]</summary>
    public static readonly IReadOnlyList<Region> Regions = BuildRegions();

    private static Region[] BuildRegions()
    {
        var list = new List<Region>();
        for (int b = 0; b < (MaxChannels + ChannelsPerBank - 1) / ChannelsPerBank; b++)
            list.Add(new Region($"channels[{b}]", ChannelBanks + (uint)b * BetweenChannelBanks,
                                ChannelsPerBank * ChannelRecordSize));
        list.Add(new Region("zoneChannels", ZoneChannels, MaxZones * ZoneChannelListSize));
        for (int b = 0; b < (MaxScanLists + ScanListsPerBank - 1) / ScanListsPerBank; b++)
            list.Add(new Region($"scanLists[{b}]", ScanListBanks + (uint)b * BetweenScanListBanks,
                                ScanListsPerBank * ScanListSize));
        list.Add(new Region("settings", Settings, 0x0100));
        list.Add(new Region("zoneChannelListCurrent", 0x02500100, 0x0400));
        list.Add(new Region("dtmfIdList", 0x02500500, 0x0100));
        list.Add(new Region("bootSettings", 0x02500600, 0x0100));
        list.Add(new Region("aprsSettings", 0x02501000, 0x0100));
        list.Add(new Region("dmrAprsMessage", 0x02501100, 0x0100));
        list.Add(new Region("settingsExtension", SettingsExtension, 0x0200));
        list.Add(new Region("zoneNames", ZoneNames, MaxZones * ZoneNameSize));
        list.Add(new Region("radioIds", RadioIds, MaxRadioIds * RadioIdSize));
        list.Add(new Region("contactBitmap", ContactBitmap, (MaxContacts + 7) / 8));
        for (int b = 0; b < (MaxContacts + ContactsPerBank - 1) / ContactsPerBank; b++)
            list.Add(new Region($"contacts[{b}]", ContactBanks + (uint)b * BetweenContactBanks,
                                ContactsPerBank * ContactRecordSize));
        list.Add(new Region("groupLists", GroupLists, MaxGroupLists * GroupListSize));
        // Bitmaps live close together; keep each its own region so the table stays legible.
        list.Add(new Region("zoneBitmap", ZoneBitmap, (MaxZones + 7) / 8));
        list.Add(new Region("radioIdBitmap", RadioIdBitmap, (MaxRadioIds + 7) / 8));
        list.Add(new Region("scanListBitmap", ScanListBitmap, (MaxScanLists + 7) / 8));
        list.Add(new Region("hiddenZoneBitmap", HiddenZoneBitmap, (MaxZones + 7) / 8));
        list.Add(new Region("channelBitmap", ChannelBitmap, (MaxChannels + 7) / 8));
        list.Add(new Region("groupListBitmap", GroupListBitmap, (MaxGroupLists + 7) / 8));

        var ordered = list.OrderBy(r => r.Address).ToArray();
        for (int i = 1; i < ordered.Length; i++)
            if (ordered[i].Address < ordered[i - 1].End)
                throw new D878FormatException(
                    $"Region table overlap: {ordered[i - 1].Name} and {ordered[i].Name}.");
        return ordered;
    }

    private static readonly int[] OffsetOfRegion = BuildOffsets();
    public static readonly int ImageSize = OffsetOfRegion[^1] + Regions[^1].Length;

    private static int[] BuildOffsets()
    {
        var offs = new int[Regions.Count];
        int at = 0;
        for (int i = 0; i < Regions.Count; i++) { offs[i] = at; at += Regions[i].Length; }
        return offs;
    }

    public static uint RegionsStart => Regions[0].Address;
    public static uint RegionsEnd => Regions[^1].End - 1;

    /// <summary>Packed-image offset of a radio address; throws when the address is not mapped.</summary>
    public static int OffsetOf(uint address)
    {
        for (int i = 0; i < Regions.Count; i++)
            if (address >= Regions[i].Address && address < Regions[i].End)
                return OffsetOfRegion[i] + (int)(address - Regions[i].Address);
        throw new D878FormatException($"Address 0x{address:X8} is outside the region table (format §2).");
    }

    public static bool TryOffsetOf(uint address, out int offset)
    {
        for (int i = 0; i < Regions.Count; i++)
            if (address >= Regions[i].Address && address < Regions[i].End)
            {
                offset = OffsetOfRegion[i] + (int)(address - Regions[i].Address);
                return true;
            }
        offset = -1;
        return false;
    }

    /// <summary>Radio address of a packed-image offset (for hex-diff archaeology).</summary>
    public static uint AddressOf(int offset)
    {
        for (int i = 0; i < Regions.Count; i++)
            if (offset >= OffsetOfRegion[i] && offset < OffsetOfRegion[i] + Regions[i].Length)
                return Regions[i].Address + (uint)(offset - OffsetOfRegion[i]);
        throw new D878FormatException($"Offset 0x{offset:X} is outside the packed image.");
    }

    /// <summary>The banks the codeplug occupies — every 0x40000 window any region falls in.</summary>
    public static readonly IReadOnlySet<uint> CodeplugBanks = BuildBanks();

    private static HashSet<uint> BuildBanks()
    {
        var banks = new HashSet<uint>();
        foreach (var region in Regions)
            for (uint b = BankOf(region.Address); b < region.End; b += BankStride)
                banks.Add(b);
        return banks;
    }

    /// <summary>
    /// I8: a write is legal only fully inside the **storage half** of a bank the codeplug
    /// occupies, and never over a firmware flash signature.
    ///
    /// The bound is the bank's lower 0x20000 rather than a region, because a write erases the
    /// whole unit and its inter-region gaps have to be rewritten too. It stops at 0x20000
    /// because past that is the mirror, and writing the mirror corrupts a different bank.
    /// [format §5, d878uv-protocol.md §5.5/§5.6]
    /// </summary>
    public static bool IsWritable(uint address, int length)
    {
        uint bank = BankOf(address);
        if (!CodeplugBanks.Contains(bank)) return false;
        if ((ulong)address + (ulong)length > (ulong)bank + BankStorageSize) return false;
        return !TouchesFlashMarker(address, length);
    }

    /// <summary>True if [address, address+length) overlaps a firmware flash signature.</summary>
    public static bool TouchesFlashMarker(uint address, int length)
    {
        for (uint a = address; a < address + (uint)length; a++)
            if (IsFlashMarker(a)) return true;
        return false;
    }

    public static (uint Address, int Offset) ChannelSlot(int index) =>
        Bank(ChannelBanks, BetweenChannelBanks, ChannelsPerBank, ChannelRecordSize, index);

    public static (uint Address, int Offset) ContactSlot(int index) =>
        Bank(ContactBanks, BetweenContactBanks, ContactsPerBank, ContactRecordSize, index);

    public static (uint Address, int Offset) ScanListSlot(int index) =>
        Bank(ScanListBanks, BetweenScanListBanks, ScanListsPerBank, ScanListSize, index);

    private static (uint, int) Bank(uint baseAddr, uint between, int perBank, int recordSize, int index)
    {
        uint addr = baseAddr + (uint)(index / perBank) * between + (uint)(index % perBank * recordSize);
        return (addr, OffsetOf(addr));
    }

    public static int ZoneNameOffset(int index) => OffsetOf(ZoneNames + (uint)(index * ZoneNameSize));
    public static int ZoneChannelsOffset(int index) => OffsetOf(ZoneChannels + (uint)(index * ZoneChannelListSize));
    public static int GroupListOffset(int index) => OffsetOf(GroupLists + (uint)(index * GroupListSize));
    public static int RadioIdOffset(int index) => OffsetOf(RadioIds + (uint)(index * RadioIdSize));

    /// <summary>Allocation bitmaps are LSB-first within each byte. [format §3]</summary>
    public static bool BitmapHas(ReadOnlySpan<byte> image, uint bitmapAddress, int index)
    {
        int at = OffsetOf(bitmapAddress) + index / 8;
        return (image[at] >> (index % 8) & 1) != 0;
    }
}
