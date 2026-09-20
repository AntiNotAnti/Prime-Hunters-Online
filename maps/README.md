# Maps

No level lives here, only recipes: the game ships the 27 multiplayer rooms of
the cartridge and nothing else. What lives here is the *hook* for custom ones —
drop a `.json` in, or a folder with a `.json` in it, and the game builds it and
lists it. A map whose source level is not on this machine is left out rather
than listed and broken.

Each `.json` describes one custom room. The binaries a room is actually made
of are generated on the machine that runs the game, from that machine's own
extracted files, because a room's textures are borrowed from a shipped room
and end up inside its model file. That is why no `.bin` is ever committed, and
why the asset guard refuses one.

- `q3dm17.json.example` — rename to `q3dm17.json` and drop your own `pak0.pk3`
  beside it to convert the original Quake III map. Nothing here downloads it
  for you: that is id Software's commercial data.
- `dust2/dust2.json` — de_dust2, by way of the DeFRaG level `df_dust2`, which
  ships beside it. A map keeps its level in a folder of its own like this; it
  is looked for beside the map file first.
- `arena/arena.json` — **TEST ARENA**, the network harness's own room. Forty
  units square, eight spawns on a ring looking inward, line of sight from
  anywhere to anywhere. It borrows its textures from a shipped room like every
  other map here, so it needs no level of its own.
- `pads/pads.json` — **TEST PADS**, the same room with four jump pads throwing
  hunters across the middle and a ceiling raised for the arc. Built for the one
  case hit registration is hardest in: a target crossing fast and vertically,
  against a headshot band 0.30 units tall. TEST ARENA is its control — they
  differ by the pads and the ceiling and by nothing else.
  `.claude/testing/HITRIG.md`.

A map's level travels with it, so a downloaded release has its custom maps
ready and the first launch builds them. The `.tex` does not: it is baked from
the level when it is missing, exactly like the room binaries.

## Bundles

What ships, and what you hand somebody, is a **`.fpmap` bundle**: the recipe,
the level and the baked textures in one file, with the level trimmed to the
lumps the importer actually reads. de_dust2 comes out at 376 KB against the
2.8 MB its folder weighs, and it is one file rather than three — which is what
makes a map something you can send, and what a downloader will want when a
server starts offering its maps to the players joining it.

```
FruityPrime -mapbundle           # cook every map beside the executable
FruityPrime -mapbundle DUST2 -mapdir maps
```

The folder is what a map is *worked on* as; the bundle is what leaves. Bundles
are not committed — the workflow cooks them before it publishes — and a folder
and a bundle of the same name are the same map, so the bundle wins and the room
is registered once. It is also the only shape that reaches Android: an APK's
asset list does not recurse into folders.

A bundle does not settle whether a level may be handed out. Cooking somebody's
level into a smaller container leaves it their level.

What still may not be committed is somebody else's commercial data — the
cartridge dump, and id Software's `pak0.pk3` and friends, which the asset
guard refuses by name. Everything else is a judgement for whoever commits it:
publish a level you have the right to publish.

The format, the Quake 3 importer and the traps are in
`../.claude/mapgen/MAP-PIPELINE.md`. `FruityPrime -mapgen` builds every map in
this folder; `FruityPrime -mapmaterials "MP3 PROVING GROUND"` prints the
textures a shipped room can lend.
