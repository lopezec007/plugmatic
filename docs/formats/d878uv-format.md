# AnyTone AT-D878UVII+ — codeplug format

Normative for `Plugmatic.Radios.D878uv/Format`. Same discipline as the DM-32UV docs:
facts extracted here first, implementation written from this document, every fact
annotated. **Hardware outranks both references** — on the DM-32UV a confidently
documented qdmr fact turned out to be wrong, so nothing here is trusted until the
`verified:` column says `hw`.

**Sources**
- `qdmr` (GPL-3.0, facts only — `d868uv_codeplug.hh`, `anytone_codeplug.hh`,
  `d878uv_codeplug.hh`): the only source with a full field-level map.
- `dmrconfig` (BSD-3-Clause — `anytone_ht.c`): independent confirmation of the bank
  base addresses.
- This radio: AT-D878UVII+ (`--radio d878uv`).

---

## 1. Address space and the canonical image

Unlike the DM-32UV (a flat 4 KiB-block image), the AnyTone codeplug is a **sparse set
of regions** in a 32-bit address space reaching past 0x04800000. Plugmatic stores it as
a **canonical packed image**: the regions of §2, concatenated in ascending address
order, each at a fixed offset. The layout is fixed by this document, so two reads of
the same radio produce byte-comparable files and `dev diffbin` works as usual.

`Layout.OffsetOf(address)` / `Layout.AddressOf(offset)` map between the two. A region
table change is an image-format change: bump `imageLayoutVersion` in the manifest.

## 2. Region table

Bank bases marked `both` appear in qdmr **and** dmrconfig with identical values.
Bitmap addresses appear only in qdmr (`qdmr` column) and are the first thing to
confirm against hardware, since a wrong bitmap address means a wrong channel count.

| Region | Address | Extent | Derivation | source | verified |
|---|---|---|---|---|---|
| Settings | 0x02500000 | 0x0100 | settings block | both | pending |
| Zone channel list (current) | 0x02500100 | 0x0400 | zoneChannelList | qdmr | pending |
| DTMF ID list | 0x02500500 | 0x0100 | dtmfIDList | qdmr | pending |
| Boot settings | 0x02500600 | 0x0100 | bootSettings | qdmr | pending |
| APRS settings | 0x02501000 | 0x0100 | aprsSettings | qdmr | pending |
| DMR APRS message | 0x02501100 | 0x0100 | dmrAPRSMessage | qdmr | pending |
| Settings extension (878) | 0x02501400 | 0x0200 | settingsExtension | qdmr | pending |
| Radio-ID bitmap | 0x024C1320 | 0x0020 | 250 bits → 32 B | qdmr | pending |
| Radio IDs | 0x02580000 | 0x1F40 | 250 × 0x20 | both | pending |
| Zone bitmap | 0x024C1300 | 0x0020 | 250 bits → 32 B | qdmr | pending |
| Hidden-zone bitmap (878) | 0x024C1360 | 0x0020 | 250 bits | qdmr | pending |
| Zone names | 0x02540000 | 0x1F40 | 250 × 0x20 | qdmr | pending |
| Zone channel lists | 0x01000000 | 0x1F400 | 250 × 0x200 | both | pending |
| Channel bitmap | 0x024C1500 | 0x01F4 | 4000 bits → 500 B | qdmr | pending |
| Channel banks ×32 | 0x00800000 + n·0x40000 | 0x2000 each | 128 × 0x40 | both | pending |
| Scan-list bitmap | 0x024C1340 | 0x0020 | 250 bits | qdmr | pending |
| Scan-list banks ×16 | 0x01080000 + n·0x40000 | 0x2000 each | 16 × 0x200 | both | pending |
| Group-list bitmap | 0x025C0B10 | 0x0020 | 250 bits | qdmr | pending |
| Group lists | 0x02980000 | 0x1F400 | 250 × 0x200 | qdmr | pending |
| Contact bitmap | 0x02640000 | 0x04E2 | 10000 bits → 1250 B | qdmr | pending |
| Contact banks ×10 | 0x02680000 + n·0x40000 | 0x9C40 each | 1000 × 0x64 | both | pending |

Counts and strides come from qdmr's `Limit`/`Offset` structs: 4000 channels
(128/bank, record 0x40), 250 zones (name 0x20, channel list 0x200), 250 scan lists
(16/bank, record 0x200), 250 group lists (0x200), 250 radio IDs (0x20), 10000 contacts
(1000/bank, record 0x64).

**Reading everything vs. reading what exists.** Both references read only the records
their bitmaps mark as allocated. Plugmatic reads **every region in this table in full**
so that a `read` is a true backup — the DM-32UV taught us that a backup which skips
regions is not a backup (deviations, 2026-08-10). Unallocated records read back as
filler and are simply not decoded.

## 3. Channel record (0x40 bytes)

