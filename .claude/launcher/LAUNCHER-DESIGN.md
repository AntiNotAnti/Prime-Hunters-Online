# Launcher — design and UI

This document describes the UI components, the logo handling, and the shape of
the windows.

One toolkit

The launcher is Avalonia everywhere and is rendered into the same game window as
the match on desktop. Android hosts the same shared controls over its GL surface.
The modern FPS hub is a presentation layer over portable launcher/network state;
it does not own a second copy of match or lobby truth.

| File | What |
|---|---|
| `Shell.cs`, `UiSurface.cs` | one-window lifecycle, off-screen UI composition and input |
| `HubHomeView.cs` | persistent command hub |
| `HubPlayView.cs`, `HubQuickPlayView.cs` | deployment choices and Quick Play progress |
| `HubServerBrowserView.cs` | modern public-server presentation |
| `ServerBrowserService.cs` | renderer-neutral discovery, best-server policy and joining |
| `LobbyScreen.cs` | authoritative lobby state presented as Roster / Arena / Match Rules |
| `HubSettingsView.cs`, `SettingsView.cs` | category landing and transactional settings |
| `PauseMenuView.cs`, `InGameMenu.cs` | in-match menu stack over the live match |
| `HubTheme.cs`, `HubNavButton.cs`, `GuiTheme.cs` | FPS palette, typography and shared controls |

The old deck controls remain only as transitional building blocks inside dense
screens that have not yet been structurally replaced. Detailed notes below about
deck springs/cards document those controls and past performance failures; they
are not the target visual language for new hub surfaces.

Painting and controls

- Player-facing UI text uses **Inter**. JetBrains Mono is limited to technical
  metadata/status. Pixelify and Hey November are retained legacy assets, not the
  current display system.
- New hub surfaces use flat `HubNavButton` controls and cold dark/cyan tokens.
  Dense legacy controls consume the same palette/type roles while their layouts
  migrate.
- **An animation has to invalidate the surface, not just the control.**
  `InvalidateVisual` marks a visual dirty inside Avalonia and says nothing to
  `UiSurface`, which decides whether the screens are rasterised at all: an
  untouched surface is redrawn on its backstop only -- 50 ms while something
  happened recently, 250 ms once it has been still for three seconds. A spring
  that reposts itself through the dispatcher is therefore stepped at four
  frames a second, and `DeckButton.Settle` clamps each step to 50 ms, so the
  motion runs at a fifth of its real speed: the fifth-of-a-second hover pop
  arrived a full second late. `UiSurface.RequestFrame` is the hook -- it runs
  the step at the top of the next tick *and* marks the surface dirty --
  and `DeckButton.RequestAnotherFrame` goes through it. Scrolling never showed
  the bug because a wheel event invalidates the surface itself.
- **A screen pushed from `OnAttachedToVisualTree` is laid out on the wrong
  em.** A control added to the tree while an ancestor is still being attached
  never inherits `Deck.EmProperty` from the `DeckStage` above it, so it is
  measured on the attached property's own default -- 10.81, which happens to be
  what `-uishot`'s 940-point capture produces and is why no desktop picture
  ever showed it -- and the panel comes out a column of text a dozen characters
  wide with no card behind it. That is what a fresh install on a phone opened
  onto: `StartScreen` opens the setup screen from its attach when there are no
  game files, while the *same* screen reached from PLAY a second later was
  perfect. One dispatcher turn (`DispatcherPriority.Loaded`) is the whole fix.
  Anything else that wants to open a screen as another one arrives has to do
  the same.
- **The setup screen offers one way in: the platform's own file dialog.** It
  used to show the button *and* a path to type at the same time, on the reading
  that somebody who knows where their dump is would rather paste it. That is
  one offer too many on the one screen with exactly one thing to do, and on a
  phone it cannot work at all -- the picker hands back a `content://` document
  with no path behind it, so a typed path cannot name what the button opens.
  The row is gone on every platform. The cost is stated rather than worked
  around: a Linux box with neither zenity nor kdialog has no dialog for the
  button to open (the desktop heads draw these screens with Avalonia's headless
  backend, so the toolkit's own `StorageProvider` is not theirs to use -- see
  `NativeFilePicker`), and the button says so and names what to install.
  `-shellshot` presses that screen on CI, where there are never game files, and
  it used to aim at the typed row's word: removing the row failed the whole
  capture. It presses the tick now, with `NativeFilePicker.Suppressed` set for
  the length of the script so the press takes the no-dialog path rather than
  opening a modal nobody can answer.
