# Android — the renderer and the touch controls

The head is `src/MphRead.Android/`. It compiles the same sources as the desktop
project with `ANDROID` defined, and it now builds a match, not just a screen.
Two things had to be answered to get there: the engine draws through OpenTK's
desktop GL, and it reads a keyboard and a mouse. Neither exists on a phone.


## The renderer: one alias

The engine calls `GL.Something` from about 250 places in files that are
upstream's. Rewriting those was not an option -- everything this project adds
lives under `Mods/` so a pull from NoneGiven/MphRead stays a fast-forward. So
the Android head points the *name* somewhere else, in one line of its csproj:

```xml
<Using Include="MphRead.Mods.Render.GlEs" Alias="GL" />
```

A global using alias is resolved before the `using OpenTK.Graphics.OpenGL;` at
the top of those files, in every namespace of the compilation (checked: it wins
in `MphRead` and in `MphRead.Formats` alike). Not one call site changed, and the
desktop build never sees it. The enum types the call sites pass are still the
desktop ones -- they are the same GL constants -- and `GlEs` casts them across
to `OpenTK.Graphics.ES30`.

`Mods/Render/GlEs.cs` then answers the four things ES 3.0 does not have:

| Missing | Answer |
|---|---|
| Immediate mode | `Begin`/`Vertex3`/`End` accumulate into a vertex buffer. Quads, quad strips, triangle strips and fans become indexed triangles; `LineLoop` becomes lines |
| Display lists | `NewList`/`EndList` bake that buffer into a VBO, an IBO and a VAO. `CallList` is one `glDrawElements` -- the same trade the display list was making |
| The current colour | A vertex with no colour of its own takes the colour current at *execution* time, which is the `GL.Color3(item.Diffuse)` `DoMaterial` issues per render item. Vertices carry a flag; the ones without their own colour read the `imm_color` uniform |
| `glAlphaFunc` | The engine asks for exactly two comparisons, Equal 1.0 and Less 1.0. The fragment shader discards on them |

And one thing that is bookkeeping rather than emulation: the engine binds
texture names it made up itself (`_textureCount++`), which a compatibility
profile allows and ES does not. `GlEs` keeps a map from those to real names and
creates one on first bind, with a single high-water counter so `GenTexture`
keeps handing out the next number in the engine's own sequence.

**What is lost:** `glPolygonMode`, so the wireframe and collision-volume debug
views draw solid. Nothing a player sees uses it.

## The shaders

`Mods/Render/EsShaders.cs` holds the six shaders of `Shaders.cs` written for
`#version 300 es`. The desktop ones are GLSL 1.20 reading `gl_Vertex`,
`gl_Color`, `gl_Normal` and `gl_MultiTexCoord0` out of the fixed-function
pipeline -- which is *why* the desktop build needs a compatibility profile and
`MESA_GL_VERSION_OVERRIDE=4.5COMPAT`. The ES ones declare those four as
attributes at fixed locations 0-4 and are otherwise the same program, expression
for expression, plus `imm_color`/`a_color_set` and `alpha_test`.

`GlEs.ShaderSource` substitutes them by reference as the engine compiles. They
do not follow a change to `Shaders.cs` on their own, and a silent divergence
would be a rendering bug with no message anywhere, so each is checked against
the SHA-256 of the source it was written from and a mismatch throws with the
name of the shader that moved. Recompute with the extractor in the commit
message or by hand: normalise CRLF to LF first.

Verified with `glslang` (16.5.0) when they were written, and since then by
SwiftShader on the emulator, which compiled and linked all four programs --
main, RTT, the shift program and the cel shading ink pass, the last three
sharing the RTT vertex shader -- while drawing a room. That is a check that
they compile, not a check that the picture is right; see the note on
SwiftShader's own artefacts in `.claude/KNOWN-GAPS.md`.

## The loop

`GameView` is a **`SurfaceView` with an EGL context and a thread of its own**.
Everything the engine does with GL -- a room's textures, its baked geometry,
the shaders, the drawing -- happens on that thread, so the scene is *built*
there rather than by whoever asked for the match. Loading blocks that thread
for seconds, which is the right thread to block: the UI thread stays free and
the loading notice on top keeps drawing.

### Why not `GLSurfaceView`, which it used to be

**Because `GLSurfaceView` answers a window event by making the UI thread wait
for the GL thread.** `surfaceChanged` hands the new size over and then blocks
until that thread has been all the way round its loop; `onPause` and
`onResume` do the same with a handshake of their own. So any window event that
landed while a room was loading froze the UI thread for the length of the
load, and Android put its own "isn't responding" dialog over the black loading
screen -- a white box over a black one, with nothing to press, which is what
starting a match from portrait did.

Measured here, with a room load stretched to 12 s and a window resize injected
into it (`adb shell wm size`), against a heartbeat posted to the UI thread
every 200 ms:

| | worst gap on the UI thread |
|---|---|
| `GLSurfaceView` | **16,921 ms** |
| our own EGL and thread | 1,092 ms, and nothing during the load |

Android's input-dispatch ANR threshold is 5,000 ms.

Waiting for the window to hold still before creating the view --
`MainActivity.WaitForSteadyWindow` -- made that *rarer* and could never make it
impossible: a phone resizes its own window whenever it likes, for the system
bars, for insets, for a call. Now `surfaceCreated`, `surfaceChanged` and
`surfaceDestroyed` write a field and return, and the loop picks the change up
when it is next between frames.

Two things come free from owning the context:

- **It survives the surface going away and coming back**, so a match is not
  lost to the home button. `GLSurfaceView` only kept it as a favour, through
  `PreserveEGLContextOnPause`; here nothing destroys it until the match ends.
- **Pausing is a flag**, not a handshake, so `OnPause` cannot block either.

`surfaceDestroyed` is the one callback that *should* wait -- Android wants the
surface unused by the time it returns -- and it does, for up to two seconds.
Not longer: the render thread cannot answer from inside a room load, and
hanging the UI thread is the thing this class exists to stop. A swap against a
surface the framework has taken back returns false rather than crashing, which
is handled by letting go and waiting for the next one.

**The window should still have stopped moving before the match starts.** That
is no longer what keeps the app from freezing, but a match that starts
mid-rotation starts at the wrong size and re-lays-out under the player. So
`MainActivity.WaitForSteadyWindow` does not create the `GameView` until the
content view has held the same size for 250 ms and is landscape (or three
seconds have passed and the display clearly will not turn). Waiting for the
*configuration change* is not enough: it arrives before the window has been
laid out at its new size. The orientation is then pinned with
`ScreenOrientation.Locked` for the length of the load and released back to
`SensorLandscape` once the match is on screen.

