# Custom maps: the generator and the Quake 3 importer

## What ships: one file

**A map is handed out as a `.fpmap` bundle** -- the recipe, the level and the
baked texture pack in one zip, with the level trimmed to the lumps the importer
reads (`Q3Bsp.UsedLumps`: entities, textures, planes, models, brushes,
brushsides, vertexes, meshverts, faces). Everything else a compiler writes --
lightmaps, light volumes, visdata, the BSP tree -- is for a renderer that
lights and culls the Quake way, and this importer does neither: in df_dust2
they are 7.6 MB of the 8.8. Cooked and compressed, de_dust2 is **376 KB**
against the **2.8 MB** its folder weighed.

| | Command | Note |
|---|---|---|
| cook | `-mapbundle ["NAME"] [-mapdir DIR] [-out FILE]` | default output is the top of `maps/`, one file per map |
| read | nothing | `MapDefinition.Load` opens a bundle like a recipe; `Q3Bsp.Load` already opened a zip and found a level in it by name, which is how it reads a `.pk3` |

- The bundle is a **build artifact**: gitignored, cooked by both workflows
  before they publish. The folder it is cooked from -- recipe, `.pk3`, `.tex` --
  is the source, and a `.pk3` is kept out of every package by
  `CopyToPublishDirectory=Never` while still being copied to a *build* output,
  which is what the convert-and-test loop uses.
- **A folder and a bundle of the same name are the same map.** `MapFiles()`
  lists bundles first and drops any recipe with a bundle's name, so a checkout
  that has both registers one room and not two.
- The recipe inside a bundle is rewritten as it is cooked: `import.source`
  points at `maps/<level>.bsp` *inside* the bundle, so a bundle names nothing
  outside itself.
- **Cooking bakes the texture pack if it is not already there**, and refuses
  to write a bundle it cannot give one to. The pack is derived from the
  level's own art, so it is gitignored like the room binaries and the game
  bakes it the first time a map is played -- which means the machine that
  cooks a release has never played the map and has none, and a bundle carries
  the level trimmed to the lumps the importer reads, which hold no art at all.
  Every published bundle before this fix carried `"Textures": ""`: a room with
  no materials, which came out as an `ArgumentOutOfRangeException` the moment
  somebody picked DUST2 -- in a Windows GUI process with no console to say so,
  with the launcher's picture missing for the same reason. Three things now
  stand between that and a player: `Cook` bakes and then throws,
  `Q3Import` names the missing pack instead of indexing a list that is not
  there, and `tools/check-maps-shipped.sh` reads the recipe out of each
  shipped bundle and checks the pack it names is inside it.
- **A custom room that failed to build is refused, not loaded.** It is still
  in the room table -- the table is built once, from the recipes -- so the
  launcher lists it and the picker shows a frame for it.
  `CustomRooms.WhyUnplayable` answers why, and `MatchStart.Launch` prints that
  and returns instead of reaching for binaries that are not there.
- **It is what put custom maps on Android.** An APK's asset list does not
  recurse, and the asset glob was `maps\*.json` -- so a map that keeps its
  level in a folder of its own arrived as neither, and the phone listed 27
  rooms. One file at the top of `maps/` is visible to both halves.
- A bundle does not settle whether a level may be handed out. Cooking
  somebody's level into a smaller container leaves it their level.

**A map is two files: the recipe and the level, and both ship.** The asset
guard used to refuse a `.pk3` by extension; it now refuses only id Software's
own paks, by name. What that guard is for is keeping somebody else's
*commercial* data out -- the cartridge, and a game somebody bought -- and a
custom map made by a person who wants it played is not that. Whether a
particular level may be published is a judgement no script can make, so it is
made by whoever commits it.

The room binaries are still generated on the player's machine, because they
land where that machine's extracted files are. So is the texture pack: a map
file that names one and a folder that has only the `.pk3` is the normal state
of a fresh clone, and the importer bakes it. `tools/check-maps-shipped.sh`
runs in CI over the repository and over every published package, because a map
file that arrives without its level registers a room the game then declines to
build -- the player just sees the 27 cartridge rooms and no sign anything was
meant to be there.

**Only recipes used to ship.** The game is the 27 multiplayer
rooms of the cartridge plus whatever the player has the source for; what is
described below is the hook. `maps/q3dm17.json.example` is the worked example,
and `maps/dust2/dust2.json` is a real one. A map whose level is *not* here --
the Quake III example, which is id Software's commercial data -- is left out at
startup and `-rooms` prints 27 again. The three maps that used to travel with the repository --
`longestyard`, `testbox` and the converted OpenArena level `wrackdm17`, with
its stripped `.bsp` and its baked `.tex` -- were taken out, along with the GPL
notice they needed.

