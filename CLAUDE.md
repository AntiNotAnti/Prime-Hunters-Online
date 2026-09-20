# Fruity Prime — tools, design, and the mechanics catalogue

**The project is Fruity Prime. The code is still `namespace MphRead`, and stays
that way.** Upstream is NoneGiven/MphRead and every pull from it is a
fast-forward only while the 221 files that declare that namespace and the 271
that import it are untouched; renaming it would put a conflict in all of them
for a string only a developer ever reads. The rename is the product, the
binaries, the window title and the release artifacts. `Mods/Branding.cs` is
where the name lives — nothing else should spell it out.

| Build | Binary |
|---|---|
| Windows game | `FruityPrime.exe` |
| Windows server | `FruityPrimeServer.exe` |
| Linux game, Linux and ARM64 server | `FruityPrime` |

This file exists so a fresh session can pick the work up without rediscovering
the environment or the failure modes. Everything below has been used; nothing
is aspirational. It stays short on purpose: depth for a given area lives in
`.claude/` (indexed in `.claude/CLAUDE-INDEX.md`) and is loaded only when that
area is the one being touched.

## Where things are

| Path | What |
|---|---|
| `~/GIT/Fruity-Prime` | the source. Upstream is NoneGiven/MphRead; everything added lives under `src/MphRead/Mods/` so pulling upstream stays a fast-forward. (It was `~/MphRead-dev` before the rename, and that path is gone) |
| `src/MphRead.Android/` | the Android head: the same sources, an APK, a front screen and a match, over GL ES and touch controls |
| `src/MphRead/Mods/Network/` | the whole multiplayer feature |
| `src/MphRead/Mods/Launcher/` | the launcher: `Gui/` is every window (Avalonia, all platforms), `Portable/` is the logic and the text screen |
| `~/mph-test/` | the extracted game files and `paths.txt`. **`paths.txt` has to sit next to the DLL** you are running, so copy it into `src/MphRead/bin/Release/net10.0/` and run `dotnet FruityPrime.dll` from there |
| `~/mph-net-test/` | the test rig -- `bin/` (with its own `paths.txt`, game files and thumbnail cache), `run-check.sh`, `compare-reports.py`, `hard/`, and every `run-*.sh` named in the table below. It **does** exist on this box, whatever an older copy of this file said. A two-client run against a real server needs nothing more than two `-netcheck` processes and the game files |
| `C:\Users\livetek\Desktop\MPH\MphRead-develop\` | the Windows deliverable |
| `net.livetek.fr:27888` | the dedicated server on the user's Pi (systemd unit `mphread-server`) |

## Environment recipe (WSL)

Three things will waste an hour each if you do not know them:

```bash
export PATH="$HOME/.dotnet:$PATH"          # dotnet is not on PATH
export DOTNET_ROOT="$HOME/.dotnet"         # else the apphost cannot find a runtime
export MESA_GL_VERSION_OVERRIDE=4.5COMPAT  # else Mesa hands out a Core profile
export ALSOFT_DRIVERS=null PULSE_SERVER=   # else ALSA retries stall frames
```

- **`DOTNET_ROOT` is what the built `./FruityPrime` needs, and `PATH` is not.**
  The apphost looks for `libhostfxr.so` under `DOTNET_ROOT` or a system install,
  neither of which exists here, so running the binary directly dies with *"You
  must install .NET to run this application"* while `dotnet FruityPrime.dll`
  from the same directory works. Either export it or run through `dotnet`.

- If `~/.dotnet` is empty, the SDK is not installed at all:
  `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 9.0`
  puts it there.
- The launcher used to need `libICE` and `libSM`, and no longer does: it opens
  no window of its own, so it binds no X11 client libraries. `fontconfig` is
  still needed (`libfontconfig1`), and a screen that will not start now means
  Skia or the fonts, not the windowing system.
- `dotnet` aborting on startup with *"Couldn't find a valid ICU package"* is a
  missing `libicu`, not a broken SDK. `sudo apt install libicu-dev` is the fix;
  `export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` gets a build out of a box
  with no root, at the cost of culture-aware string handling — fine for
  building and for the server checks, not something to leave set while
  testing anything that formats text for a player.
- `dotnet publish -o DIR` does **not** copy `libSkiaSharp.so` or
  `libHarfBuzzSharp.so` next to the binary, and without them every Avalonia
  screen dies with *"The type initializer for 'SkiaSharp.SKImageInfo' threw an
  exception"* -- which `-uishot` reports as "no Avalonia backend on this
  machine", so it reads as a missing display rather than a missing file. Copy
  them out of `~/.nuget/packages/{skiasharp,harfbuzzsharp}.nativeassets.linux/*/runtimes/linux-x64/native/`.
- **`MESA_GL_VERSION_OVERRIDE=4.5COMPAT` is not optional.** Without it Mesa gives
  a Core profile despite the Compatability request, every `GL.Begin` fails
  silently with `InvalidOperation`, and every frame renders black. Nothing in
  any log says so.
- A window created with `StartVisible = false` has no usable back buffer under
  Mesa. Screenshots must read the scene's offscreen target
  (`Scene.ReadSceneTarget`, used by `Mods/ScreenCapture.cs`), which carries the
  world but not the HUD.
- `paths.txt` must sit **next to the DLL**, not in the working directory:
  `ConsoleSetup.Run` selects the installation directory on Windows/Linux and the user-data directory on macOS (see `.claude/build-deploy/MACOS.md`).
- Audio failures used to kill the process from a static constructor. That is now
  non-fatal, but under WSL the audio device is flaky enough that the test rig
  disables it outright.
- Do not `cp` over a DLL while a process is using it: .NET memory-maps it and
  the process dies with an opaque crash. Stop the server first, or write to a
  new name and `mv`.

## Commands

| Command | Use |
|---|---|
| `MphRead -server ... -noshadowfreeze` | run the room with the Judicator's ice wave as a cone rather than as a column of infinite height. A rule, broadcast to every client in the match state, because the machine resolving a shot decides who it hit |
| `MphRead -server -port N -players 8` | dedicated **authoritative** server: it runs the match itself, so it needs the game files and `paths.txt` beside the binary, and it refuses to start without them. `-simulate`/`-authority` are accepted and do nothing. `-servername "NAME"` is what a browser shows; it announces itself to `net.livetek.fr` unless `-nomaster` is passed, and `-master HOST -masterport N` points it elsewhere. `-affinityweapons` is a **match rule broadcast to every client**, not a local preference: the affinity weapons are a different row of the damage table, so a client playing by its own settings ran a victim's health down at a different rate from the machine keeping score. The **damage level is pinned to medium (x1) everywhere** and has no flag -- it multiplied every weapon's damage and was the one rule each machine read out of its own file |
| `MphRead -simcheck "ROOM" [-players N] [-seconds N]` | what a room costs a server: peak memory, milliseconds a simulation step, and whether every slot spawned. Runs the headless engine with nobody connected. The measurement that decides whether a given box can be the authority for a given map |
| `MphReadServer.exe -server ...` | the same server on Windows, as its own console binary. `MphRead.exe` can also do it, but it is a GUI binary: a shell will not wait for it and its exit code never reaches `%ERRORLEVEL%`. Run with no arguments it prints what it is for |
| `MphRead -masterserver [-port N] [-public HOST] [-hostports A-B]` | the server directory the launcher's browser asks, and the machine that runs matches for players who cannot open a port. Same binary, no game files, keeps nothing on disk. `-public` is the address to publish for servers registering from this same machine, whose heartbeats arrive over the loopback |
| `MphRead -hostgame "ROOM" [-mode M] [-maprotation "A,B,C"] [-master HOST]` | ask the directory to run a match and join it. No port forwarding anywhere; the only way to host from a machine with no launcher. `-maprotation` is the rest of the cycle, comma separated -- the map named by `-hostgame` is always first, so the two cannot disagree about what starts |
| `MphRead -hostlocal "A,B,C" [-mode M] [-servername N] [-seconds N]` | the launcher's create-server screen, **Dedicated** half, with no launcher: start a server on this machine, on the first free port from 27888, and report where it landed. The one path in that feature a rendered screen cannot check -- it spawns a process, writes a rotation, copies `paths.txt` and waits for a socket, and each of those fails differently on a headless box |
| `MphRead -installserver` | fetch and unpack the dedicated-server package for this platform into `server/`, which is what the create-server screen's **install** mark does. "The button did nothing" is otherwise unanswerable from a machine with no display: the two halves that can fail -- finding the asset and unpacking it -- both land on one sentence on screen. Prints the tag it installed, which is the **latest release** and not necessarily this build -- a protocol mismatch there is a server this client cannot join |
| `MphRead -hosts [-master HOST] [-masterport N]` | the create-server screen's **Host on** page, printed: every box the directory names, asked on the directory port as well, with the reason against each one that cannot run a match. The question that page exists to answer, and the one thing a screenshot of it cannot be checked against |
| `MphRead -servers [-master HOST] [-masterport N]` | print the server list the launcher's browser would show, with each server's map, players and round trip -- and whether that directory will **start** games, which is what the create-server screen's "Host on" row asks it and the only way to tell a directory that is down from one that simply does not host |
| `MphRead -connect HOST -port N -name X -hunter H` | join from the command line, no launcher |
| `MphRead -netcheck HOST -port N -name X -hunter H -seconds N [-shots DIR] [-size WxH]` | a real client driven by a script, which reports what it saw. Exit code 0 = pass. `-spectate [SEC]` makes it stop playing and watch, `-rejoin SEC` puts it back in -- the one player state the tour cannot reach on its own. `-mapvote N` votes on the results screen's map list -- agreeing with whatever is in front, proposing row N when nothing is -- and is **off** unless asked, since a scripted client that votes changes what a real server plays next and the hard-case batch runs against the public one. `-hudshots` opens a real window and photographs *it*, which is the only capture that carries the HUD: a results screen is HUD and nothing else |
| `MphRead -netlag MS[:JITTER]` / `-netloss PCT` | play, or run any check, over a line this client makes up: `-netlag 200` adds 200 ms to the round trip (half each way), `-netlag 200:40` gives it jitter, `-netloss 5` eats one datagram in twenty. Works against the real server, on any platform, with no proxy and no `sudo` -- and unlike `hard/run-latency.sh`'s netem it can be given to **one** client while the others stay fast, which is the case a player with a bad line actually is. Every report says so when it is on |
| `MphRead -nounlagged` | resolve shots against the present, the way every build before lag compensation did. The control for measuring it; on by default. `.claude/multiplayer/NETWORK-UNLAGGED.md` |
| `MphRead -nohitprediction` / `-nohitmarker` / `-nodeathprediction` | wait for the authority before a hit lands, the way every build before instant hit registration did; drop the mark over the crosshair that says one has; and stop a prediction killing **somebody else**, which since protocol 7 it does by default -- the claim below is what made that safe again. All three are on by default, and a **self**-kill is predicted whatever any of them say. `.claude/multiplayer/NETWORK-PREDICTION.md` |
| `MphRead -noclaims` | stop a client telling the authority which of its own shots landed. On by default: a hit the authority's own rewind cannot find -- because the rewind hit its ceiling, because the trigger pull was recovered from a press history, or because **the shooter was killed during the round trip and the authority never ran the shot at all** -- is declared, checked against the authority's own history, and either applied or refused with a reason. That last case is the one a player calls unfair rather than laggy, and the rule it is answered by is: a shot counts unless its shooter had already been put down by a hit aimed at a strictly earlier world, and two shots aimed at the same world both count. Every weapon, not just the Imperialist. `.claude/multiplayer/NETWORK-HITCLAIMS.md` |
| `MphRead -nointerp` / `-relayedpuppets` | draw remote players by snapping them to whichever snapshot arrived last, the way every build before protocol 7 did, instead of reading them off a playout clock held a few frames behind. Interpolation is on by default and is why opponents on a bad line move instead of stuttering; it costs a few frames of extra rewind and gives nothing up in hit registration, because the read point travels in the intent as a sub-frame ack and the authority rewinds to exactly it. `-relayedpuppets` also hands puppet positions back to the owner's relayed intent, which is the full protocol-6 arm. `.claude/multiplayer/NETWORK-SMOOTHING.md` |
| `MphRead -maxrewind N` | the furthest back a shot may be resolved, in frames. **45 (750 ms)** by default since protocol 7, against 24 (400 ms) before it: at a 320 ms round trip with jitter the old ceiling was clamping **89% of shots**, with the requested-depth distribution's mode two frames past it. `.claude/multiplayer/NETWORK-UNLAGGED.md` |
| `MphRead -debuglog` | write the file the launcher's corner switch writes, for one run. `.claude/DEBUG-LOGS.md` |
| `~/mph-net-test/probe-chat.py [HOST] [PORT]` | what the server does with chat, asked the way no real client can: a spoofed sender, and a flood. `.claude/multiplayer/NETWORK-CHAT.md` |
| `~/mph-net-test/run-remote.sh HOST PORT SECONDS hunter...` | the same check against a server that is not on this machine -- which is the one that matters, since eight clients on one box measure the box |
| `~/mph-net-test/run-demo.sh SEC [authority\|client]` | record a demo from a scripted client and print what landed in the file. The authority is the case that matters: it is whichever client joined first, so it is normally whoever set the match up, and the server sends it no snapshots at all |
| `~/mph-net-test/run-rejoin.sh SEC LEAVE REJOIN [host] [port]` | the rejoin scenario, with a control: A hosts and leaves, the authority moves, then one client takes the vacated slot and another takes a fresh one. Prints what each took. `.claude/multiplayer/NETWORK-DIAGNOSTICS.md` |
| `~/mph-net-test/run-mapvote.sh SEC hunter...` | `run-rotate.sh` with the clients voting: four 30-second matches, and it reports votes cast against votes the server carried. What proves the results screen's map vote end to end |
| `~/mph-net-test/hard/run-all.sh` / `run-all2.sh` | the hard-case batch against the Pi: a ninth player, a line that goes away, 100-300 ms, packet loss, everybody spectating, everybody recording, a match boundary, an authority leaving, and a ramp to twenty-odd matches at once. `.claude/testing/TEST-HARD-CASES.md` |
| `~/mph-net-test/run-lag.sh MS SECONDS hunter...` | the same check against a loopback server behind `udp-lag.py`, which holds every datagram for `MS` before passing it on. A latency bug reproduced at a number you chose, rather than at whatever the internet is doing -- and the Pi answers in 7-17 ms, so it is the *worse* instrument for one |
| `MphRead -maptest "ROOM" -players 8 -seconds 22` | load one room with a full house, drive every player, and report what the map holds and whether it survived |
| `MphRead -maptest "ROOM" -players 8 -bots` | the same, but AI bots instead of the scripted tour -- a different code path, the only one that finds what only `PlayerAi` touches |
| `MphRead -maptest "ROOM" -hunter H -hudshots` | put that hunter in slot 0, whose eyes and whose HUD every capture is taken through. Each of the eight lays its readouts out differently, so a HUD picture with no hunter named is a picture of Samus's and of nobody else's |
| `MphRead -maptest "ROOM" -renderprobe` | stand on every spawn point in the room in turn, read the frame, walk forward five seconds, read the worst. Catches a room that draws nothing -- the failure no other check can see, because everything else about it passes. `-shots DIR` writes the PNGs, `-allnodes` draws without room-part culling (which separates "the geometry is missing" from "the cull lost it"), `-hudshots` uses a real visible window and reads *its* buffer, which is the only capture that includes the HUD, and `-size WxH` sets that window's shape -- the HUD is laid out in a 4:3 space and stretched, so how it looks is partly a question about the window. Under WSL a HUD capture needs the X11 backend: `WAYLAND_DISPLAY=` `DISPLAY=:0`, or every window read comes back black |
| `MphRead -maptest "TEST ARENA" -players 8` | the harness's own room (`maps/arena/`): forty units square, eight spawns on a ring looking inward, nothing far from anything. Where damage, hit registration and the affliction states are actually measurable -- a real map's corridors mean most of the tour's shots land on a wall |
| `MphRead -maptest "TEST PADS" -players 8` | the same room with four jump pads throwing hunters across the middle, in a box tall enough for the arc (`maps/pads/`). The one case hit registration is hardest in: a target crossing at 0.3 units a frame or more, against a headshot band 0.3 units tall. TEST ARENA is the control -- the two differ by the pads and the ceiling height and by nothing else |
| `MphRead -rooms` | list every multiplayer room, one per line, for a shell loop. **27** is the whole cartridge and the right answer with no custom map source present; anything more is a custom map |
| `MphRead -q3convert FILE.pk3 -map LEVEL -name ROOM [-noclip] [-noitems]` | a Quake 3 .pk3 to a custom map in one command: textures baked from the level's own art, scale and extents picked from its geometry, spawns and pickups from its entities. It **places** no weapons or powerups -- where those go decides how the map plays -- but it writes down the ones the level's own author placed and turns `keepItems` off, so what the room holds is in the recipe rather than read invisibly out of the .bsp on every generation. `-noitems` writes none and still turns it off. `.claude/mapgen/MAP-PIPELINE.md` |
| `MphRead -mapitems "ROOM"` / `-mapitems FILE.pk3 -map LEVEL` | what pickups a level already holds, printed as the `items` block a recipe would carry, with the Quake classname each came from and which are handed out by the level's scripts rather than walked over. Writes nothing: a recipe carries comments and is nobody's to rewrite |
| `MphRead -mapcheck "ROOM"` | what a custom map's collision will be, built in memory and written nowhere: against the format's limits, faces that reject part of their own interior (the run-time edge test, run early), what the surfaces are and how many of them hurt, what a hand-edited `.obj` changed against the geometry's own collision, and every drawn surface with nothing solid behind it. The check for collision a person edits by hand -- every way of getting that wrong is invisible in a 3D tool and in the game until somebody walks into it |
| a recipe's `"collision": { "source": "x.obj" }` | what stops a player, read from a Wavefront OBJ instead of derived from the level, **replacing** it. `tools/collision-to-obj.py --group terrain` writes the file to start from; a material named `<terrain>[_attribute...]` (`sand`, `lava_damaging`, `metal_nobeams`) carries everything the format holds per face, and winding is what says which side blocks. What it is for: a converted level's collision includes every face nobody can reach -- a quarter of df_dust2's collision area -- and no importer can tell which of those are wanted. `.claude/mapgen/MAP-PIPELINE.md` |
| `MphRead -mapgen ["NAME"]` | generate the room binaries for the custom maps in `maps/` (recursively: a map may sit in a folder of its own with its level and textures beside it, or be a single `.fpmap` bundle), from the player's own textures. `-mapmaterials "ROOM"` prints what textures a room can lend. A map is a JSON file; the `.bin` it produces is never committed. `.claude/mapgen/MAP-PIPELINE.md` |
| `MphRead -mapbundle ["NAME"] [-mapdir DIR]` | cook a map into the one file it ships and is handed out as: recipe, level and baked textures in a `.fpmap`, with the level trimmed to the lumps the importer reads (376 KB for de_dust2, against 2.8 MB for the folder). What the workflow runs before it publishes -- the bundle is not committed, and the `.pk3` it is cooked from never reaches a package. `-mapdir` is resolved against the directory the command was typed in |
| `MphRead -gamepad [-seconds N]` | what a connected pad is doing, with no match in the way: its name, its axes, and which game action each button reaches. The only thing that tells "not connected" from "connected but GLFW has no mapping for it" from "the dead zone is eating it" apart. `.claude/GAMEPAD.md` |
| `MphRead -cel on\|off [-celbands N] [-celedge N]` / `-fog on\|off` / `-prohud on\|off` | render options for every path that never opens a launcher, which is every screenshot command. `.claude/render/CEL-SHADING.md` |
| `MphRead -fov N` | how wide the view is, 60 to 120 degrees, default **78** -- the DS's own (`NormalFov` 39, doubled). A multiplier on whatever the camera asked for, so a weapon's zoom and every scripted shot keep the proportions they had on the cartridge. For the paths that never open a launcher, like `-cel` and `-fog`: a session that does opens one takes the number from the settings' **Field of view** row instead |
| `MphRead -fpscap N\|display` | how fast the picture is drawn. The simulation is pinned at 60 Hz on every setting, so this does not touch what the game does. `.claude/render/FRAME-PACING.md` |
| `MphRead -crosshair STYLE` / `-crosshairsize Small\|Medium\|Big` | which crosshair the pro HUD draws, for the screenshot commands that open no launcher. Styles are Cross, Dot, CrossDot, Circle, Brackets |
| `MphRead -weaponstyle static\|dynamic` | where the gun and the crosshair sit -- Quake's welded pair or the DS game's drifting one, which is the settings screen's Weapon row. For the same paths, and a sharper reason: the two answers differ mainly in what the middle of the picture is doing, so a screenshot is how the difference is checked at all. `quake` and `metroid` are accepted as the same two answers |
| `MphRead -tapcheck` | the rule that tells a tap from the start of a scroll, against gestures written down rather than performed: does a finger that goes down on a settings row and then drags the page still answer that row. It must not -- acting on the *press* is what made scrolling the settings on a phone toggle, cycle and re-slide every row the drag passed over. Needs no display, no toolkit and no touchscreen (`Mods/Launcher/Gui/Tap.cs`) |
| `MphRead -frametimingcheck` | the fixed-step accumulator on its own, against frame times chosen rather than measured: does the game still run at 60.000 Hz when the screen runs at 144, at 165, at a jitter, or at 40. Needs no game files and no display |
| `MphRead -maptest "ROOM" -drawrate N` | draw each simulation step N times, which is what a 144 Hz screen does to a 60 Hz game. Asserts that drawing did not advance the world. How the decoupled loop is checked from a box with no monitor |
| `MphRead -shellshot DIR` | the same screens photographed **in the game window**, walking the whole loop: front screen, a key, a click, a match loaded into that window, the pause menu over it, and the front screen again after leaving. What `-uishot` cannot answer -- it renders layouts, not the composite. Needs a display (Xvfb is one) |
| `MphRead -uidesign DIR` | everything **behind** the front screen -- Play with maps, Play with servers, settings, the pause menu -- laid out **six** different ways and photographed, for choosing between them by looking rather than by describing. The front screen is deliberately not in it. The six differ in information architecture rather than in where a menu is pinned, since moving anchors around produces six pictures of one design; the photograph, the real content at its real density and `GuiTheme`'s palette are held constant. Nothing it draws ships (`Mods/Launcher/Gui/UiDesigns.cs`) |
| `MphRead -uishot DIR` | pictures of the launcher's own screens -- home, settings, the map picker, the pause menu -- rendered without anyone looking at a display. The one part of the program that could not otherwise be checked from a headless box |
| `MphRead -demoinfo FILE [-replay]` | what a recorded match contains -- records, frames, a packet-type histogram, and how well it compressed. `-replay` then runs the file through the real player with no room or window and reports how the packets landed per frame, which is the measurement "the replay stutters" is about. Needs no game files. `.claude/multiplayer/NETWORK-DEMOS.md` |
| `MphRead -netcheck ... -recorddemo` | the harness client, recording a demo as it plays |
| `MphRead -mechanics` | print the catalogue in `MECHANICS.md`, generated from the game's own tables |
| `MphRead` (no arguments, Windows or macOS) | the front screen. The Windows build is a GUI binary, so double-clicking it opens the launcher with no terminal behind it |
| `MphRead -menu` | the console menu, for people who typed something |
| `MphRead -launcher [-console]` | the front screen explicitly; `-console` also gives it a terminal. The same Avalonia screen on Windows, Linux and macOS, or the text one when there is no display. A bare `MphRead` on Linux still opens upstream's `-menu` prompts, unchanged |
| `FruityPrime -launcher -text` | the text front screen on a machine that has a display. What an SSH session gets anyway |
| `FruityPrime -update` | check GitHub for a newer release and open its page. Installs nothing; the one command that answers "am I on the latest build" |
| `FruityPrime -noupdate` | do none of that, on any command that would have |
| `FruityPrime -server ... -noautoupdate` | keep a dedicated server on the build it was started with. It updates itself otherwise -- see Updating |
| `FruityPrime -credits` | who this is built on and who forked it, from `Mods/Credits.cs` -- which also holds the ko-fi address the settings' Credits page offers |
| `MphRead -uinativeres` | rasterise the launcher's screens at the window's own resolution however big it is, rather than capping them at 1080p and magnifying. Sharper type above 1080p, and a much slower redraw -- which only shows while something is moving, so it is a straight choice between crisper menus and menus that scroll |
| `MphRead -fullscreen` / `-windowed` / `-nohelmet` | display choices for the paths that never open a launcher |
| `MphRead -windowcheck` | the window's memory, both halves, without anybody watching a window: it opens the shell at a saved size nothing else would produce, then proves that closing it would keep the size it has now. The save half runs *as the window closes*, so a scripted run has no way to reach it otherwise -- and the whole feature fails silently, since a window that opens at the default looks exactly like one that was never resized. Writes nothing; the preference is put back before it returns |

## The launcher

**One window, for the whole program.** The front screen, the settings, the
pause menu and the match are all drawn into the game window: the launcher is
rendered by Avalonia's headless backend into a buffer and composited onto the
frame (`Mods/Launcher/Gui/UiSurface.cs`, `Mods/Render/UiOverlay.cs`), and a
match is a scene loaded into that same window and unloaded again
(`Mods/Launcher/Gui/Shell.cs`). Starting a match no longer closes anything and
the pause menu is no longer a second window chasing the first one's rectangle.
`.claude/launcher/LAUNCHER-WINDOW.md` has the traps -- the engine names its own
textures, and the fixed-function state belongs to whoever drew last.

**The launcher is redrawn when something changes, not when the game draws.**
It used to be re-rendered every frame -- 3.5 ms of a 120 Hz frame, Skia
re-rasterising the whole screen plus a full framebuffer upload, to produce the
pixels already on screen. That is 43% of a frame spent proving a static menu
had not changed, and it is why a 120 fps game could carry a launcher that felt
like ten. `UiSurface.Invalidate` marks it dirty, every input path calls it, and
a clean surface reuses the texture already on the GPU: **435 ms/s of CPU down
to 14 ms/s at rest**. Redraws are capped at 60 Hz even when busy, with a
backstop redraw every 50 ms for three seconds after anything happens and every
250 ms once it has been still -- because a preview loading or a server
answering arrives through a dispatcher job that announces nothing, and a
backstop turns a missed invalidation from a frozen screen into one a quarter
second late. Redraws are as fast as the window while something is
moving, and the rows lay their text out once and keep it rather
than re-shaping every string on every repaint. **The wheel is the
`ScrollViewer`'s own and nothing animates it**: a notch moves the scroller and
that is the end of it. There was a `SmoothScroll` that glided each notch over
110 ms, on the reading that a jump per notch is about ten jumps a second and
therefore indistinguishable from ten frames a second -- and it was **removed**
on the report that the jump is what a launcher is supposed to feel like. It
also cost what an animation costs here: a glide is a redraw a frame for a
tenth of a second, and a redraw is the whole window (see below). Do not put it
back without that number in front of you. `-debuglog` writes a `[ui]` line
every second saying what the screens actually cost: frames, redraws, and the
split between dispatcher jobs, rasterising and the GL upload. That line is how
"the menus feel slow" gets answered at all, since the launcher's cost is spent
inside the game's own frame and shows up as the game being slow.
`.claude/launcher/LAUNCHER-WINDOW.md` has the trap that undoes it
all in one line.

**A redraw costs the whole window, so the whole window is what had to get
cheaper.** The dirty flag answers "how often"; it says nothing about "how
much", and the answer to that was: everything, every time. Skia rasterises on
the CPU into a framebuffer the headless backend hands over *fresh* each frame,
so the moment anything moves there is nothing in the previous frame to reuse
and the entire window is drawn again -- which is free while a menu sits still
and ruinous for the one thing in the launcher that moves a lot of pixels, a
list under the wheel. Measured on an i5-10600K, one settings page being
scrolled, milliseconds a redraw and the frame rate that implies:

| window | four layers | baked | baked + capped |
|---|---|---|---|
| 1280x720 | 4.6 (217 fps) | 2.9 (344 fps) | same |
| 1920x1080 | 24.2 (41 fps) | 13.0 (77 fps) | same |
| 2560x1440 | 40.9 (24 fps) | 21.5 (46 fps) | 13.0 (77 fps) |
| 3840x2160 | 91.7 (11 fps) | 60.6 (17 fps) | 13.0 (77 fps) |

That table *is* "the menus scroll at five frames a second": a launcher that
cost more than a frame to draw, redrawn on every frame for as long as anything
moved. Three changes, and they are independent -- the third is that the wheel
no longer animates at all, so a notch is one redraw instead of seven:

- **The backdrop is baked** (`Mods/Launcher/Gui/BakedBackdrop.cs`). It was four
  full-window layers -- the photograph stretched, a gradient, a corner vignette
  and the wash -- rasterised together on every redraw. They are now rendered
  once into one bitmap, at the device resolution, re-cut only when the window
  changes size, and blitted. At 1:1 the result is **byte for byte the same
  picture**, checked rather than assumed, and it is worth about 1.9x at every
  size. The wash went into the bake with them, which is why `UiLayout.Page` no
  longer lays one over the top and `Backdrop` takes a `BackdropWash` instead.
- **The raster is capped at 1920x1080** and the result is stretched over the
  window by the GL blit, which is linear and free (`UiSurface.Raster`). Nothing
  at or below 1080p is touched at all. Above it the screens are drawn at 1080p
  and magnified -- softer type, the way a 1080p picture looks on a larger
  screen, in exchange for a menu that keeps up. The **layout box does not
  move**: the curve is asked about the window and then scaled down with
  everything else, so a capped 4K window lays its screens out in exactly the
  box it always did and only the pixels are fewer. `-uinativeres` turns it off.

The one thing to be careful of: above the cap the surface is smaller than the
window, so **the pointer has to be converted** (`_raster`, applied in
`PointerMoved` and nowhere else). Miss it and every click lands short by a
quarter of the screen at 4K. `ClickOn` undoes the conversion on the way in
rather than skipping it, so `-shellshot`'s click check still proves the real
path.

**Every screen is scaled from the window's height, by one rule, and capped by
what the window can hold** (`UiSurface.Factor`): 720 pixels tall draws them as
they were authored, and the curve is steeper than proportion -- a window twice
as tall draws them about 2.8 times as large, since a bigger window is usually a
bigger screen further off. The cap is the other half and was added after the
curve: a window wider than it is tall is the ordinary case, and the height
alone got it wrong there -- 1440p asked for 2.875, which leaves the screens 890
by 500 points to lay themselves out in when they are authored for 960 by 600.
That is what "the text goes abnormally large when the window is wider than it
is tall" is, and in the same breath it is what pushed the column of settings
past the bottom of its grid row and straight over the tick in the corner. So
the layout box never goes below 960x600: the curve rounds to the nearest
eighth, the cap rounds *down* to one, and the smaller of the two wins. About
1.1 at 1280x768, 1.75 at 1080p, 2.375 at 1440p. The display's own scaling factor is
deliberately not multiplied in (GLFW is DPI-aware and already hands this
program more pixels there), and the in-game screens have no scale of their own
-- both were tried and both produced the same complaint from the other side:
text too big in the window the program opens in and too small in fullscreen,
then a front screen that "went small again" when a match ended. Every change
writes one `[ui] screens at N×` line to the debug log.

**The window comes back the size and in the corner it was left**
(`Mods/WindowGeometry.cs`, `window_size`/`window_pos`/`window_maximized` in
`launcher.txt`). Read before the window is shown, and **kept as it happens**:
the resize, move and maximize callbacks update the preference in memory (free)
and the file is written once the shape has held still for a second and a half.
Saving only as the window closes was the first design and it is not enough --
it loses the size to a crash, to a kill, to a machine that sleeps and does not
come back, and to any exit path that never runs the close handler, which is
exactly how it was reported as "ne s'enregistre pas". Closing still writes, as
a backstop for the window that was never resized. Five things about it are
deliberate:

- **Only the shell window.** Every other `RenderWindow` this process opens is
  a measuring instrument (`-maptest`, `-renderprobe`, the thumbnail runs, the
  network harness) that was handed a size on purpose, so none of them reads
  the preference or writes it. `-shellshot` uses the shell window and *also*
  stands down (`WindowGeometry.Enabled`), because its script maximizes the
  window half way through and photographing the launcher must not be how
  somebody's window size changes.
- **The position travels with the size**, because half of it is no feature: a
  window that comes back the right shape in the middle of the screen has
  still been moved.
- **A saved rectangle is a claim about hardware that may be gone.** It is
  checked against the displays that exist *now*; a corner on none of them is
  given up and only the size is kept, since a window restored onto an
  unplugged monitor cannot be dragged back. The size is clamped to the
  display it lands on rather than refused, so a window saved on a bigger
  screen comes back as large as this one allows.
- **The per-frame cost is a bool.** `WindowGeometry.Flush` is called once a
  frame from `Reveal` and returns immediately unless something moved; `Store`
  also refuses a shape identical to the one already held, which is most calls,
  since the move callback fires for a resize as well.
- **Maximized is a state, not a size.** Restoring a maximized window by its
  rectangle fills the screen without *being* maximized -- the caption button
  offers to restore a window that is not, and it will not follow a change of
  resolution -- so the flag is kept separately and the rectangle underneath it
  is still saved, and un-maximizing lands where it used to. Fullscreen is
  never what gets saved: `WindowMode.WindowedSize` is the geometry fullscreen
  was entered *from*, or quitting from fullscreen would give the next session
  a windowed screen-sized window with its title bar off the bottom.

**One launcher, in Avalonia, on Windows, Linux, macOS and Android** — one
thread, one toolkit setup per process (`GuiLauncher.EnsureSetup`). `-launcher`
opens a front screen, not a settings dialog.

**Every screen has the same layout, and it is one column down the middle.**
A photograph, a soft wash over it, and a **well** of fixed width centred in the
frame: what the screen is called at the top, a strip of pages or sources under
it, the screen's own content under that, and — on anything that asks a
question — the cross and the tick **side by side at the foot**, no on the left
and yes on the right. No card, no panel, no box anywhere. `UiLayout.Page` is
the whole of it and every screen is built from it, which is what stops nine
screens inventing nine answers again.

Two things about it are deliberate and easy to undo by accident:

- **The well's width is fixed** (`WellPlay` 820, `WellSettings` 640,
  `WellShort` 480) and does not follow the window. A layout that fills the
  window puts a settings row's label against one edge and its control against
  the other, so on a wide screen the two ends of one row are a foot apart —
  and the wider the display, the worse it gets. Here a wider window gives the
  *photograph* more room and the content exactly what it had. The numbers are
  chosen against the smallest layout box the surface hands out, 960x600, so
  there is a margin either side at every size the program allows.
- **The marks are together, not in opposite corners.** A cross in one corner
  and a tick in the other are two things to find; side by side under the
  content they are one thing to read, in the order they are read in, directly
  under where the eye already is. The pause menu has none, and that is not an
  omission: every entry on it is an action, so there is no question for a yes
  and a no to answer — and Resume as a tick *and* as the first word of the menu
  is one action drawn twice.

It was OpenQuake3/defrag's shape before — a column anchored in the bottom-left
corner with the wordmark in the opposite one — which is still where the
anchors in `UiLayout` come from. The front screen moved with the rest: a front
screen laid out differently from everything it opens is the one screen that
does not look like the program. It gets `LightWash()` rather than `Wash()`,
since three words need almost no ground and the default is set by the densest
screen there is.

| Screen | What it does |
|---|---|
| Start | the wordmark over Play, Settings, Quit, centred. No heading -- the wordmark is the heading. The build sits under the column, centred, and is the update button when there is one to take. Replaced entirely by the game-files screen while there is nothing to load -- a menu whose entries are all refused is a program that looks broken |
| Play | one list, and a strip over it saying what the list is: the picture of what is selected sits at the top of the well with the settings about it to its right, and the list runs the full width underneath -- a centred well has no side to hang a column off without stopping being centred, and the picture is the answer to "which map is that", so it is the thing that gets the room. **Online** (the directory's servers, with name, hunter and an address box), **Offline** (maps, with match type, hunter, bots and skill -- and nothing else: the `Where` row that used to sit here, whose second answer had the directory run a *server*, is gone, because a server is not a variant of a match against bots and the one face of Play that is about not being online is the wrong place to make one), **Story** (save slots, hunter, continue or start over), **Clips** (this machine's recordings, plus the system picker last). Choosing is two presses: a click selects the row -- the picture of the map, the details and, for a server, the address box fill in -- and the tick at the foot (**JOIN**, **START**, **WATCH**, or a second click, or Enter) is what commits. Three words, not four: joining somebody else's server is its own act and keeps **JOIN**, while Offline and Story both start a game of your own and both say **START**. The picture is the map on every face that has one, the server browser included -- a server row says which map it is running, and the map is most of what decides whether to join. It was one press, which put players in matches they had only meant to read the ping of. This one screen replaced seven -- host, join, browser, adventure, demos, the map grid and the vote picker. The Online face carries a third mark, **CREATE SERVER**, between BACK and JOIN -- running a server is neither leaving the browser nor joining a row on it, and beside the act it is an alternative to is the one place on this screen it belongs |
| Settings | three pages, not six. **Game** is display, audio and the match rules -- including **Field of view**, a slider from 60 to 120 degrees defaulting to the DS's own 78, applied as a multiplier on whatever the camera asked for so a zoom or a scripted shot keeps the proportions it had on the cartridge, and applied as it is dragged (cancel puts it back); **Controls** is mouse, pen, touch, pad and keys; **Player** is name, hunter, suit, server addresses, updates, game files, the debugging-log switch and the credits. Reachable from the pause menu during a match, where the backdrop is the scrim alone so the game shows through. **Pro mode HUD** is the whole HUD question in one switch -- no helmet, plain fixed crosshair, weapon list at 170%, fixed weapon, and its own energy, ammo and score readouts in place of the game's; off is the game as the DS drew it. Two rows appear under it while it is on and nowhere else: **Crosshair size** and **Crosshair type**, the type row carrying a live picture of the answer at the chosen size. Cheats, bugfixes, the leftover feature flags and the HUD-readout opacity have **no UI** and no longer load from `settings.json` |
| Create server | six rows and a warning: **Server name**, **Game type**, **Your hunter**, **Map rotation** (a row that opens every map as a list where a press appends and a second press takes back, numbered `#1 #2 #3` in the order they were pressed -- a rotation is a sequence, which is why it is not a column of tick boxes), **Host on** and **Server type**. Two kinds, and the difference is whose machine runs it. **Hosted** is the default: a directory starts an ordinary `DedicatedServer` on its own box and answers with the port, so there is no router to configure -- and **Host on** opens a page of its own listing **every server the directory names**, each asked on the port the browser already pings it on -- with a ping against the ones that will open a game and a reason against the ones that will not. A server says so in a flags byte on its status reply, and it is the machine that would run the match, so there is nothing in between to ask. The first shape of this asked for a *directory* on each box, which meant deploying one per region that listed nothing and existed purely to be answered -- a component invented to satisfy a layering mistake. There is still exactly one directory in the world: it answers "who is up", and each server answers "can you open me a game". The directory is a candidate too, since it has a port range and will open one. **Dedicated server** runs it here **in its own console window, as its own process that outlives the client** -- quitting to the front screen or closing the game does not end the match anybody else is in. On Windows that means the console binary out of the server package, *not* the game's own exe: `FruityPrime.exe` accepts `-server` but is a GUI binary, so a server started from it has no window, logs nowhere and cannot be closed. Its absence is what the **install** mark at the foot is for, and it is the only case that mark appears in. The router and firewall have to let UDP 27888 in before anybody outside can reach it, which the row says before it is picked. Either way the player is joined without the launcher closing, and a dedicated one is dialled on **127.0.0.1** rather than on this machine's public address -- the loopback is the one address certain to reach a server on this box, and a router that hairpins badly would otherwise look exactly like a server that did not start. `Mods/Launcher/Gui/CreateServerScreen.cs`, `Mods/Network/LocalServer.cs` |
| Confirm | one sentence, centred, with the two marks directly under it. Quitting, leaving a match, resetting every keybind and wiping a save slot are four consequences and one screen |
| Pause | the same centred column, over the scrim **in the game window itself** (and the scrim alone -- no wash, or the match it exists to keep visible goes away), with the match still running behind it. No heading and no footer, unlike every other screen: "paused" says what the player has just done with the frozen match behind it saying the same thing, and the match's name names something they are looking straight at -- neither is what anybody pressed Escape to find out. It is and **longer than the rest on purpose**: Resume, Vote map, Spectate/Rejoin, Fullscreen, Record demo, Settings, Leave match, Quit. Voting on a map, going fullscreen, spectating and recording are things you can only want *during* a match, so this is the one screen they can live on -- everything else is short precisely so this can be long. **Vote map** opens Play with the strip taken away, because calling a vote is picking a map |

The debugging-log switch left the front screen for Settings → Player. It is
not something anybody came to the launcher for: it is what somebody is asked
to turn on when they report a crash nobody else can reproduce. Switched on it
writes `logs/FruityPrime-<when>.log` beside the executable (the app's data
directory on Android) with everything the program prints plus the machine, the
driver, every model read and the stack of anything that kills it. **Share
logs** sits under it, only when logs exist, and zips them into the phone's
share sheet -- the app's own directory being one no file manager will browse.
`.claude/DEBUG-LOGS.md`

`-uidesign DIR` is the other side of that: the two faces of Play, the settings
and the pause menu drawn six ways at 1280x720 and written out as twenty-four
pictures, so "what should this look like" can be answered by looking. The
front screen is left out on purpose -- it is settled. Nothing in it ships.

`-uishot DIR` photographs all of it with no display: `start`, the four faces
of `play`, `play-vote`, `create-server`, `create-server-dedicated`,
`create-server-maps`, `create-server-hosts`, `settings`, `settings-player`, `setup`, `confirm`,
`pausemenu`, `pausemenu-small` and `serverbrowser`. `-shellshot DIR` is the
other half -- it opens the real window and photographs the composite at each
stop of the loop (front screen, a key, a click, a match, the pause menu over
it, the front screen again), which is what `-uishot` cannot answer. That one
needs a display; Xvfb is one.

Gotchas worth keeping in view without opening another file:

- **Cheats are all off in a networked match**, not just the obviously leaky
  ones: `NetLaunch.DisableCheatsForMatch` walks every `public static bool` on
  `Cheats` by reflection, so the list can't drift.
- **There is no console window at all** on the Windows build (`WinExe`).
  `Mods.ConsoleWindow.Prepare` attaches to a parent console when a command was
  typed, allocates one when double-clicked, and does neither for the launcher.
- **A bare invocation opens the launcher on Windows and macOS**; on Linux it
  still opens upstream's console menu, since that's a screen people there
  already use — `-launcher` asks for the window there too.
- Offline matches can hold eight players; `PlayerEntity.MaxPlayers` defaults
  to four (a DS match's cap), so the launcher raises it before creating
  players or asking for seven opponents silently produces three.
- **The weapon icons are supersampled, and they are the only thing in the
  program that is.** `Mods/Render/SmoothHudIcon.cs`: the art is one texel per
  DS pixel and lands in a box two or three times that size (more in pro mode,
  which draws the list at 170%), so nearest magnification gave every icon a
  staircase with two-pixel steps in some rows and three in others -- reported
  as "the icons are blurry", which it is not; it is a wobbly edge. These
  frame is replicated **hard-edged at 8x**, no interpolation, and
  `HudObjectInstance.Smooth` asks `DrawHudObject` for linear filtering on that
  one texture: the GPU is then *minifying* sharp squares, which is those same
  squares with a hairline of anti-aliasing. Everything else stays nearest,
  because everything else is meant to look like the DS. **Two traps.** The
  factor has to stay above the largest magnification in play (about seven, at
  4K with the list at 170%) or the GPU magnifies instead and the icon goes
  soft -- 4x did exactly that at 1440p and got the same word back. And
  resampling the mask (bilinear, then a smoothstep) rounds every corner into a
  blob: sharp is the goal, the anti-aliasing only stops the edge wobbling.
  `DrawWeaponList` reuses the instances rather than rebuilding them, because
  the HUD is set up again on every rotation and nine 256x256 textures a map
  is a leak.
- **Changing hunter is asked on the results screen, not in the pause menu.**
  The two rows that used to sit there ("Respawn as", "Suit colour") are gone:
  a pause menu is opened instead of playing, so the one screen where the
  change is free was the one screen that never offered it. `Mods/EndScreen.cs`
  puts it in the top right corner of the ten-second results screen instead --
  the hunter's own portrait with an arrow either side, four suit swatches
  whose colours are read out of that hunter's model (`Mods/HunterSuits.cs`,
  nothing is written down), and the next map's name off
  `MatchStatePacket.NextRoomKey`, which had been on the wire since the
  rotation was written and never read. **The hunter is the real model, not a
  sprite** (`Mods/Render/HunterPreview.cs`): it is an `EntityBase` that is
  *never inserted into the scene* -- so it takes no slot, runs no Process,
  holds no NodeRef and cannot outlive a room change -- whose items are
  collected last and drawn in a pass of their own
  (`Mods/Render/PreviewPass.cs`) into a scissored corner with its own camera,
  its own fixed lighting and its own cleared depth buffer, so nothing in the
  level can occlude, light or cull it. It stands still, facing the camera
  (these models are authored facing -Z, hence the half turn) in the `Idle`
  animation, because with no animation set the skeleton draws as authored,
  which is a T-pose. The panel is drawn in four boxes with a hole where the
  model lands, since the model reaches the frame before the HUD does. The
  sprite portrait is still there as the fallback for a model that will not
  load. Arrow keys or the d-pad. The answer is
  still `RespawnChoice`'s and is still cashed in at the next spawn. **Under
  it is the vote for the next map**, which is `callvote map` moved to the one
  moment nobody is playing: **every** map, scrolled, with the launcher's own
  preview beside each one. Picking proposes, picking what somebody else picked
  validates, picking another proposes that instead, and **the map with the
  most votes is the one loaded** -- no threshold, since an intermission
  interrupts nobody and a bar to clear only produces the outcome nobody voted
  for. Maps with votes are **pulled to the top of the list**, so the room can
  see what it is choosing between without scrolling for it; the leader is
  marked NEXT and the rotation's own map is what plays if nobody picks
  anything. Mouse wheel or the arrows to scroll, click or Space to pick, click
  again to take it back. `Mods/MapPick.cs`,
  `.claude/multiplayer/NETWORK-MATCHEND.md`. The scoreboard is squeezed left
  and the ping column and the radar go away while the screen is up -- both
  live in the corner the pickers do.
  `GameState.MatchEndingSeconds` and `DedicatedServer.EndSequenceSeconds`
  are one number in two places and have to move together. The cursor is
  released for the length of the results screen (`Renderer.OnRenderFrame`
  reads `EndScreen.Available`) because the picker is something you click:
  arrows for the hunter, the swatches directly for the suit. The hit boxes are
  published by the draw (`EndScreen.NoteLayout`) rather than worked out twice,
  so they cannot drift from the picture. **The panel's height is derived from
  the stack inside it** (`EndRow`/`EndStackHeight`), not stated: it was a
  constant 92 that the content did not fit, so READY hung out of the bottom
  edge and the "NEXT: ROOM" line -- placed by measuring *up* from that same
  edge -- was drawn straight through the middle of it, which at the 1.45x the
  picker used to be drawn at on a phone was the whole button. The suit caption
  and the colour's name are one line now ("SUIT: ORANGE") rather than two on
  either side of the swatches, and `EndScale` is 1.3 on Android.
- **Two additions to the directory's wire, both after the fixed block and
  neither a protocol bump.** A `HostRequest` may carry the asker's whole map
  cycle -- `[count][count x (40-byte room key + mode)]` appended past
  `HostRequestPacket.Size` -- and a `MasterList` reply ends with a flags byte
  saying whether that directory starts games at all. Both are invisible to the
  other side's older build: a directory from before reads exactly `Size` bytes
  and plays the single map it always did, and a launcher from before stops
  reading once it has taken `count` entries. So `NetConfig.ProtocolVersion`
  does **not** move -- but a directory has to be **redeployed** before a
  rotation asked for is more than a rotation of one. **Silence is not a no.**
  The flag has three states and the third is the one that matters: hosting is
  on by default and has to be turned *off* with `-hostports none`, so every
  directory deployed in the world hosts, and a launcher reading "did not say"
  as "will not" offers nothing to anybody until every one of them is
  redeployed -- which is what "Host on: nobody" against the live directory
  was. Only an explicit no is a no (`MasterListResult.WillHost`); the cost of
  being wrong is a clear refusal at the moment the button is pressed, against
  a row that says nothing and explains less.
- `PacketType.StatusQuery` answers "what map, what mode, how many players"
  without claiming a slot, which is what lets the browser poll idly. A server
  built before it falls back to a slot-taking Hello/Bye probe — redeploy the
  server to get the cheap path. Full account, plus the directory and hosting
  design: `.claude/multiplayer/NETWORK-BROWSER.md`.

Deep dive (UI components, settings window, first-run/extraction, macOS/Android):
`.claude/launcher/LAUNCHER-OVERVIEW.md`, `LAUNCHER-DESIGN.md`,
`LAUNCHER-SETTINGS.md`, `LAUNCHER-FIRSTRUN.md`.

## Android

The head builds a playable APK. The engine's `GL` is redirected to OpenGL ES 3.0
by **one using alias** in the Android csproj, pointing the name at
`Mods/Render/GlEs.cs`, which emulates the four things ES does not have —
immediate mode, display lists, the current colour and the alpha test — so not
one call site in upstream's renderer changed. Input is the same trick from the
other end: `AndroidInput` hands the scene a keyboard and a mouse of its own and
presses whatever the player has bound, which is why rebinding, aim sensitivity
and the DS weapon wheel all work without touching `ProcessAllInput`.

**Which on-screen buttons are drawn is the player's.** Settings → Controls →
On-screen buttons is a master switch and one toggle per button, and it exists
because the buttons sit on top of the thing they get in the way of: aiming is a
drag anywhere on the right of the screen, and a drag that starts inside a
circle presses the circle. A button turned off is drawn nowhere and takes no
touch, so the glass it was on becomes aim. Nothing there can strand a player:
movement is the stick, aiming is a drag, jump is a double tap and boost is a
flick, and not one of the four is a button. Kept in `controls.txt` with the
rest of the controls (`Mods/Input/TouchSettings.cs`).

**Controls used to reset every time the app closed**, and it was two faults at
once: `InputSettings.Load` is called from `ModEntry.TryHandleHeadless`, which
this head never runs, and `controls.txt` was written beside the executable --
a directory an Android package does not own, so every save was refused and the
exception swallowed. It follows `LauncherPrefs.Directory` now, and
`AndroidApp.BuildHome` loads it.

**The front screen runs; the match has never been loaded.** An emulator (API
30, x86_64, software CPU and GL) shows the screen and the game-files card; what
that box cannot do is load a room, having no extracted game files, so the
renderer, the touch controls and demo playback (which the head can now do --
see the port notes) are still unmeasured on a device. Two traps that killed the
app before any of this project's code ran — an activity theme that was not an
AppCompat descendant, and a Debug APK that carries no managed code unless
`EmbedAssembliesIntoApk=true` — are written up with the rest in
`.claude/android/ANDROID-PORT.md`, along with the build recipe, the game-files
directory, and how to run an emulator here.

## Gamepads

**A pad and the touchscreen are both live at once on Android.** Using the pad
puts the on-screen layout away and does nothing else: the surface underneath
keeps working, so a touch does what it landed on *and* brings the layout back.
A stick in one hand and a thumb on FIRE is a normal way to hold a phone, and
the weapon wheel cannot be reached from a pad at all.

A pad plays the game on the desktop and on Android, over USB or Bluetooth,
in an Xbox-shaped layout: sticks move and aim, right trigger shoots, A jumps,
B morphs, the bumpers and d-pad change weapon, Back is the scoreboard and
Start is the pause menu. There is **no weapon wheel on a pad** -- it reads an
absolute pointer position, which a stick does not have.

It reaches the game the way the touch controls do, from the other end: after
`ProcessAllInput` has run, the pad's contribution is **ored** onto the same
keybinds the keyboard just filled in, so a pad and a keyboard work at once and
no upstream call site changed. Aim is the exception, since a stick is analogue
-- it goes in at `ApplyModAim`, in the same units and at the same point in the
frame as the mouse's.

`FruityPrime -gamepad` prints what a pad is doing with no match in the way,
and distinguishes "not connected" from "connected but unmapped". Layout, feel
(radial dead zone, squared look curve, 3.5 degrees a frame at full stick), the
four settings, and how to test one with a virtual pad on `uinput`:
`.claude/GAMEPAD.md`.

## Pen tablets, and the weapon wheel

**Inside the pen zone the pointer is a pen, not a mouse**, and that is three
rules rather than one (`Mods/Input/StylusZone.cs`). A touch is owned by
whatever it went down on until it lifts, so a drag may wander anywhere without
becoming something else. **Aiming is that drag on the map and nothing else** --
not the pointer's raw movement, which for a tablet is the distance the hand
travelled to reach the weapon it was going for, and which is why "I cannot aim
with the pen" and "my aim jumps when I reach for a button" were the same bug.
And **the tip never fires** while it is on the zone: the DS put the trigger on
a shoulder button, this screen has no trigger, and here the tip arrives as the
left mouse button, which is the fire bind. A touch that begins *outside* the
zone is an ordinary click and is left alone, which is what a tablet player
shoots with. The weapon select is **held**, not tapped -- the wheel has to
still be up when the pen reaches it -- and **the wheel itself is drawn in the
zone**, not across the window: it is the DS's bottom screen, so with a zone
marked out the zone is where it belongs, and the arc is measured in the same
rectangle it is drawn in (`ModPlaceWeaponSelect` and `UpdateWeaponArc` read the
same four numbers). A pen that has left the zone has left the wheel and
chooses nothing. Placing the zone keeps the corner the pen
went down on where it was put (it used to slide upwards under the hand,
because the height is derived from the width and the width was still growing),
and the arrows, `[`/`]`, Shift and Enter place it exactly.

**The weapon wheel is a drag on a mouse and the DS's arc on anything that
points at a place** (`Mods/Input/WeaponWheel.cs`). The arc reads the cursor's
position against five angle thresholds, which is right for a pen or a finger
and useless for a mouse: it meant releasing the pointer mid-match and reaching
for the top-right corner of the screen with it, through a wheel drawn over a
fight, with the view unable to follow -- and the cursor had to appear
somewhere, which on the arc is already a weapon chosen. So on a mouse: hold,
move up or down, let go. The pointer stays grabbed, a tenth of the window's
height is one weapon, unavailable and empty weapons are skipped, and nothing
wraps. `Scene.ShowCursor` is what forks the two.

## Boosting with a flick of the mouse

**A whip of the mouse boosts Samus's ball, in the direction of the whip**
(`Mods/Input/MouseFlick.cs`). The gesture already existed on the touch head --
a flick on the aim side, the way a flick of the stylus did on the DS -- and
the desktop had the same free hand without anybody noticing: the ball is
steered with the roll binds against the camera's basis and the camera trails
it by itself, so **nothing in `ProcessAlt` reads a mouse delta at all** for the
four hunters that roll. It goes in through the plumbing the swipe already uses
(`SwipeBoostRequested`/`SwipeBoostX`/`SwipeBoostY`), so it is one forced full
charge into the release branch and not a second boost path, and it needs no
protocol change -- a player's position is reported rather than re-simulated
from their buttons, so the boost reaches every other machine as movement.

**No setting, and the threshold is a turn rather than a distance.** A number
of pixels describes the player's mouse (the same whip is 200 px on one desk
and 2000 on the next) and a fraction of the window describes their monitor,
which has less to do with it again; what a flick *is*, is the movement that
would have spun them round on foot. So the delta is run through the game's own
aim arithmetic -- `delta / 4 * sensitivity` degrees -- and a third of a turn
inside five simulation frames is a flick, which self-calibrates to the
sensitivity the player already chose. The gates are all "is the mouse saying
something else": the weapon wheel is answered by dragging, the boost bind held
is a charge being built on purpose, and a gap in the frame numbers (the ball
just closed, the player was frozen or paused) throws the history away.
`-debuglog` says so the first time one is read.

**The direction is the whip's own, against the camera's basis** -- an aim,
not a choice between four things. It *was* snapped to forward/back/left/right,
the four the roll binds and the DS's d-pad offer, on the report that a
sideways flick "went forward again" (*"gauche/droite, ça va quand même vers
l'avant"*). That was the wrong cure for a real complaint. The direction being
handed over was contaminated -- see the speed weighting below -- and snapping
merely hid it on the two axes where rounding happened to land right, while
turning the other two into a lie: *"quand je fais back, c'est toujours 100%
back mais jamais dans la direction exact de ma souris"*. A flick back and to
the left came out flat back every time, whatever the hand did. The measurement
is fixed where it is taken instead, and what arrives is used as it is. The
touch head's swipe goes through the same code and is now analogue too, which a
thumb wants as much as a hand does.

Four things were wrong with the first versions, and the first two were both
reported as one sentence -- *"the mouse goes down and Samus still goes up"*:

- **What is measured is a straight burst ending on this frame, not the
  window's largest displacement.** Summing whichever run of recent frames came
  out longest fires on a hand that merely wandered a long way, and hands back
  the direction of the wandering rather than of the whip: flicking down while
  the sum was still dominated by an earlier upward drift gave a boost upwards.
  The burst is now grown backwards from the newest frame and stops at the
  first sample that is slow or that points more than 30 degrees away from the
  run already gathered, so what it adds up is one movement and the direction
  it reports is that movement's. **The hand also has to come to rest between
  flicks** -- a cooldown alone lets one long sweep fire once per cooldown for
  as long as it goes on -- which doubles as the guard for a cursor that was
  warped rather than moved (unpausing, regaining focus).
- **An aimed boost redirects the ball rather than pushing it.** Upstream's
  boost is an impulse added to whatever the ball is already doing, which is
  right when it goes where the ball was already pointing and useless when it
  does not: flicked backwards at speed it cancelled some of the roll and the
  ball carried on forwards. So when -- and only when -- a flick aimed it, the
  horizontal speed is projected onto the direction asked for first, dropping
  the part going the other way and the part going sideways, and **nothing is
  added**: the ball leaves along the flick with the momentum it already had in
  that direction and no more. The DS's own boost is untouched.
- **The direction is weighted by speed, and the run-up barely votes.** A whip
  starts from rest, so its first frames are the hand breaking away -- slow,
  and pointing wherever the wrist happened to be. Summing displacement gives
  those frames a say in proportion to how far they went; each frame weighted
  by its own speed gives them one in proportion to the square of it, so the
  peak of the whip decides. That, with the coherence tightened from 45 degrees
  to 30, is what makes the angle worth handing over at all -- and it is why
  the four-way snap could go.
- **What the roll binds put into the flick frame is dropped.** The traction
  block runs earlier in the frame than the boost and only checks
  `_boostAimLock`, which the boost sets *after* it -- so a player holding
  forward as they flicked sideways got one frame of forward added on top of
  the dash, and the aimed boost left at an angle nobody asked for. That is the
  *"vers la diagonal en haut à gauche"* half. Only the horizontal part is
  cleared, and only when a flick aimed the boost.

`-debuglog` writes an `[input]` line for **every** flick, from both ends: the
screen direction and its angle where the gesture is read, then what that is in
forward/left against the ball's own basis and the world vector it was sent
along. "It goes forward when I flick sideways" is three questions (did it
fire, was it read as sideways, did the ball go there) and those lines are the
only thing that separates them.

## Frame rate

**The simulation runs at exactly 60 Hz. The picture runs at the display's
rate.** They used to be the same call, which is why 60 was the whole frame
rate; `Scene.OnUpdateFrame` is now `OnSimulationFrame` plus `OnDrawFrame`, and
`RenderWindow` runs the first on a fixed-step accumulator
(`Mods/Render/FrameTiming.cs`) and the second every time it draws.

The simulation cannot be moved off 60 and that is not a limitation to design
around, it is the reason the split exists: every timer in the engine is counted
in frames -- `grep -rc "todo: FPS stuff"` finds **806** -- and an intent is sent
per frame, a demo is a count of frames, and `NetConfig.ProtocolVersion` would
have to move. Nothing about the wire, the demo format or the DS behaviour
changes here, because nothing about the simulation does.

- **`Scene.OnUpdateFrame()` is kept and still does one step and one picture.**
  Every harness client calls it -- `NetCheckClient`, `MapAudit`, `WeaponDps`,
  `ThumbnailCapture` -- and is therefore untouched by any of this. So is
  Android, which drives the same call.
- **Every picture is of the newest simulated state, and nothing is blended.**
  There was an interpolation pass -- entity transforms and the camera blended
  between their last two simulated states -- and it is **gone**, deliberately
  and completely. It bought smoother motion between steps and cost visible
  wrongness on everything that is pooled and reused: a beam projectile or an
  impact effect taken off the free list starts its new life holding the last
  one's transform, and a blend against that draws the shot somewhere between
  where it used to be and where it is. That is the "artifacts de tirs" and the
  wall impacts landing nowhere. Do not put it back without an answer for entity
  reuse.
- **The game's speed no longer depends on the machine.** One call for both
  meant a box managing 40 fps played in slow motion; the accumulator pays what
  it owes, measured at 60.000 Hz with a 40 Hz draw rate.
- **Look for timers living in the draw pass.** Three were found by audit and
  all three are handled by counting steps owed rather than by moving code:
  `UpdateFade` (whose delay ends cutscenes and changes rooms -- it would have
  expired 2.4x early at 144 Hz), `ProcessEffects`, and the pause map's own
  animations. Frame advance is forced back to one step per picture, because the
  request to advance is consumed *after* the frame is drawn.
- **When a frame is split in two, look at every counter both halves read.**
  Effect elements spawn particles on every *other* step (the DS ran effects at
  30 Hz) and record the parity they were created with, so their **first**
  advance is a spawning one -- which is where a burst lives. Upstream
  incremented `_frameCount` *after* `GetDrawItems`, so a spawn and the advance
  that followed it in the same frame saw the same number; the split moved the
  increment into the step, before the draw, and every element's first advance
  started failing its own check. No flash on a charging Missile, no explosion
  on a wall, while the smoke and debris of those same effects carried on --
  and every number the harness measured was unmoved. `_effectFrame` is a clock
  the effect system owns, read by both halves. 776 effect particles a run
  became 1254.
- **Draw state must be cleared in the draw pass, not in the step.** The
  single-particle table -- the fuzzball at the head of a shot, the scan-visor
  markers, the death sparks -- is filled during the entity draws and emptied
  once a frame, and the emptying stayed behind in the simulation step. A
  picture with no step behind it, which is most of them at 144 Hz, therefore
  drew the previous frame's particles a second time at the positions they had
  then, and kept doing so until the 200-entry table filled and started dropping
  the new ones. `_singleParticleCount = 0` now sits in `OnDrawFrame`.
- **The on-screen FPS counter reports the picture**, not the simulation. The
  simulation rate is invisible to a player, which is why it goes to the debug
  log -- every five seconds, or immediately on a dropped step or a stall.

Default is **Display (VSync)**, the only tear-free setting; an explicit number
turns VSync off, since asking for 120 on a 144 Hz screen with VSync on gets 72.
OpenTK 4.9 no longer separates its update and render ticks, so `UpdateFrequency`
on the window is the frame rate and `RenderFrequency` is deprecated.

Android runs the same split in `GameView.RenderLoop`, with input inside the
step loop and no sleep in display mode (`eglSwapBuffers` is the pacing there).
It builds but **has never run on a device**, like the rest of that head.

Full account, what the draw pass may and may not touch, Android, and how it is
all tested without a 144 Hz monitor: `.claude/render/FRAME-PACING.md`.

## Updating

`Mods/Update/`. The program checks GitHub for a newer release on its own, says
so, and **installs nothing where a person could decide** — "Update now" opens
the release page; download and unpacking are the player's. **A dedicated
server is the exception and installs on its own**, because every part of that
reasoning inverts when there is nobody at the keyboard. It checks by itself because
`NetConfig.ProtocolVersion` makes a server refuse a client on a different
build outright at Hello, so a copy one release behind can't join anything, and
that's worth automating; it does not install because that means downloading
and executing a file with no signing behind it, so the guarantee would only
ever be "TLS, and GitHub was not compromised" — not doing it is better than
doing it carefully.

| | When it checks | What "update now" does |
|---|---|---|
| Launcher window | in the background once the window is up | opens the release page; badge shows the address if there's no browser |
| Text launcher | at startup, waiting up to 2 s | prints the address, opens a browser if there is one |
| `-update` | when asked | prints the address and opens it |
| Server and directory | at startup before binding, then every 10 min | **installs it**, and restarts — but only once nobody is connected (a server) or no hosted match is running (the directory), so a busy one keeps playing and swaps when the last person leaves. `-noautoupdate` opts out |

A server updating itself is the one place the "no unsigned installs" rule is
traded away, and it is traded for a bigger one: `NetConfig.ProtocolVersion`
makes a server refuse every client on a different build at Hello, so a stale
server is a server **nobody in the world can join**, indistinguishable from
one that is switched off. A bad binary is a failure an operator can undo; that
one is a failure nobody can even see.

The swap is not the launcher's. `DesktopUpdate` starts a second process that
waits for this one to exit and then copies over the installation, which is
exactly wrong under systemd: the copier is a child, so it lives in the unit's
control group, and the moment the main process exits systemd kills the group
and restarts the unit — killing the copier mid-copy and bringing the old build
back, for ever, with no error anywhere. `Mods/Update/ServerUpdate.cs` needs no
second process: a running program on Linux holds its files by inode, so the
new build is written over the installation by the server itself, one atomic
rename at a time, and then it exits. Under a supervisor (`INVOCATION_ID`)
exiting *is* the restart; with none, it starts its successor itself — **with
the command line it was given**, since a dedicated server restarted bare opens
a launcher on a machine with nobody at it.

Windows will not delete a running image, and that used to end the swap
half-done: a Windows server downloaded every release and applied none of them,
which stopped being cosmetic the moment a protocol bump made a stale server one
nobody can join. It *will* rename a running image, so the old build is moved
aside to `.fp-old` and deleted by the next start, which is the first moment
nothing is running out of it. Both platforms now take the same path with one
step different, and a `-server` start sweeps whatever a previous update left
behind whether or not updating is still switched on.

`launcher.txt` carries `auto_update`, on by default; `-noupdate` turns it off
anywhere, including the server's. A local build without the release workflow's version stamp reports
itself `a local build` and stands down, since there's no way to tell it apart
from a release either ahead or behind.

## The test method

The failure that matters here is invisible from one side: two clients can be
perfectly connected — right slots, agreed clock — while each holds a scene
containing only itself. `-netcheck` runs the **real client** (real
`Scene.OnUpdateFrame`, real `PlayerEntity` simulation, real net hooks, hidden
window) driven by `NetTestScript`'s fixed 15-phase tour, keyed to the
**server's** clock so every client is in the same phase at once. Every client
records what it *did* and what it *saw*; `compare-reports.py` cross-checks
that what one claims to have done shows up as what every other client says it
saw.

```bash
cd ~/mph-net-test
./run-check.sh 150 Samus Weavel Sylux Trace Samus Noxus   # seconds, then hunters
```

Read the output in this order: per-feature `MISMATCH` lines, then
`scoreboards agree`, then `damage pipeline`, then `remote position snaps`.

Map sweeps (`-maptest`, `-maptest -bots`), the world/affliction probes, how to
read every metric the harness prints, and the traps that have already cost
time: `.claude/testing/TEST-HARNESS.md` and `.claude/testing/TEST-METRICS.md`
(the latter also carries the last verified pass/fail status).

## Building and releasing

`.github/workflows/build.yml` publishes `win-x64`, `linux-x64`,
`linux-x64-server`, `linux-arm64`, `osx-x64` and `osx-arm64` on every push and
PR; `release.yml` builds those six plus the Windows server (seven packages) and
the APK on a pushed `v*` tag, and leaves them on a **draft** release for a
person to read and publish -- it is never published by the workflow itself,
and it is deliberately not flagged a prerelease, since GitHub's
`releases/latest` (what the in-app update check asks) skips those:
Releases here are numbered from **v0.1.0** and are their own line, not
upstream's: `Program.Version` (0.35.1.0) is upstream's data-format number and
is what `paths.txt` is checked against, while the tag is what
`BuildVersion.Current` reads. The two never meet, which is why the release
numbering could start over without invalidating anybody's extracted files.

```bash
git tag v0.2.0 && git push origin v0.2.0
```

Or run `release` from the Actions tab with the tag box empty and a **bump**
picked (`patch`/`minor`/`major`): the workflow reads the newest `v*` tag,
works out the next one, creates it on the commit the run was dispatched from
and builds that. Nothing to clone, nothing to type. That is the only
auto-tagging there is -- a push to master tags nothing, because every push
would then be a release, and the draft still waits for a person either way.
It lives inside `release.yml` rather than in a workflow of its own because a
tag pushed with the default `GITHUB_TOKEN` does not trigger another workflow.

The release notes are a standing block (beta, bring your own cartridge, which
package is which) with GitHub's own generated changelog appended under a rule
-- every commit and merged PR since the previous tag. If the generator fails
the block still goes out, with a warning in the log.

`tools/check-no-game-assets.sh` (no Nintendo asset ever published) and
`tools/check-dedicated-server.sh` (the server actually starts) both run in CI
and are worth running locally before pushing:

```bash
tools/check-no-game-assets.sh                    # the repository
tools/check-no-game-assets.sh publish/win-x64    # a build
```

Tagging (including the bump path and its traps), PE-header subsystem
split, why only the Windows server is renamed, and the CI runner layout: `.claude/build-deploy/BUILD-WORKFLOW.md`.

## Deployment

```bash
# server and directory (rebuilds ARM64, installs both units, restarts them)
MPH_SERVER_HOST=net.livetek.fr MPH_SERVER_USER=livetek \
  MPH_SERVER_PASS="$(read -rsp 'pi password: ' p; echo "$p")" ./deploy-server.sh
# MPH_DEPLOY_MASTER=0 to leave the directory alone
```

The exe is often locked by a running game: write `MphRead.new.exe`, then `mv`.

**`NetConfig.ProtocolVersion` is 8.** Version 8 changes continuous-weapon
phase timing without changing packet layout, so mixed builds must be refused.
Server **and** every client must use the same protocol — a mismatched client is
refused at Hello even though version 8's wire format is unchanged. An older
build would read the packets but simulate different continuous-weapon events.
Deploy the server
before handing out a client built against a new protocol. Publish commands and
the deploy script's env vars: `.claude/build-deploy/DEPLOY-SERVERS.md`.

`-simulate` is the one server option that needs game files on the server box.
It changes nothing on the wire, so it can be turned on and off between
restarts without touching a single client.

## Multiplayer: bugs found and fixed

A "damage is broken" report chased as latency for a fortnight turned out to be
eleven separate faults — frozen remote puppets,
shots fired from ankle height, respawn placement races, a derived-velocity
launch bug, a stale settling guard, a per-machine damage-sequence reset, a
divergence backstop comparing against the wrong instant, jump pads misread as
desyncs, a damage-direction vector abused as a launch velocity, unreplicated
ammo making a puppet briefly untouchable, and an unordered snapshot stream —
plus a double-counted kill that could end a match early for one client and not
another, and a transport queue that dropped the newest packets under load
instead of the oldest. None of it was actually latency; all of it reproduced
at single-digit-millisecond pings on loopback or the Pi.

A round from real matches on 2026-09-07: **every client died on the map
rotation, and MP2 HARVESTER's results screen came up black.** One cause.
`CameraSequence.Intro` is a static loaded only by `SceneSetup.LoadNewRoom`,
and **a rotation does not go through it** -- it is a room *transition* -- so
after the first map of a session the results screen flew the sequence
belonging to the map the session started on, and every `NodeRef` in its
keyframes named a level no longer in memory. Out of range that is an
`ArgumentOutOfRangeException` in `RoomEntity.DrawRoomParts` that kills every
client in the match on the same frame; in range it is a room drawn from a
part the camera is not in. Three changes:
`NetRoomChange.ReloadIntroCamSeq` gives each map its own sequence and
`Initialize`s it; `RoomEntity.ModCanPlace` refuses to cull against a ref this
room cannot place (indices in range **and** `NodeRef.RoomName` this room or
one of its connectors); and nothing culls at all while `MatchState` is not
`InProgress`, because the end-of-match camera is an authored orbit that is
free to sit outside every room part in the level -- which is the black
results screen and is not a stale ref at all. Reproduced and confirmed fixed
with `run-rotate.sh`: six rotations over four maps, zero crashes, and the
backstop never fires now that the cause is gone.

A second round, from reports out of real matches on 2026-09-04: a freeze that
existed on one machine only, players who went invisible at the top of one map,
a gun that fell off the bottom of the screen on a pad and could not be raised
or fired again, a weapon cycle that stopped dead on an empty weapon, and no
camera at all in alt form on a pad. Only the first is a network fault; the
rest are input and rendering, and three of the five were invisible to every
check here because nothing in the harness holds a controller.

Shapes worth keeping without opening anything else:

- **An affliction is not state until it is sent.** Freeze is applied inside
  `TakeDamage` from the *beam entity*, and `NetDamage.Replay` has no beam to
  give -- so a player frozen on the authority was frozen nowhere else: they
  walked around normally on their own screen while the machine running the
  simulation, which pins a puppet wherever its owner last said it was, drew a
  block of ice sliding across the room. `PlayerState.FlagFrozen` carries the
  state instead of the cause, and the countdown still runs locally. The same
  was then reported of the other two: the Volt Driver's screen distortion and
  the Magmaul's flames reached nobody but the authority either
  (`FlagDisrupted`, `FlagBurning`, and the flags byte is now full). A frozen
  puppet also stopped taking its owner's reported positions, since those
  describe a moment before the ice.
- **A puppet is moved after the movement step, and only the position moved.**
  `Move` set the position, the previous position and the node ref -- not the
  collision volume, which the engine recomputes inside the step this
  correction comes *after*. The blob shadow and the burn effect are drawn from
  that volume, and shots are tested against it, so for every remote player all
  three described where this machine had guessed they were while the model was
  drawn where they are. "The shadow is behind the character" was the visible
  third of it.
- **A press can be lost; a state cannot.** The alt form was replicated by
  replaying the morph press and nothing else, so one press that did not take
  left the authority's copy in the wrong form for the rest of the life -- and
  every other client agreed with it, since `FlagAltForm` is read off that
  copy. `IntentButtons.AltFormState` had been in the packet all along, used
  only to convert reported positions between forms; the authority now
  reconciles against it, and only the authority does.
- **A lookup that fails is not the same as a lookup that is stale.**
  `GetNodeRefByPosition` returns nothing for a position no room part contains
  -- the top of AD2 ALINOS PERCH, among others -- and the fallback kept the
  node the puppet already had, which the viewer often cannot see. That is a
  player who can shoot you from somewhere you cannot see them, with their
  shadow still moving about underneath. Now: probe the body and half a unit
  either side first, and if it still cannot be placed, say so and draw it
  anyway.
- **Nothing that answers "has this person touched anything lately" knew about
  the pad -- or about a remote player.** `Input.HasInput` is written in the
  pass that turns a keyboard into binds, and three kinds of player never go
  through it: a pad (ored on afterwards), a puppet (driven from relayed
  intents), and a scripted client. All three therefore looked idle, and the
  engine lowers an idle player's gun -- which `CanShoot` and `TryEquipWeapon`
  both refuse to work through. On a pad that meant a gun that fell off the
  screen and a player who could not fire again; on a **puppet** it meant a
  player who held still and fired had their shots fail to spawn *on the
  authority*, which is the only machine whose shots count. Shots spawned per
  slot went from 28-63 to 118-142 once it was fixed. `ModNoteInput`.

- **A stale input is not harmless just because it's only a position.** The
  intent stream has no notion of "this predates what just happened," so
  anything the authority does to a player of its own accord (a spawn, a
  teleport) can be undone by the next packet that predates it.
- **Look for this shape whenever a remote player can do something on their
  own machine and not on anyone else's:** the puppet is running the same code
  with different *resources* (ammo, in this case), and only the owner's copy
  of those is authoritative.
- **`untested` is a question about the harness, not a pass or a fail.** The
  zoom-replication check read `untested` for months because the tour never
  actually pressed the zoom button, not because zoom was broken.
- **A frame counter that restarts is not an out-of-order packet.** Every
  ordering guard on the wire compared frame numbers and nothing else, so a
  client rejoining a match it had been in for five minutes -- counter back to
  1, the authority still holding 18000 -- had every intent it sent refused for
  the next five minutes. Reproduced, and the shape to look for is any guard
  that says "older than what I have" without also asking "older by how much".
- **A ping is a measurement of the code path, not only of the wire.** The
  number on the scoreboard read 20 ms to a server 1 ms away by ICMP because
  the reply waited for the next rendered frame and for two poll-sleeps on the
  way. Now 1 ms on loopback, where it was 8-11.
- **A randomised run against a loopback server is a regression check, not a
  real-world one, and must not be reported as one** — it has none of the
  reordering, jitter or CPU load the bugs above were found under.

**The server can be the simulation authority** -- `-simulate`. The authority
was never a property of being a player: it is the property of being the
machine every other player's intent is pointed at, and until now that was
whichever client joined first. The engine's simulation needs no GL context at
all (the frame split had already put every GL call in `OnDrawFrame`), so the
server runs the *real* engine rather than a model of it -- which is what
answers the old objection that a reimplementation would be a second answer
free to disagree with the first. What it buys is fairness and resilience:
nobody is at zero latency any more, no handover when the authority leaves, and
`HandleSnapshot` refuses every client's world outright. What it does **not**
buy is a shorter wait for your own hit to register -- that is a round trip
wherever the authority sits, and shortening it is client-side prediction,
which is not implemented. The wire does not move: a client is told it is the
authority by receiving `PacketType.Authority` and in no other way, so a
simulating server simply never sends it. Measured at **110 MB and 0.31 ms a
step** for an 8-player room, against 337 MB for a full client, by dropping
work whose only output was a picture. `.claude/multiplayer/NETWORK-SERVERAUTH.md`.

**A shot the authority cannot find is declared, checked and arbitrated**
(protocol 7, `Mods/Network/NetHitClaims.cs`). The rewind below and the
prediction under it are the authority and the shooter running the *same* test
on the *same* positions, which is why they agree -- and there are three cases
where they cannot run the same test at all: the rewind hit its ceiling
(measured at **89% of shots clamped** on a 320 ms jittery line under the old
400 ms ceiling), the trigger pull arrived out of a press history, or **the
shooter was killed during the round trip**, so the authority never ran the shot
-- a dead player's presses do nothing. The third is the one a player calls
unfair: you shoot, the body drops, and then it stands back up because the person
you shot had already killed you on the machine keeping score. A `HitClaim`
carries the victim, the weapon, the damage, the world-frame the shooter was
looking at and **where the shooter's copy of the victim was standing**; the
authority refuses it unless its own history puts that player within 2 units of
that spot at that frame, so a claim can only rescue a hit the authority's own
record says was there to be had. A validated claim waits 18 frames and is
dropped the moment the authority resolves the same hit itself, which is what
stops the damage landing twice. **The arbitration**: a shot counts even when its
shooter is dead by the time it arrives, unless they were put down by a hit aimed
at a *strictly earlier* world; two shots aimed at the same world both count, a
trade. Claims are settled in fire-frame order so a mutual kill comes out the
same way whichever datagram won the race. Predicted kills on other players came
back on with it -- the authority no longer disagrees silently. Every weapon.
`-noclaims`. `.claude/multiplayer/NETWORK-HITCLAIMS.md`.

**Remote players are read off a playout clock, not snapped to the last snapshot
that arrived** (`Mods/Network/NetSmoothing.cs`). The stutter on a bad line is
not lost packets and not a slow machine -- it is a 60 Hz stream played back at
the rate it arrived, so a puppet stands still for three frames and then jumps
three frames' worth. Positions are buffered and read on a clock that ticks once
a simulation frame, held two to eight frames behind the newest snapshot, and
nothing is ever extrapolated (a guessed position puts a player through a wall
and then snaps them out of it). It does not cost hit registration, which is the
thing to be careful of: the read point is a *number*, so `IntentPacket.AckSubFrame`
sends it and the authority interpolates its own history between the same two
frames by the same fraction -- more exact than the integer ack it replaces. The
smoothed position **is** the position: model, hitbox, shadow and shot.
`-nointerp`. `.claude/multiplayer/NETWORK-SMOOTHING.md`.

**Shots are resolved against the world the shooter was looking at**, not the
one that exists by the time their trigger arrives -- backwards reconciliation,
ported from Q-Zandronum's `unlagged.cpp`. The error it removes is one-sided and
exactly a client's round trip: the authority spawns a remote player's beam
against the puppets it holds *now*, while that player aimed at the puppets a
snapshot showed them a round trip ago. `IntentPacket.AckFrame` is the whole
input -- the snapshot frame the shooter was looking at -- and the authority
rewinds everyone else to it, spawns, and then walks the shot forward to the
present one frame at a time, re-reconciling at each step. That second half is
Q-Zandronum's own and is the half this game needed: almost nothing here is
hitscan, so without it a laggy player's Missile merely leaves the muzzle late.
The authority itself is rewound by zero, because it already aims and resolves
against the same puppets. Measured at 156 ms of rewind against 150 ms injected,
with 0 mismatches on the 3-client instrument. `-nounlagged` is the control.
`.claude/multiplayer/NETWORK-UNLAGGED.md`.

**A client's own hits land the frame it fires them**, rather than a round trip
later -- which is the half the rewind deliberately did not buy, and the only
thing that gives anybody an instant hit when a *server* is simulating the
match and nobody is the authority. It is sound only because the rewind is
there: the authority puts everyone back to the snapshot frame the shooter had
applied, which is the world the shooter's own machine is holding when it
fires, so the local resolution and the authority's are the same test on the
same positions -- run earlier, on the machine that already has the inputs.
Two rules keep a prediction from becoming a lie: it **never scores and never
ends a match** -- the scoreboard is assigned from the snapshot for every slot
and `EndIfPointGoalReached` is already refused on a machine that is not
keeping the score -- and it is **only your own shot**, on somebody else or on
yourself, since incoming damage is a question about a shot fired on another
machine and this one has a worse answer to it than the authority does. **Your
own splash on yourself is predicted** -- a rocket jump is not damage that
arrives late, it is a jump that does not happen, and the push comes out of
`TakeDamage` with the damage. Source, target and input are all on this machine,
so it is arithmetic rather than a bet on a rewind; it is counted apart from the
rest for that reason, and **it is the one prediction that is still allowed to
kill** -- a rocket jump at low health, a recoil, a crusher, and above all a
fall into the void, which is the one death a player has already watched happen.

**The prediction is held rather than assigned over.** A victim's health is the
authority's number less what this machine has landed on them and not yet had
confirmed, a victim predicted dead stays down instead of being stood back up
by a snapshot that has not heard about it yet, and the health this machine's
own Shock Coil drains is credited on top of the authority's until it catches
up. The hold lasts one measured round trip and a margin -- not the two seconds
a prediction is kept for the statistics -- because a mispredicted hit is a
wrong health bar and a wrong health bar has to right itself in the time the
answer takes. **Killing somebody else is not predicted**, and that is a change
made on the strength of a real line rather than a loopback: against Japan a
client could kill the same opponent twice for one kill on the scoreboard,
because the authority disagreed and the next snapshot stood the body back up.
The damage is clamped to leave the victim on one point of health, so the flinch
and the mark are still instant and only the body falling is owed a round trip;
`-deathprediction` puts it back for measuring. **A self-kill is predicted**,
whatever that switch says. Nothing is rolled back because nothing durable is
written -- health, the score and a wrongly killed puppet's spawn all come off
the next snapshot on the lines that always carried them.
Confirmation is a white X around the crosshair, drawn on every machine and in
every match, offline included -- `.claude/multiplayer/NETWORK-PREDICTION.md`.
Measured at **86-98% of predictions confirmed** across runs -- 86% on the
largest sample, with 150 ms injected -- and at zero mismatches with a
simulating server, where all three clients predict. Quote the range: the
scripted tour does not fire the same shots twice. `-nohitprediction` is the
control, `-deathprediction` turns the lethal half back on for measuring, and
`-nohitmarker` turns off just the mark. No protocol change.

**"The same calculation, run earlier" needs the same inputs, and five of them
were not on the wire.** A prediction is sound because the authority rewinds to
the world the shooter was looking at -- but that only makes the two machines
agree while they are running the damage table over the same numbers, and the
charge level, the double-damage powerup, the alt-form ram's strength, the
damage level and the affinity-weapons rule were each either **re-derived** on
the authority from the relayed buttons or read out of the player's **own
settings file**. A partial-charge weapon's damage is a continuous function of
the frames the trigger was held (the Power Beam runs 6 to 36 over 24 frames),
double damage is a factor of two the authority's copy of a shooter can simply
not have, and the damage level is x0.75/x1/x1.25 on *every hit of every
weapon*. All five are settled: the first three travel in four bytes appended past
`IntentPacket.Size`, and the last two in spare bits of
`MatchStatePacket.Flags` where **zero means the server did not say** -- so
neither is a protocol change, and the broadcast only takes effect once the
server is redeployed. The **damage level is pinned to medium, x1**, and is
published as such: it is not an option any more, since the only thing three
answers bought was three ways for two machines to disagree
(`GameState.DamageLevel`). `-affinityweapons` is the one that is still a
choice. Weavel's halfturret health is
the one left, and a hit split with a turret is therefore not allowed to predict
a death.

**But the error a player actually sees was in the health bar, not the damage.**
Measured against the Japan server at 250 ms: the drawn bar sat a mean 26 and a
worst **61** points *below* the authority's, and never above it -- the client
always overestimated, never under. `lethal` is decided against that drawn
number and `HealthFor` floors it at 1, so that is a client killing people the
authority refuses to kill. Both causes were the `_shownHealth` floor: it
refused a rise the *authority itself* was reporting (a health pickup, a
respawn) and it is re-armed by every predicted hit, so on a fast or continuous
weapon it never lifted; and a `Duplicate` verdict dropped the debit half a
round trip before the snapshot carrying the lower health arrived, leaving the
bar on the floor alone. The floor now lifts on any rise the authority reports,
and a confirmed verdict settles the books while the **snapshot** settles the
picture. Worst gap 61 -> 0/6/1 points across three clients, with `floor held`
falling from 120/218/172 samples to 0/1/5. `DescribeHealth` prints the two bars
side by side and is what found it.

**And the arbitration is about when the trigger was pulled, not when the round
landed.** A claim carries two frames -- `AckFrame`, the world the shooter's
screen was showing when the hit *resolved*, and `LaunchFrame`, the world the
shot was *fired* in -- and the rule "a shot counts unless its shooter had
already been put down by a hit aimed at a strictly earlier world" was reading
the first. For a Missile, which is in the air for the better part of a second,
that asks whether you were dead when your rocket landed rather than when you
fired it, and voids a shot that left the gun before the shot that killed you
had even been aimed. It is invisible on a fast weapon, whose two frames are a
frame apart -- which is the shape the complaint came in: kills undone with the
Missile and the Magmaul, none with the Power Beam or the Imperialist.
`void (dead shooter)` went **20 -> 0** against Japan at 250 ms. The authority's
own side had the same error: a victim's death was stamped with the attacker's
ack at *impact*, so a slow projectile's kill recorded the wrong world in the
number every later claim on that player is judged against.

**A shot that travelled does not decide a death.** The authority resolves a
whole flight inside the frame the trigger was pulled (`NetUnlagged`'s
catch-up); the shooter's own copy is a projectile crossing the room against
puppets held a few frames behind. So for anything that travels the authority's
answer arrives **first**, the client adopts a bar that already contains the hit,
and its own copy of the same shot lands on top -- and the claim for it is then
matched as a duplicate and answered "already resolved", so it counts as
*confirmed* while the kill is undone. A client cannot tell its own
already-resolved shot from its next one, because the snapshot carries a count
of hits and no identity for the shot behind them; three heuristics were tried
and the best of them still suppressed 46 good predictions out of 49 on
loopback. What is exact is `BeamProjectileEntity.Age`: past three frames of
flight the damage is clamped to leave the victim on one point and the authority
does the killing. The hit stays instant -- flinch, knockback, mark and bar on
the frame it is fired -- and only the body falling waits a round trip. A Power
Beam or Imperialist round arrives in a frame and is untouched, which is the
split the complaint came in: **7 predicted kills / 6 undone -> 0 / 0** on a
Missile volley against Japan at 250 ms. `-hitrig missile` is the rig.

**And a prediction is retired by name.** The snapshot carries a count of hits
on a victim and the slot of only the *last* attacker, so a client's own hit
followed inside one snapshot window by somebody else's was never matched: its
debit stayed on the books while the authority's own health already had it, and
a shot or two later the client predicted a kill on somebody comfortably alive.
Three uncharged missiles are 96 damage against a hunter's 99, which is how
*"my client thinks three missiles killed him"* is only three points of stale
debit. Every hit claim's verdict now retires the exact prediction it was
declared under. The authority measures the rest for free: a claim carries the
shooter's own number for a shot and the authority already pairs it with its own
hit for that shot, so `sim: predicted vs resolved damage` prints the agreement
per weapon. `.claude/multiplayer/NETWORK-HITCLAIMS.md`.

**Chat is T**, three lines bottom left in green on nothing, gone ten seconds
after they arrive -- the frame counter sits in the right-hand corner, which is
where it went when the log was still in the top-left one. Bottom left because
that is where every game that took Quake's shape puts it and where players
look; the block is anchored at y 168, above Pro mode's energy panel, and steps
right past the weapon column when the modern HUD is drawing one. It draws with a font of its own (`Mods/Chat/ChatFont.cs`, pixel art in
the file, no asset): the game's has one alphabet, so every line typed came out
shouted and half as wide again as it needed to be. `PacketType.Chat` is additive and needs no protocol bump, so an older
server drops it silently and chat simply does nothing there until it is
redeployed. **Never in the story** -- `ChatBox.Available` is
`!GameState.SinglePlayer` -- and on Android it is a CHAT button that asks for
the soft keyboard, or any keyboard that happens to be attached. The server writes the slot and the name onto every line it relays
rather than trusting the sender's, and rate limits at the relay. Packet
numbers 24 and 25 are left free for a voice channel.
`.claude/multiplayer/NETWORK-CHAT.md`.

Recording and watching a match back -- the file format, the two things a demo
has to synthesize because they were never received, and why the player counts
frames rather than milliseconds: `.claude/multiplayer/NETWORK-DEMOS.md`.

Full postmortem, measurements, before/after tables, and the traps that cost
the most time: `.claude/multiplayer/NETWORK-DIAGNOSTICS.md`. The
double-counted-kill bug and match-end/rotation handling specifically:
`.claude/multiplayer/NETWORK-MATCHEND.md`. Current verified pass/fail status:
`.claude/testing/TEST-METRICS.md`.

## Known gaps

Claims that are unproven or only partly proven — not bugs, but not to be
re-claimed as solid either: `.claude/KNOWN-GAPS.md`.

## Mechanics catalogue

Weapons, damage multipliers, hunters, movement, states/afflictions, spawning,
match modes, world interactions, pickups, bots, and the multiplayer protocol
rules are in `MECHANICS.md` at the repository root.

`MphRead -mechanics` **prints** the catalogue to stdout; it writes no file.
Do not regenerate the committed one by redirecting it over the top --
`MECHANICS.md` carries detail the generator cannot produce (the affliction
table's charge rules, among others), so a wholesale `> MECHANICS.md` silently
deletes it. Change `MechanicsDump.cs` for anything derived from the game's
tables, and edit the file directly for anything that is not.