- **Photographing the results: hold `Ending`, not `GameOver`.** Both satisfy
  `EndScreen.Available`, so the deck panel comes up either way -- and the HUD
  draws the *scoreboard* only on `Ending`; `GameOver` is the words GAME OVER
  and nothing else. A capture held at `GameOver` is a capture of half the
  screen, which is what the first two attempts were.
  `Shell.HoldResults` sets `Ending` with thirty seconds on the clock (the real
  screen runs for ten and then rotates, and a shot that has to be caught
  inside ten seconds fails on a slow machine) and calls `MapPick.Begin` by
  hand, since the ballot normally arrives in a packet and there is no server.
  `shell-endgame` and `shell-endgame-hunter` are the two shots.
- **The results panel is a deck screen; the scoreboard is not.** The engine
  draws the results itself and that stays the game's screen -- plain rows in
  the HUD's own idiom, no cards, no lips, no radius. `EndPanelView` is only
  the half the theme should touch: the map ballot and the hunter picker, which
  were arrows and swatches beside a 32x32 sprite. It is `.endside` -- 22 ems
  down the right, pinned top and bottom (`DeckCard.Fill`, which every other
  panel must not have or the Story face's two rows end up in the middle of a
  card the height of the screen).
  **It decides nothing.** Every press goes where the HUD's own picker went:
  `MapPick.Choose`, `EndScreen.Pick`, `EndScreen.ToggleReady`. The server owns
  the rotation and the respawn, and a second opinion held in a menu is how two
  screens come to disagree about what you picked -- so `Refresh` pulls the
  match's state in once a frame and the only writes are the three above.
  **It is watched for, not announced.** The results arrive because the server
  said the match is over and nothing on this machine hears that;
  `Shell.TickEndPanel` watches `EndScreen.Available`, which is the same
  question the HUD asks. `ModDrawEndScreen` returns early while the panel is
  up, and only that panel steps aside.
  The hunter in it is the **real model**: `HunterStand` publishes its
  rectangle into `Scene.Preview*` as well as `LauncherHunter`, so the scene's
  own pass draws it during a match exactly as it does on the results screen,
  and `Scene.PreviewDrawnLastFrame` is what both paths set and the stand reads
  to decide between a hole and its boxes.
- **"Call the vote" did nothing.** The vote face picks from the card grid now
  and the list it used to read is not even built, so the commit asked an empty
  list. `SelectedRoom` answers from the grid on both faces that use one. The
  lesson is the usual one: a face that changes where it reads from has to
  change where it commits from in the same breath.
- **F11 and Alt+Enter are the window's, not a screen's.** The shell takes the
  whole keyboard while a screen is up -- right for every other key -- so
  fullscreen was the one thing the launcher could not do, and a player who
  wanted it had to start a match first. `OnKeyDown` now offers those two to
  `WindowMode` before the shell, *unless* a rebind row is waiting: "press a
  key" has to be able to be told F11, or F11 is the one key nobody can bind
  (`KeyRow.AnyListening`).
  The test for it is worth a note of its own. `-shellshot`'s `Key` helper
  hands the key straight to `UiSurface`, which is the right shape for testing
  a screen and exactly the wrong shape here: it skips the code that claims the
  key, so it could never catch this. `WindowKey` goes through
  `RenderWindow.FeedKey`, and the assertion is on `WindowMode.IsFullscreen` --
  not on `WindowState`, because this game's fullscreen is a borderless
  maximised window and `WindowState` never says `Fullscreen`. Both of those
  were wrong first and both made the probe pass or fail for the wrong reason.
- **The support mark is a button, not a control of its own.** It is
  `class="btn f-rust heart"` in the reference -- the same object as QUIT
  beside it, with an SVG where the word goes. As its own control it had no
  bevel, no spring, no lean towards the pointer and no press that travels by
  the lip it loses, which beside QUIT read as a different program's button.
  `DeckButton.Glyph` draws a picture in place of the label and `GlyphColour`
  is tinted by the same `brightness(1.22) saturate(1.15)` the face gets, which
  is `fill: currentColor` under the element's own filter.
- **The vote is the same cards the offline face uses.** `MapPick`'s note says
  why the ballot is every map rather than four the server drew up -- "where
  next" is not multiple choice -- and the reference draws that as the card
  grid with a count in the corner of each (`DeckTile.Tally`) and an accent
  ring on whatever is ahead (`.leader`). It was a column of names beside one
  preview.