Everything lives in `src/MphRead/Mods/MapGen/`. Two upstream files carry a
change and both are one token: `RepackCollision` gained the word `partial`,
and `Metadata`'s two room tables are wrapped in a call that appends the custom
rooms. `Scene` was already `partial`, so the preview camera needed nothing.

## What a room is, and what had to be built

A room is four files: `_Model.bin` (geometry, materials and, here, the
textures inline), `_Anim.bin`, `_Collision.bin`, and `_Ent.bin` under
`levels/entities`. Upstream already had writers for all three real ones -- they
existed to round-trip the game's own files in a test -- so the work was
feeding them structures that never came from a file:

| Piece | Where | Note |
|---|---|---|
| `Repack.PackModel` | upstream | takes `Node`/`Material`/`Mesh` objects, which only have constructors from the raw structs |
| `RawStructs.cs` | new | writes those raw structs byte by byte and marshals them back, so no upstream constructor had to be widened |
| `MapCollisionPacker.cs` | new | writes the collision file. Upstream's packer produces the same bytes but is quadratic twice over -- it scans the point list it is building, and asks every grid cell about every face. Fine for a hundred faces, hopeless for the six thousand a converted level has |
| `Repack.PackEntities` | upstream, was private | reached through the partial class |

## Traps that cost time, in the order they bite

- **Display lists are packed four opcodes to a word.** A list whose length is
  not a multiple of four cannot be written at all; pad with `NOP`.
- **A room needs a node whose name starts with `rm`**, or `RoomEntity.Setup`
  finds no room part and asserts. It must also *have a child*: entities
  reference a node by name, and `GetNodeRefByName` asserts `ChildIndex != -1`.
  Hence two nodes -- `rmMain` with no meshes, and `geo1` under it with all of
  them.
- **A node name starting with `_` is layer-filtered.** Anything else is in
  every layer, which is what a custom map wants.
- **`Node.MeshId` is a byte offset**, not an index: it is the first mesh times
  two.
- **Collision `LayerMask` is not just a layer mask.** Its low two bits are the
  normal's primary axis, which the point-on-face test reads to choose its
  projection plane; bit 2 means "in every layer". Get the axis wrong and the
  surface is there but nothing stands on it.
- **A jump pad with no `TriggerFlags` never fires.** It needs `PlayerBiped`,
  `PlayerAlt`, and `IncludeBots` or the bots ignore it. Its volume is
  entity-local and its `BoxPosition` is the *corner*, not the centre.
- **Camera limits are inherited from the room metadata**, so a custom map
  leaves them zero rather than borrowing another room's box.
- **The collision grid is indexed with 16 bits.** Cell size is fixed at 4
  units (the run-time lookup divides by four), so a big level means a lot of
  cells, and each face is listed in every cell it touches. Past 65535 listings
  the format cannot express it -- `MapCollisionPacker` says so by name instead
  of writing something that half works.
- **A Quake level is sealed inside a shell of sky brushes.** Importing it puts
  hundreds of units of empty grid around the level: wrackdm17 measured
  372x244x294 units with the shell and 157x77x176 without, which is the
  difference between overflowing that 16-bit index and fitting comfortably.
- **Vertices are 16-bit fixed point**: model space is +/-8 units, multiplied by
  the model scale of 2^`scaleFactor`. Texture coordinates are 1.11.4 and run
  out at 2047 texels, which is why both the brush projection and the importer
  rebase UVs per face.

## Textures

Two ways, and the second is the better one.

**Baked from the level's own art.** `tools/bake-textures.py` decodes the
shaders a level draws with, scales them to 64x64 and quantises each to a
256-colour palette, into a `.tex` pack the packer turns into `TextureInfo` and
`PaletteInfo` directly -- one material per shader. Ahead of time, because the
conversion runs on the machine that plays the game and the Android head has no
JPEG decoder: its STB natives are desktop builds, left out of the APK on
purpose. Baking is what lets a converted map look like itself, and it takes
the cartridge out of the loop entirely -- a room generated this way contains
nothing from the dump.

