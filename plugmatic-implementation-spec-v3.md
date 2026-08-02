# Plugmatic — DMR Radio Auto-Programmer — Implementation Specification

**Audience:** Claude Code (VS Code), implementing this project from scratch.
**Status of this document:** authoritative spec. Decisions marked **LOCKED** are final —
do not revisit them without asking the user. Items marked **VERIFY** must be checked
against the real environment/hardware before the dependent code is considered done.
**Spec version:** 3.0 (2026-08-01) — supersedes 2.0. Headline change: **no qdmr
dependency**; the DM-32UV codeplug format and wire protocol are implemented natively.

---

## 0. Instructions to the implementer (Claude Code)

1. Implement in the phase order of §10. Do not start a phase before the prior phase's
   definition-of-done is met. The order deliberately front-loads protocol risk.
2. All business logic MUST be testable without radio hardware and without network access,
   via the abstractions in §4 (notably `ISerialLink` + the in-process `FakeRadio`).
3. MUST/MUST NOT = hard requirement. SHOULD = default unless documented otherwise.
   MAY = your discretion.
4. When reality contradicts this spec (an offset, a command byte, a VID/PID), update the
   protocol/format docs under `docs/formats/`, note it in `docs/deviations.md`, and ask
   the user when the deviation is architectural.
5. **A real Baofeng DM-32UV is available for development.** The user explicitly accepts
   brick risk in service of development speed — but follow the bring-up ladder (§3.5)
   anyway; not bricking it is still preferred. Hardware sessions are driven through the
   `plugmatic dev` commands (§7.9) and the checklists in §9.3; the user runs the radio
   end.
6. Never invoke a hardware write from automated tests. Hardware paths are exercised only
   via §9.3 with the user present.

---

## 1. Overview

A cross-platform CLI tool that, given a location (ZIP, lat/long, or UTM) and a radio
model, fetches repeater data from authoritative sources, generates a complete codeplug,
and programs the radio — always reading and archiving the radio's existing codeplug
before any write. First supported radio: **Baofeng DM-32UV**, via a **natively
implemented** binary codeplug codec and serial wire protocol (no external CPS, no qdmr).

---

## 2. Locked decisions

| # | Decision | Value |
|---|---|---|
| D1 | Language / framework | C# on **.NET 10** (current LTS). Target `net10.0`. **LOCKED** |
| D2 | Platforms | **Linux (Debian 13 "Trixie"+) and Windows 11.** Rationale: with qdmr gone, radio I/O is plain USB-serial via `System.IO.Ports`, identical on both OSes — Windows support is trivial, so it stays. *Escape hatch:* if protocol RE reveals the radio requires raw-USB (non-serial) transport, Windows support is dropped and the project becomes Linux-only. **LOCKED** |
| D3 | License | **0BSD**. `LICENSE` at repo root. **LOCKED** |
| D4 | Radio I/O strategy | **Native implementation** of the DM-32UV codeplug binary format and serial programming protocol, in this repo. No qdmr/dmrconf dependency at build time or runtime. **LOCKED** |
| D5 | qdmr as reference — license hygiene | qdmr (GPL-3.0) MAY be consulted as a *reference for protocol and format facts* only, via the discipline in §3.2: facts are extracted into original written specs under `docs/formats/`, implementation is written from those specs, and every fact is independently verified against captures/diffs. **No qdmr code is copied, translated, or ported.** **LOCKED** |
| D6 | Data directory contract | `~/Plugmatic/` with `config/` and `radios/` per §5. **LOCKED** |
| D7 | Read-before-write invariant | It is **impossible** to write without a fresh read in the same run. No flag exists to skip it. **LOCKED** |
| D8 | GMRS policy | Included; RX-only by default; TX enabled only after explicit typed acknowledgment; per-channel-class bandwidth/power enforcement per §6.3.3. **LOCKED** |
| D9 | 467-interstitial channels | Always RX-only regardless of acknowledgment (0.5 W ERP / integrated-antenna requirement cannot be met by this hardware). Confirmed by user. **LOCKED** |
| D10 | NOAA policy | NOAA WX frequencies included **by default** in a dedicated RX-only zone per §6.3.4. **LOCKED** |
| D11 | Contacts database | Out of scope for v1. Talkgroup contacts required by channels ARE in scope; RadioID 50k user-DB loading is NOT. **LOCKED** |
| D12 | Interface | CLI only (System.CommandLine). **LOCKED** |
| D13 | Timestamps | UTC everywhere; run dirs named `yyyyMMdd_HHmmss`; ISO-8601 in manifests. **LOCKED** |
| D14 | Firmware safety | The tool MUST NOT implement or emit bootloader/DFU/firmware-update command sequences. Write operations are restricted to codeplug/config memory regions identified in the format spec. **LOCKED** |

