# DM-32UV Codeplug Binary Format (normative)

Status: **draft — pre-hardware**. Annotation scheme is identical to
`dm32uv-protocol.md` (`source:` qdmr-observed / dm32-spec / cps-diff / hw,
`verified:` cross / hw / pending). The codec in `Plugmatic.Radios.Dm32uv/Format`
implements THIS document; code comments cite sections as `[format §N]`.

Known open conflicts between sources are tracked in §12 and mirrored in
`docs/deviations.md` once resolved.

---

## 1. Image container (`*.bin` artifacts, codec in/out)

The codec operates on the radio's **virtual codeplug address space** (see protocol doc
§6.3 for how physical blocks map to virtual addresses).

- An image file is exactly **0x68000 bytes (416 KiB)**; file offset == virtual address.
- The space is organised in 4 KiB blocks. Virtual block `v` (0x00–0x67) occupies
  `[v*0x1000, v*0x1000+0xFFF]`; its **last byte holds `v` itself** (the metadata byte,
  protocol doc §6).
- A block is **present** iff its last byte equals its own block number. Absent blocks
  are 0xFF-filled. This makes the container self-describing; no sidecar needed.
- Blocks 0x00–0x02 are never part of a codeplug image (0x02 is factory calibration —
  deliberately outside our window; 0x00/0x01 are invalid metadata values).
  The meaningful window is **[0x03000, 0x68000)**. (source: qdmr-observed window
  0x3000–0x68000; verified: pending)

`Compare(a,b)` = per-block: both absent → equal; presence mismatch → differ; both
present → bytewise compare minus masked ranges (§11).

## 2. Virtual memory map

| Virt block(s) | Address | Contents | v1 codec treatment |
|---|---|---|---|
| 0x03 | 0x03000 | Digital emergency systems | opaque passthrough |
| 0x04 | 0x04000 | Settings super-block: general settings @+0x000 (0x100 B, §9), APRS settings @+0x300 (0x100 B), password settings @+0x400 (0x100 B) | general settings decoded partially (§9); rest passthrough. Passwords are secrets → never logged (I7) |
| 0x05 | 0x05000 | Unused/unknown | passthrough |
| 0x06 | 0x06000 | DTMF encode config | passthrough |
| 0x07 | 0x07000 | Config header (structure unknown; not parsed by CPS either) | passthrough |
| 0x0A | 0x0A000 | Quick text messages (count @0x00, 129 B entries from 0x80) | passthrough |
| 0x0B | 0x0B000 | **Contact index** (§6.2) | decode + re-encode |
| 0x0F | 0x0F000 | **RX group lists** (§7) | decode + encode |
| 0x10 | 0x10000 | Extended/menu settings @+0x000, encryption keys @+0x300 (32 × 0x2C B); dm32-spec instead labels this block "analog emergency systems" — see §12 conflict C4 | passthrough |
| 0x11 | 0x11000 | **Scan lists** (§8) | decode + encode |
| 0x12–0x41 | 0x12000 | **Channel banks** (48 blocks, §4) | decode + encode |
| 0x42–0x43 | 0x42000 | **Channel extensions** = per-channel TX contact (§5) | decode + encode |
| 0x44–0x48 | 0x44000 | **Contacts** (5 banks × 170 × 24 B, §6.1) | decode + encode |
| 0x5C–0x63 | 0x5C000 | **Zones** (8 banks, §3) | decode + encode |
| 0x65 | 0x65000 | Roaming zones (33 B entries) | decode-tolerant, passthrough on encode |
| 0x66 | 0x66000 | Roaming channels (26 B entries: name 16, RX BCD 4, TX BCD 4, CC 1, slot 1) | decode-tolerant, passthrough on encode |
| 0x67 | 0x67000 | **Radio ID list** (§10) | decode + encode |

(source: qdmr-observed top-level offsets, cross-checked against dm32-spec metadata
table — agreement on 0x0F/0x11/0x12–0x41/0x42–0x43/0x44–0x48/0x5C/0x65/0x66/0x67;
verified: cross for those rows, pending for 0x03–0x0A and C4)

"Passthrough": on decode, raw bytes are retained on the IR (`RawBlocks`); on encode
they are copied back verbatim if present. From-scratch encodes (no base) simply omit
passthrough blocks — the radio write path always has the pre-write image (D7), so
hardware writes always carry the radio's own settings forward.

