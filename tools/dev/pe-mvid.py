#!/usr/bin/env python3
"""Read a .NET assembly's MVID straight out of the PE file, with NO CLR involvement.

Why this exists: session_info reports an MVID for the DLL on disk via McpLink's own
BuildInfo.ReadMvid. Predicting that value with the same mechanism would be a tautology --
the code under test would be confirming itself. This walks the PE headers -> CLI header ->
metadata root -> #~ table stream -> Module table row 0 -> #GUID heap by hand, so agreement
between the two is agreement between two independent routes.

usage: pe-mvid.py <assembly.dll> [more.dll ...]
"""
import struct
import sys
import uuid


def read_mvid(path):
    d = open(path, 'rb').read()

    # --- PE headers -------------------------------------------------------
    if d[:2] != b'MZ':
        raise ValueError('not a PE file (no MZ)')
    pe = struct.unpack_from('<I', d, 0x3C)[0]
    if d[pe:pe + 4] != b'PE\0\0':
        raise ValueError('not a PE file (no PE signature)')
    coff = pe + 4
    nsec = struct.unpack_from('<H', d, coff + 2)[0]
    optsz = struct.unpack_from('<H', d, coff + 16)[0]
    opt = coff + 20
    magic = struct.unpack_from('<H', d, opt)[0]
    if magic == 0x10B:      # PE32
        dirs = opt + 96
    elif magic == 0x20B:    # PE32+
        dirs = opt + 112
    else:
        raise ValueError('unknown optional header magic %#x' % magic)

    # data directory 14 == CLI header
    cli_rva, cli_sz = struct.unpack_from('<II', d, dirs + 14 * 8)
    if cli_rva == 0:
        raise ValueError('no CLI header -- not a managed assembly')

    # --- section table, for RVA -> file offset -----------------------------
    sections = []
    st = opt + optsz
    for i in range(nsec):
        s = st + i * 40
        vsize, vaddr, rawsize, rawptr = struct.unpack_from('<IIII', d, s + 8)
        sections.append((vaddr, max(vsize, rawsize), rawptr))

    def off(rva):
        for vaddr, size, rawptr in sections:
            if vaddr <= rva < vaddr + size:
                return rawptr + (rva - vaddr)
        raise ValueError('RVA %#x not in any section' % rva)

    # --- CLI header -> metadata root --------------------------------------
    md_rva, md_sz = struct.unpack_from('<II', d, off(cli_rva) + 8)
    md = off(md_rva)
    if d[md:md + 4] != b'BSJB':
        raise ValueError('bad metadata signature')
    vlen = struct.unpack_from('<I', d, md + 12)[0]   # already padded to a multiple of 4
    p = md + 16 + vlen              # -> Flags(2), NumberOfStreams(2), then the stream headers
    nstreams = struct.unpack_from('<H', d, p + 2)[0]
    p += 4

    streams = {}
    for _ in range(nstreams):
        soff, ssize = struct.unpack_from('<II', d, p)
        p += 8
        end = d.index(b'\0', p)
        name = d[p:end].decode('ascii')
        streams[name] = (md + soff, ssize)
        p = end + 1
        p = (p + 3) & ~3            # 4-byte aligned

    tbl_name = '#~' if '#~' in streams else '#-'
    tbl, _ = streams[tbl_name]
    guid_off, guid_sz = streams['#GUID']

    # --- #~ header: heap index sizes + row counts -------------------------
    heapsizes = d[tbl + 6]
    str_wide = 4 if heapsizes & 0x01 else 2
    guid_wide = 4 if heapsizes & 0x02 else 2
    valid = struct.unpack_from('<Q', d, tbl + 8)[0]
    if not (valid & 1):
        raise ValueError('Module table (0) absent')
    q = tbl + 24 + 4 * bin(valid).count('1')   # rows[] then the table data

    # Module row 0: Generation(2) Name(#Strings) Mvid(#GUID) EncId(#GUID) EncBaseId(#GUID)
    gi_off = q + 2 + str_wide
    gi = struct.unpack_from('<I' if guid_wide == 4 else '<H', d, gi_off)[0]
    if gi == 0:
        raise ValueError('Module.Mvid index is 0 (no GUID)')
    g = guid_off + (gi - 1) * 16
    if g + 16 > guid_off + guid_sz:
        raise ValueError('GUID index out of heap bounds')
    return uuid.UUID(bytes_le=d[g:g + 16])


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    rc = 0
    for a in sys.argv[1:]:
        try:
            print('%s  %s' % (read_mvid(a), a))
        except Exception as e:
            print('ERROR %s: %s' % (a, e))
            rc = 1
    sys.exit(rc)