**That wait is also the thing that can eat a start, and four ways it could
were fixed.** The front screen is hidden the moment `StartMatch` is called and
the `GameView` does not exist yet, so anything that delays the start is a
black screen with nothing on it -- which is what "it will not start a map"
looks like from the player's side. So:

- The three-second deadline is **no longer conditional on the window having
  settled**. It used to be `steady && waited >= RotateMs`, and a window that
  never held still left the loop posting itself for ever. Waiting is a nicety;
  starting is the job.
- The loading notice goes up in `StartMatch`, not in `BeginMatch`, so there is
  never a black screen with nothing on it. `ShowNotice` is shared and
  `BringToFront`s it once the touch overlay is in.
- **Back cancels a pending start** (`CancelPending`) and puts the front screen
  back, rather than doing nothing because `InMatch` is still false.
- **A second `StartMatch` while one is pending is refused.** It used to
  overwrite `_orientationBefore` with the landscape it had just asked for, so
  `EndMatch` would restore *that* and leave the front screen stuck sideways
  for the rest of the session -- and pressing START twice is what a player
  does when the first press seems to do nothing.

There is also a hard 8 s give-up for the one case that cannot be started from
at all -- a content view that never reports a size, which would build a scene
for a zero-pixel window -- and it says so on screen instead of starting
something the player can neither see nor leave. Every decision logs one
`[android]` line with the sizes involved.

The order is the desktop's: `GameState.ApplyPause()`, `Scene.OnUpdateFrame()`,
`Scene.OnRenderFrame()`, `Scene.AfterRenderFrame()`, then `eglSwapBuffers`.

The pacing is not. `RenderWindow` asks OpenTK for 60 updates a second; here the
buffer swaps at the display's rate, which on a modern phone is 90 or 120, and
**the update is the frame**: the render item lists are built during the update
and cleared after the draw, so a render with no update in front of it draws
nothing. Rendering more often than the game ticks is therefore not an option --
the thread waits for the next 60 Hz tick instead.

## Frame rate

The match loop is decoupled the way the desktop's is: the simulation runs at
exactly 60 Hz on `FrameTiming`'s accumulator and the picture runs at the FPS
limit, up to 240. `GameView.RenderLoop` used to sleep to a hard `1.0 / 60.0`
around one `OnUpdateFrame`, which is why a 120 Hz phone drew 60.

Three things are specific to this head:

- **Input goes inside the step loop**, not beside it. `ApplyInput` works out
  this step's rising edges from the touch state, so running it once per
  *picture* would turn one tap on FIRE into two presses at 120 Hz.
- **In display mode the loop does not sleep.** `eglSwapBuffers` blocks until
  the panel is ready; sleeping as well is double pacing and would halve the
  rate. `MinFrameSeconds` is only a floor, so a driver that does not block (an
  emulator, a surface with no vsync) spins at 500 Hz rather than flat out.
- **`Surface.SetFrameRate`** (API 30+, guarded and caught) tells SurfaceFlinger
  what the surface intends, because a 120 Hz phone often sits at 60 until
  something asks. Below API 30 the FPS limit still caps the loop; it just
  cannot raise the panel. Best-effort throughout -- a device that refuses is
  not a reason to end a match.

The setting is the launcher's own **FPS limit** row, shared with the desktop,
so it needs nothing of its own here.

**None of this has run on a device**, for the same reason nothing else in this
head has: the emulator available here has no extracted game files and cannot
load a match. It builds, and the code under it is the code the desktop
measurements were taken on. Treat it as untested.

## The controls

No hook in the engine's input path, and none needed. `ProcessAllInput` reads a
`KeyboardState` and a `MouseState` once a frame and turns them into the
`Keybind` flags the player code reads. `AndroidInput` hands the scene a keyboard
and a mouse of its own and presses the keys the player has bound. OpenTK does
not let anyone else build those two types, so the constructors and setters are
reached by reflection, once, into delegates.

Three things come free from doing it that way:

- **Rebinding works.** A thumb on FIRE presses whatever `Controls.Shoot` says,
  key or mouse button.
- **Sensitivity and inverted aim work**, because aiming moves a pointer and the
  engine reads the same delta it always did.
- **The weapon wheel works**, because it was a touchscreen mechanic on the DS:
  `UpdateWeaponSelect` reads the pointer's absolute position. While WEAPON is
  held the pointer *is* the finger; the rest of the time it accumulates drag.
- **The wheel is picked with the same thumb that opened it**: press WEAPON,
  drag into the weapon, let go. This needs one exemption from a general rule
  --- a thumb that slides off a button releases it, which here closed the
  wheel before anything could be chosen --- so `_wheelPointer` skips that
  check exactly as `_fireAimPointer` already did for FIRE's aim drag, and
  `WeaponWheelPosition` hands the wheel that finger's position. Before this,
  picking took a second finger, and the finger people lifted to do it was the
  one on the stick: choosing a weapon meant standing still. The two-finger
  way still works and still wins when both are down, because a second finger
  put down while the wheel is open is an explicit choice.

Layout (`TouchControls`, drawn by `TouchOverlayView` with a Canvas rather than
in GL -- it is a dozen circles that change when touched):

| Control | Bind |
|---|---|
| Floating stick, left half | `MoveUp/Down/Left/Right` **and** `RollUp/Down/Left/Right`, eight-way. Both sets, because walking reads one and the morph ball the other |
| FIRE | `Shoot` on foot and `AltAttack` in the morph ball |
| JUMP | `Jump` **and** `Boost` -- one button on the DS, and the same key by default: jumping on foot is boosting in the ball |
| MORPH | `Morph` |
| VISOR / SCAN | opens the scan visor / scans the current target |
| MSSL | toggles Missile / Power Beam |
| WEAPON | `WeaponMenu`, held, with the pointer following the finger -- including that same finger once it drags off the button |
| ZOOM | `Zoom` |
| MENU | app pause menu |
| SCORE | DS map/status or multiplayer scoreboard |
| CHAT | opens multiplayer chat and the soft keyboard |
| CLIP | saves the rolling instant-replay buffer while it is active |
| Anywhere else on the right | aim |

Drag is converted to density-independent pixels before it becomes pointer
movement, so a swipe turns the same amount on any phone; `GameView.AimScale` is
the one number to change if it feels wrong, and the player's own mouse
sensitivity scales it after that.

### The controls step aside for a pad, and come back at a touch

There is no setting for it, and there was one — "Use a connected gamepad",
now gone. Being *connected* was never the question a player was asking: a pad
paired with the phone for something else is still paired while they play with
their thumbs. What `TouchControls.NotePadActivity` watches is the pad actually
being **used** — a button down, or a stick past the dead zone, which is what
`GamepadInput.InUse` answers — and the answer is reversed by the next finger
on the glass.

