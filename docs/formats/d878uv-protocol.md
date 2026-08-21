# AnyTone AT-D878UVII+ — serial programming protocol

Normative for `Plugmatic.Radios.D878uv/Protocol`. Same discipline as the DM-32UV docs
(spec §3.2): facts are extracted here from references, cross-validated against at least
two independent sources, and implemented **from this document**. Every fact carries
`source:` and `verified:` annotations. Hardware evidence outranks every reference —
that rule already caught a wrong qdmr fact on the DM-32UV.

**Sources**
- `dmrconfig` (Serge Vakulenko, **BSD-3-Clause** — permissive; `serial.c`). Primary.
- `qdmr` (GPL-3.0 — **facts only**, never code; `anytone_interface.{hh,cc}`). Secondary.
- This radio: AT-D878UVII+, USB `28e9:018a` (GD32 Virtual ComPort), `/dev/ttyACM0`.

Where the two references disagree it is noted explicitly; neither is trusted over
hardware.

---

## 1. Transport

USB CDC-ACM virtual serial port — **not** a USB-serial cable like the DM-32UV's CH340.
The radio itself enumerates when powered on with the programming cable attached.

| Property | Value | source / verified |
|---|---|---|
| Baud | 115200-8-N-1 | dmrconfig + qdmr; verified: hw |
| Linux device | `/dev/ttyACMn` | verified: hw (`/dev/ttyACM0`) |
| USB ID (this unit) | `28e9:018a` GD32 Virtual ComPort | verified: hw |
| USB ID (older units) | `0483:5740` STM Virtual COM | source: dmrconfig; verified: no |
| Flow control | none; DTR/RTS not required | source: dmrconfig; verified: hw |
| Line coding | irrelevant to the radio | verified: hw — the CPS sets 921600 8N1 with DTR=0/RTS=1, Plugmatic uses 115200 with Linux's default DTR=1/RTS=1, and reads *and* writes behave identically either way. Ruled out as a cause of §5.2. |

Because the port is the radio's own USB stack, unplugging or powering the radio off
mid-session removes the device node outright — the transport layer must treat a
vanished port as a hard failure, never as a timeout to retry.

## 2. Session

All commands are raw byte sequences; there is no framing envelope around the session
commands themselves.

| Step | Host sends | Radio replies | Notes |
|---|---|---|---|
| Enter program mode | `PROGRAM` (7 B, ASCII) | `51 58 06` = `"QX"` + ACK | retry ≤ 10×, 500 ms apart, flushing input each attempt |
| Identify | `02` (1 B) | 16-byte radio-info record (§3) | must start `'I'` and end `0x06` |
| Leave program mode | `END` (3 B, ASCII) | `06` | **commits staged writes** (§5.1); best-effort only when nothing was written |

(source: dmrconfig `serial_identify`/`serial_close` + qdmr `enter_program_mode`;
verified: hw — both references agree byte-for-byte.)

The radio displays a programming/PC indication while in program mode and returns to
normal operation after `END`. **Always send `END`**: a session left open can leave the
radio in program mode until it is power-cycled, and after a write `END` is the only
thing that makes the write real (§5.1).

**The port re-enumerates after every session.** `END` (and any teardown) makes the
radio drop and re-create its USB device, so `/dev/ttyACM*` disappears for roughly a
second and comes back. Code that opens a second session must wait for the port to
reappear and retry the handshake rather than treating the gap as a failure.
(verified: hw.)

## 3. Identify response (16 bytes)

| Off | Size | Field |
|---|---|---|
| 0x00 | 1 | Fixed `'I'` (0x49) |
| 0x01 | 7 | Model, ASCII, NUL-padded — e.g. `D868UVE`, `D878UV`, `D878UV2` |
| 0x08 | 1 | Band/region code |
| 0x09 | 6 | Version string, ASCII NUL-padded — e.g. `V102` |
| 0x0F | 1 | Fixed `0x06` (ACK) |

Reference example (dmrconfig): `49 44 38 36 38 55 56 45 00 56 31 30 32 00 00 06`
→ model `D868UVE`, version `V102`.

