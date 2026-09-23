# Persistent Project Prime shell

The launcher has seven cached destinations (News, Play, Hunter License, Theatre,
Forge, Offline, Settings) and one session-owned Lobby route. `StartScreen` owns
setup, updates and game handoff; `PrimeRouter` changes only the workspace.
The header, footer and overlay host remain mounted while navigating.

The launcher first shows the bundled `launcher-bg.png` artwork with a flashing
press-start prompt. Enter/Space, controller Start/A, or a click/tap reveals the
already mounted shell through a short fade. The gate appears once per launcher
instance; match returns retain the shell. Setup and update prompts wait until the
gate is dismissed, and shell navigation stays blocked during the transition.
Reduced motion keeps the prompt steady and makes the reveal immediate. The
startup bitmap and flash timer are released after continuing.

The shared palette uses Project Prime blue `#1C72E1` for primary actions,
electric blue `#2B9EF7` for interactive highlights, bright blue `#61B6F6` for
accent text, and deep blue `#175AB1` for pressed actions. Semantic success,
warning and error colors remain distinct.

## Implementation map

| Plan area | Implementation |
| --- | --- |
| P0 theme and shell | `Theme/PrimeTheme`, `PrimeMetrics`, shared controls, `Shell/PrimeShell`, router, workspace cache and modal host |
| P1 News | Bundled structured dispatch provider, filters, feature and feed; no invented live events |
| P2 Play | Direct multiplayer browser, filters/search, selected-session inspector, favorites, loadout, cancellable quick play and direct connect; lobby creation sheet |
| P3 Offline | Bot configuration and advanced rules alongside three actual Adventure save slots and shared loadout |
| P4 License | Stable profile snapshot, retained sidebar and tab content, overview metrics, history, account and existing career features |
| P5 Theatre | Archive/search/organization, cached thumbnails, metadata, integrity/export actions and a shell-native desktop editor mode over the existing replay scene |
| P6 Settings | Existing settings controls/persistence arranged in three columns; one draft across categories, Apply/Discard/Cancel navigation guard and live FOV schematic |
| P7 Lobby | Session-owned coordinator, persistent return indicator, roster, loadout, arena, rules, comms, invites, teams/admin and destructive-action confirmations |
| P8 Forge | Cached Map Studio document, viewport, undo/selection/camera state and playtest return; shared sheets and theme |
| Input and layout | Q/E and controller bumpers, spatial arrows/d-pad, per-route focus memory, modal trapping/restoration, responsive tactical canvas |
| Regression coverage | `-primeuicheck`, existing controller/pointer suites, CI route captures and unchanged networking/editor/replay regression suites |

Existing row controls (choices, sliders, fields, toggles) retain their tested input
behavior and share the Prime palette through compatibility theme aliases. There
is no second settings serializer or new network/profile service. Superseded home,
play-type, offline/adventure and settings-category landing screens are removed.

## Typography

Rajdhani Bold supplies headings and Rajdhani SemiBold supplies tactical buttons,
map names and server titles. Inter remains the reading face for descriptions and
settings; JetBrains Mono remains the telemetry face. The two unmodified Rajdhani
TTFs and SIL OFL 1.1 license are bundled for desktop and Android; no installed
system font or network download is needed at runtime. Source:
https://github.com/google/fonts/tree/main/ofl/rajdhani.

## Ownership and behavior

- `PrimeWorkspaceHost` lazily creates and retains controls. Route changes detach
  the inactive visual tree, stop its visual timers and retain selections/drafts.
  Forge and Theatre release retained native resources when their owner ends.
- `LobbySessionCoordinator` pumps the lobby control plane every 50 ms while the
  launcher is attached. Inactive lobby presentation is not rebuilt. A server
  start overrides route guards while preserving a settings draft; gameplay takes
  over the pump. Leaving/losing the session removes the Lobby route/cache.
- Roster topology changes create/remove rows. Readiness, hunter, team, selection
  and ping changes update existing rows. Display refreshes are throttled to one
  second; pending commands/start barriers retain their existing backend cadence.
  Both coordinator and chrome use a bounded UI-thread pulse that also works in
  the embedded desktop event loop. No lobby pulse runs without an active screen.