**Hiding the layout hides the layout, and nothing else.** The screen keeps
working the whole time the pad is in use: both are live at once, and that is
the point — a stick in one hand and a thumb on FIRE is a normal way to hold a
phone, and the weapon wheel cannot be reached from a pad at all (it reads an
absolute pointer position; see GAMEPAD.md), so a player who wants it has to be
able to touch the screen without first paying a press to wake the controls up.

Two things that are easy to get wrong here and are not:

- **The view stays; only the drawing stops.** `TouchOverlayView` is still the
  only thing receiving touches, so a touch while the controls are invisible is
  an ordinary one: it does what it landed on *and* brings the layout back.
  Making the view `Gone` would hand every touch to whatever is underneath and
  there would be nothing left to bring them back with.
- **Nothing is released when the layout goes away.** It used to be —
  everything held was let go, because a thumb resting on FIRE as the player
  picked the pad up would otherwise be held for ever, *the finger's release
  landing on a control that had stopped listening*. Nothing stops listening
  any more, so the release arrives and does its job; cancelling would instead
  cut off a player deliberately holding a button with one hand and a stick
  with the other.

Both of those were the right answer to "the player has put the pad down and
gone back to the glass", and the wrong one to "the player is using both",
which is the commoner case. Changed 2026-09-04 on that report.

**The one exception is a dialog box.** `PlayerDialog.CheckButtonPressed` reads
`Input.ClickX/Y` — the OK button is a *position*, because on the DS it was a
touch screen — and `GamepadInput` deliberately drives no pointer. So
`GameView.ApplyInput` sets `TouchControls.ForceVisible` from
`GameState.DialogPause` every frame, pad or no pad. Without it the story stops
at the first scan, with nothing on screen to press. A flag the loop keeps
setting rather than a one-shot "show yourself": a thumb still resting on the
stick would otherwise put the controls away again on the very next motion
event, and the box would flicker for as long as the pad was held.

Pad events reach the state through `MainActivity.DispatchKeyEvent` and
`DispatchGenericMotionEvent` rather than through `GameView` — a pad has to
reach the settings screen too, where no `GameView` exists, so that a button
can be rebound. `GameView`'s own handlers are kept as a fallback and never see
a pad event in this app.

### The player chooses which buttons exist

Settings → Controls → **On-screen buttons**: a master switch and one toggle per
button, applied in `ApplyLayoutLocked` as an `AND` over whatever the situation
had already decided. The state lives in core, in
`Mods/Input/TouchSettings.cs`, because the settings screen is shared code and
`TouchAction` is this head's own type; `TouchControls.SettingOf` maps between
them. It is saved in `controls.txt` with the rest of the controls.

It exists because of a report, and the report is the design rationale: *"the
on-screen touch commands interfere with the aiming and it's very easy to
accidentally hit those."* They do, and no layout fixes it -- aiming is a drag
anywhere on the right of the screen, the buttons have to be reachable, and a
drag that starts inside a circle presses the circle. A button turned off is
drawn nowhere **and takes no touch** (the hit test in `Down` already skipped
invisible buttons), so the glass it occupied becomes aim.

Turning every one of them off cannot strand a player: movement is the stick,
which appears wherever a thumb lands on the left; aiming is a drag; jump is a
double tap on the aiming side; boost in the ball is a flick. None of the four is
a button.

`MainActivity.ClosePauseMenu` calls `TouchControls.ReloadSettings`, since the
settings are reachable from the pause menu mid-match.

### A HUD rectangle beats a round button

`SetTapTargets` publishes rectangles the HUD has drawn -- currently the map
vote's ACCEPT and DENY -- and `PointerDownLocked` tests them **before** it
tests the eleven circles, not after. That ordering is the whole of why voting
did nothing on a phone: the vote panel was drawn in the top-left corner of the
HUD and MENU, SCORE and CHAT are drawn in the top-left corner of the glass, so
every tap meant for a ballot went to whichever circle was over it -- ACCEPT
opened the pause menu, DENY opened chat. A permanent control underneath a
rectangle that exists for thirty seconds and only while something is asking a
question with it has not been reached for.

The panel was also moved out from under them (`PlayerEntity.VoteLeft` /
`VoteTop`: x 52, y 42 on Android against x 6, y 8 elsewhere), because a
prompt legible only in the gaps between three circles is still a bad prompt
even once the taps land. The row's own extent is fixed in HUD units on any
window shape -- it is placed at 0.12 of the height with a radius of 0.06, so it
owns everything above y 37 -- and the right-hand pair, ZOOM and WEAPON, is what
caps the panel's width: WEAPON reaches x 216 and y 45 on a 4:3 tablet, where a
HUD unit across is a HUD unit down. The two answers are drawn at 1.35x there,
for the reason `EndScale` exists.

The catch-all is unchanged and still sits after the buttons: while the results
screen is up, everything the buttons did not take belongs to the HUD, because
there is no world to aim at.

### Scrolling the settings used to answer the rows

The launcher's screens are the desktop's, and the desktop's rows acted on the
press: `ToggleRow` flipped in `OnPointerPressed`, `ChoiceRow` stepped there,
`KeyRow` and `PadRow` began listening there, `SliderRow` took the value and the
pointer there. A mouse never notices -- it scrolls with the wheel and presses
only what it means. A finger has one gesture for both, and every scroll starts
as a press on whatever is under it, so dragging the settings page toggled,
cycled, re-bound and re-slid every row the drag began on.

The rule is now in one place, `Mods/Launcher/Gui/Tap.cs`, and it is the same
one Android itself uses: the press decides nothing, travel past eight points
gives the gesture up, and the release acts only if it lands inside the control.
It applies to a finger and a stylus and not to a mouse, so the desktop keeps
what it had. `MphRead -tapcheck` is the check; see
`.claude/launcher/LAUNCHER-DESIGN.md` for the rest, including why `SliderRow`
waits to see which way the finger went.

**Untested on a device**, like everything else in this head: the emulator here
cannot load a match, and what a real finger does to a real scroller is exactly
what a written-down gesture cannot prove.

### Controls no longer reset when the app closes

Two faults, both of them "the desktop does this somewhere this head never runs":

- **Nothing called `InputSettings.Load`.** It is called from
  `ModEntry.TryHandleHeadless`, which is the desktop's every-invocation entry
  point and is not on this head's path at all. `AndroidApp.BuildHome` calls it
  now, after `LauncherPrefs.Load` names the directory.
- **`controls.txt` was written to `AppContext.BaseDirectory`**, which an Android
  package does not own. Every write was refused, and `InputSettings.Save`
  swallows the exception deliberately (it runs from the pause menu, on the
  thread the menu is on). It follows `LauncherPrefs.Directory` now — the same
  property the head already points at the app's data directory for
  `launcher.txt` and `paths.txt`.