(source: qdmr `RadioInfoResponse` struct + dmrconfig comment; the two agree on every
field boundary; verified: hw — see §7.)

**Model gate.** dmrconfig distinguishes `D868UVE`, `D878UV` and `D878UV2` by prefix
match on this field. Plugmatic's I2 preflight compares it against the requested
`--radio` and aborts on mismatch, with no override.

## 4. Read

Fixed-size, address-addressed reads over a flat 32-bit address space.

```
host:  52 aa aa aa aa nn                      'R', address (4 B, BIG-endian), length
radio: 57 aa aa aa aa nn <nn bytes> ss 06     'W', address echoed, length, data, sum, ACK
```

- **Address is big-endian.** This is the opposite of the DM-32UV's little-endian
  addressing — do not carry that assumption across. (source: dmrconfig
  `cmd[1] = addr>>24 …`; confirmed by qdmr `qToBigEndian`; verified: hw.)
- `ss` = 8-bit sum of the response bytes from the address field through the last data
  byte, i.e. `sum(reply[1 .. 5+len])`, truncated to 8 bits. Both references compute it
  identically. (verified: hw.)
- The trailing `06` is part of the response frame, not a separate ACK.
- Total response length is `len + 8`.

**Length**: dmrconfig reads `0x40` (64) bytes per frame; qdmr reads `0x10` (16) and
*rejects* any response whose length is not 16. Both are accepted by the radio — the 16
in qdmr is its own request size, not a radio limit. Plugmatic reads 64 for throughput
and falls back to 16 on any framing error. (source: both; verified: hw.)

## 5. Write

```
host:  57 aa aa aa aa nn <nn bytes> ss 06     'W', address (BE), length, data, sum, ACK
radio: 06
```

- Identical framing to the read *response* — the same struct travels in both
  directions.
- `ss` = 8-bit sum of `cmd[1 .. 5+len]` (address + length + data), as for reads.
  (verified: hw — recomputed over every frame of the CPS write capture; all match.)
- **Write length is 16 bytes.** Both references say so and the official CPS uses 16 for
  every one of its 6,022 write frames. Plugmatic writes no other length.
  (source: dmrconfig `DATASZ = 16`, qdmr `size = 16`; verified: hw + CPS capture.)
- A write is acknowledged with a bare `0x06`. **The ACK means "staged", not "applied"** —
  see §5.1. Anything else aborts the session; write frames are never retried (a
  half-applied retry is worse than a clean failure — same rule as the DM-32UV,
  protocol §8).

### 5.1 Writes are staged and commit on `END` (hw-verified, 2026-08-20)

This radio does **not** apply a write when it acknowledges it. Writes accumulate in a
staging area that is invisible to reads, and the session teardown decides their fate.
Four rules, each established by direct experiment on this unit (D878UV2, fw **V101**):

| # | Rule | Evidence |
|---|---|---|
| W1 | `END` commits every write staged in the session | write, `END`, reopen, re-read → new byte present |
| W2 | Dropping the port without `END` discards everything staged | write, close port, reopen, re-read → original byte |
| W3 | **Any read after a write discards everything staged** | write, read, `END`, reopen → original byte. True whether the read targets the written address or an unrelated one |
| W4 | Reads *before* the first write are harmless | read, write, `END`, reopen → new byte present |

Writes staged across several regions in one session commit together (verified with two
writes 256 KB apart — both landed).

These four rules are necessary but **not sufficient** to write safely — §5.4 is the
other half, and it is the dangerous one.

Consequences, and they are not optional:

- **A session is two-phase: all reads, then all writes, then `END`.** Once a write has
  been sent, a read is a data-loss bug, not a diagnostic. `D878uvProtocol` enforces this
  by throwing on a read after a write rather than letting the radio silently discard.
- **Verification must happen in a new session.** Reading back in the writing session
  both returns stale bytes *and* destroys the write it was trying to check.
- **`END` is a commit, not best-effort teardown.** After writes it must be sent and its
  `0x06` confirmed; a swallowed failure there silently discards the whole codeplug.

