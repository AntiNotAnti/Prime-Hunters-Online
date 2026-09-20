#!/usr/bin/env python3
"""Strip a Quake 3 BSP down to the lumps the map importer actually reads.

A shipped level is mostly things this engine has no use for. wrackdm17 is
5.59 MB, and 4.42 MB of that is the light grid -- Quake's own lighting, which
a converted map does not keep: the borrowed textures come from the player's
game files and the shading comes from the vertex colours. Lightmaps, the BSP
tree, the leaf arrays and the visibility data go the same way; none of them is
opened by Q3Bsp.cs, which reads geometry, brushes and entities and nothing
else.

What is left is 647 KB, in the same IBSP 46 format, and the importer cannot
tell the difference. That is the difference between a map that can travel with
the application and one that cannot.

    tools/strip-bsp.py pak1-maps.pk3 wrackdm17 maps/wrackdm17.bsp
    tools/strip-bsp.py some.bsp - out.bsp
"""
import struct
import sys
import zipfile

# The lump order of IBSP 46.
LUMPS = [
    "entities", "textures", "planes", "nodes", "leafs", "leaffaces",
    "leafbrushes", "models", "brushes", "brushsides", "vertexes", "meshverts",
    "effects", "faces", "lightmaps", "lightvols", "visdata"
]

# What Q3Bsp.cs reads. Everything else is emptied rather than removed: the
# header is a fixed 17 entries, so a dropped lump is one with zero length.
KEEP = {"entities", "textures", "planes", "models", "brushes", "brushsides",
        "vertexes", "meshverts", "faces"}


def read_source(path, map_name):
    if path.lower().endswith(".pk3"):
        with zipfile.ZipFile(path) as archive:
            return archive.read(f"maps/{map_name}.bsp")
    with open(path, "rb") as handle:
        return handle.read()


def strip(data):
    if data[:4] != b"IBSP" or struct.unpack("<i", data[4:8])[0] != 46:
        raise SystemExit("not an IBSP 46 file")
    header = bytearray(b"IBSP" + struct.pack("<i", 46))
    body = bytearray()
    start = 8 + len(LUMPS) * 8
    for index, name in enumerate(LUMPS):
        offset, length = struct.unpack("<ii", data[8 + index * 8:16 + index * 8])
        if name not in KEEP:
            header += struct.pack("<ii", start, 0)
            continue
        header += struct.pack("<ii", start + len(body), length)
        body += data[offset:offset + length]
        # Lumps are four-byte aligned in every file the loader has seen; keep
        # that true so nothing has to care.
        while len(body) % 4:
            body += b"\0"
    return bytes(header) + bytes(body)


def main():
    if len(sys.argv) != 4:
        raise SystemExit(__doc__)
    source, map_name, target = sys.argv[1:]
    data = read_source(source, map_name)
    out = strip(data)
    with open(target, "wb") as handle:
        handle.write(out)
    print(f"{len(data):,} -> {len(out):,} bytes ({len(out) * 100 // len(data)}%)  {target}")


if __name__ == "__main__":
    main()
