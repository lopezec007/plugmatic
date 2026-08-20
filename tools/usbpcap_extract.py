#!/usr/bin/env python3
"""Extract an AnyTone serial session from a Wireshark/USBPcap .pcapng capture.

Wireshark on Windows records the CPS talking to the radio over USB CDC-ACM. This turns
that back into the byte stream the protocol docs are written against:

    tools/usbpcap_extract.py cps_write.pcapng            # annotated frame list
    tools/usbpcap_extract.py cps_write.pcapng --summary  # commands + address runs

No tshark needed. Only the CDC bulk endpoints are followed; control transfers are
decoded separately with --control (that is how the 921600/DTR/RTS line setup in
d878uv-protocol.md §1 was established).
"""
import argparse
import collections
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import _pcapng as pcapng   # noqa: E402


def bulk_stream(path, device=None):
    """(timestamp, direction, payload) for CDC bulk data, submit/complete de-duplicated."""
    out = []
    for ts, _lt, pkt in pcapng.packets(path):
        h = pcapng.parse_usbpcap(pkt)
        if not h or h['transfer'] != 3 or not h['payload']:
            continue
        if device is not None and h['device'] != device:
            continue
        inbound = bool(h['endpoint'] & 0x80)
        from_device = bool(h['info'] & 1)
        if inbound != from_device:      # keep OUT submits and IN completions
            continue
        out.append((ts, h['device'], 'RX' if inbound else 'TX', h['payload']))
    return out


def busiest_device(stream):
    counts = collections.Counter(dev for _, dev, _, _ in stream)
    return counts.most_common(1)[0][0] if counts else None


def describe(direction, payload):
    """One-line gloss of a frame, per d878uv-protocol.md §2/§4/§5."""
    if direction == 'TX':
        if payload.startswith(b'PROGRAM'):
            return 'enter program mode'
        if payload.startswith(b'END'):
            return 'END (commits staged writes)'
        if payload[:1] == b'\x02':
            return 'identify'
        if payload[:1] == b'R' and len(payload) >= 6:
            return 'read  0x%08X +%d' % (struct.unpack('>I', payload[1:5])[0], payload[5])
        if payload[:1] == b'W' and len(payload) >= 6:
            return 'WRITE 0x%08X +%d' % (struct.unpack('>I', payload[1:5])[0], payload[5])
    else:
        if payload == b'QX\x06':
            return 'program mode ack'
        if payload == b'\x06':
            return 'ack'
        if payload[:1] == b'W' and len(payload) >= 6:
            return 'data  0x%08X +%d' % (struct.unpack('>I', payload[1:5])[0], payload[5])
        if payload[:1] == b'I':
            return 'identity %s' % payload[1:8].rstrip(b'\0').decode('ascii', 'replace')
    return ''


def runs_of(addresses, step=16):
    """Collapse a sorted address list into contiguous (start, end_exclusive, count)."""
    if not addresses:
        return []
    out, start, prev, n = [], addresses[0], addresses[0], 1
    for a in addresses[1:]:
        if a == prev + step:
            prev, n = a, n + 1
        else:
            out.append((start, prev + step, n))
            start, prev, n = a, a, 1
    out.append((start, prev + step, n))
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('capture')
    ap.add_argument('--device', type=int, help='USB device address (default: busiest)')
    ap.add_argument('--summary', action='store_true', help='commands and address runs only')
    ap.add_argument('--control', action='store_true', help='decode CDC control requests')
    ap.add_argument('--limit', type=int, default=0, help='stop after N frames')
    args = ap.parse_args()

    if args.control:
        for ts, name in control_requests(args.capture):
            print('%12.3fms  %s' % (ts / 1000.0, name))
        return

    stream = bulk_stream(args.capture)
    device = args.device if args.device is not None else busiest_device(stream)
    stream = [x for x in stream if x[1] == device]
    if not stream:
        sys.exit('no CDC bulk traffic found (try --device)')
    t0 = stream[0][0]
    print('# device %d, %d frames' % (device, len(stream)), file=sys.stderr)

    if not args.summary:
        for i, (ts, _dev, direction, payload) in enumerate(stream):
            if args.limit and i >= args.limit:
                break
            print('%6d %10.3fms %s %-4d %-52s %s'
                  % (i, (ts - t0) / 1000.0, direction, len(payload),
                     payload[:24].hex(' ') + (' ...' if len(payload) > 24 else ''),
                     describe(direction, payload)))
        return

    commands, reads, writes = [], [], []
    for _ts, _dev, direction, payload in stream:
        if direction != 'TX':
            continue
        if payload[:1] == b'W' and len(payload) >= 6:
            writes.append(struct.unpack('>I', payload[1:5])[0])
        elif payload[:1] == b'R' and len(payload) >= 6:
            reads.append(struct.unpack('>I', payload[1:5])[0])
        else:
            commands.append(describe(direction, payload) or payload[:8].hex(' '))
    print('session commands (everything that is not a read or write frame):')
    for c in commands:
        print('   %s' % c)
    for label, addrs in (('read', reads), ('write', writes)):
        print('\n%s frames: %d' % (label, len(addrs)))
        for start, end, n in runs_of(sorted(addrs)):
            print('   0x%08X - 0x%08X  len=0x%-6X frames=%d' % (start, end, end - start, n))


def control_requests(path):
    """CDC SET/GET_LINE_CODING and SET_CONTROL_LINE_STATE, in capture order."""
    names = {(0x21, 0x20): 'SET_LINE_CODING', (0xA1, 0x21): 'GET_LINE_CODING',
             (0x21, 0x22): 'SET_CONTROL_LINE_STATE', (0x21, 0x23): 'SEND_BREAK'}
    t0 = None
    for ts, _lt, pkt in pcapng.packets(path):
        h = pcapng.parse_usbpcap(pkt)
        if not h or h['transfer'] != 2 or len(h['payload']) < 8:
            continue
        extra = pkt[27:h['hlen']] if h['hlen'] > 27 else b''
        if not extra or extra[0] != 0:          # SETUP stage only
            continue
        bm, req, value, _index, _length = struct.unpack('<BBHHH', h['payload'][:8])
        name = names.get((bm, req))
        if not name:
            continue
        if name == 'SET_LINE_CODING' and len(h['payload']) >= 15:
            rate, stop, parity, bits = struct.unpack('<IBBB', h['payload'][8:15])
            name += ' rate=%d stop=%d parity=%d bits=%d' % (rate, stop, parity, bits)
        elif name == 'SET_CONTROL_LINE_STATE':
            name += ' DTR=%d RTS=%d' % (value & 1, (value >> 1) & 1)
        if t0 is None:
            t0 = ts
        yield ts - t0, name


if __name__ == '__main__':
    main()
