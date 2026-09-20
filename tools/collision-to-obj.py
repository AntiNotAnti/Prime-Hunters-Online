#!/usr/bin/env python3
"""A room's collision as a Wavefront OBJ, so it can be looked at in a 3D tool.

The game can draw its own collision -- Alt+K -- but only in a Debug build, and
only from inside the room, one viewpoint at a time. Neither of those helps with
the question that actually comes up, which is "what is the collision *shaped*
like here, and what is the geometry it is supposed to match": that wants two
meshes side by side in something you can orbit, and the game exports a model
(`FruityPrime -export NAME`, Collada) but nothing at all of its collision.

So this reads `<room>_Collision.bin` and writes the faces out as they are. It
converts nothing and infers nothing -- the numbers in the OBJ are the numbers
in the file, which are the numbers the game prints -- and it reads the shipped
rooms and the generated custom ones alike, since a custom map is written in the
game's own format.

Two details matter for reading the result:

**Every face is wound to agree with its own stored plane.** A collision face is
one-sided: `CheckSphereBetweenPoints` refuses a contact whose starting point is
behind the plane, so a face blocks from the side its normal points at and is
not there at all from the other. Winding the OBJ to match means a 3D tool with
backface culling on shows exactly the surfaces that would stop you, and a
surface that faces away -- the outside of a wall you are standing inside of --
disappears, which is the truth about it rather than a display artifact.

**It is Y-up, as the game is.** A coordinate read off the game's own debug
output pastes straight into a 3D tool's "jump to" box, which is the point.
`--zup` rotates it for Blender and for anything else that wants Z up.

    python tools/collision-to-obj.py path/to/mk_blockfort_Collision.bin
    python tools/collision-to-obj.py mk_blockfort --files ~/mph-test/files/AMHJ1
    python tools/collision-to-obj.py dust2 --files FILES --group terrain --zup

The room binaries are generated into the player's own extracted files, under
`_archives/<room>/`, so `--files` is that directory -- the one `paths.txt`
points at. Nothing here writes to it.
"""

import argparse
import math
import os
import struct
import sys
from pathlib import Path

# The header the game reads (Formats/Collision.cs, `CollisionHeader`): 84
# bytes, and the type tag is what tells an MPH room from a First Hunt one.
_HEADER = "<4s10I3i3i4I"
_HEADER_SIZE = 84
assert struct.calcsize(_HEADER) == _HEADER_SIZE

# Fixed point: the format stores world units multiplied by 4096.
_FX = 4096.0

# Flags bits 5-8, per `CollisionResult.Terrain`.
_TERRAIN = [
    "metal", "orangeholo", "greenholo", "blueholo", "ice", "snow",
    "sand", "rock", "lava", "acid", "gorea", "unknown11", "all",
]


class CollisionFile:
    """The parts of `wc01` this needs: the points, the planes and the faces."""

    def __init__(self, data: bytes, name: str):
        if len(data) < _HEADER_SIZE:
            raise ValueError(f"{name} is too short to be a collision file.")
        fields = struct.unpack_from(_HEADER, data, 0)
        if fields[0] != b"wc01":
            # First Hunt rooms use a different layout entirely, and the game
            # reads them down a separate path. Say so rather than producing
            # nonsense out of the wrong offsets.
            raise ValueError(
                f"{name} is not an MPH collision file (type tag "
                f"{fields[0]!r}, expected b'wc01'). A First Hunt room uses "
                "another format, which this does not read."
            )
        (_, point_count, point_offset, plane_count, plane_offset,
         index_count, index_offset, data_count, data_offset,
         data_index_count, data_index_offset,
         self.parts_x, self.parts_y, self.parts_z,
         min_x, min_y, min_z,
         entry_count, entry_offset, portal_count, portal_offset) = fields
        self.min_position = (min_x / _FX, min_y / _FX, min_z / _FX)
        self.entry_count = entry_count
        self.portal_count = portal_count
        self.points = [
            tuple(v / _FX for v in struct.unpack_from("<3i", data, point_offset + i * 12))
            for i in range(point_count)
        ]
        self.planes = [
            tuple(v / _FX for v in struct.unpack_from("<4i", data, plane_offset + i * 16))
            for i in range(plane_count)
        ]
        self.point_indices = [
            struct.unpack_from("<H", data, index_offset + i * 2)[0]
            for i in range(index_count)
        ]
        # 16 bytes each: counter (run time only), plane, flags, layer mask,
        # padding, how many points, where they start.
        self.faces = [
            struct.unpack_from("<iHHHHHH", data, data_offset + i * 16)
            for i in range(data_count)
        ]
        self.data_index_count = data_index_count

    @classmethod
    def load(cls, path: Path) -> "CollisionFile":
        return cls(path.read_bytes(), path.name)

    def face_points(self, face):
        """A face's own points, without the copy of the first that follows them.

        The format repeats each face's opening index after its last one,
        because the run-time edge test reads `index + 1` and relies on finding
        it. It is the same point, and a polygon that states it twice is a
        polygon most tools will not thank you for.
        """
        count, start = face[5], face[6]
        return [self.points[self.point_indices[start + k]] for k in range(count)]


