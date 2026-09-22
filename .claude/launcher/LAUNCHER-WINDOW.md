# One window — the launcher inside the game

The program opens **one** window, for its whole life. The front screen, the
map picker, the settings, the pause menu and the match are all drawn into it.

This is what replaced: an Avalonia launcher window that closed when a match
started, a GL window created per match and destroyed when it ended, and — over
a running match — a borderless topmost Avalonia window sized to the game's
client rectangle and moved onto it again every frame, with a second one the
same size for the settings. Three arrangements of the same screens, and each
of them visibly a different program from the one behind it.

## How it works

| Piece | What it does |
|---|---|
| `Mods/Launcher/Gui/Shell.cs` | the loop: opens the window, shows the front screen, builds a match into the window when a plan is chosen, unloads it when the player leaves, closes the window when they quit |
| `Mods/Launcher/Gui/UiSurface.cs` | one Avalonia top-level, never shown to the window manager. Renders a screen into a buffer of pixels and feeds it the input GLFW reports |
| `Mods/Render/UiOverlay.cs` | the GL half: uploads that buffer and draws it as one quad over the frame |
| `Mods/Launcher/Gui/InGameMenu.cs` | the pause menu's stack (menu → settings → map vote), over a scrim, in the same surface |

The toolkit runs on Avalonia's **headless** backend with Skia
(`GuiLauncher.EnsureSetup`). Headless is a unit-testing backend by origin —
`UseHeadlessDrawing = false` is what turns the real renderer back on — but
what it gives here is the thing no desktop backend will: a real layout, a real
Skia render, and the result handed back as pixels instead of put on a screen.
`AvaloniaHeadlessPlatform.ForceRenderTimerTick()` draws a frame and
`TopLevel.GetLastRenderedFrame()` hands it over.

Input goes in the same way, through `HeadlessWindowExtensions`: `MouseMove`,
`MouseDown`/`MouseUp`, `MouseWheel`, `KeyPress`/`KeyRelease` and
`KeyTextInput`, fed from `RenderWindow`'s own GLFW handlers. `Shell.UiVisible`
is what decides whether a frame's input belongs to a screen or to the match —
while a screen is up it takes all of it, which is what a window on top used to
get from the window manager.

The scene lives and dies inside the window rather than with it:
`RenderWindow.BeginScene` / `LoadScene` / `EndScene`, with
`MatchStart.Begin(window, settings, plan)` building the match into a window
that already exists. `RenderWindow.Scene` is still non-nullable for every
other caller — the map sweeps, the network harness, the thumbnail runs all
build their scene in the constructor as they always did; `HasScene` is the
question only the launcher has to ask.

## Traps, all of them paid for

- **Texture names belong to the GL context.** Scenes and launcher overlays now
  allocate names with `GL.GenTexture`. Each scene tracks and releases its own
  model/HUD/movie textures. Shared model display lists use reference-counted
  leases so closing a replica does not erase a foreground or sibling scene.
  The former per-scene counters collided when two worlds shared one context;
  reserved high-numbered UI names are no longer needed.
- **The fixed-function state belongs to whoever touched it last.** The overlay
  draws with `UseProgram(0)`, and the frame it draws over has left the active
  texture unit on 1 (the visor mask), a current colour that is not white, and
  matrices belonging to a world. Unit 0 explicitly, unit 1 unbound, `TexEnv`
  `Replace`, both matrices pushed and loaded identity.
- **Premultiplied alpha.** Avalonia renders premultiplied, so the blend is
  `One, OneMinusSrcAlpha`. `SrcAlpha` darkens every glyph edge against the
  match behind the pause menu.
- **`Size` is not the client area, and the engine drew with it.** OpenTK's
  `NativeWindow.Size` is the *outer* window — frame and title bar included
  (1356×865 for a 1280×768 client). The scene was built and resized against
  it, so it rendered a picture bigger than the window it was drawn into, and
  GL's origin being the bottom-left corner meant the difference came off the
  **top**: "the top of the picture is cropped in windowed mode, by about the
  height of the title bar", and only windowed, since fullscreen has no
  decorations. Everything drawn now measures in `FramebufferSize`
  (`RenderWindow.PixelSize`), the pointer in `ClientSize` — which is what GLFW
  reports it in — and `PointerPixels` converts between them, which is the same
  conversion that keeps a click landing on the right control at 150% display
  scaling.
