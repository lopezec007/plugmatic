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
| Leave program mode | `END` (3 B, ASCII) | `06` | best-effort on teardown |

(source: dmrconfig `serial_identify`/`serial_close` + qdmr `enter_program_mode`;
verified: hw — both references agree byte-for-byte.)

The radio displays a programming/PC indication while in program mode and returns to
normal operation after `END`. **Always send `END`**: a session left open can leave the
radio in program mode until it is power-cycled.

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
- **Write length is 16 bytes**, in both references, even though reads may be 64.
  Plugmatic does not write any other length until hardware says otherwise.
  (source: dmrconfig `DATASZ = 16` for writes + qdmr `size = 16`; verified: pending.)
- A write is acknowledged with a bare `0x06`. Anything else aborts the session; write
  frames are never retried (a half-applied retry is worse than a clean failure —
  same rule as the DM-32UV, protocol §8).

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
| — | Write framing | not attempted (no writes before the format doc is complete) |

**USB re-enumeration (hw-verified).** This radio drops and re-creates its USB device
when a session ends, so `/dev/ttyACM*` is recreated after *every* command and any
`chmod` on it is lost. Use a udev rule or `dialout` membership; a per-session chmod is
not workable here (unlike the DM-32UV's separate USB-serial cable).