The symptom was "control settings still reset to default when quitting the
game", and the shape to remember is that **a swallowed write error and a load
that never runs look identical from the outside**.

### Spectating

The spectator's screen is the same dozen circles doing different jobs, and
`TouchControls.ApplyLayoutLocked` is the one place that decides which are on
and what they say — visibility used to be set by each of the three callers
reaching into the button list for the ones it knew about, which only works
while no two of them care about the same button, and spectating cares about
nine.

| Button | While spectating |
|---|---|
| FIRE → **NEXT** | next player. The desktop's left click |
| VISOR → **VIEW** | the map camera, or the player being watched. The desktop's Space |
| JUMP → **UP**, MORPH → **DOWN** | the free camera only, and only shown on it |
| Left stick | drives the free camera |
| Aim drag | turns the free camera |
| SCORE | the scoreboard, the one world-independent HUD control a spectator keeps |
| CLIP | save the rolling instant-replay buffer while it is active |
| everything else | gone |

**The free camera is not driven through binds**, and that is the part worth
knowing. It is the room viewer's Roam camera (`Scene.SetFreeCamera`), which
`Scene.OnKeyHeld` moves by reading W/A/S/D, Space and V **off the keyboard
directly** and `Scene.OnMouseMove` turns. So `GameView.Spectate` presses those
letters through `AndroidInput.ApplyKey` and hands the aim drag to
`OnMouseMove` — the same call the desktop's mouse makes, so the player's own
sensitivity and inversion apply. The letters are a copy of that method's table
and the one place this can go out of step with it.

Before this, the spectator branch pressed nothing and threw the aim delta
away: the free camera opened and could not be moved, turned or left.

The Android pause menu itself is on Avalonia's UI thread while the scene belongs
to `GameView`'s GL thread. Spectate/Rejoin therefore queue a command to that
render loop; `SpectatorMode.Start` (including lazy HUD setup) and the resulting
`Scene.SetFreeCamera` call execute only there. The loop also consumes camera
requests created later by NEXT/VIEW touch input. This avoids touching scene/HUD
state from the UI thread and keeps Android's camera state in step with the
desktop path.

`SpectatorMode.Rejoin` never puts a score *up*. A score below zero is a
penalty — a suicide costs a point — and clearing it by spectating for a second
would make the pause menu the cheapest way out of one, so only a positive
score goes back to zero. That is shared, not Android's.

## Playing online

`AndroidMatch.Build` is the head's half of `MatchStart.Launch`, and it was
missing the networked half of it: it loaded `plan.RoomKey` and filled the slots
with local players whatever the plan said. An online plan carries an **empty
room key on purpose** — the front screen joins before it knows what is running —
so joining a server failed with *"No room with this name is known"*, which is
what an empty string is. It now defers to `NetLaunch.ServerRoom()` for the map
*and the mode*, builds the slots with `NetLaunch.BuildPlayers`, and loads with
`NetLaunch.RoomPlayerCount` so every client lays out the same world.

## Game files

The package's own directory is read-only, and the extracted game files are
hundreds of megabytes the player has to copy onto the device themselves. So both
`LauncherPrefs.Directory` and `GameFiles.Root` (added for this) point at
`GetExternalFilesDir(null)` -- the directory reachable over USB under
`Android/data/com.projectprime.game/files` without the app asking for a storage
permission -- and that is made the working directory, because upstream's `Paths`
reads `paths.txt` relative to it.

Extract on a desktop, then copy `paths.txt` and the extracted folders there. The
front screen says so when they are missing and has a "Check for game files"
entry, since they arrive while it is already up.

## Sound

Both halves play, and neither of them the way the desktop does it.

- **Music** goes through SoundFlow, which ships `libminiaudio.so` for Android;
  it is in the APK.
- **SFX** used to be silent, and the reason is worth keeping: they go through
  OpenAL, OpenTK ships the bindings only, and `Silk.NET.OpenAL.Soft.Native` —
  where the desktop gets its native — has runtimes for Windows, Linux and macOS
  and none for a phone. `Sfx.Load` caught the missing library, installed the
  silent stub, and the game played its music with nothing else. That is exactly
  the split a Windows machine with no `oalinst.exe` shows, and it was fixed
  there the same way it has now been fixed here: give the name something to
  resolve to.

  **The renderer's trick, again.** Two using aliases in the csproj point `AL`
  and `ALC` at `Mods/Sound/AlEs.cs`, for every file in the compilation, and not
  one of the fifty-odd call sites in `Sound/Sfx.cs` and `Formats/Movie.cs`
  changed. Behind the name is `Mods/Sound/SfxMixer.cs`, which does what OpenAL
  was doing — buffers, voices, per-source gain and pitch, SOFT loop points,
  buffer queues, the linear-clamped distance model and stereo placement from
  the listener's orientation — and hands one block of stereo at a time to
  miniaudio, on the **music's own playback device** (`MusicPlayer.Engine` and
  `MusicPlayer.PlaybackDevice` were added for this). A second device would be a
  second audio callback, a second buffer of latency and one of the two ducked.

  Not emulated, because the engine never asks: HRTF, doppler, velocity, effects.

## Map previews

Rendered on the device from the player's own extracted files, and nothing about
them is shown. Three things used to be wrong and all three are the same
mistake — doing it on a `GLSurfaceView`:

- `OffscreenGl.cs` creates an EGL **pbuffer** context on an ordinary thread, so
  there is no view, nothing on screen, and no forced landscape. The scene never
  draws to the pbuffer anyway: `Scene.ReadSceneTarget` reads the renderer's own
  framebuffer object, and EGL simply will not make a context current without a
  surface.
- **Several at once, in processes of their own.** A scene is not a local thing —
  the entity lists, the player roster and the game state are static, so two
  scenes in one process would be one world with two cameras. The desktop
  answers that with ten worker processes and so does this: `PreviewService.cs`
  declares six services with `android:process`, each handed a share of the room
  list round-robin, each dropping a marker file when it is done. The launcher
  watches the cache directory rather than talking to them, which needs no IPC
  and survives a worker being killed for memory. Whatever they could not
  produce is rendered in-process afterwards, so a device that refuses to start
  services still gets its pictures.
- **640x360, not 1600x900.** Every pixel is paid for four times over — fill
  rate, a `glReadPixels` stall, a managed pixel loop in `AndroidPng`, and a PNG
  encode — and the launcher shows them in a 248-point band.

### ⚠️ The settings file follows `ChooseRoot`, and it is not always the SD card