- **The launcher draws the real hunter, and it stands a scene up to do it.**
  `HunterStand`'s boxes were there because "the engine is not running while
  the launcher's screens are", and that stopped being true when the launcher
  became a screen inside the game's own window. `Mods/Render/LauncherHunter.cs`
  owns a `Scene` with **nothing in it** -- no room, no players, no entities --
  because a scene is what owns the shader and the render-item list, and
  `HunterPreviewEntity` is an entity that is never inserted into one. It is
  the results screen's own pass (`Mods/Render/PreviewPass.cs`), asked from a
  frame with no world in it.
  Four things had to be right, and each was wrong first:
  1. **The scene must not be the window's.** `OnRenderFrame` routes on
     `_scene == null` -- that is what tells the shell's frame from a match's --
     so `BeginScene` would have sent the window down the match path with no
     room in it. `RenderWindow.NewSideScene` hands one over without the window
     taking it.
  2. **Nothing generates the model's display lists.** In a match this is
     invisible: the player is the same hunter model, `Read` caches the `Model`,
     and the list id is written onto that shared object -- so the preview has
     always been reusing lists the player's entity made. With no player the
     first draw died looking a palette up by an id nobody registered.
     `ModStepPreview` now calls `InitEntity` once per hunter.
  3. **It goes over the screens, not under them.** Under is the obvious place
     and it does not work: the drawer is an opaque panel drawn by the screens,
     so a model beneath the texture is a model behind a card. The stand draws
     *nothing* where the model goes and `LauncherHunter.Draw` fills that
     rectangle afterwards, clearing it to its own background first.
  4. **The rectangle crosses two coordinate systems.** The layout transform
     that scales the screens is *inside* the top level, so a point translated
     into it has already been scaled; multiplying by the factor as well put
     the rectangle a third of a window off the right edge, where the scissor
     clipped it to nothing -- which looks exactly like a model that failed to
     load. Both corners are translated now, and the one-shot `ui` line carries
     the rectangle for that reason.
  **The heartbeat publishes, not `Render`.** A control that is not visible is
  never rendered, so a rectangle published from the drawing would go on being
  true after the drawer had slid shut, leaving a hunter painted over a panel
  that is no longer there. The 33 ms timer can ask whether it is visible; a
  draw that never happens cannot.
  **A side scene must not start the console prompt.** `Scene.OnLoad` spawns
  the task that draws the load/camera prompt, and its first `Console.Clear`
  throws *The handle is invalid* on the Windows build -- a GUI binary with no
  console -- on a task nobody awaits, so the finalizer rethrows it.
  `Scene.SideScene` is what says not to: this one has no room to load into and
  nobody to prompt.
  **A model that is not there must be asked for once.** `HunterPreviewEntity.
  SetUp` only returned early on success, so a failed load retried every call.
  On the results screen that was ten seconds of it; on the launcher, where the
  drawer sits open as long as somebody likes, it is a log growing by two lines
  a frame for ever -- which is how a player first reported it. The hunter that
  failed is remembered.
  **Every failure is the boxes again.** No scene, no model, the frames before
  one has loaded: all leave `Drawn` false and the stand draws itself the way
  it always did. That is the whole safety argument for putting a renderer this
  deep under a launcher -- and it is why `-uishot`, which has no GL at all,
  still photographs the boxes.
- **A selected row's ring needs room inside the clip.** `box-shadow: 0 0 0 2px`
  spreads *outward*, and `UiList`'s scroller clips to its own width because
  horizontal scrolling is disabled -- so a row stretched to that width had the
  left and right of its accent ring cut off while the top and bottom survived
  in the gap between rows. Three points of horizontal margin on the rows panel:
  the two the ring spreads, plus one for the rounding.
- **One drawer, two things it can be about.** `DeckSide` now opens for a
  server as well as a map, because they are the same question -- something
  chosen on the left, the hunter you take into it, one button -- which is why
  the reference has one `.side` and not two. Only the middle differs: a
  server's Map/Mode/Players/Ping against a map's rules. The facts are read off
  `ServerRow`'s own last answer rather than by asking the directory again,
  since a second query would show a ping from a moment the list is not
  displaying. `-shellshot` presses a live row and photographs it
  (`shell-server-side`).
- **The badge is a bare chip.** `DeckChip` with an empty `Label` draws the
  value alone -- the reference's `.code`, which is what the drawer's badge is
  there ("mp3", "host", a flag) rather than a labelled key/value pair.