A shader with no image of its own (a light or an effect, defined in a
`.shader` script rather than a file -- four of wrackdm17's twenty-two) has its
surfaces dropped rather than painted with somebody else's texture.

**The sky** is baked too, and `keepSky` draws it. A sky shader names no image
of its own -- `skyparms` points at six box sides or a pair of scrolling cloud
layers -- so `textures/skies/cloudsky` is answered by `cloudsky_1`, and the
first suffix that exists is taken. Its surfaces are drawn but never collision,
and their texture coordinates are **thrown away and reprojected**: Quake never
reads them, it draws a dome from the shader, so the numbers in the file tile a
cloud texture some fifty times across the lid and it comes out as a
checkerboard. Two repeats across the level reads as a sky. Without any of this
the level has a black ceiling, which on a desert map looks like a bug.

Images are matched **case-insensitively**: a shader name in a `.bsp` is not
the spelling of the file it came from -- the compiler upper-cases some of them,
and a level whose author worked on Windows has `SandTrim.JPG` answering to
`textures/dust2/SANDTRIM`. Matching exactly finds nothing and the level comes
out with no textures at all.

**Borrowed from a shipped room**, the older way, still used by the hand-built
maps. A map borrows them from a room the player already has: `textureSource` names
it, and each material says which of that room's materials to take the texture
and palette from. They are copied as a pair, so a material cannot end up with
someone else's palette, and they are written *inline* in the model file, so a
room is one file with no separate `_Tex.bin`.

`-mapmaterials "MP3 PROVING GROUND"` prints the menu.

## The Quake 3 importer

`Q3Bsp.cs` reads the lumps (IBSP 46, from a `.bsp` or straight out of a
`.pk3`); `Q3Import.cs` converts. Three things are translated rather than
copied:

- **Axes.** Quake is Z-up, this engine is Y-up. `(x, y, z) -> (x, z, -y)`
  keeps the handedness, so no surface ends up inside out.
- **Winding.** Preserving the handedness is not enough: Quake winds a front
  face clockwise and culls GL's *front*, while this engine culls the back of a
  counter-clockwise one. Carried across unchanged, every surface is visible
  only from the side you are never on -- the floor disappears from above and
  the level reads as *missing geometry* rather than as inside-out, which is
  what made this cost a day. Each triangle's vertices are therefore emitted in
  reverse. The check is one shot straight down: if the floor is not there, the
  winding is wrong.
- **Scale.** The number that matters is the one that keeps the level's routes
  intact. Samus leaves the ground at 1228/4096 per frame against 77/4096 of
  gravity: 2.39 units up, about 7.7 across at her walking cap. A Quake player
  leaves at 270 u/s under 800 u/s^2: 45.6 up, about 216 across. So dividing by
  less than 216/7.7 = **28.2** makes the world too big for its own jumps --
  at 22, which this defaulted to when it was chosen by feel, a full-length
  Quake jump is 9.8 units against her 7.7 and the route is simply gone.
  `unitsPerUnit` now defaults to **28**. 35 would match the architecture
  exactly (a 56-unit Quake player against Samus's 1.6); anything above 28 only
  makes jumping easier than the author intended, anything below breaks routes.
- **Jump pads.** Quake solves a pad's launch velocity at runtime from where it
  points, so the arc is re-solved here under this game's gravity
  (`bipedGravity`, -77/4096 per frame squared at 30 fps). Carrying the velocity
  across would land players nowhere near the target.

Collision comes from the brushes, not the drawn surfaces: each brush side is
recovered by starting with a plane-sized sheet and clipping it against every
other plane of the brush. Bezier patches are skipped and counted -- they are
control points rather than triangles, and their collision lives with the patch,
so a skipped one leaves a hole rather than an invisible wall.

**Collision comes from model 0 only.** Models 1 and up are the level's moving
and triggering parts and their brushes sit in the same list; a trigger's brush
is a volume, not a wall, but the format keeps the trigger shader's contents and
in at least one real level those say solid. Importing them put seven invisible
walls in the middle of df_dust2, standing exactly where its author had put a
tripwire.

**`keepClip` (default true) keeps the level's player-clip brushes.** True is
right for a level authored for the game it came from, where a clip usually
stops an exploit or smooths a staircase. It is wrong for one whose clips fence
a route, which is every race map: df_dust2 has seventy of them and they turn a
map you want to roam into a corridor.

**Bezier patches are tessellated**, not dropped. A patch is where a level keeps
its curves -- an archway, a ramp, a pipe -- and in Quake its collision comes
from the patch rather than from a brush behind it, so dropping them took out
both at once: a doorway with a hole where its arch should be, that you could
also walk through. `patchLevel` (default 3) is how many quads along each side
of each biquadratic piece.

**A collision polygon is only as good as its worst edge, and the run-time face
test never says so.** `CheckSphereBetweenPoints` walks a face's points in
order, crosses each edge's direction with the face normal and rejects the
contact if it is outside any of them. That is a correct test for a convex
polygon whose edges all have a meaningful direction, and it fails **silently
and catastrophically** for one that does not: a face with a bad edge does not
misbehave near that edge, it rejects most of its own interior and the surface
is simply not there. `MK_BlockFort` -- geometry from 3DS Max, clipped with
`s_common/modelclip` -- arrived with both ways of getting it wrong at once,
and the report was "you walk through the outside wall and fall out of the
map". Two fixes, both in `Q3Import`:

- **`Clip` clamps the crossing parameter to 0..1.** A point is kept when it is
  inside the plane *or within epsilon of it*, while the crossing between two
  points is solved for where the plane is rather than for where epsilon is, so
  a pair straddling that gap solves to a `t` outside 0..1 -- and the sheet
  being clipped starts 131,072 units across, so "just past the end of the
  edge" is thousands of units away. The polygon comes out with a vertex out of
  order, i.e. a bowtie. 19 of that level's brush sides, among them every
  fort's ramp and its middle floor's corner.
- **`Weld`'s tolerance scales with the polygon.** The clipping runs at a
  magnitude where a float carries about 0.008 of a unit and half a dozen clips
  compound it, so a corner two clips arrive at separately lands twice, some
  0.08 of a unit apart -- four times the old fixed tolerance of 0.02. The pair
  survives into the collision file as an edge a thousandth of a unit long
  whose direction is whichever way the rounding fell, and on the arena's east
  wall it fell along the wall: the lower half of an 82 x 9 unit wall rejected
  everything more than 0.03 units below its top edge, so **half of every one
  of that level's four outside walls had no collision at all**. 113 of its
  collision edges were shorter than a unit; 12 survive this, all of them real.
  A thousandth of the polygon's own extent is far above the clipping error at
  any size and far below anything an author drew.

The check that finds this is cheap and worth reaching for whenever a surface
"is not there": take the generated `_Collision.bin`, sample each face's own
interior, and run the engine's edge test against the face the points came
from. A face that rejects its own middle is broken. Both maps now read zero.

**Buried brush sides are dropped**, and that is what makes a real level fit.
A level's walls are stacks of brushes, so most brush sides face into another
brush and nothing outside the solid can ever touch them -- on df_dust2, 6,119
of 10,624. They are not free: the grid lists every face in every cell it
reaches and indexes those listings with 16 bits, and the buried ones alone
overflow it at any scale worth playing. A side is dropped only when its centre,
every corner and every edge midpoint -- each drawn a little in from the rim and
pushed a unit out along the normal -- all land inside another solid brush.
Every sample has to agree, because missing a buried face costs one listing
while dropping an exposed one leaves a hole in the floor.

`-mapgen` reports the number the grid needs when it does not fit, so the
scale can be chosen by arithmetic instead of by bisection.

`keepSpawns` (default true) says whether to take the level's own player
starts. True is right for a deathmatch level, authored with eight of them in
the places its author wanted people to appear. It is wrong for anything else:
a race level has one start, often on a ledge sealed off from the course, and a
player who spawns there is stuck. Turn it off and the map file's spawns are
the only ones.

`keepItems` (default true) says whether to take the level's own pickups --
health, armour, ammo, weapons -- on top of the recipe's own `items`.

**The default is true because that is what every map did before there was a
choice**, and an existing recipe has to keep generating the room it was
generating. It is the wrong answer for a map anybody is working on, and the
reason is that the level's pickups were arriving *invisibly*: they are read
out of the `.bsp` on every generation, so a recipe listing three items could
produce a room holding thirty, and an author who wanted one of the level's own
moved or gone had nowhere to say so. `-mapitems` writes them out, `keepItems:
false` stops them arriving twice, and the recipe is then the only answer to
what the map holds -- which is the point of there being a recipe.

`-q3convert` now does both halves itself: it transcribes what it finds into
`items` and sets `keepItems` to false, so a freshly converted map is already
in that state. `-noitems` writes none and still sets it false.

### What a level already holds

```bash
FruityPrime -mapitems "ROOM"                       # from the recipe
FruityPrime -mapitems level.pk3 -map NAME [-scale N]   # before there is one
```

Prints the level's pickups as the `items` block a recipe would carry -- one
line each, in world units, with the Quake classname each came from and what
this game puts in its place. Given a room name it reads that map's recipe, so
the scale is the one the room is actually built at and the count it prints is
the count the room actually has.

**It writes nothing.** A recipe is allowed `//` comments -- every one in the
repository opens with a line saying where to get the level -- and serializing
one back over itself would throw those away along with whatever the author had
laid out by hand. So the block is printed to paste and the file stays theirs.

It also says which pickups carry a `targetname`. Those are handed out by the
level's own scripts (`target_give`) rather than walked over, and a mapper
usually stands them in a closet nobody can reach: df_dust2's rocket launcher
and its ammo are one of these, 0.6 units apart behind the architecture. They
are still items standing in the world, so nothing is dropped on their account
-- but they are the ones an author most often wants to delete, so they are
marked.

### Verified against a real level

OpenArena's `wrackdm17` -- its homage to The Longest Yard, freely licensed --
converted and played: 804 surfaces to 3075 triangles, 993 brushes to 6122
collision faces, 25 spawns, 13 jump pads, 61 item spawns, eight players
spawning and dying in the void at the kill height. That was the proof the
importer works; the map itself no longer ships. Quake 3's own `pak0.pk3` is
not in this repository and is not fetched by anything here; it is id Software's
commercial data, and the same rule the project applies to the cartridge applies
to it.

**df_dust2**, a DeFRaG rebuild of Counter-Strike's de_dust2, is the second and
the one that stretched the format: 2,314 surfaces to 9,540 triangles, 1,751
solid brushes to 4,505 collision faces once the buried sides were dropped, 20
baked textures, 290 x 85 x 331 units at 34 Quake units each. 34 is not a
preference: below it the collision grid does not fit, and much above it a
128-unit wall becomes something Samus can jump. It is a race map with one
sealed start, so `keepSpawns` is off and the ten spawn points, the weapons and
the pickups are the map file's own. Neither the `.pk3` nor the `.tex` is in
this repository -- both are somebody else's game data -- and `.gitignore`
refuses them by extension.

`tools/make-test-bsp.py` writes a synthetic IBSP 46 level -- a floor, walls, a
platform, spawns, a push trigger and items -- so the importer can be exercised
with no game data at all. That is how it was tested before any real level was
available.

## Converting a level, in one command

```bash
FruityPrime -q3convert path/to/level.pk3 -map LEVELNAME -name ROOM [-noclip] [-noitems]
```

Bakes the textures, picks the scale, the extents, the kill height and the
vertex precision from the level's own geometry, writes the spawns and the
pickups from its entities, copies the `.pk3` in beside the result, and leaves a
`maps/<room>/<room>.json` that `-mapgen` can build. `-scale N` overrides the
scale, `-texsize N` the texture size, `-out DIR` where it lands.

It does **not** *place* weapons or powerups. Where those go is a judgement
about how the map plays -- which routes meet, what is worth contesting -- and
a generator that scattered them evenly would produce a map worse than one with
none. It prints the list of pickups a custom map may use, and leaves choosing
to a person.

It does write down the ones the level's own author placed, which is a
different thing. Those were being read out of the `.bsp` on every generation
anyway; listing them under `items` and setting `keepItems` to false only moves
them somewhere an author can edit them. `-noitems` writes none and still sets
the flag, for a level whose pickups are nothing you want.

The texture baking is in `MapTextureBake.cs` now, not only in
`tools/bake-textures.py`: the archive is a zip, the decoder is the one the
exporter already uses, and the median-cut quantiser is fifty lines, so a
conversion that needed a Python with Pillow on it needs nothing. The Python is
still there and still produces the same file.

**How the scale is chosen.** `TargetExtent` is 130 units and 35 is the floor.
The floor is the architecture matching exactly -- a 56-unit Quake player
against Samus's 1.6 -- and it is not the answer, because a level built for a
player who covers 216 units in a jump, converted for one who covers 7.7, is a
correct model of a map nobody can cross. Nor is the level's own player a
guide: df_dust2's crates are 128 and 192 units, which are de_dust2's 64 and 96
*doubled*, so that level is built at twice the scale of the map it copies. 130
was arrived at by measuring the crates. It puts the small one at 1.56 units
against Samus's 1.6 -- waist-high, which is what a crate is.

## Collision a person edits: the OBJ round trip

```bash
python tools/collision-to-obj.py dust2 --files FILES --group terrain   # out
# edit dust2_Collision.obj in Blender
FruityPrime -mapcheck "DUST2"      # what it will be
FruityPrime -mapgen "DUST2"        # in
```

with, in the recipe:

```jsonc
"collision": { "source": "dust2_collision.obj", "zUp": false }
```

**It replaces the collision the geometry makes, rather than adding to it**,
and that is the point rather than a limitation. The reason to reach for this
is that a converted level's collision is a by-product of somebody else's
level: every brush side survives, including the ones nobody can ever touch,
because the buried-face test only drops a side that is *inside another brush*,
not one that is merely somewhere no player can go. On df_dust2 that is **372
faces and 20,359 square units -- about a quarter of the room's collision area
and a seventh of its grid references** -- and no importer can fix it, because
the faces really are in the source. Which of them are worth keeping is a
judgement about the map. Adding could never delete one.

`map.Solid` and `map.Faces` are separate lists, so a map is still *drawn* from
the converted level; only what stops you comes from the mesh. `MapNodePacker`
reads the same list, so the bots' waypoints follow an edit with nothing else
to do.

**Winding is the whole contract.** A collision face is one-sided --
`CheckSphereBetweenPoints` refuses a contact that starts behind the plane -- so
the polygon's own normal is the side it blocks from, and a face flipped in
Blender is a face you walk through. The exporter winds every face to agree with
its stored plane, and `CollisionObj` derives the plane from the winding by the
same Newell sum. `vn` is read by neither: a tool that writes a normal
disagreeing with its own winding would otherwise get to decide which side of a
wall you can walk through.

### The material name is the whole per-face vocabulary

`<terrain>[_attribute...]`, because a material name is the only per-face
channel an OBJ has and everything the collision format holds per face fits in
one:

| | |
|---|---|
| terrain | `metal` `orangeholo` `greenholo` `blueholo` `ice` `snow` `sand` `rock` `lava` `acid` `gorea` |
| attributes | `slip0`-`slip3`, `damaging`, `reflect`, `noplayers`, `nobeams`, `noscan` |

So `sand`, `rock_slip1`, `lava_damaging`, `metal_nobeams`. Terrain decides the
footstep, landing and sliding sounds (`Metadata.TerrainSfx`), the footstep
effects, the beam impact splats, and the debug view's colours; `Lava` is
behaviour -- `PlayerFlags1.OnLava`, and the AI avoids it. **`nobeams` is the
one that earns its keep on a converted level**: a Quake player-clip brush is a
wall shots are meant to fly through, and without it every clip in the level
stops bullets. The cartridge's own MP3 PROVING GROUND uses it on four faces.

A face with **no material at all is plain metal**, no attributes -- which is
what an ordinary OBJ out of a tool nobody asked to write materials means, and
is also what every converted map is today, since nothing in the importer has
ever set a terrain. A name that is *not* one of these is **refused**, never
ignored: a misspelt `damaging` would silently be an ordinary floor, and a
misspelt anything on a lava face is a floor that kills without saying so.
Blender's `.001` suffix on a renamed material is stripped.

### Numbers, and what a round trip is exact about

Coordinates are snapped to 1/4096 on the way in, which is what the file stores.
Without it a number that has been through decimal text lands a fraction off the
one beside it and the packer, which deduplicates points by exact equality,
writes two points where the mesh had one. `Fixed.ToInt` **truncates** rather
than rounds, and anything comparing a built face against a written one has to
do the same or every corner reads as moved.

The Newell sum is taken **in double and about the polygon's own centre**. In
single precision, on a wall fifty units from the origin whose own edges are a
fraction of a unit long, cancellation eats most of the answer: a face that
should have been (1, 0, 0) came out (0.9998, 0, 0), which is a different plane,
and df_dust2 ended up with 2,879 planes where it had 2,716.

Measured, exporting df_dust2's collision and generating from it unedited:

```
kept      5468 faces
removed    100 faces  (0 u2)     <- the ones that enclose no area
added        0 faces
```

and 10,308 points down to **7,829**, because the file itself holds 2,479
duplicate points: the packer deduplicates before quantising, so two patch
vertices a millionth apart are two points that get written as the same three
integers. Snapping first removes them. 399,896 bytes to 357,312.

## Checking one before it is generated

```bash
FruityPrime -mapcheck "ROOM"
```

Builds the map in memory, writes nothing, and reports five things. It exists
because every way of getting hand-edited collision wrong is invisible: a face
turned inside out is a wall you walk through, a concave polygon rejects part of
its own interior, a floor deleted by accident is a hole you fall down, and a
material name with `damaging` in it that should not be there is a floor that
kills. None of those look like anything in Blender, and the first three do not
look like anything in the game either until somebody walks into them.

1. **Against the format's limits** -- faces, distinct points, and grid
   references, which is the one that bites first: the grid lists every face in
   every cell it reaches and indexes those listings with sixteen bits.
   df_dust2 sits at 36% of it.
2. **Shape** -- faces enclosing no area, and faces that **reject part of their
   own interior**, which is `GetEdgeDotDifference` transcribed with its own
   `-0.03125` margin rather than an approximation of it. This is what catches a
   concave polygon, which is what a 3D tool produces the moment somebody drags
   a vertex past its neighbours.
3. **What the surfaces are** -- the terrain census, and the count of faces that
   hurt on its own line whether or not it is zero, because "lava" where "rock"
   was meant is not an error and never will be.
4. **What the .obj changed**, against the collision the geometry would have
   made: kept, removed with their area, added. The most useful line once a map
   has a mesh, because the edit is not visible anywhere else -- the recipe says
   a filename, the .obj is thousands of numbers, and a face deleted on purpose
   and a face deleted by a stray click look exactly alike.
5. **Drawn surfaces with nothing solid behind them**, sampled half a unit apart
   over every drawn polygon. Read as places to look at rather than as faults: a
   level draws plenty that was never meant to stop anybody, and a converted
   level's collision is often a coarser version of what is drawn -- a
   `modelclip` ramp arrives as a staircase of flat plates -- so the check
   allows a unit behind the drawn surface and a quarter in front and still
   reports the trim.

## Pickups a custom map may use

`MapBuilder.MultiplayerItems`, enforced when the map is built rather than
written down somewhere. Health, UA and missile pickups in all three sizes,
Double Damage, Cloak, Deathalt, and the weapons. What is refused is the
story's permanent upgrades -- energy tank, missile expansion, UA expansion,
artifact -- which raise a hunter's *capacity* for the rest of the game instead
of topping it up until the end of the match: one of those in a deathmatch is a
player who is simply better than everyone else for as long as the session
lasts. The Quake importer maps mega health and body armour onto the largest
thing that runs out, for the same reason.

## Bots

A custom room generates its own node data -- `MapNodePacker.cs`, written into
`levels/nodeData/<room>_Node.bin` alongside the other three files. Without it
`nodePath` was null and bots wandered.

It samples the collision on a two-unit grid for standing places with headroom,
connects them where the step is small and no near-vertical surface crosses the
line, then thins that graph down to the waypoints written -- over the graph,
not over the room, so two points either side of a wall are never merged.
**The fine grid has to be finer than the map's doorways**: at six units the
first attempt stepped straight over every one of df_dust2's and produced a
room of disconnected islands.

Two lists per node, because the AI reads two. What it navigates with is the
second one: a routing table giving, for every destination in the room, which
neighbour to set off towards, run-length encoded as pairs of (how many
destinations this covers, which node). Traps, all found by comparing the
output against a cartridge file:

- **A run is a pair of 16-bit values**, so four bytes. Advancing the offsets by
  two put every list after the first halfway into the one before it.
- **`indexCount` is 0** in the game's own files, and the two bytes it would
  occupy stay: the header says the data begins two after the index does. With
  a count of 1 the AI can select a second set that is not there.
- **`MaxDistance` is not the spacing.** The game's rooms put it between 0.6 and
  2 units whatever their nodes are spaced at; a bot that thinks it has arrived
  from five units away stops short of every corner.
- **The other list is empty** on two thirds of the nodes in the game's own
  rooms, so it is left empty here too.
- A missing node file is tolerated, not fatal: `SceneSetup.LoadNodeData` says
  so and carries on, because a room whose bots wander is better than a match
  that will not start.

`-maptest` reports `moved N/M (furthest X units)`. That metric exists because
"the bots do not move" was a report nothing in the harness could confirm or
deny -- a bot standing still counts as spawned, and never firing or dying
reads the same as one that is losing.

## Commands

```bash
FruityPrime -q3convert level.pk3 -map NAME -name ROOM   # a .pk3 to a map file
FruityPrime -q3maps  path/to/pak.pk3      # what levels are in there
FruityPrime -q3shaders path/to/pak.pk3 -map wrackdm17   # what it draws with
FruityPrime -mapgen                       # generate every map in maps/
FruityPrime -mapgen "LONGEST YARD"        # just one
FruityPrime -mapmaterials "MP3 PROVING GROUND"   # what textures can be borrowed
FruityPrime -mapitems "DUST2"             # what pickups the level already holds
FruityPrime -mapcheck "DUST2"             # what its collision will be, before generating
python tools/collision-to-obj.py dust2 --files FILES --group terrain   # collision out, to edit
FruityPrime -maptest "LONGEST YARD" -players 8 -seconds 22
FruityPrime -thumbnail "LONGEST YARD"     # the launcher's picture
```

Nothing has to be generated by hand: `ModEntry.TryHandle` builds any map whose
binaries are missing or older than its source, and `MatchStart.Launch` does the
same immediately before a launcher session loads a room.

**Both, because TryHandle is not on every path.** The front screen is
dispatched from `TryHandleHeadless`, which returns as soon as it has run one,
so a launcher session never reaches `TryHandle` -- and on Windows,
double-clicking the binary *is* the launcher. Nothing built the binaries, the
room was registered from its JSON regardless, and it sat in the map picker and
took the process down the moment anybody picked it. Every command
(`-mapgen`, `-maptest`, `-rooms`, `-thumbnail`) goes through `TryHandle` and
was therefore fine, which is exactly why this survived a day of testing: the
one path a player uses is the one no check went down. A map that cannot be built prints one line and leaves the
room registered but unloadable -- see below.

A map may live in a folder of its own -- `maps/dust2/dust2.json` beside its
`df_dust2.pk3` and `df_dust2.tex` -- which is the tidy arrangement once a map
brings a level and a texture pack with it. Map files are found recursively, and
`import.source` and `import.textures` are looked for **beside the map file**
first, then in `maps/`, then beside the game files.

`maps/**/*.json` is copied next to the binary by the build and read at startup;
the generated `.bin` files land in the player's own extracted files under
`_archives/<name>/`. **The JSON is the thing that belongs in git.** The
binaries are derived from the player's cartridge dump (the textures) and, for
an imported map, from their copy of another game -- the asset guard rejects
both, correctly.

## On Android

Three things that are all the same problem: none of the desktop's assumptions
about files hold in an APK.

- **The map files ship as `AndroidAsset`** and are unpacked on first launch by
  `AndroidMaps.cs`. Assets in a package are not files -- only `AssetManager`
  opens them, so `Directory.EnumerateFiles` finds nothing however the path is
  spelled. They land in the external directory the extracted game files
  already use, which is also where a player can drop one in over USB; only the
  names that came out of the package are overwritten on an update.
- **`CustomRooms.MapDirectory` is settable** for that reason: the package's own
  directory is read only.
- **The level a converted map is made from is never in the package.** It is not
  ours to ship, and the generated room binaries are worse -- the borrowed
  textures are inlined in the model file, so a `.bin` carries cartridge data.
  So `import.source` takes a bare name and is looked for beside the map files
  and then beside the game files: the player puts their own `.pk3` where they
  already put their own files, and the device converts it there. Proven: an
  emulator built `wrackdm17` from a pushed `pak1-maps.pk3` into the same
  202,764 and 621,332 bytes the desktop produces.
- **`ChooseRoot` probes before it commits.** `GetExternalFilesDir` can return a
  path that cannot be written to -- it did, on a clean install, on every launch
  after the first -- and the old code trusted a non-null answer. One launch
  wrote internally, the next looked externally, and the game appeared to lose
  the files the player had copied. Now whichever root already holds a
  `paths.txt` wins, else the first that a write probe succeeds in.
- **`AndroidConsole` sends `Console` to logcat.** Mono does that for a debug
  build and not for release, so the one class of bug that only appears in
  release also had no diagnostics. Every wrong turn above was invisible until
  this existed.
- **Nothing calls `ModEntry.TryHandle`** on this head -- the entry point is an
  activity, not `Main` -- so the missing-binary build runs from
  `AndroidMaps.EnsureBuilt`, off the UI thread at startup and again before a
  match and before previews.
- **The map types are trimmer roots.** A release APK is trimmed, the map files
  are read with reflection-based JSON, and the trimmer cannot see that. What
  it produces is not a crash but a map that loads into an object of defaults:
  rooms with no name, no geometry and no spawns, in release only.

Verified on an emulator with a release APK, back when three maps shipped: all
three were unpacked, all three were built on the device from the player's own
extracted files -- byte for byte what the desktop generates, `wrackdm17`
included once its `.pk3` was in the maps folder -- and all three were listed in
the map picker. A map whose source level is absent is left out rather than
listed and crashing. With nothing shipping, the APK now logs
`[android] 0 bundled map files` and the picker shows the 27 cartridge rooms.

## Not done yet

- **A map is left out when its source level is missing**, which is the case
  that happens (the map file travels with the repository, the Quake level it
  was made from does not). A map that fails to build for any *other* reason
  still appears in the room list and crashes when picked: registration happens
  in `Metadata`'s static initialiser, before the game files are known, so that
  is as much as it can check.
- **Nothing hashes the map** in the network handshake: two clients on the same
  build with different `maps/` will disagree silently.

## Handing a custom map to a client that does not have it

**Not implemented. The packet numbers are spent, and that is the whole of it.**

`PacketType` 32-35 are reserved for it -- `MapOffer` (the server names the map,
its hash and its size), `MapWant` (the client asks for the bytes from offset N),
`MapChunk` (one piece of the `.fpmap`), `MapDone` (the client has it and it
hashes right). Nothing in this build sends or answers any of them.

They were spent early on purpose. `NetConfig.ProtocolVersion` 7 already refuses
every client built before it, for reasons that have nothing to do with map
transfer; taking the numbers now means that refusal is the same refusal that
will cover the transfer, instead of a second protocol bump -- and a second bump
is a second day of every server in the world having to be redeployed before
anybody can play. A client built today cannot meet a server that speaks the
transfer and misread a chunk as something else, because it cannot connect to it
at all.

What is settled about the shape, so that the numbers mean something:

- **the bundle is the unit.** `-mapbundle` already cooks a map into one
  `.fpmap` -- recipe, level and baked textures, level trimmed to the lumps the
  importer reads, 376 KB for de_dust2 against 2.8 MB for the folder. That file
  is what would travel, and its hash is what identifies it.
- **the offer comes before the load, not during it.** A client that is told
  about a custom map at the moment the rotation reaches it has a room to load
  and no bytes to load it from; the offer belongs with the match state, early
  enough that the transfer finishes before the map is needed.
- **the hash is the name.** Two people with a map called `de_dust2` do not
  necessarily have the same map, and a rotation that names one by string alone
  cannot tell.
- **`net.livetek.fr` is where they come from.** The directory already knows
  which servers are up and is the one machine in the world every launcher
  talks to; hosting the bundles there means a server does not have to serve
  them out of its own bandwidth mid-match.