---

## 3. Protocol & format acquisition (now the critical path)

Two artifacts must be produced and maintained under `docs/formats/`, and they are the
normative source for all codec/transport code:

- `dm32uv-format.md` — the codeplug binary layout: memory map, record tables (channels,
  zones, contacts, RX group lists, scan lists, settings), field offsets, types,
  endianness, encodings (BCD frequencies, string encoding), padding, and the mapping
  between the factory CPS `.data` save file and the on-wire radio image.
- `dm32uv-protocol.md` — the wire protocol: serial parameters, handshake, identify
  response, read/write command framing (command byte, address, length, payload,
  checksum algorithm), ACK/NAK semantics, block size, timing, session teardown.

Three information sources feed these documents; **cross-validate every fact against at
least two of them** before relying on it in code:

### 3.1 Factory CPS file diffing (no hardware risk)

Performed on the Windows box with the factory CPS (v1.41+):
save a minimal `base.data` → mutate exactly one field in CPS → save → binary diff
(`fc /b a.data b.data` or `cmp -l`) → record offset/encoding. Iterate per field family:
frequencies (expect packed BCD, check nibble order), names (ASCII vs UTF-16LE), color
code/slot bitfields, 24-bit LE talkgroup IDs, enable flags. Add a second channel/zone to
measure record stride and table base addresses. Public GitHub DM-32UV `.data` files
(~644 KB) provide extra validation samples; the user's own CPS saves are the primary
corpus.

### 3.2 qdmr as a fact reference (license discipline — D5)

qdmr ≥ 0.14 implements this radio; its source and generated codeplug documentation
encode the facts we need. Procedure, strictly:
1. Read qdmr source/docs to *identify facts* (an offset, a command byte, a checksum
   algorithm name).
2. Write the fact, in original prose, into the relevant `docs/formats/*.md` with a
   `source: qdmr-observed, verified: <how>` annotation.
3. Implement **from the doc**, never with qdmr source open in the editor, and never by
   translating qdmr code structure.
4. Flip `verified:` only after the fact is confirmed by §3.1 or §3.3 evidence.
Protocol facts are not copyrightable; expression is. This discipline keeps 0BSD clean.

### 3.3 Live capture and probing (real radio available)

- **Passive capture (ground truth):** on Windows, USBPcap + Wireshark while the factory
  CPS performs a Read and (separately) a Write of a known `.data` file:
  `USBPcapCMD.exe -d \\.\USBPcapN -o cps_read.pcapng`, then extract bulk payloads:
  `tshark -r cps_write.pcapng -Y "usb.capdata" -T fields -e usb.capdata` and concatenate.
  Alternative with cleaner output: a com0com virtual-pair logging bridge between CPS and
  the real port (serial-level logs beat USB frames).
- **Active probing (our tool, either OS):** once the handshake is understood from
  capture, `plugmatic dev` commands (§7.9) replay identify and read operations directly.
  **Probing/fuzzing is permitted for READ-class commands only**; write-class frames are
  only ever sent as byte-exact replays of captured CPS traffic or as §3.5 ladder steps.

### 3.4 Brick-risk posture

User accepts brick risk; minimize it anyway:
- Codeplug writes in this radio class target config memory, not firmware; the realistic
  failure mode is corrupted settings (recoverable via CPS/full restore), not death.
- The dangerous surface is bootloader/firmware mode — categorically banned (D14).
- Interrupted flash writes are the main corruption vector: document "battery charged,
  cable seated" in the hardware checklist; the write path MUST stream without
  long GC-pause-prone allocations mid-session and MUST complete or fail fast.