Channel *i* lives at `channelBanks + (i / 128) × 0x40000 + (i % 128) × 0x40`, and is
present only if bit *i* of the channel bitmap is set (LSB-first within each byte).

| Off | Field | Notes |
|---|---|---|
| 0x00 | **RX frequency, 8-digit BCD, most-significant pair first, unit 10 Hz** | `44 63 25 00` = 44632500 → 446.325 MHz (**verified: hw**) |
| 0x04 | **TX offset** magnitude, same BCD encoding; sign from the repeater-mode bits. On simplex channels it holds the TX frequency and is ignored | 445.200 rx + rpt=2 + 5.000 → TX 440.200 (**verified: hw**) |
| 0x08 | bits 1–0 mode | 0 analog, 1 digital (2/3 = mixed variants) |
| 0x08 | bits 3–2 power | 0 low … 3 turbo |
| 0x08 | bits 5–4 bandwidth | 0 = 12.5 kHz, 1 = 25 kHz |
| 0x08 | bits 7–6 repeater mode | 0 simplex, 1 TX = RX + offset, 2 TX = RX − offset |
| 0x09 | bit 5 RX-only | the `TxPermit: Inhibited` mapping |
| 0x0A | u8 TX CTCSS index | index into the standard tone table |
| 0x0B | u8 RX CTCSS index | |
| 0x0C | u16 LE TX DCS code | |
| 0x0E | u16 LE RX DCS code | |
| 0x14 | u32 LE TX contact index | into the contact table |
| 0x18 | u8 radio-ID index | which of the 250 radio IDs transmits here |
| 0x1B | u8 scan-list index | 0xFF = none |
| 0x1C | u8 RX group-list index | 0xFF = none |
| 0x20 | u8 colour code | |
| 0x21 | bit 0 time slot | 0 = TS1, 1 = TS2 |
| 0x23 | char[16] name, NUL-terminated | |

(source: qdmr `AnytoneCodeplug::ChannelElement::Offset`; **hardware corrected two of
its facts** — see below. Remaining rows are `verified: pending`.)

**Hardware verification, 2026-08-10** (`dev peek 0x00800000`, this radio, fw V101):

```
00800000  44632500 44632500 1C000000 00000000   Dc%.Dc%.........
00800010  26050000 00000000 00000000 00000000   &...............
00800020  010000 "Channel 1\0"                  ...Channel 1....
00800040  44652500 44652500 1D000000 ...        (channel 2, +0x40 stride)
```

Confirmed: bank base 0x00800000, 0x40 stride, name at 0x23, colour code at 0x20,
channel bitmap at 0x024C1500 (reads 84 allocated channels: 1–52 and 101–132, a
plausible real codeplug rather than filler).

Corrected against qdmr:
1. **Frequencies are BCD, not little-endian integers.** qdmr's `getUInt32_le × 10`
   would decode 446.325 MHz as 24.5 MHz. This one is real and important.

Retracted: an earlier revision of this document claimed 0x04 was the TX frequency
rather than an offset. That was inferred from **simplex channels only**, where the
field happens to hold the TX frequency (equal to RX) and is ignored. Duplex records
disprove it — `N0SZ UHF BH` reads rx 445.200, field 5.000, repeater-mode 2, i.e.
TX = 440.200 MHz, a valid repeater input. qdmr's `txFrequencyOffset` + `repeaterMode`
model is correct; the lesson is to check a *duplex* record before drawing conclusions
about an offset field.

Repeater-mode bits (0x08 bits 7–6), **verified: hw**: 0 simplex, 1 TX = RX + offset,
2 TX = RX − offset. Mode bits 1–0: 0 analog, 1 digital (verified against channels whose
names say so). Bandwidth bit 4 and power bits 3–2 look consistent but are not yet
independently confirmed.

## 4. Zones, contacts, radio IDs (verified) — scan/group lists (pending)

**Bitmap polarity differs per table (hw-verified).** Channel, zone, scan-list,
group-list and radio-ID bitmaps are *normal* (bit set = allocated). The **contact
bitmap is inverted** — a **cleared** bit means allocated. Reading it normally reports
9980 contacts on a radio that has 20. (The DM-32UV has the same inversion on its
contact index; apparently a house style worth expecting on any new radio.)

- **Zone** *i* — **verified: hw**: name at `zoneNames + i × 0x20`, channel list at
  `zoneChannels + i × 0x200` as u16 LE channel indices terminated by 0xFFFF. Decodes
  15 zones with sensible names on this radio.
- **Radio ID** *i* — **verified: hw**: `radioIDs + i × 0x20`; **BCD** DMR ID in bytes
  0x00–0x03, name from **byte 0x05** (16 chars). `03 21 76 32 00 "AT-D878UVII+"`
  → ID 3217632. Note this is BCD like the frequencies, *not* a little-endian integer.
