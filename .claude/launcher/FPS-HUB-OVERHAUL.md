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

- Add reduced-motion/high-contrast theme tokens before more animation is introduced.
- Baseline UI composition cost at 1080p/1440p/4K.

## P1 — Play and multiplayer

Turn Play from a multipurpose form into hub destinations backed by shared state:

1. **Quick Play**
   - matchmaking policy / preferred mode
   - region/ping preference
   - one primary action
2. **Server Browser**
   - filters, sort, favorites and recent servers
   - rich detail drawer
   - compatibility and latency badges
3. **Custom Match**
   - create/join split
   - lobby discovery
   - map rotation summary
4. **Offline / Training**
   - map cards
   - bot count/skill
   - fast rematch
5. **Story**
   - save cards and progression summary

Current implementation:

- `HubPlayView` now separates Multiplayer / Offline / Adventure.
- `HubMultiplayerView` owns Quick Play / Server Browser / Custom Match, removing redundant multiplayer actions from Home.
- `ServerBrowserService` owns renderer-neutral discovery, probing, endpoint parsing and
  joining; both the transitional Play face and the new browser share its join path.
- `HubServerBrowserView` is the modern server browser with direct-connect, operative
  identity, selected-session detail, Refresh/Create/Join actions and lobby handoff.
- Populated desktop/phone browser captures use deterministic sample data, never the live
  directory.

- `Quick Play` now chooses the lowest-latency compatible open server through the
  same portable discovery service and joins through the same shared join path.

Map Editor intentionally remains a placeholder while the UI shell is stabilized.
Still to add: server filtering/sorting/favorites/recent history, and the final
Offline/Adventure hub-native match configuration surfaces.

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

- `HubSettingsView` provides a modern Display / Audio / Controls / Replays /
  Profile / Credits landing surface while `SettingsView` remains the single
  transactional save/apply implementation.
- `PauseMenuView` now uses the hub action language and explicitly says when a
  live network session continues behind the menu.

### Settings

New information architecture:

- General
- Video
- Graphics
- Audio
- Controller
- Keyboard & Mouse
- Stylus
- Gameplay
- Network
- Accessibility

Add search, per-setting descriptions, Basic/Advanced grouping, category reset, dirty
state and restart-required markers.

### Clips / replay studio

- thumbnail timeline cards
- duration/date/map/player metadata
- favorite/filter/search
- record/clip health diagnostics
- direct playback and export actions

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