- **The overlay sets its own viewport.** It is drawn after a scene that left
  the viewport wherever its own idea of the window's size put it. A screen
  drawn into a viewport that is not the framebuffer is a menu hanging off the
  edge of the window, which is what F11 then Escape produced.
- **Mutating a transform is not a layout change.** The screens are scaled by a
  `ScaleTransform` on a `LayoutTransformControl`, and the control watches the
  `LayoutTransform` *property*: setting `ScaleX`/`ScaleY` on the object it
  already holds changes nothing it can see. The content therefore kept the
  size it was last laid out at while the frame around it grew — a front screen
  in the corner of an enlarged window with black around it, and a pause menu
  covering a quarter of the screen after F11. A resize assigns a new
  transform.
- **The scale is applied once, inside the top-level.** The
  `LayoutTransformControl` that scales the screens lives *inside* the Avalonia
  top-level, so the top-level's coordinate space is the window's pixels and
  the toolkit applies the transform itself on the way down to a control.
  Dividing the incoming pointer by the scale as well meant that at any scale
  but 1 -- which is to say as soon as the window was made bigger -- every
  click landed at a fraction of where it was pressed and nothing could be
  pressed at all. It survived the first round of checks because `ClickOn`, the
  only automated click, multiplied by the same factor going in: the two errors
  cancelled and the check passed at every size. `-shellshot` now clicks at
  1.5x as well as at 1.
- **One source of truth for the size, and it is the window's.** The screen
  inside the host is given no `Width`/`Height` of its own: the host stretches
  and `LayoutTransformControl` measures its child through the inverse of the
  scale, so the screen is measured at exactly (window ÷ scale) and arranged to
  fill. Sizing the view by hand instead meant two numbers that could disagree
  with the window, and they did — the surface was only resized *while a screen
  was up*, so a window made bigger during a match told it nothing and Escape
  built the pause menu against the size the window had when the match started.
  `Shell.TickUi` now takes the window's size and the display's scaling every
  frame, screen up or not.
- **The UI is scaled, not stretched.** `UiSurface.Scale` puts the screens in a
  `LayoutTransformControl` — the layout happens at a constant size and Skia
  draws at the window's own resolution, so text stays sharp on a 4K display
  instead of being a 1280-wide picture blown up.
- **One scale rule, for every screen, and it is steeper than proportional.**
  The screens are laid out in a space scaled from the window's height
  (`UiSurface.Factor`): 720 pixels tall draws them as authored, and the
  exponent is 1.5, so a window twice as tall draws them about 2.8 times as
  large. Two wrong answers came before it, each reported from the same build
  in the same sentence. Multiplying by the display's scaling factor
  double-counted the DPI -- GLFW is DPI-aware and already hands this program
  more pixels on a 150% display -- which pinned one absolute scale to every
  window: too big in the window the program opens in, too small the moment it
  went fullscreen. Straight proportion fixed that and left the type a constant
  *fraction* of the picture, which is right for a HUD and wrong for something
  you read: a 1280x768 window is an arm's length away and a fullscreen picture
  is usually a bigger screen further off. Giving the in-game screens their own
  multiplier fixed the menus and broke the seam the other way -- the front
  screen that comes back when a match ends "went small again", because a menu
  is a menu and two of them at two sizes is the thing anybody notices. About
  1.1 at 1280x768 and about 1.85 at 1080p were the two anchors the curve was
  drawn through. Every change writes one `[ui] screens at N×` line to the debug
  log, because the scale is decided from a number the player cannot see.
- **The curve is capped by what the window can actually hold**, which is the
  correction to the above and the reason 1080p now reads 1.75 rather than 1.85.
  The height alone decided the scale, and a window wider than it is tall -- the
  ordinary case -- is exactly where that is wrong: 2560x1440 asked for 2.875,
  leaving the screens 890 by 500 points to lay themselves out in when they are
  authored for something near 960 by 600. Both halves of the report are that
  one number. "The text goes abnormally large when the window is wider than
  long" is the type staying the size the curve asked for while everything
  around it is squeezed; "the start button is hidden behind the buttons" is
  `PlayScreen`'s column of settings, taller than the grid row it was given,
  drawn straight over the tick in the corner -- a `StackPanel` short of room
  does not shrink and is not clipped, it overflows. So `Factor` takes the
  smaller of the curve and `min(width/960, height/600)`, and the cap rounds
  *down* to an eighth where the curve rounds to the nearest, since a cap
  rounded to the nearest is a cap that can be exceeded. The column is a
  `ScrollViewer` as well, so nothing can reach the corner marks even at the
  0.6 clamp.
