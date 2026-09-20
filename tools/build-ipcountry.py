#!/usr/bin/env python3
"""Rebuild src/MphRead/Assets/Geo/ipcountry.bin.gz from DB-IP Lite.

The launcher shows a flag beside a server, and the only thing it knows about
a server is its address -- so the country has to come from the address, and
from a table that ships, because a launcher that asks a third party where
every server in your browser is has told that third party what you play.

    python3 tools/build-ipcountry.py [YYYY-MM]

Defaults to the current month's release. DB-IP Lite is CC BY 4.0; the
attribution it requires lives beside the output and on the Credits page.
"""
import datetime
import gzip
import ipaddress
import os
import struct
import sys
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "src", "MphRead", "Assets", "Geo", "ipcountry.bin.gz")


def main() -> int:
    month = sys.argv[1] if len(sys.argv) > 1 else datetime.date.today().strftime("%Y-%m")
    url = f"https://download.db-ip.com/free/dbip-country-lite-{month}.csv.gz"
    print(f"fetching {url}")
    raw = urllib.request.urlopen(url, timeout=120).read()

    rows = []
    for line in gzip.decompress(raw).decode().splitlines():
        start, end, code = line.split(",")
        if ":" in start:        # IPv6: the game speaks v4 today
            continue
        rows.append((int(ipaddress.IPv4Address(start)), code))
    rows.sort()

    codes = sorted({c for _, c in rows})
    index = {c: i for i, c in enumerate(codes)}
    buf = bytearray(b"FPGEO1")
    buf += struct.pack("<H", len(codes))
    for c in codes:
        buf += c.encode("ascii")[:2].ljust(2, b"?")
    buf += struct.pack("<I", len(rows))
    for first, code in rows:
        buf += struct.pack("<IB", first, index[code])

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with gzip.open(OUT, "wb", compresslevel=9) as f:
        f.write(buf)
    print(f"{len(rows)} ranges, {len(codes)} countries -> "
          f"{os.path.getsize(OUT) // 1024} KiB at {os.path.normpath(OUT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