- Recovery assets: the first factory read is archived (§3.5 step 1, tagged
  `factory-golden` in its manifest); public DM-32UV firmware + CPS archives exist on
  GitHub as last-resort recovery references.

### 3.5 Hardware bring-up ladder (in order; each step gates the next)

1. **Identify:** replay handshake; parse model/firmware response. Archive it.
2. **Full read ×3:** byte-compare the three images (establishes byte-stability and any
   volatile regions → recorded in `dm32uv-format.md`). First read's run is tagged
   `factory-golden`.
3. **Decode:** native codec decodes the real image; cross-check against the CPS's own
   `.data` for the same radio state.
4. **No-op write:** write back the exact image just read. Radio must behave identically.
   This validates framing/checksum/teardown with zero semantic risk.
5. **Single-channel plug:** encode → write → verify on the radio's screen → read back.
6. **Full generated plug.**

---

## 4. Solution architecture

```
Location Resolver → Repeater Providers → Codeplug Builder (IR) ─┐
                                                                ▼
                                              Radio Codec (IR ⇄ binary image)
                                                                ▼
                                   Radio Protocol (framing) over ISerialLink (port I/O)
                                                                │
                                              Run Manager (~/Plugmatic/radios/<r>/<run>/)
```

### 4.1 Solution layout

```
plugmatic/
  LICENSE                          # 0BSD
  Plugmatic.sln
  src/
    Plugmatic.Core/                # IR, BuildProfile, validation, RunManager, manifest
    Plugmatic.Location/            # zip / latlong / utm resolvers
    Plugmatic.Providers/           # RepeaterBook, RadioID, BrandMeister, SQLite cache
    Plugmatic.Radios/              # shared: ISerialLink, IRadioProtocol, IRadioCodec,
                                   #   RadioCapabilities, port discovery
    Plugmatic.Radios.Dm32uv/
      Format/                      # binary layout: readers/writers per docs/formats/dm32uv-format.md
      Protocol/                    # wire protocol per docs/formats/dm32uv-protocol.md
    Plugmatic.Cli/                 # System.CommandLine host (incl. `dev` group)
  tests/
    Plugmatic.Tests/               # unit + integration; FakeRadio; fixture images
    fixtures/                      # sample .data files, captured images, canned API JSON
  profiles/
    colorado-default.yaml
  docs/
    formats/dm32uv-format.md       # normative (living) — §3
    formats/dm32uv-protocol.md     # normative (living) — §3
    hardware-checklist.md
    deviations.md
```

### 4.2 Core interfaces

```csharp
public interface ILocationResolver { GeoPoint Resolve(string input, LocationFormat? forced = null); }
public interface IRepeaterProvider { Task<IReadOnlyList<Repeater>> QueryAsync(GeoPoint center, Distance radius, RepeaterQueryOptions opts, CancellationToken ct); }
public interface ICodeplugBuilder  { BuildResult Build(IReadOnlyList<Repeater> repeaters, BuildProfile profile, GmrsPolicy gmrs); }

// Format layer — pure, synchronous, no I/O:
public interface IRadioCodec
{
    byte[] Encode(Codeplug ir);
    Codeplug Decode(ReadOnlyMemory<byte> image);
    RadioCapabilities Capabilities { get; }
    ImageComparison Compare(ReadOnlyMemory<byte> a, ReadOnlyMemory<byte> b); // honors volatile-region masks
}

// Wire layer — protocol over an abstract serial link:
public interface ISerialLink : IAsyncDisposable
{
    Task OpenAsync(SerialSettings settings, CancellationToken ct);     // 115200-8-N-1 default (VERIFY)
    ValueTask<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct);
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct);
}
public interface IRadioProtocol
{
    Task<RadioIdentity> IdentifyAsync(ISerialLink link, CancellationToken ct);
    Task<byte[]> ReadImageAsync(ISerialLink link, IProgress<TransferProgress> p, CancellationToken ct);
    Task WriteImageAsync(ISerialLink link, ReadOnlyMemory<byte> image, IProgress<TransferProgress> p, CancellationToken ct);
}

public interface IRunManager { RunContext CreateRun(string radioModel, string runType);
                               void Finalize(RunContext run, RunOutcome outcome); }
```

