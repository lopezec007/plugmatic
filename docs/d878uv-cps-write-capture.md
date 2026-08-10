# Capturing an AnyTone CPS write (to unblock D878UVII+ write support)

**Why:** plugmatic can read, back up and decode your AnyTone completely, and its encoder
reproduces the radio's own image byte-for-byte. But the radio **acknowledges our write
frames and silently does not apply them** — proven on both allocated and unallocated
records, across a session close and reopen. Neither qdmr nor dmrconfig performs any
extra step, so this firmware (D878UV2, V101) evidently needs something no open-source
tool implements.

**What this capture gives us:** the official CPS *can* write to your radio. Recording
what it sends and diffing it against our frames will show the missing step directly —
almost certainly a short command somewhere before the first write frame or after the
last one. That is the whole answer, and it is not guessable safely.

**Time needed:** about 20–30 minutes, once, on a Windows machine.

---

## What you need

- The Windows PC with the **AnyTone CPS** that matches your radio (D878UVII+, firmware
  V101). If the CPS refuses to talk to the radio, this whole exercise stalls — check
  that first.
- The radio and its programming cable.
- **Wireshark** with the **USBPcap** component: <https://www.wireshark.org/download.html>
  During installation, tick **"Install USBPcap"** (it is not selected by default).
  A reboot is required afterwards.

## Safety

- The CPS doing a normal write is exactly what it's designed for; this is not a risky
  operation. Nothing plugmatic does is involved.
- You already have three byte-identical plugmatic backups of this radio, and the CPS
  can also save its own `.rdt` before writing. Do that anyway — one click, and it means
  a bad CPS write costs you nothing.
- The capture will contain your codeplug: callsign, DMR ID, channels. That's your own
  data and fine to keep locally; just be aware of it before posting a capture publicly.

---

## Step 1 — identify the radio's USBPcap interface

1. Plug in the radio and switch it on.
2. Launch Wireshark. In the interface list you'll see `USBPcap1`, `USBPcap2`, …
3. Click one and watch for traffic when you switch the radio off and on. The one that
   shows the radio appearing/disappearing is the root hub your radio sits on.

If several look plausible, capture on each briefly and pick the one where the AnyTone
appears. Wireshark shows the device description on enumeration.

## Step 2 — capture a CPS **read** first (the control sample)

This one is short, and it lets me confirm the capture pipeline is faithful before
trusting the write capture — we already know exactly what a correct read looks like, so
it doubles as a sanity check.

1. Start capturing on the USBPcap interface from step 1.
2. In the CPS: **Program → Read from radio**. Let it finish.
3. Stop the capture. **File → Save As** → `cps_read.pcapng`.

## Step 3 — capture a CPS **write** (the one that matters)

1. In the CPS, make one small, obvious change so the write is meaningful — for example
   rename a channel to `CPSTEST`. Note what you changed.
2. Start a **new** capture on the same interface.
3. In the CPS: **Program → Write to radio**. Let it run to completion.
4. Stop the capture. **File → Save As** → `cps_write.pcapng`.

Please note roughly **how long the write took** — that tells me whether the CPS uses
the same 16-byte frames we do or something faster.

## Step 4 — shrink it before sending

A full write is on the order of 100,000 frames, so the raw file may be tens of MB.
Either compress the `.pcapng` files (right-click → *Send to → Compressed folder*), or
export just the payload bytes with `tshark`, which is what I actually need.

Open **Command Prompt** in the folder with the captures. `tshark` lives in the
Wireshark install directory:

```
set PATH=%PATH%;C:\Program Files\Wireshark

tshark -r cps_read.pcapng  -Y "usb.capdata" -T fields -e usb.capdata > cps_read.txt
tshark -r cps_write.pcapng -Y "usb.capdata" -T fields -e usb.capdata > cps_write.txt
```

If `usb.capdata` yields nothing, try the CDC field instead:

```
tshark -r cps_write.pcapng -T fields -e usb.capdata -e usbcdc.payload > cps_write.txt
```

**If the write export is still huge**, the middle is thousands of identical write
frames and is of no interest. These two slices contain the answer:

```
tshark -r cps_write.pcapng -Y "usb.capdata" -T fields -e usb.capdata -c 400 > cps_write_head.txt

powershell -Command "Get-Content cps_write.txt -Tail 200 > cps_write_tail.txt"
```

## Step 5 — hand them over

Copy whatever you produced to the Linux box, into:

```
~/Plugmatic/captures/
```

Any of these is enough, in order of usefulness:

1. `cps_write.txt` (or `cps_write_head.txt` + `cps_write_tail.txt`) **and**
   `cps_read.txt`
2. The compressed `.pcapng` files — I can extract them here
3. Just the head slice of the write, if everything else fails

Then tell me it's there and I'll take it from there.

---

## What I'll do with it

1. Verify the read capture matches the framing we already implement — confirms the
   capture is faithful end to end.
2. Walk the write capture from the `PROGRAM` handshake to the first `W` frame, looking
   for any command we don't send.
3. Check the tail for a commit/finalise step after the last `W`.
4. Write the finding into `docs/formats/d878uv-protocol.md` as a hardware-verified
   fact, implement from the doc, and re-run ladder step 4 — this time with the
   mutation test (`dev writetest --prove`), which is what caught the problem in the
   first place.

## If USBPcap doesn't cooperate

The radio presents a USB CDC serial port, so a serial-level logger works too and is
often easier to read. Any Windows serial monitor that can log a COM port while another
application owns it will do; the output I need is simply a chronological list of bytes
with a direction marker per line, e.g.

```
[14:22:05.123] TX  50 52 4F 47 52 41 4D
[14:22:05.156] RX  51 58 06
```

That format is ideal — it's exactly how the DM-32UV capture we already vendored is
structured, and it needs no extraction step at all.

## If the CPS can't write either

Worth knowing, and not a wasted trip: it would mean the radio is refusing writes for a
reason outside the protocol — a lock setting, a firmware state, or a hardware fault.
Tell me if that happens and we'll chase it from that angle instead.
