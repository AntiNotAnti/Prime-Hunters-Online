# Launcher — overview

This file summarises the launcher features and where its code lives.

Basics

`MphRead -launcher` opens a responsive FPS-style command hub inside the game
window. The home surface exposes **Play, Map Editor, Replay Studio, Settings and Quit**.
Play opens Multiplayer, Offline and Adventure. Multiplayer is one integrated
workspace: live server browser in the body, Quick Play as a primary action, and
Create Lobby for hosting. Map Editor is intentionally a placeholder while the
editor itself remains out of scope.

The front screen is **drawn inside the game window** -- one window for the
whole program, matches loaded into it and unloaded again. See
`LAUNCHER-WINDOW.md` for how, and for the traps.

| Entry | What it does |
|---|---|
| Play | opens `HubPlayView`: Multiplayer, hub-native Offline setup or hub-native Adventure saves |
| Multiplayer | one workspace with live servers, Quick Play, direct Join, Refresh and Create Lobby; selected-server map artwork and hunter/suit setup stay visible in the same screen |
| Map Editor | placeholder for the future visual custom-map editor |
| Replay Studio | opens `HubReplayStudioView`: recordings/virtual clips, map and metadata detail, watch/import/rename/favorite/integrity/recovery/export/delete, then the existing in-match Replay Studio editor during playback |
| Settings | display, audio, controls, player/profile and launcher preferences |
| Game files | first-run cartridge setup; normal hub actions stay behind it until setup completes |

Key implementation notes

- **One launcher, in Avalonia, on every platform.** Windows, Linux and macOS run
  the same screens; there is no second toolkit and no per-platform launcher any
  more. `Mods/Launcher/Gui/` is the whole of it. The home surface is now
  `HubHomeView`; the existing Play/Lobby/Settings/Replay views remain shared
  underneath it while the FPS-hub migration proceeds.
- The FPS shell uses **Inter** for player-facing headings/labels and JetBrains Mono
  only for technical/status data. The old Pixelify display role is no longer the
  launcher default.
- Shared controls are still painted by this code (`GuiTheme`, `ChoiceRow`,
  `SliderRow`, `KeyRow`, `HubNavButton`); text boxes and scroll bars use the
  toolkit surface under the same palette.
- The picture is a map preview out of `thumbnails/`, rendered from the user's own
  files -- no art is shipped. A `splash.png` beside the exe replaces the home
  picture.
- Choices live in `launcher.txt` beside the exe (`LauncherPrefs`) and keys in
  `controls.txt` (`InputSettings`).
- Two front screens coexist: the window (`Mods/Launcher/Gui/`) and the text one
  (`Mods/Launcher/Portable/TextLauncher.cs`), over shared logic in
  `Mods/Launcher/Portable/`.
- `-launcher -text` forces the text launcher; the code falls back to text when
  there is no display.

Windows, and the loop

- A bare invocation opens the launcher on Windows, Linux and macOS. On a
  headless Linux session the graphical launcher fails cleanly and falls back to
  the text launcher. `-launcher` explicitly requests the launcher, while
  `-menu` keeps the upstream console menu available on demand.
- The toolkit is set up **once per process, on the game's own thread**
  (`GuiLauncher.EnsureSetup`), on Avalonia's headless backend: the screens are
  rendered into a buffer and composited into the game window. `Shell.Run` is
  the loop -- one front screen, then a match built into the same window, then
  the front screen again; "Quit" is what ends the program, and closing the
  window is the same thing because there is only one.

First-run behaviour and progress

- First run shows only the game-files card until extraction completes.
- The progress bar is milestone-driven: `SetupProgress` classifies output into
  phases rather than counting files first.

macOS and Android

- **macOS** publishes and runs `-smoketest` on matching Apple Silicon and
  Intel runners. Releases are ad-hoc signed `.app` bundles in `.tar.gz` files.
  Native dependencies stay beside the executable inside Contents/MacOS, maps
  live in Contents/Resources, and writable state goes to Application Support. See `../build-deploy/MACOS.md`.
  The smoke test checks headless startup; it does not prove a visible GLFW
  window, OpenGL gameplay, or Gatekeeper acceptance of an Internet download.
- **Android** is `src/MphRead.Android/`, a head project compiling the same
  sources with `ANDROID` defined. It builds the launcher and a playable match:
  desktop GL calls are redirected to the OpenGL ES compatibility layer and
  keyboard/mouse input is synthesized from touch/controller input. The launcher
  and an offline match have been driven on an emulator with extracted game files.
  Emulator rendering is not a quality reference; real-device touch, driver and
  presentation behavior remain the important manual checks. See
  `../android/ANDROID-PORT.md` and `../KNOWN-GAPS.md`.
- The head is still a compile check on shared code: it **stops building** the
  moment that code grows something desktop-only. It already forced out
  `LauncherPrefs.Directory` (an Android package's directory is read-only, so
  the head points `launcher.txt` at the app's data directory), the matching
  `GameFiles.Root` for `paths.txt`, and the `ANDROID` guard in `GuiLauncher`
  (Android stands the toolkit up from its activity, with no desktop backend
  to detect).
- Building Android needs the workload, a JDK 17 and an SDK with the platform
  required by `net10.0-android36.0` (currently Android API 36):
  ```bash
  export JAVA_HOME=$HOME/jdk17
  dotnet workload install android
  dotnet build src/MphRead.Android/MphRead.Android.csproj -c Debug \
    -p:AndroidSdkDirectory=$HOME/android-sdk
  ```
  `EnableAvaloniaXamlCompilation=false` is deliberate: there is no XAML in
  this project (every screen is C#), and with AvaloniaResource items present
  the XAML compiler runs anyway and disagrees with the Android SDK about
  where it left the assembly, failing the build after a clean compile. The
  `avares://` assets are embedded by a different target and are unaffected.

## ⚠️ "Random" is a menu entry, not a hunter

`Hunter.Random` is offered by every front screen and has no entry in
`Metadata.HunterModels`, which has one for each of the seven and for the
Guardian. Nothing rolled it into a real hunter, so picking Random and starting
anything threw `KeyNotFoundException` the moment `PlayerEntity.Create` asked
for a model -- *"The given key 'Random' was not present in the dictionary"* on
the desktop, and `Arg_KeyNotFoundWithKey` on Android, which is the same
exception with the message strings trimmed out. Every platform, every match
kind, since Random was added.

`Hunters.Resolve` (`Mods/Launcher/Portable/LaunchPlan.cs`) rolls it, and
`LaunchPlan.Hunter` resolves on the way in, so no consumer of a plan can see
Random. Two things about it are not obvious:

- **The roll is held for one launch.** Joining a server announces the hunter
  (`NetLaunch.Join`, which resolves too) *before* the plan carrying it is
  built, so two independent rolls would put a player on the roster as one
  hunter and draw them as another -- on their own screen and everyone else's.
- **It is rerolled where a launch begins**, not where the match ends:
  `GuiLauncher` and `TextLauncher` before they ask, and `HomeView.Reset`,
  which is Android's equivalent (one HomeView lives for the life of the app).
  Without that, "Random" picks one hunter and gives it to you for the rest of
  the session.

The preference keeps the word: `LauncherPrefs.LastHunter` still stores Random,
so the picker still shows it and the next match rolls again.

See also: .claude/launcher/LAUNCHER-DESIGN.md, .claude/launcher/LAUNCHER-SETTINGS.md, .claude/launcher/LAUNCHER-FIRSTRUN.md
