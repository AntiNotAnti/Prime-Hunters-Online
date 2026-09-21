# Modern FPS Hub overhaul

Status: **P0 validated; P1 multiplayer/lobby overhaul in progress on `feature/modern-fps-hub`**

The goal is to turn the launcher/menu collection into a coherent game shell without
rewriting networking, replay, settings, or match startup logic at the same time.

## Framework decision

Keep **Avalonia** for the first overhaul.

The repository already has one-window desktop composition, Android reuse, controller
navigation, file setup, updater integration, lobby UI, replay UI and in-game overlays.
Replacing the toolkit before separating those concerns would turn a UX redesign into a
platform rewrite.

React remains viable later through game-oriented middleware such as Gameface, and
RmlUi/Noesis remain viable renderer alternatives. The shell must therefore keep game
state and navigation contracts independent from individual Avalonia controls.

## P0 — shell foundation

Implemented:

- New flat tactical hub visual language in `HubTheme`.
- New controller-focusable `HubNavButton` with no idle animation.
- New responsive `HubHomeView`:
  - Play
  - Map Editor (placeholder)
  - Replay Studio
  - Settings
  - Quit
- Live local profile/hunter/game-data status.
- Existing `HunterStand` reused rather than introducing another preview path.
- Desktop three-column layout and compact Android/small-window navigation.
- Existing Play/Lobby/Settings/Replay screens remain the source of truth.
- Existing game-file setup and updater paths remain owned by `StartScreen`.
- Screenshot automation updated to address the new hub controls.
- Renderer-neutral `HubState`, `HubSnapshot` and destination enums.
- Explicit controller IDs/neighbours for desktop and compact hub layouts.
- Headless controller checks for hub, deployment and modern server-browser actions.
- CI now preserves `-uishot` layouts as a `launcher-layouts` artifact.
- Full Windows/Linux/macOS/Android/server matrix green on the corrected hub baseline.
- Launcher-wide display typography moved from Pixelify Sans to **Inter**; JetBrains Mono is
  reserved for technical/status data.
- Shared dark/cyan tactical palette applied to legacy controls during migration.

Remaining P0/performance work:

- Add high-contrast theme tokens.
- Baseline UI composition cost at 1080p/1440p/4K.

Implemented visual-motion guardrail:

- **Reduce menu motion** is persisted in `LauncherPrefs` and suppresses page-entry motion
  plus cinematic GL drift; `Deck.Still` also disables them for deterministic captures.

## P1 — Play and multiplayer

Play now has three product-level destinations:

1. **Multiplayer**
   - one integrated workspace rather than a chooser followed by another screen
   - live server directory is the main body
   - **Quick Play** is one primary action that selects/joins the best compatible open server
   - **Create Lobby** opens lobby creation; there is no separate player-facing "Custom Match" concept
   - direct address, Refresh, Join Server, hunter/suit and selected-map artwork remain in the same workspace
   - selected-server map thumbnails provide contextual game artwork instead of decorative filler
2. **Offline / Training**
   - hub-native map browser and selected-map preview
   - mode, hunter/suit, bot count and bot skill
   - direct Start Match
3. **Adventure**
   - hub-native save cards and progression summary
   - hunter selection
   - Continue / New Game

Current implementation:

- `HubPlayView` separates Multiplayer / Offline / Adventure.
- `HubMultiplayerView` is the full multiplayer workspace and uses
  `ServerBrowserService` for discovery, Quick Play and joins.
- `CreateServerScreen` is presented to the player as **Create Lobby**. Its
  hosted-vs-dedicated behavior, map rotation, host discovery and server package
  installation remain shared with the existing network implementation.
- `ServerBrowserService` owns renderer-neutral discovery, probing, endpoint
  parsing and joining; refresh/Quick Play cancellation is contained inside the
  service instead of escaping into the UI.
- Deterministic desktop/phone Multiplayer captures use sample directory data,
  including selected-map artwork, without touching the live network.
- Map Editor intentionally remains a placeholder while the editor itself is out
  of scope for this UI overhaul.
- Adventure uses the portable `AdventureSave` / `AdventureLaunch` contract.
- Offline uses the shared `OfflineLaunch` contract.