def _sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _newell(points):
    """The polygon's own normal, unnormalised, and twice its area as a length.

    Newell rather than one cross product because a collision face may have up
    to ten points and is not required to be perfectly flat.
    """
    total = (0.0, 0.0, 0.0)
    for i in range(len(points)):
        a = points[i]
        b = points[(i + 1) % len(points)]
        total = (
            total[0] + (a[1] - b[1]) * (a[2] + b[2]),
            total[1] + (a[2] - b[2]) * (a[0] + b[0]),
            total[2] + (a[0] - b[0]) * (a[1] + b[1]),
        )
    return total


def group_of(kind: str, normal, flags: int, layer_mask: int) -> str:
    if kind == "none":
        return "collision"
    if kind == "slope":
        # The distinction a person actually wants when they open the file:
        # what can be stood on, what stops you walking, what is overhead.
        if normal[1] > 0.7:
            return "floor"
        if normal[1] < -0.7:
            return "ceiling"
        return "wall"
    if kind == "axis":
        # The low two bits of the layer mask are the normal's primary axis,
        # which the run-time point-on-face test reads to pick its projection.
        return f"axis{layer_mask & 3}"
    if kind == "terrain":
        return material_of(flags)
    raise ValueError(kind)


def material_of(flags: int) -> str:
    """What this face is, as the name a recipe's "collision" key reads back.

    Everything the format holds per face goes in it, because a material name
    is the only per-face channel an OBJ has and `CollisionObj.Surface.Parse`
    is the other end of this grammar: <terrain>[_attribute...]. Anything
    dropped here is lost on the way back, so nothing is dropped.

    Written whatever --group says, so that an export made to look at is also
    one that can be edited and put back. The group and the material are
    separate things in an OBJ: `g floor` is a handle for selecting the floor
    in a 3D tool, `usemtl sand` is what the floor is made of, and a file
    carries both without either costing the other.
    """
    terrain = (flags & 0x1E0) >> 5
    name = _TERRAIN[terrain] if terrain < len(_TERRAIN) else f"terrain{terrain}"
    slip = (flags & 0x18) >> 3
    if slip:
        name += f"_slip{slip}"
    for bit, word in ((0x1, "damaging"), (0x200, "reflect"), (0x2000, "noplayers"),
                      (0x4000, "nobeams"), (0x8000, "noscan")):
        if flags & bit:
            name += f"_{word}"
    return name