- **The animation that never stops gets its own cadence.** The front screen's
  idle bob was costing about a third of a core doing nothing: 17 to 30
  full-window rasterisations a second, for ever, to move one button two and a
  half points over three and a half seconds. That is a regression this file
  caused -- routing animations through `UiSurface.RequestFrame` was right for
  the springs and raised the bob from the 50 ms backstop to sixty a second at
  the same time, and the comment on `DeckButton.Idle` ("one button on one
  screen is the budget") was written when that budget was twenty.
  `IdleAnimGap` is 66 ms: at fifteen frames a second the bob moves a sixth of
  a point between frames, which nobody can see. A frame asked for by anything
  that is *actually* moving clears the idle flag, so a spring running beside
  the bob still gets sixty. Measured on the front screen at rest: 17 redraws
  and 334 ms a second before, 8 and 166 after.
  **The springs stayed at sixty.** Dropping them to thirty was tried and put
  back: a spring runs for a fifth of a second, so it barely moves the average,
  and it is exactly the sluggishness that was reported in the first place.
- **The redraw is capped at sixty a second, and the raster is not touched.**
  An adaptive raster was tried -- time every redraw, step the surface down a
  sixteenth while it is over budget -- and it works exactly as designed and is
  the wrong trade: it ratchets down on any machine that cannot hold the budget
  and what the player gets is a permanently soft launcher. Resolution is not
  the knob. `BusyGap` is 16 ms instead of 0 instead: a redraw here is the
  whole window rasterised by Skia on the CPU, and on a 144 Hz monitor it was
  doing 144 of them a second for a menu that cannot look different at 60. On a
  60 Hz window nothing changes at all, and sixty is the floor because this is
  also the path a wheel notch takes.
- **Where the scroll cost actually is, measured.** Scrolling the map grid and
  scrolling the server list cost the *same*: ~25-30 ms a frame inside
  `Dispatcher.RunJobs`, with `draw` at ~7 ms and `DeckTile` doing 0 measures,
  0 arranges and 1 render a second. So it is neither the cards, nor layout,
  nor the rasterise-and-upload half -- it is the compositor commit for a
  surface that is rebuilt whole every time anything moves. That is the
  headless-plus-Skia-on-the-CPU arrangement itself, and the only real fix is
  to stop rasterising the screens on the CPU. Worth writing down so the next
  person does not go looking in the grid again.
- **What the grid actually cost, measured rather than guessed.** "Scrolling is
  slow while the backdrop shader is smooth" is exactly right and the two
  halves have different answers: the backdrop is a GL quad and costs the CPU
  nothing, the screens are Skia on the CPU and cost the window's area every
  time anything moves. Counting `DeckTile`'s own calls during a scroll gave
  **0 measures, 0 arranges, 1 render** a second -- so the tiles were not the
  cost and neither was layout; it was the raster. `-shellshot` now scrolls the
  grid for two seconds so the `ui` line has a steady state to report, instead
  of only photographing moments.
  Two things were still worth fixing on the way: `MapShot` decoded the
  1600x900 thumbnails at full size and every card resampled 1.4 million pixels
  to fill thirty thousand (it decodes at 512 now), and each card's render,
  drift and scrim are baked into one bitmap per size in `ArrangeOverride` --
  never in `Render`, for the reason `BakedBackdrop` states.
- **Offline is every map at once, and the rules are in the drawer the map
  opens.** It was a column of names down the left with a preview of the
  highlighted one in the corner, which is a file picker: it asks the player to
  remember what the other twenty look like. `DeckTile` is the reference's
  `.pcard` -- square, the render under a code tag and the map's name, a word
  along the bottom that says whether this is the one -- and `DeckGrid` is its
  `.grid`, three across and two below a 640-point frame. `DeckSide` is the
  `.side` drawer: 17.5em down the right, in from off the edge on the spring,
  turned a quarter on a phone held upright so it comes up from the bottom
  instead. It takes itself out of the tree when it has finished leaving, since
  it sits over the cards and an invisible one would still eat their presses.
  The steppers are *held* by the screen and lent to the drawer rather than
  parented to it, so they keep what the player set the last time it was open.
- **The stand wears a suit.** The template's turntable asked which hunter and
  stopped there; the in-game picker (`Mods/EndScreen.cs`) has always asked
  which *suit* too, and a suit is half of who you are on a scoreboard.
  `HunterStand.Suit` recolours each box by the ratio it already stands at
  against the hunter's base tint, so the palette swaps and the modelling does
  not -- which is what a recolor is. The four colours are sampled from the
  player's own model palettes by `Mods.HunterSuits`, never written down: they
  are not the same four for every hunter and a table of them would be game
  data this repository does not carry.
  **The real model is still not here**, and the reason is worth writing down:
  the in-game preview is a pass on `Scene` (`Mods/Render/PreviewPass.cs`,
  driven from `Renderer.OnRenderFrame`), and the launcher's front screen runs
  through `UiOverlay.DrawAlone`, which is the path with *no scene at all*.
  Putting a hunter model on it means standing a Scene up with no room in it,
  which is a renderer-lifecycle job and not a launcher one.
- **A value that does not fit does not ellipsize, it wraps.** `ChoiceRow` set
  `MaxTextWidth` and no `MaxTextHeight`, and its left arrow had a flat
  110-point floor -- so inside a 17.5em drawer the arrows sat 138 points apart,
  the value got about thirty points of column, and "Normal" came out as three
  stacked syllables. The floor is now `min(110, width * .42)` and the height
  limit is set, which is the same fix `DeckText.Lay` already carries.
- **`#ground` is two gradients of `--void`, and that is all it is.** It was a
  215-to-0 fall from the top, a radial vignette in the bottom-right corner and
  a full-window wash over both -- about sixty per cent of black over the
  middle of the photograph against the reference's twenty-six, which is what
  "le fond est trop sombre" was. `UiLayout.Ground` now draws the reference's
  own pair: `180deg .72 / .10@30% / .35@62% / .90` and `90deg .55 / 0@38%`, in
  #05070a rather than black -- there is a blue in `--void` that plain black
  does not have, and over lava it is the difference between shade and soot.
  The panel screens' extra ground is not a third wash either: it is `.sheet`'s
  own flat `rgba(5,7,10,.72)`, and it lives in `DeckSheet` rather than the
  bake so it can fade in with the panel it belongs to.
- **A screen arrives, it does not cut.** `DeckSheet` is the reference's
  `.sheet` opening: the scrim and the panel fade up together over `.22s` on
  `--settle`, and the panel springs out of `scale(.9) translateY(14px)` over
  `.4s` on `--spring` underneath. One control holds both halves for the same
  reason they are one element there -- darkening the frame instantly and then
  floating a panel onto it is two events where the reference has one.
  `Deck.Still` pins it at the end pose so `-uishot` photographs the screen the
  player ends up looking at.
- **The focus ring follows the input, not the focus.** `:focus-visible`, not
  `:focus`: a browser rings what the keyboard focused and not what a pointer
  did. Every control here calls `Focus()` from its own press handler, so
  drawing on `IsFocused` put a two-point amber ring around everything anybody
  clicked. `Deck.KeyboardDriving` is the session-wide fact -- set by
  `UiSurface`'s key entry points, cleared by its pointer ones -- and
  `DeckButton` rings only when that is true and the focus did not arrive from
  a pointer. The `brightness(1.22)` filter is on the same condition, for the
  same reason.
- **The keys live on the Keyboard page.** They were built inside
  `BuildGamepad`, under a "Keys" heading after the pad's buttons, so
  `Controls > Keyboard` offered four mouse rows and nothing else. The
  reference has Mouse then Keys under Keyboard, and Sticks then Buttons under
  Gamepad; `_keyRows` is the list Reset redraws, since the rows are no longer
  in the closure that owns the Reset word.
- **The support mark measures its face, not its lip**, like every button --
  the lip is a `box-shadow` and hangs below the box, so a row aligned on its
  items' bottoms lines up the faces. Counting it in left the mark five points
  high beside the row it sits in (measured: faces ended at 504, the mark at
  499). It also draws its own `data-tip` now, on hover and on
  `:focus-visible`, for the reason the reference draws one -- a platform
  tooltip arrives late and in the OS's colours, which on a screen of painted
  controls is the one thing from somewhere else.
- **The player-facing backdrop is cinematic map art, not the old wireframe JPEG.**
  `LauncherBackdrop` names the current hub scene and room. Desktop
  `LauncherPhoto` loads the locally generated thumbnail for that room and
  draws it as a native-resolution GL quad with a very slow pan/zoom; the old
  `launcher-bg.jpg` is no longer used by the normal hub. The existing
  `LauncherNoise` overlay remains at a much lower strength for subtle motion.
  Android/headless use the same room through `MapShot` and `BakedBackdrop`,
  so no game-derived backdrop is shipped in the repository.
  Scene changes come from Home/Play/Multiplayer/Offline/Adventure/Replay/
  Settings/Create Lobby/Lobby, and Multiplayer/Offline/Replay/Lobby update the
  room when the player selects one. Missing thumbnails fall back to a graded
  colour field rather than debug geometry. `Reduce menu motion` freezes the
  GL drift and `Deck.Still` keeps captures deterministic.
- **An animation that drives itself is capped at 60, input is not.**
  `UiSurface.Invalidate(animation: true)` is what `RequestFrame` raises, and
  `Tick` gives it `AnimGap` (16 ms) rather than `BusyGap` (0). A wheel notch
  happens as often as a player makes one; a spring or the front screen's idle
  bob asks again every frame it moves and the bob never stops, so uncapped on
  a 144 Hz monitor that is 144 full-surface rasterisations a second for two
  and a half points of travel. It also does not count as a touch, or the
  short backstop would be held open for ever by a button nobody is looking
  at. Measured on the real shell: 43 frames went from 43 redraws and 305 ms
  of draw a second to 32 redraws and 209 ms.
- **Nothing on the front screen may be a fixed number of points.** The
  stage's em is `clamp(9px, 1.15cqw, 15px)` (`Deck.EmFor`), so a constant is
  only right at one window size. Two were left: the wordmark at a flat 64 and
  the support mark at a flat 52x44. The reference says `7.6em` for the mark
  (`5.2em` upright on a phone, `4.4em` turned) and builds the heart out of a
  `1.55em` button -- `padding: .7em .9em`, a glyph `1.9em` by `1.27em`, a
  five-point lip -- which at a 940-wide frame is 82 points of type and a
  62x50 mark, against the 64 and 52x44 that were there. `DeckWordmark.SizeEms`
  and `DeckHeart` read `Deck.EmProperty` now and both declare `AffectsMeasure`
  on it; `StartScreen.LayOutWordmark` picks the multiplier off the frame's
  own box.
- **"The white is not as white" was the outline, not the colour.** The face is
  `#f2ede2` and comes out of the rasteriser as exactly (242, 237, 226) --
  sampled from a capture, not assumed. What made it read grey is that the
  outline is a flat three points at any size (it is `3px` in the reference
  too), so on a mark a third smaller than it should be, six passes of
  near-black eat proportionally half again as much of every stem. Sizing the
  mark correctly is the whole fix.
- **The word hops, not just the button.** `chhop`: each character lifts
  `.18em` and leans three degrees, alternating, on the spring curve, staggered
  `.022s` down the label, over `.42s`, once per hover or focus.
  `DeckText.DrawTracked` takes an optional per-character offset and
  `DeckButton` drives it off a stopwatch started in `OnPointerEntered` /
  `OnGotFocus`. Two eased segments rather than one curve through three points,
  because CSS applies the timing function between each pair of keyframes and
  running one bezier over the pair loses the snap at the top. `Deck.Spring` is
  the reference's `cubic-bezier(.18, 1.55, .35, 1)`, solved -- the overshoot
  past 1 is the character of that curve and an approximation puts it in the
  wrong place. `Deck.Still` suppresses it, so `-uishot` still photographs the
  resting pose.
- **A row that has never been arranged still gets one `Render`.** A control on
  a sub-page built with `IsVisible = false` is attached to the tree, so the
  compositor draws it once with zero `Bounds` -- and a rect derived from those
  (`KeyRow`/`PadRow`'s `Box`, four points shorter than the row) is *negative*.
  `FormattedText.MaxTextHeight` throws on that, out of the compositor's own
  pass where nothing catches it, and the process went down the moment the
  Controls page was opened. Both rows now return early at zero size.
- `GuiTheme` holds the palette and the display face. Inter is embedded in the
  build rather than looked up on the system: there is no font list every
  platform has, and a launcher that renders in whatever fontconfig happens to
  pick looks different on every distribution.

A press is not a tap

- **No control here acts on `OnPointerPressed`.** Every scroll on a phone
  begins as a press on whatever is under the finger, so a row that answers the
  press answers every drag that starts on it: scrolling the settings toggled
  the toggles, stepped the choice rows, started the rebind rows listening and
  slammed the sliders to wherever the finger went down. `Tap.cs` is the rule --
  the press only remembers where it landed, a finger that travels more than
  eight points has stopped meaning the row, and the **release** acts, and only
  inside the control. `ChoiceRow`, `ToggleRow`, `SliderRow`, `KeyRow`,
  `PadRow`, `UiListRow`, `ServerRow`, `PickRow`, `UiWord` and `UiMark` all go
  through it.
- **The distance test is for a finger or a stylus only.** A mouse scrolls with
  the wheel, so a button held across a row is not a scroll: the desktop keeps
  press-here-release-here and nothing on it changes feel.
- Eight points is deliberate: above the jitter of a finger held still, and well
  under the thirty Avalonia's `ScrollGestureRecognizer` wants before it calls a
  drag a scroll and takes the pointer (which arrives as
  `OnPointerCaptureLost`, and is a cancel).
- `SliderRow` is the one control with a gesture of its own to defend, and it
  waits for a direction: sideways past the slop is the track, anything else is
  the page. A tap that never moved still sets the value.
- `MphRead -tapcheck` drives that rule with the coordinates a finger would have
  produced -- there is no touchscreen on a build box, and a screenshot of a
  settings page says nothing about what a drag across it does.

Windows have frames

The WinForms screen was borderless and dragged by its picture. Every window here
is an ordinary decorated one: an undecorated window that a given window manager
will not let you move is a trap, and there are many window managers.

Logo and assets

One source image, chroma-keyed and cropped into four files under
`src/MphRead/Assets/`. All four allow-listed in `tools/asset-guard-allow.txt`
(PNG and JPEG are otherwise banned extensions).

| File | What | Used by |
|---|---|---|
| `fruity-prime-logo.png` | the wordmark, cherry and text together | the game-files card, the Android screen and the README |
| `fruity-prime-mark.png` | the cherry alone | the window icon |
| `fruity-prime.ico`, `fruity-prime-server.ico` | ICO frames for Windows | `ApplicationIcon` |

Notes on ICOs: 256×256 is the ICO format's ceiling; the source crop carries
detail up to ~460 px so 256 is a downsample.

Threading, and why there is only one thread

The toolkit is set up once per process **on the game's own thread**, and both
the launcher and the pause menu are windows on it:

- Avalonia allows one application per process, so a launcher that stood one up
  per visit worked exactly once and fell back to the text screen on the way back
  from the first match.
- macOS accepts windows only on the main thread, which rules out the private UI
  thread the WinForms launcher used.
- The pause menu needs the toolkit *during* a match, on the thread the render
  loop runs on.

A visit to the launcher is `Dispatcher.UIThread.PushFrame`, ended by the
window's `Closed` event. `GuiLauncher.Pump` is the other half: a nested frame
that runs until a background-priority job it posted comes back, which processes
everything pending and returns. `PauseMenu.Poll` calls it once a frame while the
menu is up -- which is why the match keeps drawing behind it -- and `Ask` calls
it once after the launcher closes, because on X11 the window's destroy request
would otherwise sit unflushed in the connection's buffer for the whole match and
leave a launcher painted over the game.

Menu entries

- **No descriptions under the titles, anywhere.** An entry called "Join" did
  not need a line saying it joins, and in the pause menu the second saying is
  what made a seven-line menu tall enough to be cut off. The only subtitles
  left are the ones reporting something the player could not otherwise know --
  missing game files on "Host", a demo that would not open, the map-preview
  progress -- and those are set when they happen, so `MenuEntry` takes its
  height from the subtitle (42 bare, 54 with one) in `OnPropertyChanged`
  rather than deciding it once in the constructor.

Pause menu

- `Escape` in a match opens it on every platform now (`Mods/PauseMenu.cs` +
  `Gui/PauseMenuWindow.cs`): Resume, Fullscreen/Windowed, Settings, Spectate or
  Rejoin, Record demo, Leave match, Quit.
- **It scales itself down rather than being cut off.** The panel's natural
  height is worked out from the entries put in it (each states its own
  `Height`), and `PauseMenuView.FitToHost` puts a `ScaleTransform` on a
  `LayoutTransformControl` around it, down to half size, when the window is
  shorter than that. The scroller under it is the last resort, not the plan:
  what a scrollbar produces here is a panel with its top and bottom cut off.
  A display at 150% is what made this ordinary -- the panel needs ~500
  device-independent pixels, which is 750 real ones, and the game window's
  floor was 600. That floor is now 1024x720 and the default window 1280x768.
- **Spectating starts on the free camera** (`Mods/SpectatorMode.cs`): "Spectate"
  puts you on the map with no HUD, a left click moves into the players and
  cycles through them, and Space toggles between the two -- `ToggleView`, not
  the camera directly, because the camera on its own would put you back behind
  your own hidden, frozen body with your own HUD on. **The HUD follows the
  camera, not the spectating**: on the free camera there is none -- it is
  CameraMode.Roam, and the scene only draws a HUD for a player's own camera --
  and following somebody shows theirs, which is what watching a recording back
  has always done and what makes watching a live match worth anything. It used
  to be hidden in both (`DrawHudObjects`/`DrawHudModels` returned early on
  `IsSpectating`), which left a spectator watching a hunter with no sign of
  what they were playing with.
  **The scoreboard is the exception on the free camera**: it is the match's
  and not a player's, so holding the show-score button draws it (and the
  filter that dims the scene) over the map. It cannot come from the usual
  place -- every keybind's state is filled in by the input pass spectating
  steps out of -- so `PlayerEntity.ProcessInput` reads that one bind off the
  keyboard snapshot against `InputSettings.Current` and leaves it in
  `SpectatorMode.ShowScoreboard`, which `Scene.ScoreboardOverFreeCamera` and
  `PlayerHud.ShowScoreboard` read.
  A spectator is also **drawn not at all** (`PlayerDraw.Draw` returns before
  `DrawShadow`, which is cast from the volume and so survived hiding the model)
  and is **not a target** (`PlayerAi`'s opponent and teammate searches ask
  `ModInPlay`, not `Health > 0`). The camera is
  the scene's, and the menu runs on the game's thread but has no scene to hand,
  so `Start`/`Rejoin` leave a `bool?` in `SpectatorMode` that
  `Scene.OnRenderFrame` acts on -- the same shape as this menu's own window
  work. Demo playback is the exception: it calls `Start(watchSomeone: true)`
  and goes straight to a player, having no view of its own to have just left.
- It talks to the game through volatile flags. GLFW window calls -- closing it,
  changing its border -- belong to the thread that created the window, so the
  menu asks and `PauseMenu.Poll` does it on the game's own thread.
- **It is the size of the game window and laid straight over it**, so it reads
  as the game's own pause screen rather than as a dialog the game opened. It was
  a 340x392 box centred on the game before, which is the shape of a settings
  prompt and not of pressing Escape in a game. `PauseMenuWindow.CoverGameWindow`
  takes the rectangle from `PauseMenu.WindowX/Y/Width/Height`.
- **It follows the game window, every frame.** Sampling that rectangle once at
  open time is not enough: drag the game and the menu stays where it was, which
  is the floating popup all over again. `PauseMenu.TakeWindowRect` re-reads the
  GLFW client rect from `Poll` -- already called once a frame while the menu is
  up -- and `PauseMenuWindow.FollowGameWindow` re-lays both the menu and the
  in-game settings when it changes. It remains a borderless window *over* the
  game rather than something drawn *inside* it, because Avalonia cannot render
  into the GL context; following is what makes that difference invisible.
- The rectangle is in client **pixels** (what GLFW reports, and what Avalonia's
  `Position` is in) while `Width`/`Height` are device-independent, so the
  display scaling has to come back out of them. Take it from
  `Screens.ScreenFromPoint(...).Scaling`, **not** `RenderScaling`: the latter
  is 1 until the window has been given a screen, so the constructor's call --
  the one that stops the window appearing mid-desktop for a frame -- would be
  wrong on any display not at 100%.
- **Nothing in it can be clipped.** The panel has a `MaxWidth` rather than a
  `Width` and sits in a `ScrollViewer`: the host is now the game window and the
  game window is whatever size it has been dragged to. Seven entries need about
  470 px of height, and below that the fixed-size version drew "Leave match"
  and "Quit" off the bottom -- a player who cannot get out of the match.
  `RenderWindow.MinimumSize` is 800x600 as well, so that case needs a window
  smaller than the game allows; `-uishot` renders a `pausemenu-small` at
  560x320 to keep the scroll path checked anyway.
- The entries are a 420-wide panel centred in it -- the same shape the Android
  overlay already used -- because a column of entries stretched across a 3840
  window is a menu you have to hunt across.
- The fill is a scrim (`GuiTheme.ScrimBrush`, `Ink` at alpha 196) with
  `TransparencyLevelHint` asking for `Transparent` and falling back to `None`.
  The match is still running behind it and that is the point; a compositor that
  will not give a window an alpha channel renders it opaque, which loses the
  view and nothing else. The panel carries a 1px `EdgeBrush` border, because
  panel and scrim are otherwise two shades of the same dark.
- **The settings window does the same when it is opened from here**
  (`SettingsWindow`, `view.InGame`): same rectangle, no decorations. A fixed
  980x660 dialog centred on its owner was two different rectangles in two
  different places for one screen -- and on a game window smaller than that, a
  dialog hanging off the edges of the thing it belongs to. From the front screen
  it is still an ordinary 980x660 dialog.
- Both are topmost so they clear a borderless-fullscreen game, and the menu
  steps out of the topmost band while the settings are up so the two are not
  left arguing about which is in front.
- **The game window itself only floats while it has the focus.**
  `WindowMode.SyncTopmost` reads `window.IsFocused` along with the fullscreen
  and pause-menu flags, once a frame. An always-on-top borderless window
  cannot be alt-tabbed away from in any way a person recognises -- the switch
  happens, the other window gets the keyboard, and the game stays drawn over
  it -- which was reported as a window that refuses to let go. Floating is
  only ever wanted for the one thing it was added for, covering the taskbar
  while the game is the window being used, and that is exactly the focused
  case.
- Its title is "<name> - paused", not the product name: the game window carries
  that, and two windows with one title is what an alt-tab list cannot tell apart.

Server browser (`Gui/ServerRow.cs`)

- **`FormattedText` wraps; `Trimming` alone does not stop it.** A
  `MaxTextWidth` with a breakable string breaks at the space rather than
  ellipsizing, so "MP3 PROVING GROUND" became two lines in a 30-pixel row and
  drew over the server beneath it. `MaxTextHeight = size * 1.6` is what forces
  one line and lets the trimming apply. A `PushClip` per cell goes with it,
  because trimming cannot help a single unbreakable word wider than its column
  -- which "PLAYERS" is at 51 pixels in a 43-pixel heading, and it simply
  overflowed into "PING".
- **Columns are pixels from the right, not fractions of the width.**
  `ServerRow.Columns` fixes ping (34), players (52) and mode (66) and gives
  what is left to the two columns that hold prose. Fractions of the launcher's
  400-pixel panel put the map column at 89 pixels, which is not a room name.
- The browse card widens the panel to 600 while it is up
  (`HomeView.PanelWidth`), because that card is a five-column table and the
  others are not. The picture beside it is decoration; the list is the thing
  being read.
- `-uishot` renders a `serverbrowser` screen at both 600 and 400 with sample
  rows, which is how the wrap and the overlap were seen and how the fix was
  checked. Neither needs a directory or a server to be up.

Implementation pitfalls

- `ScrollViewer.Padding` is not taken off the width its content is measured
  with. Every wrapped note in the settings window ran off the right edge of the
  window by exactly that much; the inset is the page's `Margin` instead.
- A `DockPanel` fills with its *last* child, so docking the Save/Cancel footer
  first put it first in the tab order -- the first Tab in the settings window
  was one press away from closing it. It is a two-row `Grid` now.
- Each window focuses its own first control when it opens. Without that a
  keyboard user tabs blindly into whatever the tree happens to offer first.
