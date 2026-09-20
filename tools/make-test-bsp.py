#!/usr/bin/env python3
"""Builds a minimal but valid Quake 3 (IBSP 46) level, purely synthetic, so the
importer can be tested without anyone's game data."""
import struct, sys

LUMPS = 17
planes, brushes, brushsides, verts, meshverts, faces, textures, models = [], [], [], [], [], [], [], []

def texture(name, flags=0, contents=1):
    textures.append((name, flags, contents))
    return len(textures) - 1

def plane(n, d):
    for i, (pn, pd) in enumerate(planes):
        if pn == n and abs(pd - d) < 1e-4:
            return i
    planes.append((n, d))
    return len(planes) - 1

def box_brush(mins, maxs, tex):
    first = len(brushsides)
    sides = [((1,0,0), maxs[0]), ((-1,0,0), -mins[0]),
             ((0,1,0), maxs[1]), ((0,-1,0), -mins[1]),
             ((0,0,1), maxs[2]), ((0,0,-1), -mins[2])]
    for n, d in sides:
        brushsides.append((plane(n, d), tex))
    brushes.append((first, len(sides), tex))

def quad(p0, p1, p2, p3, normal, tex, uvscale=0.01):
    """Two triangles, wound as given, with world-projected UVs."""
    first_vert = len(verts)
    first_mesh = len(meshverts)
    for p in (p0, p1, p2, p3):
        if abs(normal[2]) > 0.5:
            u, v = p[0] * uvscale, p[1] * uvscale
        elif abs(normal[0]) > 0.5:
            u, v = p[1] * uvscale, -p[2] * uvscale
        else:
            u, v = p[0] * uvscale, -p[2] * uvscale
        verts.append((p, (u, v), (0.0, 0.0), normal, (255, 255, 255, 255)))
    for i in (0, 1, 2, 0, 2, 3):
        meshverts.append(i)
    faces.append(dict(texture=tex, type=1, vertex=first_vert, n_vertexes=4,
                      meshvert=first_mesh, n_meshverts=6, normal=normal))

wall = texture("textures/base_wall/concrete", 0, 1)
floor = texture("textures/base_floor/metal", 0, 1)
trig = texture("textures/common/trigger", 0x80, 0x40000000)

# a floor, four walls and a centre platform
box_brush((-512, -512, -16), (512, 512, 0), floor)
box_brush((-544, -512, 0), (-512, 512, 256), wall)
box_brush((512, -512, 0), (544, 512, 256), wall)
box_brush((-512, -544, 0), (512, -512, 256), wall)
box_brush((-512, 512, 0), (512, 544, 256), wall)
box_brush((-96, -96, 0), (96, 96, 64), wall)

quad((-512,-512,0), (512,-512,0), (512,512,0), (-512,512,0), (0,0,1), floor)
quad((-96,-96,64), (96,-96,64), (96,96,64), (-96,96,64), (0,0,1), wall)
quad((-96,-96,0), (96,-96,0), (96,-96,64), (-96,-96,64), (0,-1,0), wall)
quad((96,-96,0), (96,96,0), (96,96,64), (96,-96,64), (1,0,0), wall)
for x, nx in (((-512,), -1), ((512,), 1)):
    pass
quad((-512,-512,0), (-512,512,0), (-512,512,256), (-512,-512,256), (1,0,0), wall)
quad((512,512,0), (512,-512,0), (512,-512,256), (512,512,256), (-1,0,0), wall)

# model 0 is the world; model 1 is the push trigger's volume
models.append(((-544,-544,-16), (544,544,256), 0, len(faces), 0, len(brushes)))
box_brush((-320, -320, 0), (-256, -256, 32), trig)
models.append(((-320,-320,0), (-256,-256,32), 0, 0, len(brushes)-1, 1))

entities = """
{
"classname" "worldspawn"
}
{
"classname" "info_player_deathmatch"
"origin" "-256 -256 24"
"angle" "45"
}
{
"classname" "info_player_deathmatch"
"origin" "256 256 24"
"angle" "225"
}
{
"classname" "trigger_push"
"model" "*1"
"target" "t1"
}
{
"classname" "target_position"
"targetname" "t1"
"origin" "0 0 96"
}
{
"classname" "weapon_railgun"
"origin" "0 0 88"
}
{
"classname" "item_quad"
"origin" "300 -300 24"
}
"""

def pack_lumps():
    data = []
    data.append(entities.encode("ascii") + b"\0")
    data.append(b"".join(n.encode("ascii").ljust(64, b"\0") + struct.pack("<ii", f, c) for n, f, c in textures))
    data.append(b"".join(struct.pack("<4f", n[0], n[1], n[2], d) for n, d in planes))
    for _ in range(4):  # nodes, leafs, leaffaces, leafbrushes
        data.append(b"")
    data.append(b"".join(struct.pack("<6f4i", *mins, *maxs, f, nf, b, nb) for mins, maxs, f, nf, b, nb in models))
    data.append(b"".join(struct.pack("<3i", *b) for b in brushes))
    data.append(b"".join(struct.pack("<2i", *s) for s in brushsides))
    data.append(b"".join(struct.pack("<3f2f2f3f4B", *p, *st, *lm, *n, *c) for p, st, lm, n, c in verts))
    data.append(b"".join(struct.pack("<i", m) for m in meshverts))
    data.append(b"")  # effects
    face_bytes = b""
    for f in faces:
        face_bytes += struct.pack("<7i", f["texture"], -1, f["type"], f["vertex"],
                                  f["n_vertexes"], f["meshvert"], f["n_meshverts"])
        face_bytes += struct.pack("<i", -1)          # lightmap index
        face_bytes += struct.pack("<2i2i", 0, 0, 0, 0)   # lm start, size
        face_bytes += struct.pack("<3f", 0, 0, 0)        # lm origin
        face_bytes += struct.pack("<6f", 0, 0, 0, 0, 0, 0)  # lm vecs
        face_bytes += struct.pack("<3f", *f["normal"])
        face_bytes += struct.pack("<2i", 0, 0)           # patch size
    data.append(face_bytes)
    for _ in range(3):  # lightmaps, lightvols, visdata
        data.append(b"")
    return data

lumps = pack_lumps()
assert len(lumps) == LUMPS, len(lumps)
offset = 8 + LUMPS * 8
header = b"IBSP" + struct.pack("<i", 46)
directory = b""
body = b""
for chunk in lumps:
    directory += struct.pack("<ii", offset + len(body), len(chunk))
    body += chunk
    while len(body) % 4:
        body += b"\0"
open(sys.argv[1], "wb").write(header + directory + body)
print(f"wrote {sys.argv[1]}: {len(faces)} faces, {len(brushes)} brushes, {len(verts)} vertices")