# Something to tell the groups apart by at a glance. A 3D tool will not read
# the names, but it will read these.
_COLORS = {
    "floor": (0.35, 0.70, 0.35),
    "wall": (0.75, 0.75, 0.80),
    "ceiling": (0.70, 0.45, 0.35),
    "collision": (0.75, 0.75, 0.80),
    "axis0": (0.85, 0.40, 0.40),
    "axis1": (0.45, 0.80, 0.45),
    "axis2": (0.40, 0.55, 0.90),
    # --group terrain, by the terrain the name starts with. The three that
    # hurt you are the loud ones: a face you did not mean to make damaging is
    # the mistake this whole grammar is arranged to make visible.
    "metal": (0.72, 0.74, 0.78),
    "orangeholo": (0.90, 0.60, 0.25),
    "greenholo": (0.35, 0.85, 0.45),
    "blueholo": (0.35, 0.60, 0.95),
    "ice": (0.70, 0.90, 0.95),
    "snow": (0.95, 0.95, 0.95),
    "sand": (0.85, 0.75, 0.45),
    "rock": (0.55, 0.48, 0.42),
    "lava": (1.00, 0.25, 0.05),
    "acid": (0.55, 0.95, 0.15),
    "gorea": (0.75, 0.25, 0.75),
}
_FALLBACK = (0.70, 0.70, 0.70)


def color_of(name: str):
    """A group's colour, ignoring whatever attributes follow the terrain.

    Except that `damaging` is not an attribute you want to have to read: it
    goes red whatever it is attached to.
    """
    if "_damaging" in name:
        return (1.00, 0.15, 0.15)
    return _COLORS.get(name.split("_")[0], _FALLBACK)


