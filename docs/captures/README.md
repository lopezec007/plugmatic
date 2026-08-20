# USB captures

## Why these are not in the repository

A CPS capture contains the operator's **callsign, DMR ID and entire codeplug**. This
repository has a public remote, and a push is not reversible — forks and caches keep
whatever lands there. So captures stay on the machine that made them and `.gitignore`
refuses `*.pcapng` / `*.pcapng.gz` to stop an accidental `git add -A`.

Publishing one is the operator's call, not the tooling's. If you decide to, strip the
codeplug first — the answers we needed live in the framing, not the payload.

## Where they are

    ~/Plugmatic/captures/cps_read.pcapng.gz     CPS "Read from radio",  3,622 read frames
    ~/Plugmatic/captures/cps_write.pcapng.gz    CPS "Write to radio",   6,022 write frames

Both taken 2026-08-20 against this AT-D878UVII+ (D878UV2, firmware V101) with Wireshark
4.6.8 + USBPcap on Windows 11. The read capture is the control sample: we already knew
exactly what correct read framing looks like, so it proves the capture pipeline is
faithful before anything in the write capture is trusted.

## Reading them

`tools/usbpcap_extract.py` turns a capture back into the byte stream the protocol doc is
written against. No `tshark` required, and `.gz` is read directly.

```sh
# every frame, glossed
tools/usbpcap_extract.py ~/Plugmatic/captures/cps_write.pcapng.gz | head -40

# just the session shape: commands, then contiguous address runs
tools/usbpcap_extract.py ~/Plugmatic/captures/cps_write.pcapng.gz --summary

# CDC control traffic (this is where the 921600 / DTR=0 / RTS=1 setup shows up)
tools/usbpcap_extract.py ~/Plugmatic/captures/cps_write.pcapng.gz --control
```

## What they settled

The whole CPS write session is `PROGRAM`, identify, **one** read of `0x02FA0020`, 6,022
back-to-back 16-byte write frames, `END` — framing byte-identical to ours, including the
checksum. That ruled out the missing-command theory the captures were taken to test, and
pointed at session *semantics* instead, which hardware then confirmed:

- writes are staged and commit on `END`; **any read after a write discards them**
- a write erases the whole aligned 0x40000 block around it and keeps only what the
  session staged — which is why every CPS write run is a block being rewritten in full

Both are written up in `docs/formats/d878uv-protocol.md` §5.1-§5.6.

The write capture is also a **byte-exact record of what those blocks should contain**.
That is not incidental: when two 16-byte probes erased channel banks 0 and 1, replaying
the 672 capture frames covering those blocks restored all 32,768 bytes with zero
mismatches. Keep these files.