This matches the official CPS exactly: its write session is `PROGRAM`, identify, one
read of `0x02FA0020`, 6,022 back-to-back `W` frames, `END` — and **not one read after
the first write**. (source: CPS USB capture, `Program → Write to radio`, Wireshark
4.6.8/USBPcap; verified: hw.)

### 5.2 Why this looked like "the radio ignores writes" (superseded)

An earlier round of testing concluded write support was impossible here. It was wrong,
and the way it was wrong is worth keeping:

- Its probe did *read, write, read-back, compare* in one session. The read-back both
  returned the pre-write bytes (W3 makes reads show committed memory only) **and**
  discarded the write it was checking. Every escalation of that probe failed for the
  same reason, which read as consistent confirmation.
- Its cross-session check — the one test that could have caught this — used
  `0x00841200` and `0x00FC07C0`, addresses the CPS never writes and which are not
  backed by storage. They read `FF` before and after, so the one correct experiment
  landed on the one class of address that cannot answer the question.

**The lesson to carry:** when a write is acknowledged and appears not to land, prove
the *target address is writable at all* before concluding the write path is broken —
pick an address the vendor tool itself writes. Also note the corrected form of the
earlier lesson: a mutation test is still mandatory, but on this radio it must read back
in a **separate session**, or it reports a false negative just as loudly as a no-op
writeback reports a false positive.

### 5.3 What the CPS writes

The CPS writes **96,352 bytes** in 6,022 frames (≈3.4 s), not the whole 1.63 MB image:
only the records the bitmaps mark as allocated, plus the bitmaps and settings. Its gaps
are deliberate — see §5.4 for why writing nothing to an address is how the CPS says
"this slot is empty".

### 5.4 A write erases 256 KB around it (hw-verified, 2026-08-20)

**This is the fact that governs everything about writing to this radio.**

A 16-byte write does not modify 16 bytes. It erases the entire aligned **0x40000
(256 KB) block** containing the address and reprograms only the bytes staged in that
same session. Every other byte in the block comes back `0xFF`.

| Evidence | Result |
|---|---|
| 64 marks laid 0x1000 apart across `0x00880000-0x008BF000`, committed | all 64 present |
| one 16-byte write at `0x00888000`, committed | **62 marks erased, 0 survived** — the whole 0x40000 block |
| a 16-byte probe at `0x00800030` | erased `0x00800000-0x0083FFFF`, including 5,224 bytes of live channel data |
| a 16-byte probe at `0x00840000` | erased `0x00840000-0x0087FFFF`; `0x00800030` survived it |

The last row bounds the block from both sides: `0x00800030` and `0x00840000` are 0x3FFD0
apart and land in *different* blocks, so the affected span is 0x40000, aligned — which is
also the channel-bank stride `BetweenChannelBanks`.

**Caveat added 2026-08-21.** "0x40000 goes FF" is what was *observed*; it is not
necessarily one 0x40000 erase page. Some 0x20000 spans read back duplicated (§5.5), so a
128 KB erase would look like 256 KB through that duplication. Treat 0x40000 as the blast
radius to check for damage, not as a proven page size.

This reframes §5.1 entirely. Staging is not an optimisation; it is how the radio
assembles a complete block before erasing and reprogramming it. It also explains the
CPS's write pattern: every contiguous run it sends is a block being rewritten in full,
and the gaps inside those runs are slots it intends to leave erased.

**Consequence: a write may only be issued for a block whose every byte is known.**
Writing "just the chunk that changed" destroys the rest of the block. Plugmatic's write
plan therefore works in whole erase blocks and **refuses** any block the region table
does not completely describe, naming the block and how much of it is uncovered.

**Firmware flash signatures.** The last 16 bytes of every 0x20000 half-block hold
`FF FF FF FF 22 33 44 55 FF FF FF FF 55 55 AA AA`. This is firmware-managed, not
codeplug data: it is present in blocks Plugmatic has never written, and it is back in
place after an erase. Anything asking "is this block empty?" must ignore it
(`Layout.IsFlashMarker`).