`GameState`'s save folder is the relative path `Savedata`, resolved against
the working directory, which `CustomizeAppBuilder` sets to whatever
`ChooseRoot` picked. Both candidate roots can end up holding a `paths.txt`
from earlier runs, and the one that wins is then the only one the game reads.
An emulator here chose the **internal** directory
(`/data/user/0/com.projectprime.game/files`) while a hand-written
`settings.json` sat unread in the external one, and three rounds of "the ES
renderer draws cel shading fine" were measured with cel shading off.

`[android] N bundled map files -> <path>` names the root it chose, in the
first seconds of every launch. Read it before trusting anything pushed with
`adb push`, and push to that path.

### ⚠️ A preview run owns the world, so a match takes it away

`MainActivity.StartMatch` used to *refuse* while an in-process preview run was
going, toast "Still rendering map previews; try again in a moment" and call
`AndroidApp.Home.Reset()`. That is what **"je clic et rien ne se passe"** on
the offline face was, and it is worse than it reads: a first launch renders
every map's picture without being asked (`StartScreen.CatchUpPreviews`), which
is minutes on a phone, so the whole of a player's first session met it —
`Reset()` threw the screen stack away, lost the map they had picked, and put
them back on the front screen with nothing to read but a toast behind an
immersive window.

The picture of a map nobody has asked for yet does not outrank playing. The run
is now asked to stop between rooms (`_stopPreviews`, read by
`PreviewRun.Render`'s `cancelled` callback, which already existed and was never
passed one), and `WaitForSteadyWindow` holds the start — with its own deadlines
paused — until the room in flight is finished with, which is a second or two.
Whatever it did not get to is rendered by the next launch or from the setup
screen's **Render map previews** entry. `RenderHere` also refuses to *begin*
while `_pending != null`, so a run cannot be born into the gap between a start
being asked for and the `GameView` existing.

### ⚠️ `ThumbnailMode` is process-wide

`Mods/ThumbnailMode.cs` suppresses the HUD and mutes the sound. The desktop
never had to leave it: every capture is a worker process that exits when its
picture is written. Android renders previews in the app's own process, so
entering and never leaving meant **the next match had no HUD and no sound** —
which is exactly how it was reported. `ThumbnailMode.Exit()` exists for that,
`PreviewRun` calls it in a `finally`, and `MainActivity.StartMatch` refuses
while an in-process run is going.


## The moving backdrop

The front screen's photograph breathes on the desktop: a small field of
domain-warped noise laid over it in `overlay` at 62 per cent, which is the one
thing on that screen that is never still. It did not breathe here, and the
reason is structural — `Mods/Render/LauncherPhoto.cs` and `LauncherNoise.cs`
are GL's, they are **excluded from this csproj**, and there is no GL window
under the launcher on this head at all.

So the field moved into `Mods/Render/NoiseField.cs`, which is arithmetic and
nothing else and is compiled everywhere, and
`Mods/Launcher/Gui/MovingBackdrop.cs` draws it through Skia. The blend is
Skia's own `BitmapBlendingMode.Overlay`, which against an opaque destination
works out to exactly the `mix(b, over, strength)` the desktop's fragment shader
ends on — the same picture, arrived at the same way. The two must never grow
their own copies of the arithmetic; that is why the field is a third file
rather than a method on either of them.

The layer has to sit **between** the photograph and the washes, as it does on
the desktop, so `UiLayout.Backdrop` lays the backdrop down in three pieces
where nothing below is drawing it (`BackdropPart.Photo`, the layer, then
`BackdropPart.Washes`). Both bakes are cached bitmaps and a blit apiece; only
the middle layer costs anything a frame, and it stops the moment it leaves the
tree.

Three things about drawing a bitmap from a control on this head, each of which
looked like the layer simply not existing:

- **`Bgra8888` is not drawn.** An `Avalonia.Media.Imaging.Bitmap` built from
  raw pixels as `PixelFormat.Bgra8888` renders as nothing at all through
  Avalonia's GL ES backend — no exception, no log line, no pixels. BGRA needs
  `GL_EXT_texture_format_BGRA8888` and is not there. `Rgba8888` works and is
  what every GLES driver has. A PNG loaded from `avares://` draws either way,
  which is what makes this so slow to find: `DrawImage` is clearly working.
- **The bitmap is cut outside the render pass.** A control's `Render` records
  render data; a bitmap made during that pass is not one the compositor picks
  up. `BakedBackdrop` bakes from its arrange for the same reason and this does
  too.
- **A control whose first render draws nothing gets no second render.**
  `InvalidateVisual` on it is then a no-op for ever, which reads as a layer
  that steps happily in the debug log and never moves on the glass. Cutting
  the first bitmap in `ArrangeOverride` — before the first render — is what
  makes it draw at all.

And one about how many of them are running. **Leaving the tree is not the only
way a backdrop stops being looked at.** Every screen builds its own, and
`StartScreen.Push` does not detach the screen underneath — it covers it with an
opaque photograph. So a player on Play or Settings, which are the two pages
anybody scrolls, had *two* of these filling a noise field and blending a
full-window layer thirty times a second, one of them behind the other.
`MovingBackdrop` now keeps a list of the live ones and only the last to arrive
steps; the rest hold the frame they had and pick the loop up when they are
uncovered.

## Animations step on the compositor's clock, not on the dispatcher

`Deck.NextFrame` is what every self-driving animation in `Mods/Launcher/Gui`
asks for its next step through — the button pop, the idle bob, the sheet's
spring, a tile settling. On the desktop it goes to `UiSurface.RequestFrame`,
which is the game's own frame. Here it was a bare
`Dispatcher.UIThread.Post(step, DispatcherPriority.Render)`, and a dispatcher
post is not a frame: it runs as soon as the queue reaches that priority, so a
step that asks for the next one — and the idle bob asks for ever — goes round
as fast as the dispatcher can turn, with the touch events a drag is made of
queued behind it.

It takes `TopLevel.RequestAnimationFrame` now. That is throttled on the
compositor (`MediaContext.CommitCompositorsWithThrottling`), and its callbacks
are drained from a queue that has already been swapped out — so re-asking from
inside one lands on the frame *after*, which is what the `_framePending` flag
on each control was trying to arrange by hand. It needs the asking control to
find its top level, which is why the method takes one; the shell ignores it,
having exactly one surface.

## Custom maps

They reach the phone as **`.ppmap` bundles** and could not reach it any other
way: `AndroidAsset` was globbed as `maps\*.json` and `AssetManager.List` does
not recurse, so a map that keeps its level in a folder of its own -- which is
how every converted map is worked on -- matched neither side and the phone
listed the 27 cartridge rooms. A bundle is one file at the top of `maps/`, and
`AndroidMaps.Install` unpacks it into the external directory beside the
extracted game files, where a player can also drop one of their own over USB.
The room binaries are still built on the device, from that player's own
textures; `AndroidMaps.EnsureBuilt` is what runs the builder before a match.

