# DM-32UV Wire Protocol (normative)

Status: **draft — pre-hardware**. Every fact below is annotated:

- `source:` where the fact was learned. `qdmr-observed` = read from qdmr ≥ 0.15 source
  (facts only, per repo license discipline; no code copied or translated).
  `dm32-spec` = the MIT-licensed community protocol documentation
  (github.com/infamy/DM32-Protocol-Spec, incl. its `serial_capture_example.txt` capture
  of the factory CPS).
- `verified:` how the fact was confirmed against a second source or live hardware.
  `cross` = qdmr and dm32-spec agree independently. `hw` = confirmed against our own
  radio (fill in run dir). `pending` = single-source; treat as provisional.

The FakeRadio test double implements the radio side of THIS document. When a live
capture contradicts this document, fix the document (and FakeRadio), then the code.

---

## 1. Transport

| Fact | Value | source / verified |
|---|---|---|
| Physical | USB-serial programming cable ("K-plug"). The USB bridge belongs to the **cable**, not the radio. Our cable: CH340, VID:PID `1A86:7523`, enumerates as `/dev/ttyUSB0` / `COMx`. Other cables (e.g. Prolific `067B:23A3`) exist in the wild. | source: lsusb on our cable + qdmr-observed (Prolific IDs); verified: hw (enumeration only) |
| Serial parameters | **115200 baud, 8 data bits, no parity, 1 stop bit, no flow control** | source: qdmr-observed + dm32-spec; verified: cross |
| Control lines on open | RTS **de-asserted**, DTR **asserted** | source: qdmr-observed; verified: pending |
| Per-operation timeout | 1000 ms is safe for everything except block writes; block writes need up to **5000 ms** | source: qdmr-observed (1 s everywhere) + dm32-spec (500 ms reads / 5 s writes); verified: cross (union taken) |
| Byte order | All multi-byte integers in frames are **little-endian** | source: both; verified: cross |

There is no checksum anywhere in the framing. Integrity is provided by our
read-back-and-compare step, not the protocol.

## 2. Session state machine

```
Closed → Open ─(handshake §3)→ SysInfo ─(PROGRAM §5)→ Program ─(R/W §6)→ Program
                                                            └─(teardown §7)→ Closed
```

Read/write commands are only accepted in `Program` state. V-frame queries happen in
`SysInfo` state. The sequence and its ordering are mandatory.
(source: both; verified: cross)

## 3. Handshake (Open → SysInfo)

Pace: wait ≥ 500 ms after opening the port before the first byte; ≥ 10 ms between
subsequent commands (qdmr uses 100 ms; use 20 ms default, it is well within both).
Flush the input buffer before PSEARCH. (source: both; verified: cross)

### 3.1 PSEARCH — identify

- Send ASCII `PSEARCH` (7 bytes, no terminator).
- Receive **8 bytes**: `0x06` (ACK) followed by a 7-character ASCII model identifier.
- The DM-32UV identifies as **`DP570UV`**. Anything else = wrong/unsupported radio.
- Retry up to 3 times, 500 ms apart, clearing input before each try (the CH340 can
  swallow the first attempt right after open).

(source: both, incl. capture lines 3–5; verified: cross)

### 3.2 PASSSTA — password status

- Send ASCII `PASSSTA` (7 bytes).
- Receive **3 bytes**: `0x50` ('P') + 2 status bytes. Observed values `50 00 00` and
  `50 FF FF`; both fine. Only the first byte is validated.

(source: both; verified: cross)

### 3.3 SYSINFO

- Send ASCII `SYSINFO` (7 bytes). Receive **1 byte**: `0x06` ACK.

(source: both; verified: cross)

## 4. V-frame queries (SysInfo state)

Request: 5 bytes `56 00 00 00 <id>` (`'V'`, three zero bytes, value id).
Response: `56 <id> <len>` then `len` payload bytes. Validate the echoed id.