## 3. Zones (blocks 0x5C–0x63)

Bank = 4 KiB. **Bank 0 (0x5C000)** starts with a 16-byte header:

| Off | Type | Field |
|---|---|---|
| 0x00 | u8 | Total zone count (all banks) |
| 0x01 | u16 LE | VFO A channel number, 1-based; 0 = unset |
| 0x03 | u16 LE | VFO B channel number, 1-based; 0 = unset |
| 0x05 | u16 LE | VFO A zone number, 1-based; 0 = unset |
| 0x07 | u16 LE | VFO B zone number, 1-based; 0 = unset |
| 0x09–0x0F | — | padding |

Zone records: **145 bytes (0x91)** each, starting at +0x10 in every bank,
**28 per bank**, packed. Zone `i` (0-based, global) lives in bank `i/28` at
`0x10 + (i%28)*0x91`. Max 250 zones.

| Off | Type | Field |
|---|---|---|
| 0x00 | char[16] | Name, ASCII, 0x00-terminated/padded (§12 C2 for pad byte) |
| 0x10 | u8 | Channel count (≤ 64) |
| 0x11 | u16[64] LE | Channel numbers, **1-based**; 0 = empty slot |

(source: qdmr-observed sizes/offsets; dm32-spec agrees on 145 B, 16 B block header,
count+list layout, but reads the name as 11 B — the extra 5 bytes are 0xFF padding
either way; verified: cross, name-length nuance C2)

## 4. Channels (blocks 0x12–0x41)

Block 0x12 starts with a 16-byte header: u16 LE **total channel count** at +0x00,
rest padding. Channel records are **48 bytes (0x30)**:

- Block 0x12 holds channels 0–83 (84 records) starting at +0x10.
- Blocks 0x13–0x41 hold **85** records each starting at +0x00.
- Channel `i` (0-based): block 0 if `i<84`, else block `1+(i-84)/85`, index
  `(i-84)%85`. Capacity 84+47*85 = 4079 ≥ radio limit 4000.
- 84 (not 85) in block 0: header 16 + 84×48 = 4048 ≤ 4095; 85 would collide with the
  metadata byte. (source: qdmr-observed; dm32-spec claims 85 — arithmetic rules it
  out; verified: cross-with-correction, confirm at hw)

Channel record — multi-byte ints LE, frequencies §11.1, tones §11.2:

| Off | Field | Encoding |
|---|---|---|
| 0x00 | Name | char[16] ASCII 0x00-terminated (§12 C2) |
| 0x10 | RX frequency | BCD8 LE, units of 10 Hz |
| 0x14 | TX frequency | BCD8 LE; **0xFFFFFFFF = no TX frequency** |
| 0x18 | bits 7–4: channel type (0 FM, 1 DMR, 2 FM-fixed, 3 DMR-fixed) · bit 3: **RX-only ("forbid TX")** · bits 2–1: power (0 Low, 1 Medium, 2 High) · bit 0: lone worker | |
| 0x19 | bit 7: FM bandwidth (0 = 12.5 kHz narrow, 1 = 25 kHz wide) · bit 6: scan add (dm32-spec; qdmr silent) · bits 5–2: scan list index, **1-based nibble, 0 = none** · bits 1–0: reserved | |
| 0x1A | bit 7: prevent talkaround · bits 5–4: admit criterion (0 always, 1 channel-free, 2 tone/CC-match, 3 tone-mismatch) · bit 2: RX DMR-APRS · bits 1–0: reserved | |
| 0x1B | bit 7: emergency notification · bit 6: emergency ACK · bits 4–0: emergency system index, 1-based, 0 = none | |
| 0x1C | bits 7–4: squelch level 0–15 · bits 3–2: (DMR-)APRS mode · bits 1–0: reserved | |
| 0x1D | **DMR:** bit 7 private-call ACK · bit 6 data ACK · bit 5 DCDM · bit 4 timeslot (0=TS1, 1=TS2) · bits 3–0 color code. **FM (dm32-spec):** bit 7 VOX · bit 6 scramble · bit 5 compander · bit 4 talkback. Mode-dependent overlay — §12 C3. | |
| 0x1E | Encryption key index, 1-based; 0 = none | u8 |
| 0x1F | bit 6: encryption enable · bits 4–0: RX group list index, **1-based, 0 = none** | |
| 0x20 | DMR-APRS report channel index (qdmr); dm32-spec says "color code" — §12 C3 | u8 |
| 0x21 | RX tone | u16 LE, §11.2 |
| 0x23 | TX tone | u16 LE, §11.2 |
| 0x25 | bit 4: VOX enable; others unknown | |
| 0x26 | bit 7: show PTT-ID · bit 4: optional-signaling enable · bits 3–0: opt-sig type (0 none, 1 DTMF, 2 two-tone, 3 five-tone, 4 MDC1200) | |
| 0x27 | bits 7–4: step frequency (0=2.5k,1=5k,2=6.25k,3=10k,4=12.5k,5=25k,6=50k,7=100k) · bits 3–0: signaling (dm32-spec) | |
| 0x28 | reserved | |
| 0x29 | bit 2: PTT-ID enable; bits 7–4 PTT-ID type (0 off, 1 BOT, 2 EOT, 3 both) per dm32-spec | |
| 0x2A | unknown | |
| 0x2B | DMR radio-ID index into §10 list, 0-based; **0xFF = none/default** | u8 |
| 0x2C | reserved ×4 | |