## Demos

Watching a recorded match works here too, and needed three things the desktop
never had to think about:

- **The system file picker cannot reach this app's own recordings at all.**
  Since Android 11 `Android/data` and `Android/obb` are excluded from the
  Storage Access Framework: the picker cannot be pointed at the folder
  `DemoRecorder` writes to, and a player cannot navigate to it either. None of
  that stops *this* app reading the folder -- it owns it and needs no
  permission for it, which is the whole of the confusion. So the Demos entry
  lists the folder itself (`DemoLibrary` and `DemoPickerView`) and the system
  picker is one entry inside that list, for a demo that came from somewhere
  else. `SuggestedStartLocation` is still set for the desktop, where it is
  honoured; on Android it is ignored, and it used to be handed a *relative*
  path, which no storage provider anywhere could resolve.
- **The file picker cannot filter by pattern.** Android filters by MIME type
  and a `.ppdemo` has none, so `FileTypeFilter` is skipped here exactly as it
  is for the `.nds` picker -- setting one produces a picker in which every file
  is refused.
- **There is no path behind what it hands back.** A `content://` document has
  no local path, and `DemoPlayback.Join` takes one, so `HomeView.ChooseDemo`
  copies the document into `GameFiles.Root` and plays it from there. Kept
  rather than deleted afterwards, unlike the cartridge copy: the reader holds
  the file for the whole session, and the next demo overwrites it.

### Where a recording lands

`DemoRecorder.Start` writes to `Paths.Combine(Paths.Export, "_demos", name)`,
and `Export` is **empty** in every `paths.txt` a desktop extraction produces.
An empty first element makes that a *relative* path, resolved against the
working directory -- which `CustomizeAppBuilder` set to whatever `ChooseRoot`
picked. So on a phone a recording is:

```
<root>/_demos/<room>_<yyyy-MM-dd_HH-mm-ss>.ppdemo
```

with `<root>` normally the external files directory, reachable over USB at
`Android/data/com.projectprime.game/files/_demos/`. The
`[android] N bundled map files -> <path>` line names the root that was
actually chosen; on a device that fell back to internal storage the folder is
under `/data/user/0/...` and only `adb` can reach it.

That path is worth having: no file manager on a modern Android can open the
folder, so copying a recording off the device means USB/MTP or `adb`. It is
written on screen in exactly one place -- the Demos list, when there is
nothing in it yet. The "saved to..." line at the end of a recording is still a
`Console.WriteLine`, which is logcat here and invisible to a player.

`DemoLibrary.List` reads the room and the moment back out of the file *name*
rather than opening anything: the name already carries both, and reading a
header out of every file to learn what the name says would turn a directory
listing into a disk full of seeks. The timestamp is a fixed nineteen
characters at the end, which is what makes it safe to split -- a room name can
contain underscores of its own, since `SanitizeFileName` puts one in for every
character a file name cannot hold.

`AndroidMatch.BuildDemo` is the rest -- the half of `MatchStart.LaunchDemo`
that is not a window: join, read the room out of the recording's own
MatchState, build the players with `localSlot: -1`, load the room.
`MainActivity.EndMatch` calls `DemoPlayback.Stop`, which `NetSession.Stop`
does not do for it: a demo feeds the session from a file rather than a socket.

## Building

```bash
export PATH="$HOME/.dotnet:$PATH"
export JAVA_HOME=$HOME/jdk17            # a JDK 17; the workload does not bring one
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1   # or install libicu
dotnet workload install android
dotnet build src/MphRead.Android/MphRead.Android.csproj -c Debug \
  -p:AndroidSdkDirectory=$HOME/android-sdk \
  -p:JavaSdkDirectory=$HOME/jdk17 \
  -p:EmbedAssembliesIntoApk=true
```

`JavaSdkDirectory` as well as `JAVA_HOME`: the Android targets look the JDK up
themselves and fail with *XA5300: The Java SDK directory could not be found*
when only the variable is set. `EmbedAssembliesIntoApk` is the one below under
"Three traps" -- a Debug APK without it installs and is hollow.

The SDK needs `platforms;android-35` and `build-tools;35.0.0` to match the
`net10.0-android36.0` target; `sdkmanager --sdk_root=$HOME/android-sdk` installs
them. `EnableAvaloniaXamlCompilation=false` is deliberate and explained in the
csproj.

The APK lands in `bin/Debug/net10.0-android36.0/com.projectprime.game-Signed.apk`
(~20 MB; a Release publish is ~45 MB, being every ABI with the trimmer run).
`adb install -r` it.

Nobody has to do any of that to get one, though: `build.yml` has an `android`
job on every push, and its **ProjectPrime-android** artifact holds the release
APK and an INSTALL.txt. `release.yml` puts `ProjectPrime-<tag>-android.apk` in a
tagged release. Both are signed with the SDK's debug key -- enough to install,
not enough for a store.

The untrimmed Debug APK is no longer built or collected: it existed to tell a
crash apart from the trimmer having removed something, which is worth building
when that question is being asked and not worth handing out beside the real one
every push. Build it by hand when you need it -- and with
`-p:EmbedAssembliesIntoApk=true`, or it holds no managed code.

**The trimmer is why `Properties/TrimmerRoots.xml` exists.** A Release publish
really does run ILLink over this app, and every use of `KeyboardState` and
`MouseState` is through a `MethodInfo`, so without the descriptor the touch
controls work in Debug and throw on the first frame of a release APK. Checked,
not assumed: the members survive in
`obj/Release/.../android-arm64/linked/OpenTK.Windowing.GraphicsLibraryFramework.dll`.

## Three traps that cost a release each

All three were found by running the APK, and none is visible from a build log.

**The activity's theme must be an AppCompat descendant.** Avalonia's
`AvaloniaMainActivity` is an AndroidX `AppCompatActivity`, and AppCompat throws
`IllegalStateException: You need to use a Theme.AppCompat theme (or descendant)
with this activity` out of `onCreate` under anything else. The head shipped with
`@android:style/Theme.Material.NoActionBar`, a framework theme that looks right
and can never work: the app died before a line of this project's code ran, every
time, on every device. `Resources/values/styles.xml` holds the theme now.

**A Debug APK built by `dotnet build` contains no managed code.** Debug defaults
`EmbedAssembliesIntoApk` to false and expects `dotnet build -t:Run` to push the
assemblies over adb afterwards. Install that APK by hand and it aborts at
startup with *"No assemblies found in .../.__override__/<abi>. Assuming this is
part of Fast Deployment."* The build succeeds, the APK installs, and it is
hollow. `-p:EmbedAssembliesIntoApk=true` is what makes a debug APK someone can
be handed; it is ~110 MB rather than 20.