Implementations: `SerialPortLink` (System.IO.Ports; the only production `ISerialLink`),
`FakeRadioLink` (in-process test double implementing the radio side of
`dm32uv-protocol.md` — see §9). All protocol traffic (both directions, hex-dumped) is
logged to `transfer.log` in the current run directory.

NuGet (minimal): `System.CommandLine`, `System.IO.Ports`, `CoordinateSharp`,
`Microsoft.Data.Sqlite`, `YamlDotNet`, `Microsoft.Management.Infrastructure`
(Windows-only, VID/PID enumeration).

---

## 5. Filesystem contract — **LOCKED**

Root: `Path.Combine(Environment.GetFolderPath(SpecialFolder.UserProfile), "Plugmatic")`
→ `C:\Users\<user>\Plugmatic` (Windows), `/home/<user>/Plugmatic` (Linux).

```
~/Plugmatic/
  config/
    config.yaml               # non-secret settings
    credentials.dat           # DPAPI on Windows; libsecret if available on Linux, else 0600 file
    cache/repeaters.sqlite    # raw provider responses + timestamps (TTL default 7 days)
    profiles/colorado-default.yaml
  radios/
    dm32uv/
      20260801_142205/        # one dir per RUN, UTC now.ToString("yyyyMMdd_HHmmss")
      20260801_151830/
```

### 5.1 Run types and artifacts

| Run type | Files |
|---|---|
| `read` | `read.bin`, `read.yaml` (decoded IR), `transfer.log`, `manifest.json` |
| `write` | `pre-write.bin`, `pre-write.yaml`, `generated.yaml`, `generated.bin`, `post-write.bin`, `transfer.log`, `manifest.json` |
| `build` (no hardware) | `generated.yaml`, `manifest.json` |
| `dev` (§7.9 sessions) | whatever the command produces (`dump.bin`, `identify.txt`, ...) + `transfer.log` + `manifest.json` |

Writes are atomic (temp + move). Run dirs are append-only during a run, immutable after
finalize.

### 5.2 Manifest (`manifest.json`)

```json
{
  "runType": "write",
  "tags": [],                                  // e.g. ["factory-golden"] on the first hardware read
  "startedUtc": "2026-08-01T14:22:05Z",
  "finishedUtc": "2026-08-01T14:24:41Z",
  "outcome": "success",
  "toolVersion": "1.0.0",
  "formatSpecVersion": "dm32uv-format.md@<git-sha>",
  "protocolSpecVersion": "dm32uv-protocol.md@<git-sha>",
  "radio": { "model": "dm32uv", "reportedId": "<identify string>", "firmware": "<if reported>" },
  "port": "COM5",
  "inputs": {
    "locationRaw": "80525", "resolvedLat": 40.5384, "resolvedLon": -105.0512,
    "radiusKm": 96.6, "profile": "colorado-default", "profileSha256": "...",
    "providerFetchTimestamps": { "repeaterbook": "...", "radioid": "..." }
  },
  "gmrs": { "txEnabled": true, "acknowledgedUtc": "2026-07-15T00:03:22Z" },
  "artifacts": { "pre-write.bin": "sha256:...", "generated.bin": "sha256:...", "post-write.bin": "sha256:..." },
  "verification": { "postWriteMatches": true, "maskedRegions": ["0x0000-0x000F (VERIFY: example)"] }
}
```

---

## 6. Module specifications

### 6.1 Location resolver

Auto-detect format; `--format zip|latlong|utm` overrides.
- **ZIP** `^\d{5}(-\d{4})?$`: offline lookup from bundled GeoNames US ZIP centroid table
  (CC-BY; embedded compressed resource; attribution in README).
- **Lat/long:** decimal degrees and basic DMS.
- **UTM:** `13T 493000 4493000` → CoordinateSharp → WGS84.
- Always echo the resolution (`Resolved 80525 → 40.5384, -105.0512 (Fort Collins, CO)`)
  before proceeding.