A cleared channel record is all 0x00. (source: qdmr-observed bit map + dm32-spec
field tables; verified: cross except cells marked C3)

**Plugmatic TxPermit mapping (D-decision):** `TxPermit: Inhibited` ⇔ byte 0x18 bit 3
set. Additionally the builder emits no TX frequency… no — TX frequency stays populated
(radio displays offset); RX-only enforcement is bit 0x18.3. This resolves the spec's
"VERIFY which bit implements RX-only".

## 5. Channel extensions — per-channel TX contact (blocks 0x42–0x43)

2-byte records; **2047 per block** (offsets 0x000–0xFFD; the block's last two bytes
are padding + metadata). Channel `i`: block `0x42 + i/2047`, offset `(i%2047)*2`.

Record encoding (the infamous mixed-endian hotfix):

- byte 0, bits 3–0 ("MSN"): bits 11–8 of a 12-bit value
- byte 1 ("LSB"): bits 7–0 of the value
- byte 0, bits 7–4: unknown/zero
- The 12-bit value is the **1-based contact index** (into §6 contacts, global index);
  **0 = no TX contact**.
- A cleared record is `01 00` — i.e. u16 LE 0x0001 with the contact index then
  cleared to 0. Observed cleared state: byte0=0x01&0xF0? qdmr clears to
  `setUInt16_le(0,0x0001)` then zeroes the index nibble+byte, leaving 0x0000.
  Net effect: absent contact = `00 00`. (verified: pending — check on hw read)