**Nothing scaled the launcher's screens here, and the phone is the one head
that cannot do without it.** Every screen in `Mods/Launcher/Gui` is authored
for a box near 960x600 of its own points; a phone in landscape hands Avalonia
about **830x390** (1080x2280 pixels at 2.75x density), and the well alone is
820 points wide. So the screens were laid out about three times too large for
the view they were in: the front screen's words ran off both edges, the marks
at the foot were below the glass, and the server browser -- which was laying
out perfectly correctly -- did it off the side of the display, which is what
"the servers are invisible" was. The desktop never showed any of this because
`UiSurface` has scaled them since it was written; the rule it used lives in
`UiLayout.Factor` now, and `UiScaleHost` is the twenty lines that apply it on
this head. Two heads with two curves is the failure that was already on
record, one screen at a time, so there is one curve.

**The scale now has two constraints: readable and actually visible.** Android's
layout point is dp while the screens were authored around desktop-sized logical
boxes, so `UiScaleHost.FactorFor` still asks the shared physical/readability
curve in the converted unit. That answer is the preferred size, not permission
to hand the child a box too small to contain its own responsive layout. The host
also computes the factor that gives the child at least
`UiLayout.MinBoxWidth x UiLayout.MinBoxHeight` and takes the smaller answer,
with the shared 0.6 floor as a guard against transient/inset resizes.

That second constraint fixes the modern hub on ordinary phone landscapes.
At 1.0 a roughly 830x390 view made screens such as Play enter their compact
stacked form, then placed whole cards and the footer below the glass. A typical
phone now lands around 0.65-0.7: the child is measured at roughly 1200-1300x600,
so the same responsive code sees a real landscape box and every menu remains in
frame. Tablets and roomy views keep the larger readability answer. The
off-screen in-match Avalonia surface calls the same `FactorFor`, so pause/results
screens do not grow a second Android-specific scaling rule.

**Selection is drawn, not inferred.** The rows worked their highlight out from
hover and focus, which is right on a desktop and empty on a touchscreen: a
finger hovers nothing and a tap does not reliably leave focus behind, so the
selected server was drawn like every other row while the address box and JOIN
were all about it. `UiList` pushes `IsSelected` onto the rows instead.

**The dedicated half of Create server is not offered here.** The package holds
no server binary and none is published for the platform, an app may not start
a second process that outlives it, a phone is behind carrier NAT, and the
process would be killed the moment the player switched away.
`CreateServerScreen.CanRunHere` is false on Android and the Server type row is
not drawn at all -- a choice with one answer is not a choice -- with the two
messages that used to say "pick Dedicated server" saying something true there
instead.

**The activity asks for landscape, from the icon onwards.** It used to ask
only for a match, so the program opened portrait, turned sideways to play and
turned back at the end -- two rotations a match that nobody asked for, each of
them a full re-measure of the Avalonia tree and a re-bake of the backdrop,
since the activity handles its own configuration changes. It is also the shape
the screens want: portrait gives the scale host the least of both axes and
lands on the 0.6 clamp with the box no bigger for it.

**Check a glyph against Roboto-Bold's cmap before drawing it.** Avalonia here
does not reach Android's own fallback chain the way the desktop reaches the
system's, so a character the shipped font lacks is an empty box rather than a
substitute. `UiTabs` drew its strip arrows as U+25C4/U+25BA, which Roboto-Bold
does not have: two tofu boxes either side of every tab strip on the phone and
a correct arrow on the desktop. They are U+2039/U+203A now, which is in the
font we ship. The rows' arrows never had the problem because they are geometry
rather than text (`Rows.Arrow`), which is the other way out. Still unchecked on
a device: U+2197 on Settings -> Player's *Share logs* and U+2615 on Credits.

