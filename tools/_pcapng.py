import gzip, struct, sys

def blocks(data):
    off = 0
    n = len(data)
    while off + 12 <= n:
        btype, blen = struct.unpack_from('<II', data, off)
        if blen < 12 or off + blen > n:
            break
        yield btype, data[off+8:off+blen-4]
        off += blen

USB_XFER = {0:'ISOCH',1:'INTR',2:'CTRL',3:'BULK'}

def parse_usbpcap(body):
    # USBPCAP_BUFFER_PACKET_HEADER (packed, little endian)
    if len(body) < 27: return None
    (hlen, irpid, status, function, info, bus, device, endpoint, transfer,
     datalen) = struct.unpack_from('<HQIHBHHBBI', body, 0)
    payload = body[hlen:hlen+datalen] if hlen <= len(body) else b''
    return dict(hlen=hlen, irpid=irpid, status=status, function=function,
                info=info, bus=bus, device=device, endpoint=endpoint,
                transfer=transfer, datalen=datalen, payload=payload)

def iface_linktypes(data):
    lts = []
    for btype, body in blocks(data):
        if btype == 0x00000001:
            lt, = struct.unpack_from('<H', body, 0)
            lts.append(lt)
    return lts

def _read(path):
    opener = gzip.open if str(path).endswith('.gz') else open
    with opener(path, 'rb') as fh:
        return fh.read()

def packets(path):
    data = _read(path)
    ifaces = []
    for btype, body in blocks(data):
        if btype == 0x00000001:
            lt, = struct.unpack_from('<H', body, 0)
            ifaces.append(lt)
        elif btype == 0x00000006:  # EPB
            iid, tsh, tsl, caplen, origlen = struct.unpack_from('<IIIII', body, 0)
            pkt = body[20:20+caplen]
            ts = ((tsh<<32)|tsl)
            yield ts, ifaces[iid] if iid < len(ifaces) else None, pkt
