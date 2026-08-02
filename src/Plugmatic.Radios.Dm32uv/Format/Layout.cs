namespace Plugmatic.Radios.Dm32uv.Format;

/// <summary>Virtual memory map + record geometry. Every constant traces to docs/formats/dm32uv-format.md.</summary>
public static class Layout
{
    // [format §2] virtual block numbers
    public const int SettingsBlock = 0x04;
    public const int ContactIndexBlock = 0x0B;
    public const int GroupListBlock = 0x0F;
    public const int ScanListBlock = 0x11;
    public const int FirstChannelBlock = 0x12;
    public const int ChannelBlockCount = 48;              // 0x12..0x41
    public const int FirstExtensionBlock = 0x42;
    public const int ExtensionBlockCount = 2;             // 0x42..0x43
    public const int FirstContactBlock = 0x44;
    public const int ContactBlockCount = 5;               // 0x44..0x48
    public const int FirstZoneBlock = 0x5C;
    public const int ZoneBlockCount = 8;                  // 0x5C..0x63
    public const int RadioIdBlock = 0x67;

    // [format §4] channels
    public const int ChannelRecordSize = 0x30;
    public const int ChannelBank0Header = 0x10;
    public const int ChannelsInBank0 = 84;
    public const int ChannelsPerBank = 85;
    public const int MaxChannels = 4000;

    public static (int block, int offset) ChannelSlot(int index)
    {
        if (index < ChannelsInBank0)
            return (FirstChannelBlock, ChannelBank0Header + index * ChannelRecordSize);
        int rest = index - ChannelsInBank0;
        return (FirstChannelBlock + 1 + rest / ChannelsPerBank, rest % ChannelsPerBank * ChannelRecordSize);
    }

    // [format §5] channel extensions (TX contact)
    public const int ExtensionRecordSize = 2;
    public const int ExtensionsPerBlock = 2047;

    public static (int block, int offset) ExtensionSlot(int index) =>
        (FirstExtensionBlock + index / ExtensionsPerBlock, index % ExtensionsPerBlock * ExtensionRecordSize);

    // [format §6] contacts
    public const int ContactRecordSize = 0x18;
    public const int ContactsPerBlock = 170;
    public const int MaxContacts = 800;                   // encode limit [format §12 C6]

    public static (int block, int offset) ContactSlot(int index) =>
        (FirstContactBlock + index / ContactsPerBlock, index % ContactsPerBlock * ContactRecordSize);

    // [format §6.2] contact index
    public const int ContactIndexBitmapOffset = 0x10;
    public const int ContactIndexBitmapSize = 100;
    public const int ContactIndexTableOffset = 0x100;
    public const int ContactIndexSortedOffset = 0x740;

    // [format §7] group lists
    public const int GroupListBitmapOffset = 0x00;
    public const int GroupListsOffset = 0x11;
    public const int GroupListRecordSize = 0x6D;
    public const int MaxGroupLists = 32;
    public const int MaxContactsPerGroupList = 32;

    // [format §8] scan lists
    public const int ScanListsOffset = 0x01;
    public const int ScanListRecordSize = 0x39;
    public const int MaxScanLists = 31;
    public const int MaxChannelsPerScanList = 15;
    /// <summary>Channel record's scan-list nibble is 4 bits, 1-based: only lists 0-14 are addressable per-channel.</summary>
    public const int MaxChannelReferencableScanList = 14;

    // [format §3] zones
    public const int ZoneBankHeader = 0x10;
    public const int ZoneRecordSize = 0x91;
    public const int ZonesPerBank = 28;
    public const int MaxZones = 250;
    public const int MaxChannelsPerZone = 64;

    public static (int block, int offset) ZoneSlot(int index) =>
        (FirstZoneBlock + index / ZonesPerBank, ZoneBankHeader + index % ZonesPerBank * ZoneRecordSize);

    // [format §10] radio IDs
    public const int RadioIdsOffset = 0x10;
    public const int RadioIdRecordSize = 0x10;
    public const int MaxRadioIds = 250;
}