**An asset the shared sources name is not in this package unless this csproj
says so, and a missing *font* is fatal.** The two csprojs carry their own
`AvaloniaResource` lists -- the desktop's names files under `Assets\`, this one
names the same files under `..\MphRead\Assets\` with a `Link` that puts them
back at the same path -- so a new asset added for the launcher lands on the
desktop and nowhere else, and the head that does not have it is the head nobody
is looking at. `GuiTheme`'s two faces arrived that way with the minimal-UI
launcher, and Avalonia does **not** fall back for a font family it cannot
resolve: it throws `InvalidOperationException: Could not create glyphTypeface.
Font family: Roboto (key: avares://ProjectPrime/Assets/Fonts/Roboto-Bold.ttf)`
out of the *measure pass*, which is an uncaught exception inside
`AvaloniaActivity.GlobalLayoutListener.OnGlobalLayout` and kills the process
before the front screen draws a pixel. The pictures next to them are guarded
(`UiLayout.Load` catches and returns null, so a build missing one gets no
photograph rather than no launcher); the fonts are not. The check when anything
under `src/MphRead/Assets/` is added: does `MphRead.Android.csproj` name it too.

**The synchronous `HttpClient.Send` does not exist here.** .NET for Android
defaults `UseNativeHttpHandler` to true, which puts
`Xamarin.Android.Net.AndroidMessageHandler` behind every `HttpClient` -- and
that handler implements `SendAsync` and nothing else, so the synchronous call
falls through to `HttpMessageHandler.Send`, whose whole body is a throw. The
update check used it. On a phone it therefore never reached GitHub at all: it
caught the `NotSupportedException`, wrote "could not reach GitHub" into
`UpdateCheck.LastReason`, and the front screen -- which only shows the corner
when a release was *found* -- said nothing, so a phone sat on v0.3.0 with
v0.3.1 published and no way to tell that apart from being up to date. The
desktop was fine throughout, because `SocketsHttpHandler` implements the
synchronous path. `Mods/Update/SyncHttp.cs` is the one-line answer, and the
shape to watch for is any synchronous `HttpClient` or `HttpContent` call in the
shared sources: it compiles on this head and throws on the device.

## Text input

Nothing on this head read a key event until chat needed one. `GameView` is
focusable now and handles `OnKeyDown`, which covers a keyboard plugged in,
paired over Bluetooth, or the emulator's -- and, through
`OnCreateInputConnection`, the soft keyboard the CHAT button asks for.

Three things that are not obvious, all in `GameView`:

- **`InputTypes.Null`** on the `EditorInfo` is what makes an IME send plain
  key events instead of `commitText`. Without it the view has to keep an
  editable buffer in step with `ChatBox`'s, which is two copies of one string.
- **`NoFullscreen | NoExtractUi`**, or the IME covers the match with its own
  full-screen text box in landscape.
- **A key event carries its own character** here, where GLFW raises a key
  callback and then a character callback for one press. So the desktop's
  "swallow the opening character" step must be turned *off* on this path
  (`ChatBox.Open(swallowOpeningChar: false)`), or the first real letter is
  eaten instead.

Focus and touch are separate: the touch overlay sits above `GameView` and takes
every touch, and never takes focus, so the view keeps the keys.

Full account of what chat is and what the server does with it:
`.claude/multiplayer/NETWORK-CHAT.md`.

## Testing it here

An emulator runs this without a device, and without KVM -- which WSL does not
grant unless the user is in the `kvm` group:

```bash
sdkmanager --sdk_root=$HOME/android-sdk "emulator" "system-images;android-30;default;x86_64"
avdmanager create avd -n fruity -k "system-images;android-30;default;x86_64" -d pixel_4
$ANDROID_HOME/emulator/emulator -avd fruity -no-window -no-audio -no-boot-anim \
  -gpu swiftshader_indirect -accel off -memory 4096 -cores 4
```

`-accel off` is software CPU emulation: it boots in about five minutes and the
system UI shows "isn't responding" dialogs of its own, which are the emulator
and not this app.

Four things that cost time here and are not obvious:

- **`adb push` into the app's external directory is refused** on API 30 --
  scoped storage, `Permission denied`, even for a directory the app itself
  writes. `adb root` first (the `default` system image allows it; a
  `google_apis` one does not), and it works.
- **`adb push --sync`** for the 100 MB of extracted files. A push that dies
  half way otherwise starts again from nothing.
- **`pm list packages` finding the package proves nothing** -- an install from
  a previous session leaves it there, so a wait loop on that returns
  immediately and the thing under test is last week's APK. Wait on `adb
  install` actually exiting, and check `dumpsys package ... lastUpdateTime`.
- **A 112 MB debug APK takes several minutes to install** on a software CPU,
  and `cmd: Can't find service: package` means the system is still coming up
  rather than that anything is wrong. `sys.boot_completed` is never set on
  this image; `init.svc.bootanim` going `stopped` is the signal, and the
  package service is up a while after that. `adb install -r`, `adb shell am start -n
com.projectprime.game/crc64e2a07749a868b9fd.MainActivity`, and `adb shell
screencap` are enough to see the front screen. With KVM it would be seconds
rather than minutes.

**`adb shell input tap` does not reach Avalonia at all**, and this is the one
that will cost an hour twice. The event is dispatched (it shows in
`dumpsys input`'s `RecentQueue`, the activity's `DispatchTouchEvent` sees it,
`AvaloniaView` returns handled) and no Avalonia pointer event is ever raised
from it — not on a control, not on the `TopLevel`. Every other app on the
device answers those taps, so it reads exactly like the launcher having lost
its input. Drive it with a real hardware touch instead:

```sh
# /data/local/tmp/tap.sh DEV X Y, with X and Y in the panel's own 0..32767
sendevent $D 3 47 0;  sendevent $D 3 57 100; sendevent $D 1 330 1
sendevent $D 3 53 $X; sendevent $D 3 54 $Y; sendevent $D 3 58 500
sendevent $D 3 48 5;  sendevent $D 0 0 0
sendevent $D 3 57 -1; sendevent $D 1 330 0; sendevent $D 0 0 0
```

`/dev/input/event2` is the emulator's first multi-touch device. The panel is
portrait and the app is landscape, so a landscape display point `(dx, dy)` on a
2280x1080 view of a 1080x2280 panel is `X = (1080 - dy)/1080 * 32767`,
`Y = dx/2280 * 32767`. The keyboard *does* reach Avalonia
(`input keyevent KEYCODE_TAB`, `KEYCODE_ENTER`, the arrows), which is enough to
walk the screens when a tap is not needed.

**Never leave a `wm size` override on.** Setting the display to something other
than the panel (`adb shell wm size 1080x2400`) breaks touch coordinate mapping
for the *whole system*: nothing responds to a tap anywhere, including the home
screen, and it looks precisely like the app under test being dead. `adb shell
wm size reset` before testing input.

**A fresh install is `pm clear` plus the files back.** Clearing wipes
`paths.txt` and the extracted game files with it, so:

```sh
adb root
adb shell pm clear com.projectprime.game
adb push --sync ~/mph-test/files/AMHP1 /data/data/com.projectprime.game/files/files/
adb push paths.txt /data/data/com.projectprime.game/files/paths.txt
adb shell chown -R u0_a140:u0_a140 /data/data/com.projectprime.game/files
adb shell chmod -R 777 /data/data/com.projectprime.game/files
```

Clearing and *not* putting them back is the other case worth having: it is the
first-run setup screen, which has its own history (see LAUNCHER-DESIGN.md on a
screen pushed from an attach).

SwiftShader implements GL ES 3.0, so it is a real check that the shaders
compile, link and run. It is **not** a check of any picture, and there is now a
number for how far off it is: the cel outline pass measures a flat surface's
depth kink at 0.004-0.009 under llvmpipe on the desktop and at 235-256 here,
against a threshold of 1.1. The large-scale depth field is right -- the room's
shape is plainly visible in a `fract(d * 64.0)` probe -- and the per-pixel
values are not, which is the same defect that streaks its colour. Anything
that reads neighbouring pixels and compares them will be nonsense on this
renderer.

## What has run and what has not

A room has now been loaded on the emulator: front screen, offline match, the
touch controls and the HUD, from a cold start in portrait -- repeatedly, and
including a second match started straight after backing out of the first
(which is two rotations in quick succession) and a double tap on START. The
portrait launch could **not** be made to fail here, on API 30 at 1080x2280,
with auto-rotate both on and off; the hardening above comes from reading the
wait rather than from reproducing a failure in it, and a device still failing
should be asked for its `[android]` lines. What the emulator
cannot answer is what any of it *looks* like -- SwiftShader draws this scene
with vertical streaks through every surface, with cel shading on and off alike,
so the pictures are only good for "it ran". The desktop build is where the
rendering is judged.

See `.claude/KNOWN-GAPS.md`. The first run on a new device should still be
watched for, in this order:

1. `[gles] shader ... failed to compile` in logcat. The compile status is only
   read under a debugger upstream, so `GlEs.CompileShader` logs it.
2. `Scene.FramebufferStatus` -- the offscreen target is an unsized `GL_RGB`
   colour texture with a `DEPTH24_STENCIL8` renderbuffer. RGB8 is
   colour-renderable in ES 3.0, but a driver that disagrees makes the whole
   picture black while every other signal looks fine.
3. Geometry that is inside out or missing: that is the strip/quad/fan winding in
   `GlEs.EmitIndices`, which is the one piece of this with no test behind it.
4. Untextured or wrongly-coloured meshes: that is the `imm_color`/`a_color_set`
   path, i.e. a mesh whose display list never set a colour of its own.