- **Contact** *i* — **verified: hw**: `contactBanks + (i / 1000) × 0x40000 +
  (i % 1000) × 0x64`; type byte at 0x00, 16-char name at 0x01, **BCD** DMR ID at 0x23.
  Talkgroup contacts carry type 1; the meaning of 0 and 2 is **VERIFY**. (qdmr's
  `contactsPerBlock = 4` / 0x190 grouping is not needed for a flat 0x64 stride and is
  left unreconciled.)
- **Scan list** *i* — **verified: hw**: `scanListBanks + (i / 16) × 0x40000 +
  (i % 16) × 0x200`, record 0x200 bytes:

  | Off | Field |
  |---|---|
  | 0x01 | priority-channel select |
  | 0x02 / 0x04 | primary / secondary priority channel, u16, 0xFFFF = none |
  | 0x06 / 0x08 | look-back time A / B, u16 |
  | 0x0A / 0x0C | drop-out delay / dwell time, u16 |
  | 0x0E | revert channel |
  | **0x0F** | **name, 16 chars NUL-terminated** |
  | **0x20** | **members: u16 LE 0-based channel indices, stride 2, 0xFFFF terminates** |

  Hardware agrees with qdmr on every offset here. This radio: list 0 `Main` with
  channels 0–7, list 1 `GMRS` with channels 100–129 — matching the channel bitmap's
  second allocated run exactly.

- **Group list** *i* — **verified: hw**: `groupLists + i × 0x200`; **members are u32 LE
  contact indices from 0x00** (stride 4, 0xFFFFFFFF terminates, 64 max) and the
  **name is at 0x100** (16 chars). This radio: one list, `Group List 1`, one member.

- **Channel → list references** — **verified: hw**: channel byte **0x1B = scan-list
  index**, **0x1C = RX group-list index**, both **0-based** with **0xFF = none**.
  Self-consistent on this radio: channels 1–8 carry 0x00 and are exactly the members of
  scan list 0, channels 101+ carry 0x01 and are the members of list 1.

- **Tones** — the channel's signalling-mode bits (0x09: bits 1–0 RX, bits 3–2 TX)
  select which tone field applies: 0 none, 1 CTCSS (index at 0x0A tx / 0x0B rx),
  2 DCS (u16 at 0x0C tx / 0x0E rx). **VERIFY** — every channel in this codeplug has
  mode 0, so the CTCSS index table and DCS bit layout remain unconfirmed; decoding
  without the mode gate produced bogus tones from unused bytes.

- **Channel TX contact index** (u32 at 0x14): reads **0 on every channel of this
  radio**, including analog ones. That is suspicious for a real codeplug — a per-channel
  talkgroup assignment should vary — so treat the offset as **VERIFY** and do not rely
  on it until a codeplug with known per-channel talkgroups is available.

## 5. Safety

Writes are bounds-checked against §2: an address not inside a listed region is refused
before transmission (I8). No firmware/bootloader region appears here and none may be
added (D14). The callsign/user database regions (0x04340000 / 0x04800000) are
**deliberately excluded** — contacts-DB loading is out of scope (D11), and excluding
them keeps the write surface small.

## 6. Hardware verification log

| Date | Fact | Result |
|---|---|---|
| 2026-08-10 | Identify handshake, model `D878UV2`, firmware `V101` | **verified** |
| 2026-08-10 | Read framing, big-endian address, checksum, 64-byte chunks | **verified** (1.66 MB read in 2m52s) |
| 2026-08-10 | Bitmap addresses — channel/zone/scan/group/radio-ID/contact | **verified** (84 channels, 15 zones, 20 contacts, 1 group list, 2 scan lists) |
| 2026-08-10 | Channel record: BCD frequencies, offset+repeater mode, name 0x23, colour code 0x20 | **verified** |
| 2026-08-10 | Radio ID record (BCD, name at 0x05) → 3217632 | **verified** |
| 2026-08-10 | Contact record (name 0x01, BCD ID 0x23, inverted bitmap) | **verified** |
| 2026-08-10 | Scan-list and group-list records, channel→list indices | **verified** |
| 2026-08-10 | **Byte stability: three full reads, identical (same MD5)** | **verified — no volatile regions**, so `Compare` needs no masks |
| 2026-08-10 | **Round trip `Encode(Decode(img), img) == img` on the real image** | **verified byte-exact** |
| — | Any write to the radio | not attempted; `SupportsWrite` stays false until the ladder runs |

**Encoding rule (learned twice, on both radios).** Several channel fields decode
many-to-one: modes 1–3 all mean "digital", power 2–3 both mean "high", and the offset
field is ignored on simplex channels. Writing the canonical value back therefore
*changes bytes that already meant the right thing* — the first round-trip attempt
rewrote every mode-3 channel to mode 1. The encoder compares the **decoded** value and
leaves the record untouched when it already agrees, which is what makes the round trip
byte-exact.