Visual polish now includes subtle static panel gradients, contextual map/hunter
artwork and short one-shot page-entry transitions. The old lava/wireframe launcher JPEG
has been removed from the normal player-facing path: `LauncherBackdrop` selects a
cinematic room per destination, desktop GL pans the locally generated map render, and
Android/headless bake the same `MapShot`. Multiplayer, Offline, Replay Studio and Lobby
follow the selected room. Missing art falls back to a graded field, never debug geometry.
Transitions and GL drift are disabled by `Deck.Still` for deterministic captures and by
the user's **Reduce menu motion** setting. No perpetual Avalonia animation was added to
the CPU-rasterised desktop surface.

Still to add: server filtering/sorting/favorites/recent history and deeper Replay
Studio filtering/timeline/analytics refinement.

## P1 — Lobby

Rebuild `LobbyScreen` around three regions:

- roster / teams
- selected map + match preview
- rules / owner controls

Implemented in-place over the existing authoritative `LobbyScreen`: roster, Arena,
Match Rules/owner actions, full-width lobby chat and a hub-native Leave/Ready/Start
footer. Narrow layouts stack the regions inside a scroller instead of crushing them.

Still to add: make owner actions a selected-player contextual surface rather than a
permanent owner section, and add deterministic rendered-lobby coverage.

## P2 — Settings, clips and pause

In progress:

- `HubSettingsView` provides a modern Display / Graphics / Audio / Controls /
  Replays / Profile / Credits landing surface while `SettingsView` remains the
  single transactional save/apply implementation.
- `SettingsView` now uses the same tactical hub chrome for its detail pages:
  selected category rail on desktop, horizontal category navigation on compact
  layouts, and hub-native Back / Save / Apply actions.
- `PauseMenuView` now uses the hub action language and explicitly says when a
  live network session continues behind the menu.

### Settings

Current information architecture:

- Display
- Graphics
- Audio
- Controls (Keyboard / Gamepad / Stylus)
- Replays
- Profile / Network
- Credits

Graphics now includes 25–300% internal render scale (above 100% is
supersampling), lighting, fog, bilinear/trilinear filtering, anisotropic
filtering up to 16x, cel shading bands and outline strength. Display keeps window/view/frame-pacing/HUD/accessibility controls.

Add search, per-setting descriptions, Basic/Advanced grouping, category reset, dirty
state and restart-required markers.

### Replay Studio

Implemented as a first-class Home destination in `HubReplayStudioView`:

- recording + virtual-clip library
- selected replay/map preview and metadata
- watch/import/rename/favorite/delete
- integrity checks and interrupted-recording recovery
- export and desktop folder reveal
- responsive desktop/phone layout
- direct playback into the existing Replay Studio in-match controls

Still to add: richer filtering/search, thumbnail timeline cards, batch operations,
and library-level highlight/analytics summaries.

### Pause/results

Use the same shell language without hiding the still-running network match. Keep
game-owned scoreboard/results state authoritative.

## P3 — renderer/performance

The current UI path can spend tens of milliseconds committing a moving off-screen
Avalonia surface because the full UI is rasterized on CPU. Do not paper over that with
lower-resolution UI.

After P0-P2 are structurally separated:

1. benchmark the same hub on all target resolutions;
2. move continuous visual motion to the OpenGL scene;
3. keep Avalonia redraw event-driven;
4. prototype one representative screen in:
   - NoesisGUI,
   - RmlUi,
   - React + Coherent Gameface if React/TypeScript authoring is a priority;
5. compare CPU frame cost, package size, Android support, controller input, text quality,
   build complexity and licensing before choosing a renderer migration.

## Non-negotiable acceptance criteria

- One physical game window on desktop.
- Android remains supported by the shared UI/application contracts.
- Mouse, keyboard, controller and touch remain first-class.
- No UI view becomes a second source of truth for lobby/match/network state.
- Existing game-file setup, updater, replay and launch flows remain reachable.
- 16:9, ultrawide, 4K and phone-sized layouts remain usable.
- UI screenshots/checks cover every shell destination.
- No continuous Avalonia animation is added without measuring redraw cost.