### 5.5 The block-rewrite approach, and why hardware rejected it (2026-08-21)

§5.4 says a write erases its whole 0x40000 window, so the obvious fix is: read the window
in full, splice the modelled bytes over it, rewrite it complete. Unmodelled bytes make the
round trip verbatim and nobody has to understand them. That was implemented and tested
against a fake radio that models block erase, then run on hardware with a one-byte channel
rename (`'1'` → `'Z'` at `0x0080002B`).

**Over the target window it worked perfectly.** A rawdump of `0x00800000-0x0083FFFF` before
and after showed exactly one byte changed, with the non-FF byte count identical either side
(20,264). Preservation of unmodelled bytes did what it promised.

**It also silently copied channel bank 0 over channel bank 1.** The 4,854 bytes of
`channels[1]` came back holding bank 0's records — `0x00840020` read `Channel 1` where
`GMRS RPTR 7` belonged. The transfer log settles what the host did: 1,282 write frames, all
inside `0x00800000-0x0083FFF0`, **not one frame addressed to `0x00840000`**. The radio did
that itself, in response to what we wrote.

Two observations bound the cause, and neither was known before:

- **Some 0x20000 spans read back twice.** `0x00800000-0x00820000` and
  `0x00820000-0x00840000` are byte-identical (131,072/131,072), as are the two halves of
  bank 1, `zoneChannels`, `settings` and `zoneNames`. Others are *not*: `contacts[0]` at
  `0x02680000` is real data with `0x026A0000` erased, and `0x024C0000` / `0x024E0000` hold
  different data. So it is not a blanket address-decode rule, and this project cannot
  currently say which addresses are distinct storage.
- The duplicate at `0x00820000` **pre-dates the write** — it is in the rawdump taken before
  any write that day. It was never noticed because §5.4's model was only ever checked
  against CPS-*written* addresses, never against the gaps between them.

The write went wrong precisely where it left the vendor's footprint: everything the CPS
writes for bank 0 lives below `0x00804000`, and the frames that had no counterpart in the
capture were the ones above `0x00820000` — the duplicated half.

### 5.6 Why write support is off — and what would turn it on

`D878uvRadio.SupportsWrite` is **false** and `WriteImageAsync` refuses. The protocol is
solved; the **addressing** is not. Rewriting an erase window is only safe if every address
in it is real, independent storage, and §5.5 shows that is not true and not yet predictable.

The way forward is to stop deriving write addresses from the region table and derive them
from the vendor instead:

1. **Model the writable address set from the CPS capture** — the allocated records, the
   bitmaps and the settings blocks it writes, and nothing else. The capture is 96,352 bytes
   across known ranges, and a radio programmed that way demonstrably works.
2. **Never write an address the CPS does not.** Every failure so far, including the two
   erased channel banks in §5.7 and the bank copy above, came from writing outside that set.
3. Only then re-run the ladder, checking damage over the whole erase window with
   `plugmatic dev rawdump`, and — the part that caught this one — over *neighbouring* banks
   too, not just the window that was written.

Until that exists, `plugmatic read` and backup are unaffected and complete.

### 5.7 What this has cost, and the rules that come out of it

Establishing §5.4 destroyed live data on the test radio: two 16-byte probes at
`0x00800030` and `0x00840000` erased channel banks 0 and 1, losing 5,224 bytes of
channel records — the whole of "Channel 1" among them. It was fully recovered, because
the CPS write capture is a byte-exact record of what those blocks should contain, and
replaying the 672 capture frames for those two blocks restored all 32,768 bytes with
zero mismatches.

The probe that did the damage had *passed*. It read its 16 bytes back, saw the mutation,
restored the original, and reported success — because it only ever looked at the 16
bytes it wrote. The blast radius was 16,384× the size of the thing being verified.

**Rule 1: before writing to a radio for the first time, establish the erase granularity,
and check for damage over that whole granularity — not over the bytes you wrote.** A
mutation test that only re-reads its own target cannot see the crater around it.
`plugmatic dev writetest` now refuses to probe any block that holds data, and works only in
flash it has confirmed is empty.