(source: qdmr-observed; dm32-spec confirms blocks 0x42/0x43 are "TX contact,
2 B/channel, ch 1–2048 / 2049+"; verified: cross on purpose+stride, pending on the
exact split 2047 vs 2048 — qdmr's 2047 wins until hw says otherwise, §12 C5)

## 6. Contacts

### 6.1 Contact records (blocks 0x44–0x48)

24-byte (0x18) records, **170 per bank**, packed from +0x00; global contact `i` →
block `0x44 + i/170`, offset `(i%170)*0x18`. Capacity 850; radio/CPS limit **800**
(qdmr) — treat 800 as the encode limit, decode up to 850. (§12 C6)

| Off | Field |
|---|---|
| 0x00 | u16: unknown (observed 0; possibly flags) |
| 0x02 | Name, char[16] ASCII 0x00-padded |
| 0x12 | (gap byte — part of name field padding) |
| 0x13 | DMR ID, u24 LE (plain binary, not BCD) |
| 0x16 | Call type: u8 — 3 = private, 4 = group, 5 = all-call |
| 0x17 | padding |

Note: name field is 16 chars at 0x02 → occupies 0x02–0x11; byte 0x12 is unassigned
padding before the ID. (source: qdmr-observed offsets name=0x02 len16, id=0x13,
type=0x16; verified: pending vs cps-diff)

### 6.2 Contact index (block 0x0B)

| Off | Field |
|---|---|
| 0x000 | u16 LE total contact count |
| 0x002 | u16 LE group-call count |
| 0x004 | u16 LE private-call count |
| 0x010 | Allocation bitmap, 100 bytes (800 bits), **inverted polarity**: bit cleared = slot allocated; blank = 0xFF |
| 0x100 | Index table: 800 × u16 LE entries, in contact-slot order |
| 0x740 | Sorted index table: same 800 × u16 LE entries, sorted ascending by DMR ID |

Index entry: bits 15–12 = call type (3/4/5 as §6.1); bits 11–0 = **1-based** contact
slot; 0xFFFF = empty. (source: qdmr-observed; verified: pending)

The codec must regenerate this entire block from the contact table on encode.

## 7. RX group lists (block 0x0F)

Bank header: 4-byte allocation bitmap at +0x00 (bit set = list present, normal
polarity), lists start at **+0x11**; **109-byte (0x6D)** records, stride 0x6D,
max 32 lists.

| Off | Field |
|---|---|
| 0x00 | Name, char[11] ASCII 0x00-terminated |
| 0x0B | 32 × u24 LE **DMR talkgroup IDs** (values, not indices); 0 = empty slot |
| 0x6B | 2 bytes padding |

(source: qdmr-observed; dm32-spec's garbled 0x0F description reconciles with this
given a one-byte base shift; verified: cross-weak, confirm at hw)

## 8. Scan lists (block 0x11)

Bank: u8 count at +0x00; **57-byte (0x39)** records from **+0x01**, stride 0x39, max
31. Bank tail: scan mode u8 @+0xE00 (0 time, 1 carrier, 2 search), VHF range lower/
upper u16 @+0xE01/+0xE03, UHF lower/upper u16 @+0xE05/+0xE07 (units unknown — treat
as opaque defaults; §12 C7).

| Off | Field |
|---|---|
| 0x00 | Name, char[11] ASCII |
| 0x0B | u8 channel count (≤15) |
| 0x0C | bits 5–4: transmit mode (0 current, 1 active, 2 revert) · bits 1–0: tone-detection mode (0 none, 1 non-priority, 2 priority, 3 all) |
| 0x0D | Hang time (units: 0.1 s per dm32-spec) |
| 0x0E | low nibble: primary priority channel selector · high nibble: secondary — **1-based; 0 = none** (qdmr reads these nibbles as the priority-channel index; dm32-spec calls them "priority type" with u16 channel numbers at 0x0F/0x13 — §12 C8) |
| 0x0F | u16 LE revert ("designated TX") channel number, 1-based |
| 0x11–0x17 | unknown (dm32-spec: more channel refs, "stored −2"; C8) |
| 0x15 | bits ≥2: priority sweep time (qdmr) |
| 0x18 | 15 × u16 LE channel numbers; **0 = "current channel"** marker, else 1-based |

v1 builder only writes: name, count, channel list, mode bytes zeroed-to-defaults.
(source: qdmr-observed + dm32-spec; verified: cross on name/count/list, C8 else)

## 9. General settings (block 0x04, +0x000, 256 B) — decoded subset

Fields plugmatic reads (and rewrites verbatim unless stated):

| Off | Field |
|---|---|
| 0x00 | Boot display mode (0 image, 1 message, 2 voltage) |
| 0x01 | Boot message line 1, char[14] |
| 0x0F | Boot message line 2, char[14] |
| 0x20–0x21 | Tone enable bit-flags |
| 0x30 | Display brightness 0–5 |
| 0xA0 | TX timeout |
| 0xA2/0xA3 | VOX level / delay |
| 0xA6/0xA7 | FM / DMR mic level |

Full bit map exists (source: qdmr-observed) but v1 does not edit this block — it is
carried as passthrough; only decoded for `read.yaml` display. The radio's own DMR ID +
callsign live in §10, not here.

## 10. Radio ID list (block 0x67)

u8 count at +0x00 (dm32-spec says u32 — low byte agrees; §12 C9), 15 B padding;
**16-byte records from +0x10**, stride 0x10, max 250:

| Off | Field |
|---|---|
| 0x00 | DMR ID, u24 LE binary |
| 0x03 | Name, char[12] ASCII 0x00-padded |
| 0x0F | padding |

Plugmatic's `GeneralSettings.RadioId`/`Callsign` (IR) encode to entry 0 here; channel
byte 0x2B references entries by 0-based index (0xFF = none → radio default = entry 0).
(source: qdmr-observed; dm32-spec agrees on 16 B stride + 3 B ID + name; verified:
cross except count width C9)

## 11. Primitive encodings

### 11.1 Frequencies — BCD8 LE ×10 Hz

Value stored = frequency in **units of 10 Hz** as 8 BCD digits, packed 2 digits/byte
big-endian-digit-order, then the 4 bytes stored **little-endian**. 145.350 MHz →
14 535 000 ×10 Hz → digits `14 53 50 00` → bytes on disk `00 50 53 14`.
Decode: reverse bytes, unpack nibbles, ×10 Hz. 0xFFFFFFFF (only for TX freq) = unset.
(source: both, bit-identical examples; verified: cross)

### 11.2 Tones (CTCSS/DCS) — u16, stored LE

- `0xFFFF` = none.
- bits 15–14 = type: `0` CTCSS, `2` DCS normal, `3` DCS inverted (`1` unused).
- CTCSS: bits 13–0 = four BCD nibbles, tone in 0.1 Hz: 127.3 Hz → nibbles 1,2,7,3 →
  word 0x1273 → bytes `73 12`.
- DCS: three low nibbles = the **octal** DCS code, one octal digit per nibble:
  D023N → 0o023 → word 0x8023 → bytes `23 80`; D023I → 0xC023.

(source: both, matching examples; verified: cross)

### 11.3 Strings

ASCII, 0x00-terminated when shorter than the field. Padding after the terminator:
qdmr writes 0x00s; dm32-spec observes 0xFF padding in CPS-written radios (§12 C2).
Decoder: stop at first 0x00 or 0xFF or field end. Encoder: 0x00-pad until C2 is
resolved on hardware, then follow the radio.

## 12. Open conflicts / VERIFY ledger

| # | Topic | Position A | Position B | Resolution plan |
|---|---|---|---|---|
| C1 | Program-mode 0x02 reply payload | 8×0xFF (dm32-spec) | opaque (qdmr) | log at bring-up |
| C2 | String padding after terminator | 0xFF (dm32-spec/CPS) | 0x00 (qdmr) | inspect factory-golden read |
| C3 | Channel bytes 0x1D/0x20 semantics | FM feature bits / color code (dm32-spec) | DMR ACK-flags+TS+CC / APRS channel (qdmr) | overlay hypothesis; flip CC via radio keypad, re-read, diff (`dev diffbin`) |
| C4 | Block 0x10 contents | analog emergency (dm32-spec) | menu/extended settings + encryption keys (qdmr) | decode factory-golden with both templates |
| C5 | Extension records per block | 2048 (dm32-spec) | 2047 (qdmr) | needs >2046 channels to matter; defer, encode ≤2047 |
| C6 | Contact limit | 850 (capacity) | 800 (qdmr limit) | encode-limit 800; CPS check later |
| C7 | Scan bank tail (mode+ranges) units | — | unknown | passthrough; diff after changing scan range in radio menu |
| C8 | Scan list priority fields 0x0E–0x17 | nibble selectors (qdmr) | typed u16 refs, −2 bias (dm32-spec) | builder writes zeros; diff when user sets priorities via keypad |
| C9 | Radio-ID bank count width | u32 (dm32-spec) | u8 (qdmr) | write low byte + zero next 3; inspect read |
| C10 | Zone/channel name capacity | 11 (dm32-spec) vs 16 (qdmr) zone name | — | CPS UI limit unknown; encode ≤16, builder default trims to 16 (§ channel) |

## 13. Factory CPS `.data` container — TBD

The Windows CPS saves ~644 KiB `.data` files; relationship to the wire image (header?
wrapper? full physical dump?) is **unknown until the user supplies CPS saves**
(implementation-spec §3.1 user task). `dev decode` will accept both once mapped;
this section then becomes normative for the mapping.

## 14. Volatile regions — TBD at ladder step 2

Filled from the read-×3 byte-stability experiment; consumed by `Compare()`.
Format: list of `[virtStart, virtEnd)` ranges with reason.
(none known yet)
