# Deviations from the implementation spec

| Date (UTC) | Spec ref | Deviation | Rationale | Architectural? |
|---|---|---|---|---|
| 2026-08-02 | Appendix / §6.5 | Programming cable is **CH340 (1A86:7523)**, not FTDI. FTDI kept as secondary candidate ID. | lsusb on the actual cable | no |
| 2026-08-02 | §4.2 | `IRadioCodec.Encode` gained an optional `baseImage` parameter: `Encode(Codeplug ir, ReadOnlyMemory<byte>? baseImage = null)`. Hardware writes encode on top of the same-run `pre-write.bin` so settings/unknown blocks are carried forward; `build` runs encode from scratch. | The DM-32UV image contains opaque per-radio blocks (calibration-adjacent settings) that must survive a write; D7 guarantees a base image is always available on the hardware path. | no (additive) |
| 2026-08-02 | §3.2 | In addition to qdmr, the MIT-licensed community spec github.com/infamy/DM32-Protocol-Spec is used as a primary documentation source (it is the second source for cross-validation; its CPS capture file is vendored as a fixture). | Two-source rule satisfied without hardware risk | no |
| 2026-08-02 | §6.3 (addition) | New invariant (user-directed at ladder step 6): **DMR TX requires a configured operator DMR ID** (`config set dmr.id`). Builder forces all DMR channels RX-only without it; validator rejects TX-enabled DMR channels when `Settings.RadioId == 0`; codec never touches the radio-ID block when the IR carries no ID. | Radio transmitted with a zeroed ID during testing — unidentified DMR TX must be impossible | no (additive safety) |
| 2026-08-02 | §7.5 / ladder | Ladder steps 5–6 completed; per user request the generated Fort Collins plug REMAINS on the radio (original archived as the `factory-golden` read run and in every write run's `pre-write.bin`). | User preference | no |