**Rule 2 (2026-08-21): check the neighbours too.** The block-rewrite attempt in §5.5 passed
a full 256 KB before/after rawdump of the window it wrote, and had still corrupted the bank
next door. Erase-granularity verification is a floor, not a ceiling — the only sufficient
check is a full read compared against the intended image, which is what caught it.

**Rule 3: on a radio whose vendor tool has been captured, treat the captured address set as
the writable address set.** Every byte lost on this radio was at an address the CPS never
writes. Staying inside the vendor's footprint is not a heuristic here; it is the only
evidence-backed definition of "safe to write" this project has.

## 6. Safety rules (D14 / I8)

- No bootloader, DFU or firmware-update sequence appears in this document, and none may
  be implemented. dmrconfig's separate DFU path (`dfu-libusb.c`) is deliberately **not**
  a source for this project.
- Writes are bounds-checked against the codeplug region table in
  `d878uv-format.md`; addresses outside it are refused before a byte is sent.
- Read-class probing is unrestricted (spec §3.3); write-class frames are only sent as
  part of the §3.5 ladder with the user present.

## 7. Hardware verification log

| Date | Fact | Result |
|---|---|---|
| 2026-08-10 | `PROGRAM` → `QX`+ACK handshake | **verified** |
| 2026-08-10 | Identify layout + model string | **verified**: `49 44 38 37 38 55 56 32 0E 56 31 30 31 00 00 06` → model `D878UV2`, band code 0x0E, version `V101` |
| 2026-08-10 | Read framing, big-endian address, checksum, 64-byte chunks | **verified** — 1.66 MB read with zero checksum failures |
| 2026-08-10 | `END` teardown | **verified** |
| 2026-08-20 | CPS read capture replays our exact read framing | **verified** — control sample, confirms the capture pipeline |
| 2026-08-20 | Write framing (`57` + BE address + `10` + data + sum + `06` → `06`) | **verified** — byte-identical to the CPS's 6,022 write frames |
| 2026-08-20 | W1 `END` commits staged writes | **verified** |
| 2026-08-20 | W2 close without `END` discards | **verified** |
| 2026-08-20 | W3 a read after a write discards (any address) | **verified** |
| 2026-08-20 | W4 reads before the first write are harmless | **verified** |
| 2026-08-20 | Writes staged across regions commit together | **verified** (two targets 256 KB apart) |
| 2026-08-20 | Line coding / DTR / RTS do not gate writes | **verified** — ruled out |
| 2026-08-20 | Erase block is 0x40000, aligned | **verified** — 64-mark sweep; one write erased all of `0x00880000-0x008BFFFF` |
| 2026-08-20 | A write erases the block and keeps only what the session staged | **verified** — cost 5,224 bytes of live channel data, since restored |
| 2026-08-20 | Firmware flash signature at the end of every 0x20000 half-block | **verified** — present in never-written blocks, restored after erase |
| 2026-08-20 | Damaged blocks restored from the CPS capture | **verified** — 32,768 bytes, zero mismatches |
| 2026-08-21 | Block-rewrite write path preserves unmodelled bytes in the written window | **verified** — 1 byte changed in 256 KB, non-FF count identical |
| 2026-08-21 | …and corrupts the *next* bank: writing only 0x00800000-0x0083FFF0 put bank 0's records into bank 1 | **verified** — from the transfer log; write support withdrawn |
| 2026-08-21 | Some 0x20000 spans read back duplicated (channel banks, settings, zoneNames, zoneChannels), others do not (contacts, 0x024C0000) | **verified** — pre-dates any write |
| 2026-08-21 | Both banks restored by replaying the CPS frames for them | **verified** — full image byte-identical to the pre-write read; window rawdump 0 bytes differ |

**USB re-enumeration (hw-verified).** This radio drops and re-creates its USB device
when a session ends, so `/dev/ttyACM*` is recreated after *every* command and any
`chmod` on it is lost. Use a udev rule or `dialout` membership; a per-session chmod is
not workable here (unlike the DM-32UV's separate USB-serial cable).