### 6.2 Repeater providers

Merged + deduplicated (normalized callsign + output freq, 2 km sanity check); all reads
go through the SQLite cache; `--offline` forbids sockets entirely.

**RepeaterBook (primary).** Token-based auth for approved clients (post-March-2026
policy); this tool uses the *distributed app* model — approved once, then each user
generates their own app-bound token and stores it via
`plugmatic config set repeaterbook.token <token>` (→ `credentials.dat`).
Send `User-Agent: plugmatic/<version> (+<repo URL>; <contact email>)`. Query export by
state (derived from resolved location; include neighbors when radius crosses borders),
GMRS separately (`stype=gmrs`), filter client-side by distance/mode/band. Cache ≥7 days.
**Never scrape HTML.**

**RadioID.net (supplementary, no auth).** DMR repeater enrichment: DMR IDs, color codes,
timeslot/talkgroup hints.

**BrandMeister (supplementary, optional user API key).** Per-repeater static talkgroups →
one channel per (repeater × talkgroup) with correct timeslots; fall back to
profile-defined talkgroups when absent.

### 6.3 Codeplug builder + IR

#### 6.3.1 IR model
YAML-serialized (YamlDotNet), radio-neutral: `Contact` (talkgroups; required per D11),
`RxGroupList`, `AnalogChannel`, `DigitalChannel`, `Zone`, `ScanList`,
`GeneralSettings` (radio DMR ID + callsign from config). Every channel carries
`TxPermit: Allowed | Inhibited`; the DM-32UV codec MUST map `Inhibited` onto the
radio's real RX-only mechanism (**VERIFY** which bit/field that is, from §3 work).

#### 6.3.2 BuildProfile (`profiles/*.yaml`)
Radius, bands, max channels, zone strategy (`by-town | by-repeater | by-network`),
talkgroup selection (Colorado defaults: statewide, local, TAC 310–312, Parrot 9990),
analog ham on/off, GMRS on/off, NOAA on/off (default on), naming template (≤16 chars,
**VERIFY** exact limit), distance sort.

#### 6.3.3 GMRS handling — LOCKED (D8, D9)

Default RX-only. `plugmatic config gmrs-tx enable` prints the liability-acknowledgment
statement and requires typing `I ACCEPT` verbatim; stores `gmrs.txEnabled` +
`gmrs.acknowledgedUtc`; recorded in every manifest. No per-run override exists.

With TX enabled, the **builder enforces** Part 95E class conformance:

| Channel class | Frequencies (MHz) | Mode/BW | Max power | DM-32UV setting | TX after ack? |
|---|---|---|---|---|---|
| Main / repeater outputs (GMRS 15–22) | 462.550–462.725 (25 kHz steps) | Analog FM wide (25 kHz ch / 20 kHz occupied) | 50 W | **High** | Yes |
| Repeater inputs | 467.550–467.725 | +5.000 MHz TX offset on repeater channels, tones from source data | 50 W | **High** | Yes |
| 462 interstitials (GMRS 1–7) | 462.5625–462.7125 | Analog FM wide (20 kHz) | 5 W ERP | **Medium (5 W)** — never High | Yes |
| 467 interstitials (FRS 8–14) | 467.5625–467.7125 | Narrow 12.5 kHz | 0.5 W ERP, integrated antenna only | cannot comply (min ~1 W) | **No — always RX-only (D9)** |

GMRS channels are analog FM only (never DMR); grouped in a `GMRS` zone. **VERIFY**
DM-32UV H/M/L wattages (spec: 10 W high) and map to the highest setting not exceeding
the class limit.

#### 6.3.4 NOAA Weather Radio — LOCKED (D10)
Default-on (`--no-noaa` / profile flag to exclude); zone `NOAA WX`; analog FM;
`TxPermit: Inhibited` always — no acknowledgment path exists:
WX1 162.550, WX2 162.400, WX3 162.475, WX4 162.425, WX5 162.450, WX6 162.500,
WX7 162.525.