- `PrimeGlobalState` refreshes chrome once per second. Simulation is **60 Hz**;
  no 128 Hz, player population, GPU latency, rank or server health is invented.
  Update progress is preserved across telemetry refreshes.
- `PrimeOverlayHost` blocks pointer fall-through, cycles keyboard focus, confines
  directional navigation and restores prior focus. Escape/B closes the top sheet
  first. Android Back is consumed at the root; quitting requires the system menu.
- Q/E belongs to text/binding capture or the focused Forge viewport when editing;
  otherwise it switches routes. Route motion uses the renderer frame callback,
  including the headless desktop compositor. The replay editor uses existing transport,
  timeline, cameras and export controls; explicit fullscreen hides the shell.
- Spectate joins an available ordinary player slot and then enters the existing
  spectator camera after loading. It does not bypass server capacity, passwords,
  admission rules or add a new spectator network protocol.
- Bot results offer a searchable tactical arena grid, selected destination,
  countdown and an immediate next-match action. When no arena is selected,
  the current arena repeats with the same hunter, mode, bots and difficulty.
  Desktop and Android queue the continuation through their existing launch
  paths; Forge playtests and Adventure retain their return behavior.
- The lobby map sheet uses the same tactical cards with search, a larger arena
  preview and explicit Cancel/Use Map actions.
- Invite copies the current address on explicit interaction. Locally hosted
  loopback addresses are labeled as requiring a LAN/public address for sharing.

## Supported backend limits

Mockup data is illustrative. The implementation only exposes available gameplay
and service capabilities: bundled News instead of a new feed backend; actual
profile values instead of fictional weapon proficiency/hardware telemetry;
existing lobby host/region rules instead of unsupported private/password fields.
The existing Forge renderer is desktop-only. Android shares the launcher shell,
settings and gameplay handoff; its replay presentation retains the existing
Android game surface rather than adding another native renderer.

The canvas uses a 1440×810 safe composition and proportional scaling at smaller
landscape sizes, preserving columns and primary actions. Long archives and
settings forms scroll within their own panels, not as one scrolling application.

## Verification

Run from a .NET 10 checkout:

```sh
dotnet build src/MphRead/MphRead.csproj
dotnet run --project src/MphRead/MphRead.csproj -- -primeuicheck -shots /tmp/prime-shell
dotnet run --project src/MphRead/MphRead.csproj -- -gamepadcheck
dotnet run --project src/MphRead/MphRead.csproj -- -pointercheck
dotnet run --project tools/nettest/nettest.csproj -- --lifecycle
dotnet run --project tools/map-editor-check
dotnet run --project tools/replay-timeline-check
```

`primeuicheck` checks persistent chrome/cached controls, selection/focus retention,
keyboard/controller modal confinement, settings/category discard (including a
fresh controller preset), update status, server capacity and eight-player roster
bounds. It writes PNG and geometry JSON for all eight routes at 1920×1080,
1600×900, 1440×900, 1366×768, 1280×720 and 830×390. Primary action bounds are
checked against the complete canvas. Six additional startup captures check the
artwork and bottom prompt at those sizes. Startup checks cover keyboard,
controller Start/A, pointer activation, blocked navigation, deferred setup and
one-time entry. CI uploads these as `prime-shell-layouts`.

Captures may contain the user's locally extracted game imagery and are not
committed. CI runs without proprietary assets. Local image review accompanies
geometry checks; these are not pixel-equality tests against concept art.

Manual release acceptance still requires actual mouse/touch/controller sessions
on target hardware, an Android landscape device, a live multi-client lobby
(start/leave/rejoin while visiting other tabs), and recorded replay/Forge
playtest round trips. The local game-window smoke also exercises Offline launch, fullscreen/resize,
pause/settings, default bot rematches, selected next-map transitions and explicit match return. Automated builds and landscape captures do not substitute
for these device and multiplayer checks.