- **Every screen is one column down the middle, and the column has a fixed
  width.** `UiLayout.Page` is the whole layout: backdrop, wash, a centred well
  (heading, strip, content), and the cross and tick side by side at its foot.
  Six layouts were drawn and photographed (`-uidesign`, `UiDesigns.cs`) and
  this one was chosen. Two things about it will be undone by accident if the
  reason is not written down. The first is the **fixed width**: 820 for Play,
  640 for Settings, 480 for a question or a menu, and *not* a fraction of the
  window. A layout that fills the window puts a settings row's label against
  one edge and its control against the other, so on a wide display the two
  ends of one row are a foot apart and reading it costs two glances -- and the
  wider the monitor, the worse it gets, which is the opposite of what more
  room should buy. Here a wider window gives the photograph more room and the
  content exactly what it had. The numbers are set against the smallest layout
  box `UiSurface.Factor` will hand out, 960x600, so there is a margin either
  side at every size the program allows. The second is the **marks together**:
  a cross in one corner and a tick in the other are two things to find, and
  side by side under the content they are one thing to read, in reading order,
  where the eye already is. The pause menu is given none, which is not an
  oversight -- every entry on it is an action, so there is no question for a
  yes and a no to answer, and Resume drawn as the tick *and* as the first word
  of the menu is one action drawn twice.
- **The wash has two weights and the pause menu gets neither.** `Wash()` is
  set by the densest screen there is -- fourteen settings rows that have to be
  legible over whatever the photograph is doing -- and `LightWash()` is a
  third of it, for the front screen, which carries three words and would
  otherwise throw the picture away to solve a problem it does not have. Over a
  match there is no wash at all: `Backdrop(overGame: true)` is already the
  scrim, and a second wash on top of it takes away the match the pause menu
  exists to keep visible. That was visible in the study pictures before it was
  a bug in the program.
- **A centred body carries its heading with it.** `Well(centreBody: true)`
  puts the heading, the strip and the content in one vertically-centred group
  rather than pinning the heading to the top of the well: a menu centred in
  the frame under a word forty points above it does not read as one thing, and
  a pause menu is one thing. The same flag is why a screen with no marks gets
  `WellTop` at the foot instead of `WellBottom` -- room left for marks that do
  not exist pushes the content into the top half of the frame.

- **While something is moving, the screens redraw as fast as the window
  does.** `BusyGap` was 60 Hz on the reasoning that a menu at 60 and a menu at
  144 are the same menu. They are not, and the thing that shows it is the one
  place in the launcher where a lot of pixels move at once: a list being
  scrolled. Capped at 60 on a 144 Hz screen, a glide that covers three rows in
  a tenth of a second does it in six steps of fifteen pixels, and six steps is
  something you can count. The cap is 0 now -- the cost is only ever paid
  while something is actually changing, which is exactly when it is worth
  paying, and a still menu still costs nothing because of the dirty flag.
- **Rows lay their text out once and keep it.** Building a `FormattedText`
  shapes and lays out the string, which is the expensive half of drawing text,
  and every row was doing it from scratch on every repaint for strings that
  never change. A fourteen-row map list shaped 28 strings a frame; a server
  list, whose rows draw five cells each, shaped 25 a frame. At 120 redraws a
  second that is several thousand text layouts a second, every one identical
  to the last. `UiListRow` caches its two and rebuilds only when the width,
  the detail text or the lit state changes; `ServerRow.Draw` caches on the
  type, keyed by text, width, brush, weight and size, because it is shared
  with `ServerHeader` and the five headings are the same on every list.
- **`[ui]` in the debug log says what the screens cost, once a second.**
  Frames, redraws, and the split between dispatcher jobs, rasterising and the
  GL upload. It exists because "the menus feel slow" is otherwise unanswerable
  from here: the launcher's cost is spent inside the game's own frame, so it
  shows up as the *game* being slow and not as anything anybody can point at.
  Redraws far below frames means the dirty flag is working and the cost is
  somewhere else; redraws equal to frames with a large draw figure means it is
  not. Measured on the front screen of a software-GL box: 280 frames, 4
  redraws, 12 ms a second.