#### 6.3.5 Validation (before every encode; hard-fail with readable report)
Frequency-in-band per `RadioCapabilities`; GMRS class rules; channel/zone/name limits;
duplicates; referential integrity (DigitalChannel → Contact, RxGroupList); NOAA and
467-interstitials TxInhibited; **no silent truncation anywhere**.

### 6.4 DM-32UV codec (`Plugmatic.Radios.Dm32uv/Format`)

- Pure functions over `byte[]`/`ReadOnlyMemory<byte>`; every offset/encoding traceable
  to `docs/formats/dm32uv-format.md` (cite section in code comments).
- MUST decode both the on-wire radio image and the factory CPS `.data` container
  (**VERIFY** whether they differ by header/wrapper; document the mapping).
- `Compare()` implements masked comparison using the volatile-region list from the
  format doc (populated by §3.5 step 2).
- `RadioCapabilities`: 4000 channels, 250 zones, VHF/UHF, DMR Tier II (**VERIFY** exact
  limits during §3 work; do not trust marketing copy).
- Round-trip property: `Decode(Encode(ir)) == ir` structurally, and
  `Encode(Decode(img))` must be `Compare`-equal to `img` for all fixture images.

### 6.5 DM-32UV protocol (`Plugmatic.Radios.Dm32uv/Protocol`) + port discovery

- Implements `IRadioProtocol` per `docs/formats/dm32uv-protocol.md`: handshake,
  identify, block read/write with checksum + ACK handling, retries (bounded), session
  teardown. Serial 115200-8-N-1 (**VERIFY**).
- Port discovery (`plugmatic ports`):
  1. Enumerate ports (`SerialPort.GetPortNames()`; VID/PID via CIM on Windows,
     `/sys/bus/usb-serial/devices` + `udevadm info` on Linux).
  2. Known cable VID/PIDs: FTDI `0403:6001`, CH340 `1A86:7523` (**VERIFY** actual
     cable) → candidates only.
  3. Interactive disambiguation: unplug → Enter → replug → Enter → diff port lists.
  4. `--port COM5` / `--port /dev/ttyUSB0` always wins.
- Preflight before ANY read or write: `IdentifyAsync`; the reported model must match the
  requested `--radio`. **Mismatch = abort; no override.**

---

## 7. CLI specification

`plugmatic <command>`; global `--verbose`, `--radio <model>` where relevant. Exit codes:
0 ok, 1 user/validation error, 2 environment error, 3 hardware op failed.

### 7.1 `config`
`set/get/list`; secrets (`repeaterbook.token`, `brandmeister.apikey`) →
`credentials.dat`; `config gmrs-tx enable|disable|status` per §6.3.3.

### 7.2 `fetch`
`fetch --location <zip|latlong|utm> [--radius 60mi|100km] [--offline] [--gmrs]` →
summary table (callsign, freq, offset, mode, CC, distance, source). Cache only; no run
dir.

### 7.3 `build`
`build --location <loc> --radio dm32uv [--profile <name>] [--radius ...] [--no-noaa] [--out <path>]`
Fetch (cached) → build → validate → `generated.yaml` + manifest in a new `build` run
(and to `--out` if given). Prints counts + applied GMRS TX state.

### 7.4 `read`
`read --radio dm32uv [--port ...]` → identify/model-check → read → decode → `read` run
artifacts. This is the backup mechanism; a backup is just a read run.

### 7.5 `write` — implements D7
`write --radio dm32uv (--plug <generated.yaml|run-dir> | --image <bin>) [--port ...] [--yes]`
Single run directory; abort at any failure; manifest always finalized with outcome:
1. Port → identify → **model match** (abort on mismatch).
2. **Read radio → `pre-write.bin` + `pre-write.yaml`. Unconditional. No skip flag —
   do not implement one.**
3. `--plug`: validate IR → `Encode` → `generated.bin` → `Decode(generated.bin)` →
   structural equality vs. IR (round-trip gate).
   `--image` (raw restore path): copy to `generated.bin`; gate = `Decode` succeeds and
   `Encode(Decode(img))` is `Compare`-equal to it.
4. Summary diff vs. `pre-write.yaml`; confirm (skippable with `--yes`).
5. `WriteImageAsync`.
6. Read back → `post-write.bin` → `Compare` vs. `generated.bin` (masked); record result.
7. Finalize manifest. On mid-write failure: print recovery instructions →
   `write --image <run>/pre-write.bin`.