| id | Payload | Meaning |
|---|---|---|
| 0x01 | ASCII, 14–15 B | Firmware version, e.g. `DM32.01.01.046`, `DM32.01.L01.048` (qdmr's supported version; our radio: fill in at bring-up) |
| 0x03 | ASCII 10 B | Firmware build date, e.g. `2022-06-27` |
| 0x04 | ASCII 12 B | DSP version |
| 0x05 | ASCII 12 B | Radio version |
| 0x0B | ASCII 12 B | Codeplug version |
| 0x0A | 8 B: two u32 LE | **Main config (codeplug) physical memory range**, start + inclusive end, e.g. `0x001000–0x0C8FFF` (800 KiB, 200 blocks) |
| 0x0F | 8 B | Contact-DB physical range (callsign DB; out of scope v1) |
| 0x06/0x07/0x08/0x09/0x0E | 8 B | Other memory ranges (audio index, compact table, zones, emergency, memberships) — not needed by plugmatic; listed for completeness |
| 0x02, 0x10 | binary | Unknown |
| 0x0D | 0 or 64 B | Capabilities; request quirk: byte 3 is `0x40` (`56 00 00 40 0D`). Optional; most firmware returns length 0. Do not rely on it. |

Required for plugmatic: **0x01** (manifest), **0x03** (manifest), **0x0A** (read/write
bounds — this is also the I8 bounds source). All others optional.

(source: both — table & examples dm32-spec, subset confirmed qdmr; verified: cross for
0x01/0x03/0x0A, pending for the rest)

## 5. Entering program mode (SysInfo → Program)

Three exchanges, in order, each must succeed:

1. Send 12 bytes: `FF FF FF FF 0C` + ASCII `PROGRAM` → receive `0x06`.
2. Send 1 byte `0x02` → receive **8 bytes**. dm32-spec says the payload is
   `FF ×8`; qdmr treats it as opaque. Treat as opaque 8 bytes, log it.
   (verified: cross on length; pending on content)
3. Send 1 byte `0x06` → receive `0x06`.

(source: both, capture lines 74–82; verified: cross)

## 6. Memory access (Program state)

The radio exposes a flat **physical** address space; the codeplug region within it is
reported by V-frame 0x0A. Data is organised in **4 KiB blocks**; the *last byte of each
block* (offset +0xFFF) is a **metadata byte** holding the block's *virtual* block
number (see the format doc §2 for the virtual layout).

### 6.1 Read — `'R'` (0x52)

Request (6 B): `52` + address (u24 LE) + length (u16 LE, max 0x1000).
Response: `57` ('W'!) + echoed address (u24 LE) + echoed length (u16 LE) + payload.

- Response type byte for a READ is `0x57` — do not confuse with a write frame.
- Reads may be any length ≤ 4096 and need not be aligned, but bulk reads should be
  issued as aligned 4 KiB blocks.
- Pace: ~5 ms between 1-byte metadata probes, ~25 ms between 4 KiB block reads.

Capture example (metadata probe): send `52 FF 1F 00 01 00`, receive
`57 FF 1F 00 01 00 07` (1 byte 0x07 at 0x001FFF). (source: both; verified: cross)

### 6.2 Write — `'W'` (0x57)

Request: `57` + address (u24 LE) + length (u16 LE) + payload; response **1 byte**
`0x06` ACK (`0x15` = NAK/rejected).

- Writes are issued as aligned **whole 4 KiB blocks** whose last byte carries the
  virtual block number (the codec is responsible for stamping it).
- Allow 5000 ms for the ACK; pace 10–50 ms between block writes.
- **I8 enforcement:** exactly two write targets are legal — an address range fully
  inside the V-frame-0x0A region, or the exact **adoption block 0xFF000** (§6.5;
  note it lies *outside* the reported region on observed radios — the region end
  0x0C8FFF < 0xFF000 — yet is the documented CPS/qdmr target for new blocks; it is
  still codeplug config memory, not firmware). Everything else is refused by the
  protocol layer. No bootloader, DFU, or firmware sequences exist in this codebase
  (D14); nothing outside §3–§7 of this document may ever be transmitted by write
  paths.

(source: both; verified: cross. The dm32-spec text describing "byte 4102: metadata"
is its own off-by-one confusion — its example code writes the metadata byte as the
last payload byte, which matches qdmr exactly.)

### 6.3 Address-map discovery (before any bulk read or write)

The mapping physical block → virtual block is dynamic (wear levelling / allocation
order differs per radio & firmware). Discover it fresh in every session:

1. For each 4 KiB block `p` in the 0x0A range: read 1 byte at `p + 0xFFF`.
2. Value `0x00` or `0xFF` → block unallocated; skip.
3. Otherwise value `v` maps physical block `p` ↔ virtual address `v << 12`.
4. Duplicate `v` values: **fail loudly** (should not happen; a capture proving
   otherwise updates this doc). (verified: pending — policy is ours)

**Never hardcode physical addresses.** Known first-channel physical addresses vary
wildly across radios/firmware (observed: 0x00A00A, 0x00200C, 0x00E008, 0x008006,
0x070000). (source: both; verified: cross)

### 6.4 Full codeplug read (ladder step 2, `read`, `dev dump`)

1. Discover address map (§6.3).
2. For every mapped physical block whose **virtual** address lies in the codeplug
   virtual window `[0x03000, 0x68000)` (format doc §2): read the 4 KiB block from its
   physical address; place it at its virtual address in the image.
3. The resulting sparse virtual image (+ the block list) is `read.bin` — see the
   format doc §1 for the container we store it in.

(source: qdmr-observed (window & flow); verified: pending)

### 6.5 Full codeplug write (`write`, `dev writeback`)

1. Discover address map (§6.3) — the pre-write read in the same run already did this;
   reuse that session's map.
2. For each 4 KiB block of the encoded virtual image, in ascending virtual order:
   stamp last byte = virtual block number; if the virtual block is already mapped,
   write to its existing physical address; **if unmapped, write the block to physical
   `0xFF000`** — the radio's allocator adopts it based on the stamped metadata byte.
   (source: qdmr-observed; verified: pending — exercised at ladder step 5+ only,
   no-op writeback never hits this path)
3. Every write bounds-checked per §6.2.

## 7. Session teardown

Conflict between sources — both recorded:

- dm32-spec: an explicit exit command exists: 12 bytes `FF FF FF FF 0C` + ASCII `END`
  + `00 00 00 00`… (source doc shows `END` padded with zeros to 12 bytes total),
  radio replies `0x06` and reboots.
- qdmr: states no exit command exists; it de-asserts **DTR for 500 ms** which resets
  the radio, then closes the port.

Policy: send the `END` frame, tolerate a missing/short reply (1 s timeout), then
de-assert DTR 500 ms and close. Either mechanism alone reboots the radio; doing both
is harmless. Resolve at bring-up and update `verified:`. (verified: pending)

## 8. Error handling policy (ours, asserted by FakeRadio tests)

- Unexpected byte where an ACK is expected → abort the operation with a decoded
  message (`0x15` = radio rejected the command).
- Timeout mid-read → retry the *current block* up to 2 times (fresh `R` frame is
  idempotent), then abort.
- Timeout mid-write → **no automatic retry of write frames**; abort, finalize the
  run `outcome: failed`, print the §7.5 recovery instructions. (A write frame that
  may have half-landed must not be blindly repeated; the recovery path is a fresh
  full write from `pre-write.bin`.)
- All traffic (both directions) hex-dumped to the run's `transfer.log`.

## 9. Capture assets

- `tests/fixtures/captures/cps_session_example.txt` — copy of dm32-spec's
  `serial_capture_example.txt` (MIT; attribution in NOTICE). Used by protocol tests
  as ground truth for handshake/V-frame/read framing.
- Our own captures land in `tests/fixtures/captures/` as produced (§3.3 of the
  implementation spec); named `NN-purpose.txt`, referenced from `verified:` fields.