- **The launcher is redrawn when something changes, not when the game draws.**
  It used to be rendered from scratch on every frame: measured at **3.5 ms of a
  120 Hz frame** -- about 3 ms of Skia re-rasterising the whole screen and half
  a millisecond uploading a full framebuffer to GL -- to produce, almost
  always, exactly the pixels already on screen. 43% of the frame went on
  proving that a menu nobody was touching had not changed, which is why a
  120 fps game could carry a launcher that felt like ten.
  `UiSurface.Invalidate` now marks the surface dirty and every input entry
  point calls it; a clean surface reuses the texture already on the GPU, which
  costs nothing. Measured at rest: **435 ms/s of CPU down to 14 ms/s**, a
  thirtyfold cut.
  Three numbers hold it together. `BusyGap` caps redraws at 60 Hz even when
  something *is* happening -- a menu at 60 and a menu at 144 are the same menu,
  and the frames in between belong to the game. `IdleGap` (50 ms) and
  `RestingGap` (250 ms) are the backstop, because not every change announces
  itself: a preview that finishes loading, a server that answers and a blinking
  caret all arrive through a dispatcher job, and the dispatcher will not say
  whether it ran one. Redrawing a clean surface anyway turns "a missed
  invalidation freezes the screen" into "a missed invalidation is up to a
  quarter second late", which is the difference between a bug and a rounding
  error. The gap is the short one for three seconds after anything happens --
  when work is usually in flight -- and the long one after that.
  **The trap, and it cost a measurement to find:** `Shell.TickUi` calls
  `surface.Resize` every single frame whether or not the window moved, so an
  `Invalidate()` at the top of `Resize` marks the surface dirty for ever and
  every frame is a redraw again. It goes *after* the early return.

- **There is no per-frame hook here except `UiSurface.Tick`, and the two that
  look like one both fail.** Anything that wants to move a little each frame --
  the list's wheel glide is the first -- has to go through
  `UiSurface.RequestFrame`, and the reason is worth keeping because both
  alternatives compile, run, and are wrong in different ways.
  `Dispatcher.UIThread.Post` looks like "next frame" and is not:
  `UiSurface.Tick` calls `RunJobs`, which drains jobs posted *while it is
  draining*, so anything that reposts itself runs to completion inside one
  tick. The list's whole glide finished in a single frame that way -- offset
  224 to 317 in one step, which is the snap it was written to remove.
  `TopLevel.RequestAnimationFrame` is the right contract and is never called:
  nothing pumps the compositor's animation frames under the headless backend,
  so the callback is simply dropped and the list never moves at all. And
  stepping it from `Render` is the worst of the three -- moving a scroller
  invalidates a visual, and Avalonia throws *Visual was invalidated during the
  render pass* and takes the window down. `RequestFrame` is one-shot like the
  thing it replaces, runs before the render rather than inside it, and hands
  off to `RequestAnimationFrame` on Android and any real backend.
- **The wheel is the `ScrollViewer`'s own, and nothing animates it.** A notch
  jumps the scroller and that is the end of it, the way every build before
  this did and the way the report asked for.

  There was a `SmoothScroll` here, attached to the map and server lists, each
  settings page, the play screen's options column and the game-files screen.
  Its reasoning: a jump per notch is about ten jumps a second, which is
  indistinguishable from ten frames a second however fast the window is
  drawing -- so it set a destination three row-heights away, measured off the
  content, and glided to it over 110 ms. It also aligned the stop to a row
  boundary, which a fixed pixel jump does not.

  It was **removed**, and both halves of why are worth keeping:
  - **The complaint it was built for was not it.** "The menus scroll at five
    frames a second" was the *redraw*: the whole window is re-rasterised on
    the CPU whenever anything changes, which at 1440p was 41 ms a frame. The
    glide did not cause that, but it was the one thing in the launcher that
    asked for a redraw every frame for a tenth of a second, so it was where
    the cost showed. Baking the backdrop and capping the raster (see the
    redraw section) is the fix; removing the glide is what makes a notch cost
    **one** redraw instead of seven.
  - **A jump was what was wanted.** The glide was a reading of the report, not
    a request, and when it was asked about directly the answer was to go back
    to the older behaviour. Do not restore it on the ten-jumps-a-second
    argument alone; that argument is still true and was still not the point.