def find_room(name: str, files_root: Path) -> Path:
    """The collision file for a room, wherever the extracted files put it.

    A generated custom map lands in `_archives/<room>/`, a shipped one in the
    archive it belongs to, and the name is lower case in both -- so this looks
    for the file rather than assuming the path.
    """
    target = f"{name.lower()}_collision.bin"
    matches = [p for p in files_root.rglob("*_[Cc]ollision.bin")
               if p.name.lower() == target]
    if not matches:
        raise SystemExit(
            f"No {name}_Collision.bin under {files_root}. A custom map's is "
            "generated on first build -- run `FruityPrime -mapgen` if it is "
            "not there yet."
        )
    if len(matches) > 1:
        listing = "\n  ".join(str(p) for p in sorted(matches))
        raise SystemExit(f"{name} is ambiguous; name the file instead:\n  {listing}")
    return matches[0]


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="Write a room's collision out as a Wavefront OBJ.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="The OBJ is in the game's own coordinates (Y up) unless --zup.",
    )
    parser.add_argument("target",
                        help="a *_Collision.bin, or a room name with --files")
    parser.add_argument("-o", "--out", help="output .obj (default: beside the input)")
    parser.add_argument("--files", type=Path, default=os.environ.get("MPH_FILES"),
                        help="the extracted game files, to look a room name up in "
                             "(default: $MPH_FILES)")
    parser.add_argument("--group", default="slope",
                        choices=["slope", "none", "axis", "terrain"],
                        help="how to split the OBJ into groups (default: slope). "
                             "`terrain` is the one a map can be generated from "
                             "again: it names each material <terrain>[_attribute...], "
                             "which is what the recipe's \"collision\" key reads back")
    parser.add_argument("--zup", action="store_true",
                        help="rotate to Z up, for Blender and friends")
    parser.add_argument("--no-mtl", action="store_true",
                        help="do not write the .mtl that colours the groups")
    parser.add_argument("--keep-winding", action="store_true",
                        help="write each face in the order the file stores it, "
                             "rather than winding it to agree with its plane")
    args = parser.parse_args(argv)

    path = Path(args.target)
    if not path.is_file():
        if args.files is None:
            raise SystemExit(
                f"{args.target} is not a file. Pass a path to a "
                "*_Collision.bin, or a room name together with --files."
            )
        path = find_room(args.target, Path(args.files))

    collision = CollisionFile.load(path)
    out_path = Path(args.out) if args.out else path.with_suffix(".obj")
    mtl_path = out_path.with_suffix(".mtl")

    def place(point):
        # Y up is what the game uses and what its debug output prints. Z up is
        # (x, -z, y): a rotation, so nothing is mirrored and no face turns
        # inside out on the way.
        if args.zup:
            return (point[0], -point[2], point[1])
        return point

    # Gather first, write second: the groups have to be contiguous in the file
    # and the normals deduplicated, and neither is known until every face has
    # been looked at.
    groups = {}
    normals = []
    normal_ids = {}
    degenerate = 0
    reversed_count = 0
    for face in collision.faces:
        points = collision.face_points(face)
        if len(points) < 3:
            degenerate += 1
            continue
        plane = collision.planes[face[1]]
        normal = plane[:3]
        area2 = _newell(points)
        if _dot(area2, area2) < 1e-12:
            # No plane of its own: it encloses no area, so there is nothing to
            # draw and nothing a 3D tool could show.
            degenerate += 1
            continue
        order = list(range(len(points)))
        if not args.keep_winding and _dot(area2, normal) < 0:
            order.reverse()
            reversed_count += 1
        name = (group_of(args.group, normal, face[2], face[3]), material_of(face[2]))
        key = tuple(round(v, 6) for v in place(normal))
        if key not in normal_ids:
            normal_ids[key] = len(normals) + 1
            normals.append(key)
        indices = [collision.point_indices[face[6] + k] for k in order]
        groups.setdefault(name, []).append((indices, normal_ids[key]))

    if not groups:
        raise SystemExit(f"{path.name} has no drawable collision faces.")

    with out_path.open("w", encoding="utf-8", newline="\n") as obj:
        obj.write(f"# collision of {path.name}\n")
        obj.write(f"# {len(collision.faces)} faces, {len(collision.points)} points, "
                  f"grid {collision.parts_x}x{collision.parts_y}x{collision.parts_z}\n")
        obj.write(f"# coordinates: {'Z up (rotated)' if args.zup else 'Y up, as the game stores them'}\n")
        obj.write("# faces are one-sided: each is wound to agree with its own plane,\n"
                  "# so with backface culling on you see exactly what would stop you\n")
        if not args.no_mtl:
            obj.write(f"mtllib {mtl_path.name}\n")
        for point in collision.points:
            x, y, z = place(point)
            obj.write(f"v {x:.6f} {y:.6f} {z:.6f}\n")
        for normal in normals:
            obj.write(f"vn {normal[0]:.6f} {normal[1]:.6f} {normal[2]:.6f}\n")
        written_group = None
        written_material = None
        for name in sorted(groups):
            group, material = name
            # An OBJ states a group and a material independently and keeps
            # whichever it was last told until it is told again, so a file
            # carries both and neither costs the other.
            if group != written_group:
                obj.write(f"g {group}\n")
                written_group = group
            if not args.no_mtl and material != written_material:
                obj.write(f"usemtl {material}\n")
                written_material = material
            for indices, normal_id in groups[name]:
                # OBJ counts from one, and every vertex of a collision face
                # shares the face's normal.
                corners = " ".join(f"{i + 1}//{normal_id}" for i in indices)
                obj.write(f"f {corners}\n")

    if not args.no_mtl:
        with mtl_path.open("w", encoding="utf-8", newline="\n") as mtl:
            mtl.write(f"# colours for {out_path.name}\n")
            for name in sorted({material for _, material in groups}):
                r, g, b = color_of(name)
                mtl.write(f"newmtl {name}\n")
                mtl.write(f"Kd {r:.3f} {g:.3f} {b:.3f}\n")
                mtl.write("Ka 0.000 0.000 0.000\n")
                mtl.write("d 1.0\nillum 1\n\n")

    written = sum(len(v) for v in groups.values())
    print(f"{path.name} -> {out_path}")
    print(f"  {written} faces, {len(collision.points)} points, {len(normals)} distinct normals")
    if not args.no_mtl:
        print(f"  {mtl_path.name} colours the materials")
    for name in sorted(groups):
        print(f"    {name[0]:>12} / {name[1]:<24} {len(groups[name])}")
    if reversed_count:
        print(f"  {reversed_count} faces rewound to agree with their plane")
    if degenerate:
        print(f"  {degenerate} faces skipped as degenerate (no area of their own)")
    if args.zup:
        print("  edited and put back, this needs \"zUp\": true in the recipe's \"collision\".")
    return 0


if __name__ == "__main__":
    sys.exit(main())
