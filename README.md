# plugmatic

Give it where you are; it programs your DMR radio. plugmatic fetches repeater data for
a location (ZIP, lat/long, or UTM), generates a complete codeplug — DMR talkgroup
channels, analog ham, GMRS (receive-only until you accept the license terms), NOAA
weather — and writes it to the radio over the programming cable. The radio's existing
codeplug is **always read and archived before any write**; there is no flag to skip it.

Supported radio: **Baofeng DM-32UV**, via a natively implemented codeplug codec and
serial protocol (no factory CPS, no external tools). License: 0BSD.

## Install

Prebuilt single-file binaries (from a release, or build them yourself):

```sh
dotnet publish src/Plugmatic.Cli -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src/Plugmatic.Cli -c Release -r win-x64  --self-contained -p:PublishSingleFile=true
```

From source: .NET 10 SDK, then `dotnet build` / `dotnet run --project src/Plugmatic.Cli`.

## Quick start

```sh
plugmatic doctor                      # environment check with fix-it hints
plugmatic ports                       # find the programming cable (CH340/FTDI/Prolific)
plugmatic config set dmr.id 1234567   # your DMR ID — without it, DMR channels are RX-only
plugmatic config set dmr.callsign AB0CD

plugmatic read  --radio dm32uv                       # backup = a read run
plugmatic build --location 80525 --radius 40mi       # generate (no hardware touched)
plugmatic write --radio dm32uv --plug <run-dir>      # program (auto pre-write backup)
```

Every hardware operation creates a run directory under `~/Plugmatic/radios/dm32uv/`
with the binary artifacts, decoded YAML, a full transfer log, and a manifest. To roll
back to any earlier state:

```sh
plugmatic write --radio dm32uv --image ~/Plugmatic/radios/dm32uv/<run>/pre-write.bin
```

## Linux serial permissions

The programming cable shows up as `/dev/ttyUSB0` owned by group `dialout`:

```sh
sudo usermod -aG dialout $USER   # then log out and back in
# or, for the current session only:
sudo chmod a+rw /dev/ttyUSB0
```

If ModemManager grabs the port: `sudo systemctl stop ModemManager`.

## Windows notes

The cable appears as a `COMx` port (CH340 driver from Windows Update). Pass it
explicitly if more than one port exists: `--port COM5`. Credentials are stored
DPAPI-encrypted under your user profile.

## Repeater data sources

- **RadioID.net** (no key needed): DMR repeaters, color codes, embedded static
  talkgroups. Works out of the box.
- **RepeaterBook** (primary for analog ham + GMRS): requires an approved app token —
  request API access from RepeaterBook, then
  `plugmatic config set repeaterbook.token <token>`. Without a token, plugmatic
  degrades gracefully to RadioID's DMR data and the fixed GMRS/NOAA channels.
- **BrandMeister** (optional): `plugmatic config set brandmeister.apikey <key>` for
  static-talkgroup enrichment where RadioID lacks it.

Responses are cached in SQLite for 7 days; `--offline` uses only the cache and never
opens a socket.

## GMRS

GMRS channels are generated **receive-only** by default. Transmitting on GMRS requires
an FCC GMRS license; enable TX only after reading and accepting the acknowledgment:

```sh
plugmatic config gmrs-tx enable      # prints the statement; type "I ACCEPT"
```

Power/bandwidth per FCC Part 95E class is enforced automatically (5 W class on the
462 interstitials, narrowband RX-only on the 467 interstitials — those never transmit
regardless of acknowledgment). NOAA weather channels are always receive-only.

## Safety model

- Writes are impossible without a same-run fresh backup (`pre-write.bin`).
- The radio's reported model must match; mismatch aborts, no override.
- Generated images pass an encode/decode round-trip gate before any write.
- Post-write, the image is read back and compared.
- The protocol layer contains no firmware/bootloader sequences and refuses writes
  outside the radio's reported codeplug memory region.

## Development against the radio

`plugmatic dev` — `identify`, `dump`, `decode`, `diffbin`, `replay`, `writeback`.
The DM-32UV wire protocol and codeplug format are documented (and hardware-verified)
in [docs/formats/](docs/formats/); the FakeRadio test double executes the protocol
doc in CI. See [docs/hardware-checklist.md](docs/hardware-checklist.md) for the
bring-up ladder.

## Attribution

ZIP centroid data © GeoNames (CC-BY 4.0). Protocol documentation sources credited in
[NOTICE](NOTICE).