- **A menu word lights up on the frame the pointer arrives, and does not
  move.** Two wrong answers came before that. `UiWord`'s lit colour was the
  word's own blended a little towards white, and the text colour is already all
  but white, so Settings and Quit answered the pointer with a change nobody
  could see while Play, which starts amber, was the only word that visibly
  reacted. Gliding every word to the accent over 140 ms fixed the visibility
  and was reported straight back as the button taking a fifth of a second to
  notice the pointer -- which is exactly what an eased 140 ms ramp is, and no
  amount of tuning makes a ramp feel like a state. So the answer is the
  accent, immediately, for every word, and nothing else changes: no ramp, no
  slide, no size. A menu word is not an animation. `-shellshot` takes a
  `shell-hover` picture with the pointer resting on Settings and pressing
  nothing, which is the only frame that proves the pointer alone does it --
  a click would light the same word by focusing it.

- **A `LayoutTransformControl` forgets its transform when its child goes
  away.** Setting `Child = null` clears `LayoutTransform` in Avalonia 11.3 --
  proven on its own, away from this program. So every `Hide` wiped the scale,
  and the next screen was laid out 1:1 until something happened to *change*
  the factor and assign a new transform. That is the pause menu drawn small
  over a fullscreen match, and the front screen that "goes small again" when a
  match ends: in both, the window had not changed size, so nothing re-assigned
  anything, and every earlier fix in this list that appeared to work (the
  in-game multiplier, the DPI term) only worked by *changing the factor* and
  taking the re-assignment with it. `UiSurface.ApplyScale` asserts the
  transform wherever the surface is touched. `-shellshot` prints `scale=` (what
  was asked for) and `drawn=` (what is on the host) beside every picture,
  because the two disagreed for three builds and no picture on its own says
  which is wrong.
- **The window's icon is the game window's now.** It used to be set on the
  Avalonia launcher window, and the GL window never had one — invisible while
  a session started in the launcher, and an iconless window once that was the
  only window. `Mods/Render/AppIcon.cs` decodes the mark from a plain embedded
  resource (avares:// needs Avalonia, and this runs before the toolkit and in
  builds that have none) and hands GLFW 16, 32, 48 and full size, box-filtered
  with alpha weighting — GLFW picks the closest and a 552→16 reduction left to
  the platform is a smudge.
- **GL objects have to be given back now.** The context used to die with the
  window at the end of every match. `Scene.UnloadGl` deletes the shaders, the
  offscreen target and every texture the scene uploaded, and clears the model
  cache — display lists live on the cached `Model` and `InitTextures` skips a
  mesh that already has one, so a list deleted without forgetting the model
  leaves the next match drawing through an id the driver has taken back.

## Checking it

`MphRead -uishot DIR` still renders the screens on their own and proves the
layout, with no display at all.

`MphRead -shellshot DIR` proves the rest: it opens the real window and walks
the whole loop, photographing the *window* at each stop and printing the three
sizes with each picture. `shell-start`; `shell-escape` (Escape pressed on the
front screen, so a different picture is the keyboard arriving); `shell-click`
(the word "Settings" clicked where it is drawn, so the settings page is the
pointer arithmetic being right); `shell-resized`, `shell-maximized`,
`shell-fullscreen`, `shell-fullscreen-click` and `shell-windowed` (the screen
has to follow the window, and stay clickable where it is drawn,
which is the fault three of the four size bugs above showed up as);
`shell-play`, `shell-play-offline` and `shell-play-selected` (a click selects a
row and starts nothing; the tick starts it); `shell-match`;
`shell-settings-ingame`; `shell-pause-maximized` (the window made bigger *during* the
match, with no screen up to hear about it); `shell-match-fullscreen`; `shell-pause-fullscreen` (a menu in
the wrong viewport shows up worst here); `shell-pause`; and `shell-back`. It
needs a display; Xvfb is one.

## What did not change

The screens. Every one of them is the same control it was — they are the ones
the Android head has always drawn over its own GL surface, which is the whole
reason this could be done without rewriting any of them. `PauseMenuWindow`,
`SettingsWindow`, `ScreenWindow` and `HomeWindow` are gone; nothing replaced
them.

The launcher also no longer binds `libICE` or `libSM`: there is no X11
top-level to create. `fontconfig` is still needed, and so is a display — for
the game window.
