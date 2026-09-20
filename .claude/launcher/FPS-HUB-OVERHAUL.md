# Modern FPS Hub overhaul

Status: **P0 foundation implemented on `feature/modern-fps-hub`**

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
  - Servers
  - Custom
  - Clips
  - Settings
  - Support
  - Quit
- Live local profile/hunter/game-data status.
- Existing `HunterStand` reused rather than introducing another preview path.
- Desktop three-column layout and compact Android/small-window navigation.
- Existing Play/Lobby/Settings/Replay screens remain the source of truth.
- Existing game-file setup and updater paths remain owned by `StartScreen`.
- Screenshot automation updated to address the new hub controls.

Still P0:

- Move launcher-only state behind small view-model/state objects:
  `HubState`, `NavigationState`, `PlayerProfileState`.
- Add explicit navigation IDs/neighbours for every hub action.
- Add deterministic hub UI checks for desktop, compact and controller focus.
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

Do not duplicate `NetSession` logic in views. Introduce view models that adapt existing
network state to presentation state.

## P1 — Lobby

Rebuild `LobbyScreen` around three regions:

- roster / teams
- selected map + match preview
- rules / owner controls

Owner actions become contextual to the selected player instead of permanently occupying
the screen. Chat remains docked and controller reachable. Ready/start states must be
visually unambiguous and driven only by authoritative lobby state.

## P2 — Settings, clips and pause

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