### 7.6 `diff`
`diff --old <run-dir|yaml> --new <run-dir|yaml>` — human-readable IR diff.

### 7.7 `ports`
List candidates with VID/PID + the interactive unplug/replug detector.

### 7.8 `doctor`
Checks with per-OS fix-it hints: Plugmatic dir writable; serial access (Linux: user in
`dialout` — `sudo usermod -aG dialout $USER` + re-login); a candidate port present;
RepeaterBook token present (warn only); cache healthy.

### 7.9 `dev` — protocol bring-up & RE commands (hardware sessions with the user)

Each invocation creates a `dev` run (transfer.log always captured). Read-class commands
are unrestricted; anything that transmits write-class frames requires interactive
confirmation showing exactly what will be sent.

| Command | Purpose |
|---|---|
| `dev identify [--port]` | Handshake + identify; print/store the raw response |
| `dev dump [--port] [--out dump.bin]` | Full image read via the native protocol (ladder step 2) |
| `dev decode <bin-or-.data>` | Run the codec against any image/file; print IR summary + parse warnings |
| `dev diffbin <a> <b> [--context 8]` | Hex diff of two images (drives §3.1 and volatile-region discovery) |
| `dev replay <frames.hex> [--port]` | Send captured frames byte-exact (write-class ⇒ confirmation) — used to validate captures before implementing them |
| `dev writeback <read-run-dir> [--port]` | Ladder step 4: no-op write of that run's own read image; internally uses the full §7.5 sequence |

---

## 8. Safety invariants (enforced in code, asserted in tests)

- **I1 (=D7):** the method performing a hardware write takes the same-run `pre-write.bin`
  artifact as a required argument and validates it exists and is non-empty. No code path
  reaches `WriteImageAsync` without it.
- **I2:** no write when identify result ≠ requested radio model.
- **I3:** no write of an image failing the §7.5 step-3 gate.
- **I4:** NOAA and 467-interstitial channels are TxInhibited in every generated
  codeplug, regardless of config.
- **I5:** GMRS TxAllowed only with stored acknowledgment, always class-conformant
  (§6.3.3).
- **I6:** run dirs append-only during a run, immutable after finalize.
- **I7:** secrets never appear in logs, manifests, or exceptions.
- **I8 (=D14):** the protocol layer contains no bootloader/DFU/firmware-mode sequences;
  write addresses are bounds-checked against the codeplug region defined in
  `dm32uv-format.md`.

---

## 9. Testing

### 9.1 Unit (no I/O)
Location parsing/UTM round-trips; provider merge/dedup; builder fixtures asserting
GMRS table conformance, NOAA zone, TxPermit flags, validation failures; codec
round-trip properties (§6.4) against fixture images; checksum implementations against
captured frames; manifest hashing.

### 9.2 Integration (no hardware, no network)
- Providers vs. canned HTTP fixtures via fake handler; cache TTL; `--offline` proven to
  never open a socket.
- **FakeRadio:** an in-process `ISerialLink` implementing the radio side of
  `dm32uv-protocol.md` (handshake, identify, block read/write, checksums, ACK/NAK,
  injectable faults: timeout, bad checksum, short read, mid-write disconnect). The full
  `read`/`write`/`dev` command flows — including invariants I1–I3 and failure-path
  manifest outcomes — run against it in CI. The FakeRadio doubles as an executable
  check of the protocol doc: when a capture contradicts the FakeRadio, one of them is
  wrong and the doc gets fixed.
- Fixture corpus: the user's CPS `.data` saves + captured real-radio images (added as
  they're produced in §3.5).

### 9.3 Manual hardware checklists (user-executed; `docs/hardware-checklist.md`)
A) **Bring-up (once, during P2):** the §3.5 ladder, steps 1–5, via `dev` commands.
B) **Release validation (per release):** `doctor` on Debian 13 and Windows 11; `read`
×3 byte-stable (modulo masks); `dev writeback` no-op; full Larimer County generated
plug written + spot-checked on-radio; restore drill via `write --image <pre-write.bin>`.

---

## 10. Implementation phases & definition of done

Ordered to front-load protocol risk — the codec and wire protocol are the existential
unknowns; everything else is conventional.

| Phase | Scope | Definition of done |
|---|---|---|
| **P0 Environment & recon** | Repo scaffold, 0BSD, CI (ubuntu + windows runners), `doctor`, `ports`; seed `docs/formats/*.md` from §3.1 CPS diffing + §3.2 qdmr fact extraction; user tasks: RepeaterBook app application submitted, USBPcap captures of one CPS Read and one CPS Write recorded, a handful of CPS `.data` saves added to fixtures | `dotnet test` green on both OSes; both format docs exist with the memory map and framing at least drafted, every fact annotated `source:`/`verified:` |
| **P1 Codec (file format)** | `Plugmatic.Radios.Dm32uv/Format`, `dev decode`, `dev diffbin` | Decodes the user's real CPS `.data` saves into IR that matches what CPS displays; round-trip properties pass on all fixtures |
| **P2 Wire protocol** | `Plugmatic.Radios.Dm32uv/Protocol`, `SerialPortLink`, FakeRadio, `dev identify/dump/replay/writeback`, `read` command, RunManager + manifests | FakeRadio CI suite green; hardware ladder §3.5 steps 1–4 pass on the real DM-32UV (incl. `factory-golden` archived; volatile regions documented) |
| **P3 Data layer** | Resolvers, providers, cache, `fetch`, `config` + credential store | `fetch --location 80525 --radius 60mi` matches a manual RepeaterBook search; suite passes with network disabled |
| **P4 Builder** | IR, profiles, GMRS ack flow, NOAA, validation, `build`, `diff` | Generated Larimer plug reviewed by user; I4/I5 asserted in tests; ladder step 5 (single-channel) then full plug written and verified on-radio |
| **P5 Hardening & release** | Checklist B, error taxonomy + fix-it messages, README (token walkthrough, Linux serial permissions, Windows notes), single-file publish linux-x64 + win-x64 | Checklist B passes end-to-end on both OSes; fresh-machine install from README alone succeeds |

Out of scope for v1: GUI, contacts-DB loading (D11), radios other than DM-32UV,
scheduled/automatic writes, firmware operations (D14).

---

## 11. Risks

| Risk | Mitigation |
|---|---|
| Protocol/format facts wrong or incomplete | Two-source cross-validation rule (§3); FakeRadio-as-executable-spec; ladder gates each step on evidence |
| Radio corrupted during bring-up | User accepts risk; ladder ordering, read-only fuzzing rule, D14 ban, `factory-golden` archive, public firmware/CPS recovery archives |
| CPS/firmware update changes format | Manifests pin format/protocol doc git-SHAs; captures are versioned in fixtures; re-run diff suite after any radio firmware update |
| Radio image not byte-stable across reads | §3.5 step 2 measures; masked `Compare` in codec |
| GPL taint from qdmr reference | §3.2 discipline: facts → doc → implement from doc; no code copied/translated |
| RepeaterBook approval delayed/denied | Cache/fixture-driven development; RadioID + BrandMeister carry DMR-critical data meanwhile |
| Raw-USB transport discovered (serial assumption wrong) | D2 escape hatch: drop Windows, go Linux-only; `ISerialLink` abstraction contains the blast radius |
| Wrong radio connected | I2, no override |

---

## Appendix — Reference constants

- **NOAA WX:** §6.3.4 table.
- **GMRS:** §6.3.3 table; repeater offset +5.000 MHz; analog FM only.
- **DM-32UV:** 4000 ch / 250 zones / DMR Tier II / VHF+UHF / 10 W high power (**VERIFY**
  M/L wattages); K-plug USB-serial programming cable (FTDI or CH340 — **VERIFY**
  VID:PID of the actual cable); serial 115200-8-N-1 (**VERIFY**); factory CPS `.data`
  saves ≈ 644 KB.
- **Colorado default talkgroups (profile seed):** Colorado statewide, local/regional per
  repeater network, TAC 310/311/312, Parrot 9990 (private call).
