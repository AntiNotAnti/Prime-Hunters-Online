using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Entities;
using MphRead.Mods.Network;

namespace MphRead.Mods
{
    /// <summary>
    /// Single dispatch point for everything under Mods/.
    ///
    /// Upstream is touched in exactly one place (a call to TryHandle in
    /// Program.Main) so that pulling from NoneGiven/MphRead stays a fast
    /// forward instead of a conflict hunt. Every new mod command is added
    /// here, not in Program.cs.
    /// </summary>
    public static class ModEntry
    {
        /// <summary>
        /// Returns true if a mod command handled this invocation and the
        /// program should exit without running the normal paths.
        ///
        /// Takes the raw argv rather than Program's parsed Argument type,
        /// which is private: matching on the raw strings keeps the upstream
        /// hook to a single line and adds no coupling to internals that may
        /// be refactored later.
        /// </summary>
        /// <summary>
        /// Commands dispatched before the ordinary client game-file setup check.
        /// Metadata-only replay tools, the directory/master server and several
        /// diagnostics need no extracted assets. A dedicated game server is
        /// dispatched here too, but it performs its own authoritative-world
        /// validation and refuses to start without paths.txt/game files.
        /// </summary>
        public static bool TryHandleHeadless(string[] args)
        {
            if (HasFlag(args, "replayformatcheck"))
            {
                Environment.ExitCode = Network.ReplayFormatCheck.Run();
                return true;
            }
            if (ValueAfter(args, "replayvalidate") is string validatePath)
            {
                var result = Network.ReplayArchive.Validate(validatePath);
                Console.WriteLine($"[replayvalidate] {result}");
                Environment.ExitCode = result == Network.ReplayOpenResult.Success ? 0 : 1;
                return true;
            }
            if (ValueAfter(args, "replayrecover") is string recoverPath)
            {
                bool recovered = Network.ReplayArchive.Recover(recoverPath, out string? output, out var result);
                Console.WriteLine($"[replayrecover] {result}: {output}");
                Environment.ExitCode = recovered ? 0 : 1;
                return true;
            }
            if (HasFlag(args, "replaycontrolcheck"))
            {
                Environment.ExitCode = Network.ReplayControlCheck.Run();
                return true;
            }

#if !ANDROID && !MPHREAD_SERVER
            if (OperatingSystem.IsMacOS())
            {
                // OpenTK defaults to Apple's system framework on macOS,
                // not the OpenAL Soft library shipped beside our executable.
                OpenTK.Audio.OpenAL.OpenALLibraryNameContainer.OverridePath =
                    System.IO.Path.Combine(Platform.AppPaths.ExecutableDirectory, "libopenal.1.dylib");
            }
#endif
#if MPHREAD_SHELL
            if (HasFlag(args, "glfwpathcheck"))
            {
                Environment.ExitCode = Diagnostics.GlfwPathCheck.Run();
                return true;
            }
            if (HasFlag(args, "thumbnailwindowcheck"))
            {
                Environment.ExitCode = Diagnostics.ThumbnailWindowCheck.Run(HasFlag(args, "legacyglcheck"));
                return true;
            }
            if (HasFlag(args, "windowcheck"))
            {
                Environment.ExitCode = Diagnostics.LauncherWindowCheck.Run();
                return true;
            }
#endif
            if (HasFlag(args, "smoketest"))
            {
                Environment.ExitCode = Diagnostics.CompatibilityCheck.Run();
                return true;
            }
            // Keys and mouse feel, before anything creates a player. Called
            // here because this runs for every invocation, launcher or not.
            InputSettings.Load();
            // And the file of everything the program can say about itself, if
            // the player has asked for one. Read the preferences here rather
            // than waiting for the launcher to: a crash while a map loads
            // happens on paths that never open one, and the point of the log
            // is to be already running when that happens. -debuglog turns it
            // on for a single run without the setting, for the case where the
            // launcher itself is what will not start.
            Launcher.LauncherPrefs.Load();
            if (HasFlag(args, "debuglog") || HasFlag(args, "respawnrendercheck"))
            {
                DebugLog.Force();
            }
            DebugLog.Attach();
            if (OperatingSystem.IsMacOS()) { Diagnostics.PlatformDiagnostics.Start(); }
            Update.Updater.Disabled = HasFlag(args, "noupdate");
            ApplyRenderOverrides(args);

            // This diagnostic needs assets, but must not apply/clean updates
            // or enter any of the launcher/network command paths.
            if (HasFlag(args, "respawnrendercheck"))
            {
                Update.Updater.Disabled = true;
                return false;
            }

            if (HasFlag(args, "pointercheck"))
            {
                Environment.ExitCode = Input.PointerCheck.Run();
                return true;
            }


            Input.AimAssist.AimAssistDebug.Enabled = HasFlag(args, "gamepadassistdebug");
            Input.AimAssist.AimAssistDebug.UnassistedArm = HasFlag(args, "gamepadassistbaseline");
            Input.AimAssist.AimAssistTelemetry.Configure(ValueAfter(args, "gamepadassisttelemetry"));

            if (HasFlag(args, "gamepadcheck"))
            {
                Environment.ExitCode = Input.GamepadChecks.Run(ValueAfter(args, "shots"));
                return true;
            }
            if (HasFlag(args, "gamepad"))
            {
                double seconds = 15;
                string? given = ValueAfter(args, "seconds");
                if (given != null && Double.TryParse(given, out double parsed) && parsed > 0)
                {
                    seconds = parsed;
                }
                Environment.ExitCode = Input.GamepadProbe.Run(seconds, HasFlag(args, "verbose"));
                return true;
            }

            // Arithmetic and cosmetic-noise checks need no extracted game files.
            if (HasFlag(args, "frametimingcheck"))
            {
                Environment.ExitCode = Render.FrameTimingCheck.Run();
                return true;
            }

            // The copying half of a desktop update, which is this build
            // started by the *previous* one. First, and before anything reads
            // a file or draws a window: it is not the game, it waits for the
            // old process to exit and copies itself over the installation.
            // See Mods/Update/DesktopUpdate.cs.
            int applyAt = IndexOfFlag(args, Update.DesktopUpdate.ApplyFlag);
            if (applyAt >= 0 && applyAt + 2 < args.Length)
            {
                // Two values, read by position rather than by name: the first
                // is a directory, and a directory is exactly the kind of
                // argument that can begin with a dash.
                //
                // Anything after the separator is what the updated build is to
                // be started with -- empty for a launcher, and the server's own
                // command line for a server, which is otherwise restarted as a
                // launcher on a machine with nobody at it.
                var relaunch = new System.Collections.Generic.List<string>();
                int separator = Array.IndexOf(args, Update.DesktopUpdate.RelaunchSeparator,
                    applyAt + 3);
                for (int i = separator + 1; separator >= 0 && i < args.Length; i++)
                {
                    relaunch.Add(args[i]);
                }
                Environment.ExitCode = Update.DesktopUpdate.Apply(args[applyAt + 1],
                    Int32.TryParse(args[applyAt + 2], out int parsed) ? parsed : -1,
                    relaunch);
                return true;
            }
            // Whatever the last update left behind. The headless diagnostics
            // are read-only and must leave an update staged beside the
            // executable alone; ordinary startup still clears it.
            if (!HasFlag(args, "spireposecheck") && !HasFlag(args, "formcheck"))
            {
                Update.DesktopUpdate.Clean();
            }
            // And the desktop's own installer, unless a platform head has
            // already put its own in place.
            Update.UpdateInstall.UseDesktopIfPossible();

            // A bad line, asked for. Before anything opens a socket, and for
            // every path that has one -- the game, the harness client and the
            // dedicated server alike -- so a fault that only shows up at 200
            // ms can be reproduced against the real server rather than only
            // behind a proxy in front of a local one. See Mods/Network/NetLag.
            string? netLag = ValueAfter(args, "netlag");
            if (netLag != null && !Network.NetLag.Configure(netLag))
            {
                Console.WriteLine($"[net] -netlag {netLag} is not a number of "
                    + "milliseconds (try -netlag 200 or -netlag 200:40)");
                return true;
            }
            string? netLoss = ValueAfter(args, "netloss");
            if (netLoss != null && !Network.NetLag.ConfigureLoss(netLoss))
            {
                Console.WriteLine($"[net] -netloss {netLoss} is not a percentage");
                return true;
            }
            foreach (var option in new (string Name, Func<string?, bool> Configure)[]
            {
                ("netjitter", Network.NetLag.ConfigureJitter), ("netseed", Network.NetLag.ConfigureSeed),
                ("netreorder", Network.NetLag.ConfigureReorder), ("netduplicate", Network.NetLag.ConfigureDuplicate)
            })
            {
                string? value = ValueAfter(args, option.Name);
                if (value != null && !option.Configure(value))
                {
                    Console.WriteLine($"[net] invalid -{option.Name} value: {value}");
                    return true;
                }
            }
            if (Network.NetLag.Active)
            {
                Console.WriteLine($"[net] simulating a bad line: {Network.NetLag.Describe()}");
            }

            // Lag compensation, off. Here for the same reason -netlag is: so a
            // run of the harness can measure the same match with and without
            // it, which is the only way to say what it is worth. On by
            // default, as Zandronum's sv_nounlagged is.
            if (HasFlag(args, "nounlagged"))
            {
                Network.NetUnlagged.Enabled = false;
                Console.WriteLine("[net] lag compensation off: shots resolve "
                    + "against the present");
            }

            // Puppet positions on a client come from the snapshot alone, not
            // from the owner's relayed intent. See NetHooks.SnapshotOwnsPuppets
            // for the measurement: a shooter aiming at the drawn world and
            // firing into the relayed one landed 11 of the 78 hits the
            // authority credited it with.
            if (HasFlag(args, "snapshotpuppets"))
            {
                Network.NetHooks.SnapshotOwnsPuppets = true;
                Console.WriteLine("[net] puppet positions on this client come "
                    + "from the authority's snapshot, not from relayed intents");
            }

            // Puppets are put back where their owner said after the movement
            // step on clients too, not only on the authority. Off by default
            // and measured against on: see NetHooks.PinPuppetsOnClients for
            // the frame of physics this removes from between the two worlds.
            if (HasFlag(args, "clientpin"))
            {
                Network.NetHooks.PinPuppetsOnClients = true;
                Console.WriteLine("[net] puppets are pinned to their owner's "
                    + "reported position on clients as well as on the authority");
            }

            // Recovered trigger pulls carry their own age by default. Keep
            // both switches so old test scripts still work and -nopressage is
            // the explicit control arm.
            if (HasFlag(args, "nopressage"))
            {
                Network.NetUnlagged.PressAgeEnabled = false;
                Console.WriteLine("[net] recovered trigger pulls use the carrying packet's ack only");
            }
            else if (HasFlag(args, "pressage"))
            {
                Network.NetUnlagged.PressAgeEnabled = true;
            }

            // The headshot duel, in place of the feature tour. Both arms of a
            // comparison run the same one, so a difference between them is
            // the thing being changed rather than the scenario.
            string? rig = ValueAfter(args, "hitrig");
            if (rig != null)
            {
                if (Network.HitRig.Configure(rig))
                {
                    Console.WriteLine($"[net] hit rig: {Network.HitRig.Mode}");
                }
                else
                {
                    Console.WriteLine($"[net] -hitrig {rig} refused: jump, sniper or duel");
                }
            }

            // How far back the rewind may ever be taken, in frames. The one
            // number an A/B against a real line has to be able to move
            // without moving anything else: the default 24 (400 ms) was
            // measured against Japan running into its own ceiling, and a run
            // that raises it has to be otherwise identical to the run that
            // did not. Read on the machine that simulates the match, which is
            // the only one that rewinds anything.
            string? maxRewind = ValueAfter(args, "maxrewind");
            if (maxRewind != null)
            {
                if (Network.NetUnlagged.ConfigureMaxRewind(maxRewind))
                {
                    Console.WriteLine("[net] rewind ceiling "
                        + $"{Network.NetUnlagged.MaxRewindFrames} frames "
                        + $"({Network.NetUnlagged.MaxRewindFrames * 1000 / 60} ms)");
                }
                else
                {
                    Console.WriteLine($"[net] -maxrewind {maxRewind} refused: "
                        + $"1 to {Network.NetUnlagged.MaxRewindCeiling} frames, "
                        + "and the history cannot serve more");
                }
            }

            // Client-side hit resolution, off. The other half of the same
            // measurement: -nounlagged asks what the authority's answer is
            // worth, this asks what not waiting for it is worth. On by
            // default, and inert on the machine running the match, which
            // never waited for anybody.
            if (HasFlag(args, "nohitprediction"))
            {
                Network.NetHitPrediction.Enabled = false;
                Console.WriteLine("[net] hit prediction off: a client's hits "
                    + "land when the authority says so");
            }
            if (HasFlag(args, "nohitmarker"))
            {
                Network.NetHitPrediction.MarkerEnabled = false;
                Console.WriteLine("[hud] hit marker off");
            }
            // The lethal half of the same measurement. Off by default now:
            // measured against Japan, a client could kill the same opponent
            // twice for one kill on the scoreboard, because the authority
            // disagreed and the next snapshot stood the body back up. A
            // prediction is clamped to leave the victim standing on one point
            // of health and the dying waits for the authority.
            //
            // A self-kill -- a rocket jump, a recoil, a fall into the void --
            // is predicted whatever this says, and this does not turn it off:
            // there is nothing to disagree about when the source, the target
            // and the input are all on this machine.
            if (HasFlag(args, "deathprediction") || HasFlag(args, "nodeathprediction"))
            {
                Console.WriteLine("[net] remote death waits for authority; self-death remains predicted");
            }

            // A client declaring which of its own shots landed, and the
            // authority arbitrating them. On by default since protocol 7. Off
            // restores exactly what protocol 6 did -- the authority's own
            // answer and nothing else -- which is the control arm for
            // measuring what claims are worth. Read on both ends: a client
            // with this off sends none, and a server with it off answers none.
            // Network.NetHitClaims.
            if (HasFlag(args, "noclaims"))
            {
                Network.NetHitClaims.Enabled = false;
                Console.WriteLine("[net] hit claims off: a shot counts only "
                    + "where the authority finds it itself");
            }

            // Remote players drawn on a playout clock, a few frames behind the
            // newest snapshot, rather than snapped to whichever one arrived
            // last. On by default; off is the stutter every build before
            // protocol 7 had, and the control arm. Network.NetSmoothing.
            if (HasFlag(args, "nointerp"))
            {
                Network.NetSmoothing.Enabled = false;
                Console.WriteLine("[net] puppet interpolation off: remote "
                    + "players move when their snapshots arrive");
            }

            // Puppet positions on a client come from the owner's relayed
            // intent again, the way every build before protocol 7 did. The
            // control for -snapshotpuppets, which is now the default -- see
            // NetHooks.SnapshotOwnsPuppets for the measurement that made it
            // one, and note that it also turns the playout clock off, since a
            // clock and a relayed intent writing the same puppet on alternate
            // frames is worse than either.
            if (HasFlag(args, "relayedpuppets"))
            {
                Network.NetHooks.SnapshotOwnsPuppets = false;
                Network.NetSmoothing.Enabled = false;
                Console.WriteLine("[net] puppet positions on this client come "
                    + "from relayed intents, not from the snapshot");
            }

            if (HasFlag(args, "credits"))
            {
                Credits.Print();
                return true;
            }

            // Where the maps are. Read for every invocation and before
            // anything reads the map list, which is loaded once -- and against
            // the directory the command was typed in rather than the one the
            // process moved itself to (see ConsoleSetup.LaunchDirectory), so
            // `-mapdir maps` from a checkout means that checkout's maps.
            string? mapDir = ValueAfter(args, "mapdir");
            if (mapDir != null)
            {
                MapGen.CustomRooms.MapDirectory = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(ConsoleSetup.LaunchDirectory, mapDir));
            }

            foreach (string command in new[] { "mapvalidate", "mapinspect" })
            {
                if (HasFlag(args, command))
                {
                    Environment.ExitCode = MapGen.MapCommands.Run(command, ValueAfter(args, command), ValueAfter(args, "out"));
                    return true;
                }
            }

            if (HasFlag(args, "mapstudio"))
            {
#if MPHREAD_SHELL
                Launcher.Gui.Shell.OpenStudioOnStart = true;
                Launcher.Gui.Shell.Run();
#else
                Console.WriteLine("[mapeditor] Map Studio requires a desktop game build.");
                Environment.ExitCode = 1;
#endif
                return true;
            }
#if MPHREAD_SHELL
            if(ValueAfter(args,"mapstudioshot") is {} studioShots)
            {
                Environment.ExitCode=Launcher.Gui.MapStudioScreen.Capture(System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(ConsoleSetup.LaunchDirectory,studioShots)));
                return true;
            }
#endif

            // Cooking a bundle is here, before the game-file check, for the
            // reason the dedicated server is: it reads a recipe, the level
            // beside it and the textures baked from it, and touches no
            // extracted game data at all. The workflow runs it on a runner
            // that has none, where the check exits with "press any key" on a
            // console nobody is looking at -- and then throws, because there
            // is no console to read a key from either.
            if (HasFlag(args, "mapbundle"))
            {
                string? which = ValueAfter(args, "mapbundle");
                string? outPath = ValueAfter(args, "out");
                int cooked = 0;
                int failed = 0;
                foreach (MapGen.MapCatalogEntry entry in new MapGen.MapCatalog(MapGen.CustomRooms.MapDirectory).Refresh(false))
                {
                    MapGen.MapDefinition? def = entry.Definition;
                    if (def == null)
                    {
                        continue;
                    }
                    bool requested = which != null && !which.Equals("all", StringComparison.OrdinalIgnoreCase);
                    if (requested && !which!.Equals(def.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!entry.Validation.IsValid)
                    {
                        if (requested)
                        {
                            foreach (MapGen.MapDiagnostic diagnostic in entry.Validation.Diagnostics)
                            {
                                Console.WriteLine($"{def.Name}: {diagnostic.Code}: {diagnostic.Message}");
                            }
                            failed++;
                        }
                        else
                        {
                            Console.WriteLine($"Skipping {def.Name}: its editable source is incomplete on this machine.");
                        }
                        continue;
                    }
                    if (def.SourcePath == null || def.BundlePath != null)
                    {
                        continue;
                    }
                    try
                    {
                        MapGen.MapBundle.Cook(def, def.SourcePath, outPath);
                        cooked++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"{def.Name}: {ex.Message}");
                        failed++;
                    }
                }
                if (cooked == 0 && failed == 0)
                {
                    Console.WriteLine($"No matching editable map source in {MapGen.CustomRooms.MapDirectory}.");
                }
                Environment.ExitCode = failed == 0 ? 0 : 1;
                return true;
            }


            // The explicit check, so there is always one command that answers
            // "am I on the latest build". Nothing is downloaded here either:
            // it prints the release page and opens it if there is a desktop to
            // open it on.
            if (HasFlag(args, "update"))
            {
                Update.Updater.Disabled = false;
                Update.UpdateInfo? update = Update.Updater.Check();
                if (update == null)
                {
                    Console.WriteLine($"[update] {Update.UpdateCheck.LastReason}");
                    return true;
                }
                Console.WriteLine($"[update] {Update.Updater.Describe(update.Value)}");
                Console.WriteLine($"[update] {update.Value.PageUrl}");
                Update.Updater.OpenPage(update.Value);
                return true;
            }
            // Before the game-file check, not after: a fresh install has no
            // paths.txt, and the check exits with "press any key" on a console
            // nobody is looking at. The launcher is the screen that fixes
            // that, so it has to be reachable first.
            // The launcher. The window first, the text screen when there is no
            // display to put it on -- an SSH login, a container, a machine with
            // no X or Wayland session. -text asks for the text one on a machine
            // that has both.
            //
            // No arguments means somebody double-clicked the binary, and on the
            // platforms where that is how a program is normally started that
            // has to be the launcher: the console menu behind it is for people
            // who typed something, and -menu is how they still get it. On Linux
            // a bare invocation has always opened upstream's console menu and
            // still does -- that is a screen people are already using, not an
            // empty spot to fill.
            bool doubleClicked = args.Length == 0
                && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
#if MPHREAD_SERVER
            // Except in the server package, which has no launcher of either
            // kind and ships without game files: a bare invocation there is
            // answered further down by ServerUsage, which says what the binary
            // is for. Double-clicking ProjectPrimeServer.exe must not open a
            // text launcher offering matches it cannot play.
            doubleClicked = false;
#endif
            // The launcher's own screens, looked at without anybody sitting
            // in front of them. Here rather than with the rest of the
            // commands, which run after the game-files check: none of these
            // load a room, all of them exist to be run on a box that has no
            // extracted game files -- CI is exactly that box -- and behind
            // that check they could only ever be run on a machine that was
            // already set up to play.
            // Pictures of the launcher's own screens, rendered without a
            // window. The one part of this program that could not be looked at
            // from a headless box.
            string? uiShot = ValueAfter(args, "uishot");
            if (uiShot != null)
            {
                Environment.ExitCode = RunUiCapture(uiShot);
                return true;
            }

            // What a redraw of those screens costs, split into its parts, at
            // every resolution anybody plays at. Beside the capture because it
            // is the same arrangement -- a headless top level and no game
            // files -- and because "the menus feel slow" is otherwise a report
            // nothing in this program can answer with a number.
            if (HasFlag(args, "uibench"))
            {
                Environment.ExitCode = RunUiBench(args);
                return true;
            }

            // The same three screens laid out five different ways, for
            // choosing between them by looking. Nothing it draws ships; see
            // UiDesigns.
            string? uiDesign = ValueAfter(args, "uidesign");
            if (uiDesign != null)
            {
                Environment.ExitCode = RunUiDesigns(uiDesign);
                return true;
            }

            // The same screens, but photographed *in the game window* -- which
            // is the half -uishot cannot answer, since what it renders is the
            // layout and not the composite. This opens the real shell window,
            // lets it draw, reads the window's own buffer and presses Escape
            // to prove the screens are taking input as well as pixels. Needs a
            // display; Xvfb is one.
            string? shellShot = ValueAfter(args, "shellshot");
            if (shellShot != null)
            {
                Environment.ExitCode = RunShellCapture(shellShot);
                return true;
            }

            if (HasFlag(args, "frametimingcheck"))
            {
                Environment.ExitCode = Render.FrameTimingCheck.Run();
                return true;
            }

            // The rule that tells a tap from the beginning of a scroll, which
            // is what every row on a settings page dragged by a finger turns
            // on. No display, no toolkit and no touchscreen -- see
            // Mods/Launcher/Gui/TapCheck.cs.
            if (HasFlag(args, "tapcheck"))
            {
                Environment.ExitCode = RunTapCheck();
                return true;
            }


            // Display flags, before the launcher and not after it. They used
            // to be read further down, which is past the block below: the
            // shell opens its window there and reads the saved window mode as
            // it does, so `-launcher -fullscreen` was answered windowed by a
            // preference that had not yet been overridden.
            if (HasFlag(args, "fullscreen") || HasFlag(args, "borderless"))
            {
                WindowMode.ForceStartup(WindowStartMode.BorderlessFullscreen);
            }
            else if (HasFlag(args, "windowed"))
            {
                WindowMode.ForceStartup(WindowStartMode.Windowed);
            }
            if ((HasFlag(args, "launcher") || doubleClicked) && !HasFlag(args, "menu"))
            {
#if MPHREAD_AVALONIA
                if (!HasFlag(args, "text") && Launcher.Gui.GuiLauncher.TryRun())
                {
                    return true;
                }
                // The window could not be opened. On Windows that means the
                // process has no console either -- it is a GUI binary -- so the
                // text launcher would print into nothing.
                if (OperatingSystem.IsWindows())
                {
                    Mods.ConsoleWindow.Show();
                }
#endif
                Launcher.TextLauncher.Run();
                return true;
            }
#if MPHREAD_SERVER
            // The server package, run with nothing to do. Falling through to
            // upstream's setup check would answer with "could not find
            // paths.txt, drag a ROM onto the executable", which is the wrong
            // bare-invocation experience for a console server binary. Print
            // server usage first; actually starting -server still requires a
            // valid paths.txt and extracted game files.
            if (args.Length == 0)
            {
                ServerUsage();
                return true;
            }
#endif
            // Both servers keep themselves current: check before binding,
            // check again on a timer, and swap when nobody is connected.
            //
            // This is the one place in the program that installs rather than
            // asking. A protocol change makes a server refuse every client on
            // a different build at Hello, so a stale server is a server nobody
            // in the world can join, and there is no one at the keyboard to
            // read the line that used to be printed here. -noautoupdate keeps
            // the old behaviour, and -noupdate turns off the checking too.
            // See Mods/Update/ServerUpdate.cs for why the swap is not the
            // desktop's.
            if (HasFlag(args, "masterserver") || HasFlag(args, "server")
                || HasFlag(args, "dedicated"))
            {
                Update.ServerUpdate.Enabled = !HasFlag(args, "noautoupdate");
                // The command line this server was started with, so the build
                // that replaces it comes back as the same server rather than
                // as a launcher on a machine with nobody at it. Read from the
                // environment and not from `args`, which by here has already
                // had the update flags taken out of it.
                string[] all = Environment.GetCommandLineArgs();
                var typed = new System.Collections.Generic.List<string>();
                for (int i = 1; i < all.Length; i++)
                {
                    typed.Add(all[i]);
                }
                if (Update.ServerUpdate.AtStartup(typed))
                {
                    return true;
                }
            }

            // The server directory: -masterserver. Same binary as the game
            // server on purpose -- the machine that runs one usually runs the
            // other, and a second thing to install is a second thing to forget
            // to restart.
            if (HasFlag(args, "masterserver"))
            {
                int masterPort = NetMasterConfig.DefaultPort;
                string? masterPortValue = ValueAfter(args, "port")
                    ?? ValueAfter(args, "masterport");
                if (masterPortValue != null && Int32.TryParse(masterPortValue, out int parsedMasterPort))
                {
                    masterPort = parsedMasterPort;
                }
                var master = new MasterServer(masterPort);
                using var masterSignals = new ShutdownSignals();
                // The ports it may start games on, for players whose routers
                // will not forward one. A range by default, because the whole
                // point of the feature is that it works without anybody being
                // asked to configure it; -hostports none turns it off.
                string hostPorts = ValueAfter(args, "hostports") ?? "27900-27919";
                if (!hostPorts.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = hostPorts.Split('-', 2);
                    if (parts.Length == 2 && Int32.TryParse(parts[0], out int first)
                        && Int32.TryParse(parts[1], out int last) && first > 0 && last >= first)
                    {
                        master.SetHostPorts(first, last);
                    }
                    else
                    {
                        Console.WriteLine($"[master] ignoring -hostports {hostPorts} "
                            + "(expected e.g. 27900-27919, or none)");
                    }
                }
                // The address to hand out for servers running on this same
                // machine, which is the usual arrangement: the directory and
                // one game server on one small box. Their heartbeats arrive
                // over the loopback, and a list of loopback addresses is a
                // list of servers nobody can reach.
                string? publicHost = ValueAfter(args, "public")
                    ?? ValueAfter(args, "publicaddress");
                if (publicHost != null)
                {
                    master.SetPublicAddress(publicHost);
                }
                using var masterCancel = new System.Threading.CancellationTokenSource();
                masterSignals.OnShutdown(() =>
                {
                    masterCancel.Cancel();
                    master.Stop();
                });
                master.Run(masterCancel.Token);
                return true;
            }
            // The server list, printed. Same two calls the launcher's browser
            // makes -- ask the directory, then ask each server it named -- so
            // this is how that data path gets checked on a machine with no
            // WinForms, which is every machine that is not Windows.
            if (HasFlag(args, "servers"))
            {
                ListServers(ValueAfter(args, "master") ?? NetMasterConfig.DefaultHost,
                    ValueAfter(args, "masterport"));
                return true;
            }
            // The create-server screen's "Host on" page, printed. Every box
            // the directory knows about, asked on the directory port as well,
            // with the reason against the ones that cannot run a match --
            // which is the question that page exists to answer and the one
            // thing a screenshot of it cannot be diffed against.
            if (HasFlag(args, "hosts"))
            {
                string askHost = ValueAfter(args, "master") ?? NetMasterConfig.DefaultHost;
                int askPort = ParseMasterPort(args);
                Console.WriteLine($"[hosts] asking {askHost}:{askPort} and everyone it names");
                // Printed as they answer, the way the screen fills in, so the
                // order here is the order there and a slow region is visible
                // as a slow region rather than as a pause.
                var candidates = new System.Collections.Generic.List<HostCandidate>();
                using var finished = new System.Threading.ManualResetEventSlim(false);
                NetMasterClient.FindHosts(askHost, askPort,
                    onFound: candidate =>
                    {
                        lock (candidates)
                        {
                            int before = candidates.Count;
                            NetMasterClient.Merge(candidates, candidate);
                            // Only the rows the screen would draw. A machine
                            // that is already on the list under its other port
                            // is not a second place to host.
                            if (candidates.Count > before)
                            {
                                Console.WriteLine($"  {candidate.Label,-28} "
                                    + $"{candidate.Host + ":" + candidate.Port.ToString(
                                        System.Globalization.CultureInfo.InvariantCulture),-26} "
                                    + candidate.Describe());
                            }
                        }
                    },
                    onDone: () => finished.Set());
                finished.Wait(TimeSpan.FromSeconds(20));
                int usable = 0;
                lock (candidates)
                {
                    foreach (HostCandidate candidate in candidates)
                    {
                        if (candidate.WillHost)
                        {
                            usable++;
                        }
                    }
                    Console.WriteLine($"[hosts] {usable} of {candidates.Count} can run a match");
                }
                return true;
            }
            if (!HasFlag(args, "server") && !HasFlag(args, "dedicated"))
            {
                return false;
            }
            int port = NetConfig.DefaultPort;
            string? portValue = ValueAfter(args, "port");
            if (portValue != null && Int32.TryParse(portValue, out int parsedPort))
            {
                port = parsedPort;
            }
            int maxPlayers = 4;
            string? playersValue = ValueAfter(args, "players");
            if (playersValue != null && Int32.TryParse(playersValue, out int parsedPlayers))
            {
                maxPlayers = parsedPlayers;
            }

            // Rotation follows writable user data: beside the executable on
            // Windows/Linux, outside the signed application on macOS.
            string rotationPath = ValueAfter(args, "rotation")
                ?? System.IO.Path.Combine(Platform.AppPaths.UserDataDirectory, "maprotation.txt");
            MapRotation rotation = MapRotation.LoadOrCreate(rotationPath);

            bool serverReplays = !HasFlag(args, "noserverreplays");
            string? serverReplaysValue = ValueAfterAny(args,
                "serverreplays", "server_replays");
            if (serverReplaysValue != null
                && !TryParseOnOff(serverReplaysValue, out serverReplays))
            {
                Console.WriteLine($"[server] ignoring replay setting "
                    + $"{serverReplaysValue} (expected on/off or true/false)");
                serverReplays = true;
            }
            int replayStorageGb = IntOption(args, 25, 0, 1024,
                "serverreplaystoragegb", "server_replay_storage_gb");
            int replayRetentionDays = IntOption(args, 14, 0, 36500,
                "serverreplayretentiondays", "server_replay_retention_days");
            int replayKeepLast = IntOption(args, 100, 0, 100000,
                "serverreplaykeeplast", "server_replay_keep_last");

            bool lobbyServer = HasFlag(args, "lobby");
            Network.MatchFormat serverFormat = Network.MatchFormat.Auto;
            string? formatValue = ValueAfter(args, "format");
            if (formatValue != null && !Enum.TryParse(formatValue, ignoreCase: true,
                out serverFormat))
            {
                Console.WriteLine($"[server] ignoring -format {formatValue} "
                    + "(expected Auto, FreeForAll, OneVsOne, TwoVsTwo, ThreeVsThree, "
                    + "FourVsFour, TwoVsTwoVsTwoVsTwo, or Custom)");
                serverFormat = Network.MatchFormat.Auto;
            }
            Guid ownerToken = Guid.Empty;
            string? ownerTokenValue = ValueAfter(args, "ownertoken");
            if (ownerTokenValue != null && !Guid.TryParse(ownerTokenValue, out ownerToken))
            {
                Console.WriteLine("[server] ignoring invalid -ownertoken value");
                ownerToken = Guid.Empty;
            }

            var server = new Network.DedicatedServer(port, maxPlayers, rotation)
            {
                ServerName = ValueAfter(args, "servername") ?? ValueAfter(args, "name")
                    ?? Environment.MachineName,
                SessionPolicy = lobbyServer
                    ? Network.ServerSessionPolicy.Lobby
                    : Network.ServerSessionPolicy.Continuous,
                Format = serverFormat,
                OwnerToken = ownerToken,
                FriendlyFire = HasFlag(args, "friendlyfire"),
                // The one rule here that is a fix rather than a preference:
                // -noshadowfreeze makes the Judicator's ice wave a cone
                // instead of a column, for everybody in the room.
                ShadowFreeze = !HasFlag(args, "noshadowfreeze"),
                // Whether weapon pickups are the picking hunter's affinity
                // variant -- a different row of the damage table, so it is
                // broadcast rather than left to each machine's own settings
                // file. The damage level is not an option: it is pinned to
                // medium everywhere. See GameState.DamageLevel.
                AffinityWeapons = HasFlag(args, "affinityweapons"),
                // Players may change the map by voting unless the admin says
                // otherwise. See DedicatedServer.AllowMapVotes.
                AllowMapVotes = !HasFlag(args, "novote"),
                // This process is the server, so it is the one that may
                // replace itself. See DedicatedServer.AutoUpdate.
                AutoUpdate = true,
                ReplayPolicy = new Network.ServerReplayPolicy(
                    Enabled: serverReplays,
                    StorageLimitGb: replayStorageGb,
                    RetentionDays: replayRetentionDays,
                    KeepLast: replayKeepLast)
            };
            server.SetSessionOptions(HasFlag(args, "requireready"),
                !HasFlag(args, "nojoininprogress"));
            // Whether this server will also open *extra* matches, on ports of
            // its own, for players who ask. Off unless an admin says a range,
            // because it is their bandwidth and their ports -- and unlike the
            // directory, a game server has a match of its own to protect.
            //
            // This is what lets somebody host in Tokyo. The alternative that
            // was tried first was a second directory per region, which listed
            // nothing and existed purely to be asked; a server that can be
            // asked directly is already listed, already pinged and already
            // reachable.
            string? serverHostPorts = ValueAfter(args, "hostports");
            if (serverHostPorts != null && !serverHostPorts.Equals("none",
                StringComparison.OrdinalIgnoreCase))
            {
                string[] halves = serverHostPorts.Split('-');
                if (halves.Length == 2 && Int32.TryParse(halves[0], out int hostFirst)
                    && Int32.TryParse(halves[1], out int hostLast) && hostLast >= hostFirst)
                {
                    server.Hosts.SetPorts(hostFirst, hostLast);
                }
                else
                {
                    Console.WriteLine($"[server] ignoring -hostports {serverHostPorts} "
                        + "(expected FIRST-LAST)");
                }
            }
            // -simulate and -authority used to turn the simulation on. It is
            // what a server does now, and there is no relay left to fall back
            // to, so both are accepted and ignored: every systemd unit and
            // launch script already deployed passes one of them, and a server
            // that refused to start on an argument it used to require would be
            // exactly the breakage this line exists to avoid.
            if (HasFlag(args, "simulate") || HasFlag(args, "authority"))
            {
                Console.WriteLine("[net] -simulate is the default now and does "
                    + "nothing; a server always runs the match itself");
            }
            // Listed by default. A dedicated server exists to be found, and a
            // server that has to be told to advertise itself is a server
            // nobody finds -- so the flag is the one that opts out.
            if (!HasFlag(args, "nomaster") && !HasFlag(args, "unlisted"))
            {
                string masterHost = ValueAfter(args, "master") ?? NetMasterConfig.DefaultHost;
                int reportPort = NetMasterConfig.DefaultPort;
                string? reportPortValue = ValueAfter(args, "masterport");
                if (reportPortValue != null && Int32.TryParse(reportPortValue, out int parsedReport))
                {
                    reportPort = parsedReport;
                }
                server.Reporter = new MasterReporter(masterHost, reportPort);
                Console.WriteLine($"[server] listing on {masterHost}:{reportPort} "
                    + $"as \"{server.ServerName}\" (-nomaster to stay private)");
            }
            using var cancel = new System.Threading.CancellationTokenSource();
            using var signals = new ShutdownSignals();
            signals.OnShutdown(() =>
            {
                cancel.Cancel();
                server.Stop();
            });
            try
            {
                server.Run(cancel.Token);
            }
            catch (ProgramException ex)
            {
                // A server that cannot run the match, which since this build
                // is the only kind of server there is. The reason and what to
                // do about it are already on the log; a stack trace on top of
                // them would only bury both, and an operator reading a failed
                // systemd unit wants the sentence, not the frames.
                Console.WriteLine($"[server] {ex.Message}");
                Environment.Exit(1);
            }
            return true;
        }

#if MPHREAD_SERVER
        /// <summary>
        /// What this binary is for, for somebody who started it with no
        /// arguments -- which on Windows is anybody who double-clicked it.
        /// </summary>
        private static void ServerUsage()
        {
            string exe = System.IO.Path.GetFileNameWithoutExtension(
                Environment.ProcessPath) ?? "MphReadServer";
            Console.WriteLine();
            Console.WriteLine($"{Branding.Name} dedicated server. It needs the game files.");
            Console.WriteLine();
            Console.WriteLine($"  {exe} -server -port {NetConfig.DefaultPort} -players 8 "
                + "-servername \"My server\"");
            Console.WriteLine("      run a server. Maps come from maprotation.txt, written");
            Console.WriteLine("      beside this program on first run.");
            Console.WriteLine();
            Console.WriteLine("      canonical replays: on; 25 GiB; 14 days; keep newest 100");
            Console.WriteLine("      override with -serverreplays on|off, -serverreplaystoragegb N,");
            Console.WriteLine("      -serverreplayretentiondays N and -serverreplaykeeplast N.");
            Console.WriteLine();
            Console.WriteLine($"  {exe} -masterserver -port {NetMasterConfig.DefaultPort}");
            Console.WriteLine("      run a server directory of your own.");
            Console.WriteLine();
            Console.WriteLine($"  {exe} -servers");
            Console.WriteLine("      list the servers that are up right now.");
            Console.WriteLine();
            Console.WriteLine("A server lists itself on " + NetMasterConfig.DefaultHost
                + " so players can find it;");
            Console.WriteLine("-nomaster keeps it off every list. See SERVER.txt.");
            Console.WriteLine();
            // Double-clicked, so this window is about to close with everything
            // above it still unread.
            if (OperatingSystem.IsWindows() && ConsoleWindow.OwnsItsConsole())
            {
                Console.WriteLine("Press any key to close this window...");
                ConsoleSetup.PauseIfInteractive();
            }
        }
#endif

        /// <summary>
        /// Open the shell window, prove it came back where it was told, and
        /// prove that closing it would remember where it is now.
        /// </summary>
        private static bool WindowMemoryCheck()
        {
            Launcher.LauncherPrefs.Load();
            int wasWidth = Launcher.LauncherPrefs.WindowWidth;
            int wasHeight = Launcher.LauncherPrefs.WindowHeight;
            int wasX = Launcher.LauncherPrefs.WindowX;
            int wasY = Launcher.LauncherPrefs.WindowY;
            bool wasMaximized = Launcher.LauncherPrefs.WindowMaximized;
            Console.WriteLine(wasWidth > 0
                ? $"[window] saved: {wasWidth}x{wasHeight} at {wasX},{wasY}"
                    + (wasMaximized ? ", maximized" : "")
                : "[window] nothing saved yet");
            bool ok = true;
            try
            {
                // A size nothing else would produce, so "it came back" cannot
                // be the default in disguise.
                Launcher.LauncherPrefs.WindowWidth = 1442;
                Launcher.LauncherPrefs.WindowHeight = 906;
                Launcher.LauncherPrefs.WindowX = 60;
                Launcher.LauncherPrefs.WindowY = 48;
                Launcher.LauncherPrefs.WindowMaximized = false;
                using var window = new RenderWindow(shell: true);
                Console.WriteLine($"[window] opened at {window.ClientSize.X}x"
                    + $"{window.ClientSize.Y}");
                if (window.ClientSize.X != 1442 || window.ClientSize.Y != 906)
                {
                    Console.WriteLine("[window] FAIL: it did not open at the saved size");
                    ok = false;
                }
                // And the other way: what closing it now would keep.
                window.ClientSize = new OpenTK.Mathematics.Vector2i(1180, 800);
                WindowGeometry.Remember(window);
                Console.WriteLine($"[window] would remember "
                    + $"{Launcher.LauncherPrefs.WindowWidth}x"
                    + $"{Launcher.LauncherPrefs.WindowHeight}");
                if (Launcher.LauncherPrefs.WindowWidth != 1180
                    || Launcher.LauncherPrefs.WindowHeight != 800)
                {
                    Console.WriteLine("[window] FAIL: it did not remember the new size");
                    ok = false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[window] FAIL: {ex.Message}");
                ok = false;
            }
            finally
            {
                Launcher.LauncherPrefs.WindowWidth = wasWidth;
                Launcher.LauncherPrefs.WindowHeight = wasHeight;
                Launcher.LauncherPrefs.WindowX = wasX;
                Launcher.LauncherPrefs.WindowY = wasY;
                Launcher.LauncherPrefs.WindowMaximized = wasMaximized;
            }
            Console.WriteLine(ok ? "[window] ok" : "[window] FAILED");
            return ok;
        }

        /// <summary>The directory's port from the command line, or the default.</summary>
        private static int ParseMasterPort(string[] args)
        {
            string? value = ValueAfter(args, "masterport");
            return value != null && Int32.TryParse(value, out int parsed)
                ? parsed : NetMasterConfig.DefaultPort;
        }

        private static void ListServers(string masterHost, string? portValue)
        {
            int port = NetMasterConfig.DefaultPort;
            if (portValue != null && Int32.TryParse(portValue, out int parsed))
            {
                port = parsed;
            }
            Console.WriteLine($"[servers] asking {masterHost}:{port}");
            MasterListResult result = NetMasterClient.Query(masterHost, port);
            if (!result.Answered)
            {
                Console.WriteLine($"[servers] no answer from {masterHost}:{port} -- "
                    + "it may be down, or UDP may not reach it");
                return;
            }
            // Whether it will start a game for somebody who cannot open a
            // port, which is what the launcher's create-server screen asks it.
            // Printed here because the alternative way to find out is to open
            // that screen and see an empty "Host on" row -- which looks
            // identical to a directory that is down.
            Console.WriteLine(result.CanHost switch
            {
                true => "[servers] this directory will start games for players",
                false => "[servers] this directory starts no games (no host port range)",
                // Told apart from an explicit no, because they need different
                // things done about them: one is a setting, the other is a
                // deploy.
                null => "[servers] this directory is from before it could say whether it "
                    + "starts games; it is offered anyway, since hosting is the default"
            });
            if (result.Servers.Count == 0)
            {
                Console.WriteLine("[servers] the directory is up and has nobody listed");
                return;
            }
            Console.WriteLine($"[servers] {result.Servers.Count} listed; asking each one");
            foreach (MasterListing listing in result.Servers)
            {
                // Directly, not through the directory: the round trip that
                // matters is this machine's, and the answer also proves the
                // server is reachable from here rather than only from there.
                ServerStatus status = NetStatus.Query(listing.Address, listing.Port,
                    allowJoinProbe: false);
                string name = status.ServerName.Length > 0
                    ? status.ServerName
                    : listing.ServerName.Length > 0 ? listing.ServerName : listing.Endpoint;
                if (!status.Online)
                {
                    Console.WriteLine($"  {name,-24} {listing.Endpoint,-26} did not answer");
                    continue;
                }
                string players = status.MaxPlayers > 0
                    ? $"{status.Players}/{status.MaxPlayers}"
                    : status.Players.ToString();
                string ping = status.Latency >= 0 ? $"{status.Latency} ms" : "-- ms";
                Console.WriteLine($"  {name,-24} {listing.Endpoint,-26} "
                    + $"{status.RoomKey,-20} {NetStatus.ModeName(status.Mode),-14} "
                    + $"{players,-6} {ping}");
            }
        }

        public static bool TryHandle(string[] args)
        {
            if (HasFlag(args, "respawnrendercheck"))
            {
                Environment.ExitCode = Render.RespawnRenderCheck.Run(
                    ValueAfter(args, "respawnrendercheck"),
                    HasFlag(args, "cycles") ? ValueAfter(args, "cycles") ?? "" : null,
                    HasFlag(args, "timeout") ? ValueAfter(args, "timeout") ?? "" : null);
                return true;
            }

            (int width, int height) = ParseSize(args);

            // Custom maps are registered as rooms from their JSON at startup,
            // but a room whose binaries are not on disk crashes the moment
            // something tries to load it. Generating what is missing here --
            // the one place every entry point passes through, launcher
            // included, and after the game-file check -- means a map file is
            // enough to have a working room.
            if (!HasFlag(args, "mapgen"))
            {
                MapGen.CustomRooms.GenerateMissing();
            }

            // Opt-in per-second report of what this process believes about a
            // networked session -- slot occupancy, scoreboard count, which
            // remote slots have state. The failure worth catching is not
            // visible on the wire: two correctly connected clients can each
            // hold a scene containing only themselves.
            if (HasFlag(args, "nohelmet"))
            {
                // Both of them: the helmet is drawn as three layers and the
                // visor is one of them, so zeroing only HelmetOpacity leaves a
                // tinted pane over the view that reads as a bug rather than as
                // a setting. The settings window ties the two together for the
                // same reason.
                Features.HelmetOpacity = 0;
                Features.VisorOpacity = 0;
            }
            if (HasFlag(args, "uinativeres"))
            {
#if MPHREAD_SHELL
                // Rasterise the launcher's screens at the window's own
                // resolution however big it is, rather than capping them at
                // 1080p and magnifying. Crisper above 1080p, and a lot slower
                // to redraw -- which only shows while something is moving, so
                // it is a choice between sharper type and a menu that scrolls.
                Launcher.Gui.UiSurface.NativeRaster = true;
#endif
            }
            if (HasFlag(args, "netdebug"))
            {
                Network.NetDiagnostics.Enabled = true;
                Network.MapAudit.Diagnostic = true;
            }

            // Print the game's own tables as markdown, so the mechanics
            // documentation is generated from the data rather than kept by
            // hand and quietly going stale.
            if (HasFlag(args, "mechanics"))
            {
                Network.MechanicsDump.Run();
                return true;
            }

            // What a connected pad is doing, with no match in the way. The
            // only way to tell "not connected" from "connected but not
            // mapped" from "the dead zone is eating it" apart.
            if (HasFlag(args, "gamepad"))
            {
                double seconds = 15;
                string? given = ValueAfter(args, "seconds");
                if (given != null && Double.TryParse(given, out double parsed) && parsed > 0)
                {
                    seconds = parsed;
                }
                Environment.ExitCode = Input.GamepadProbe.Run(seconds);
                return true;
            }

            // The multiplayer room list, one per line, so a shell loop can
            // walk every map without hard-coding the names.
            if (HasFlag(args, "resourceaudit"))
            {
                Environment.ExitCode = Multiplayer.ResourceAudit.Run();
                return true;
            }
            if (ValueAfter(args, "healthsimtest") is string healthRoom)
            {
                Environment.ExitCode = HealthSimulationTest.Run(healthRoom);
                return true;
            }
            if (HasFlag(args, "rooms"))
            {
                foreach (string room in ThumbnailGenerator.MultiplayerRooms())
                {
                    Console.WriteLine(room);
                }
                return true;
            }

            // Load one room with a full house of players and report what it
            // contains and whether it survived.
            string? dpsTest = ValueAfter(args, "dpstest");
            if (dpsTest != null)
            {
                Hunter dpsHunter = Hunter.Sylux;
                string? dpsHunterValue = ValueAfter(args, "hunter");
                if (dpsHunterValue != null && Enum.TryParse(dpsHunterValue, ignoreCase: true, out Hunter parsedDpsHunter))
                {
                    dpsHunter = parsedDpsHunter;
                }
                BeamType dpsBeam = BeamType.ShockCoil;
                string? dpsBeamValue = ValueAfter(args, "weapon");
                if (dpsBeamValue != null && Enum.TryParse(dpsBeamValue, ignoreCase: true, out BeamType parsedDpsBeam))
                {
                    dpsBeam = parsedDpsBeam;
                }
                double dpsSeconds = 10;
                string? dpsSecondsValue = ValueAfter(args, "seconds");
                if (dpsSecondsValue != null && Double.TryParse(dpsSecondsValue,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsedDpsSeconds))
                {
                    dpsSeconds = parsedDpsSeconds;
                }
                float dpsDistance = 2.2f;
                string? dpsDistanceValue = ValueAfter(args, "distance");
                if (dpsDistanceValue != null && Single.TryParse(dpsDistanceValue,
                    System.Globalization.CultureInfo.InvariantCulture, out float parsedDpsDistance))
                {
                    dpsDistance = parsedDpsDistance;
                }
                // -bombs measures the alt-form bombs instead of a beam: the
                // one damage source the scripted tour cannot aim.
                Environment.ExitCode = Network.WeaponDps.Run(dpsTest, dpsHunter, dpsBeam, dpsSeconds,
                    dpsDistance, HasFlag(args, "bombs"));
                return true;
            }
            // Generate the binaries for the custom maps in `maps/`. The
            // textures come out of the player's own extracted files, so this
            // has to run here rather than at build time, and what ships in the
            // repository is the JSON, never the .bin.
            if (HasFlag(args, "mapbuild"))
            {
                Environment.ExitCode = MapGen.MapCommands.Run("mapbuild", ValueAfter(args, "mapbuild"), ValueAfter(args, "out"));
                return true;
            }
            if (HasFlag(args, "mapgen"))
            {
                string? only = ValueAfter(args, "mapgen");
                bool force = HasFlag(args, "force");
                int count = 0;
                int failed = 0;
                foreach (MapGen.MapDefinition def in MapGen.CustomRooms.Definitions)
                {
                    if (only != null && !only.Equals(def.Name, StringComparison.OrdinalIgnoreCase)
                        && !only.Equals("all", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    // one map at a time: a map that cannot be built -- most
                    // often one whose source level is not where it says --
                    // must not stop the others from being generated
                    try
                    {
                        MapGen.MapPacker.Generate(def, MapGen.CustomRooms.ArchiveDirectory(def),
                            MapGen.CustomRooms.EntityDirectory(), MapGen.CustomRooms.NodeDirectory(),
                            verbose: true);
                        count++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"{def.Name}: {ex.Message}");
                        failed++;
                    }
                }
                if (count == 0 && failed == 0)
                {
                    Console.WriteLine($"No maps to generate. Put a map JSON in {MapGen.CustomRooms.MapDirectory}.");
                }
                Environment.ExitCode = failed == 0 ? 0 : 1;
                return true;
            }

            // What levels are in a .pk3, so a conversion knows what to ask
            // for. Reads the archive's index only -- nothing is extracted.
            string? q3Maps = ValueAfter(args, "q3maps");
            if (q3Maps != null)
            {
                try
                {
                    foreach (string name in MapGen.Q3Bsp.ListMaps(q3Maps))
                    {
                        Console.WriteLine(name);
                    }
                    Environment.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not read {q3Maps}: {ex.Message}");
                    Environment.ExitCode = 1;
                }
                return true;
            }

            // A .pk3 to a room, in one command: the textures baked from the
            // level's own art, the scale and the extents picked from its
            // geometry, the spawns from its entities, and a map file written
            // out. Weapons and powerups are left for a person to place.
            string? q3Convert = ValueAfter(args, "q3convert");
            if (q3Convert != null)
            {
                float? scale = null;
                if (Single.TryParse(ValueAfter(args, "scale"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float parsedScale) && parsedScale > 0)
                {
                    scale = parsedScale;
                }
                int textureSize = Int32.TryParse(ValueAfter(args, "texsize"), out int parsedSize)
                    && parsedSize >= 8 && parsedSize <= 256
                        ? parsedSize
                        : MapGen.MapTextureBake.DefaultSize;
                try
                {
                    Environment.ExitCode = MapGen.Q3Convert.Run(q3Convert, ValueAfter(args, "map"),
                        ValueAfter(args, "name"), ValueAfter(args, "out"), HasFlag(args, "noclip"),
                        HasFlag(args, "noitems"), scale, textureSize);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not convert {q3Convert}: {ex.Message}");
                    Environment.ExitCode = 1;
                }
                return true;
            }

            // What a level draws with, commonest first, so a conversion knows
            // which shaders are worth mapping to a borrowed texture.
            string? q3Shaders = ValueAfter(args, "q3shaders");
            if (q3Shaders != null)
            {
                Environment.ExitCode = MapGen.MapReport.ListShaders(q3Shaders, ValueAfter(args, "map"));
                return true;
            }

            // Everything wrong with a map's collision, said before it is
            // generated: the format's limits, faces that reject their own
            // interior, what hurts, and every drawn surface with nothing solid
            // behind it. Since collision is something a person edits by hand
            // now, and none of those look like anything in a 3D tool.
            string? mapCheck = ValueAfter(args, "mapcheck");
            if (mapCheck != null)
            {
                Environment.ExitCode = MapGen.MapCheck.Run(mapCheck);
                return true;
            }

            // What pickups a level already holds, as the "items" block a
            // recipe would carry. The level's own were always imported
            // silently; this is what lets an author write them down, turn
            // keepItems off, and own them. Reads and prints -- a recipe can
            // carry comments and is nobody's to rewrite.
            string? mapItems = ValueAfter(args, "mapitems");
            if (mapItems != null)
            {
                float? itemScale = null;
                if (Single.TryParse(ValueAfter(args, "scale"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float parsedItemScale)
                    && parsedItemScale > 0)
                {
                    itemScale = parsedItemScale;
                }
                Environment.ExitCode = MapGen.MapReport.ListItems(mapItems, ValueAfter(args, "map"), itemScale);
                return true;
            }

            // List a room's materials, with the texture each one uses, so a
            // map can say which of them it wants to borrow.
            string? mapMaterials = ValueAfter(args, "mapmaterials");
            if (mapMaterials != null)
            {
                Environment.ExitCode = MapGen.MapReport.ListMaterials(mapMaterials);
                return true;
            }

            // Spire's slam, driven through the headless simulation. Needs
            // extracted game files, like -simcheck below.
            string? spirePoseCheck = ValueAfter(args, "spireposecheck");
            if (spirePoseCheck != null)
            {
                Environment.ExitCode = Network.SpireAltPoseCheck.Run(spirePoseCheck);
                return true;
            }

            // The headless simulation on its own, with nobody connected: what
            // a room costs a server in memory and in milliseconds a step. The
            // one measurement that decides whether a given box can be the
            // authority for a given map. See Mods/Network/ServerSimCheck.cs.
            string? simCheck = ValueAfter(args, "simcheck");
            if (simCheck != null)
            {
                int simPlayers = 8;
                string? simPlayerValue = ValueAfter(args, "players");
                if (simPlayerValue != null && Int32.TryParse(simPlayerValue, out int parsedSimPlayers))
                {
                    simPlayers = parsedSimPlayers;
                }
                double simSeconds = 10;
                string? simSecondsValue = ValueAfter(args, "seconds");
                if (simSecondsValue != null && Double.TryParse(simSecondsValue,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsedSimSeconds))
                {
                    simSeconds = parsedSimSeconds;
                }
                GameMode simMode = GameMode.Battle;
                string? simModeValue = ValueAfter(args, "mode");
                if (simModeValue != null
                    && Enum.TryParse(simModeValue, ignoreCase: true, out GameMode parsedSimMode))
                {
                    simMode = parsedSimMode;
                }
                Environment.ExitCode = Network.ServerSimCheck.Run(simCheck, simPlayers,
                    simSeconds, simMode, formCheck: HasFlag(args, "formcheck"));
                return true;
            }

            string? mapTest = ValueAfter(args, "maptest");
            if (mapTest != null)
            {
                int players = 8;
                string? playerValue = ValueAfter(args, "players");
                if (playerValue != null && Int32.TryParse(playerValue, out int parsed))
                {
                    players = parsed;
                }
                double seconds = 10;
                string? secondsValue = ValueAfter(args, "seconds");
                if (secondsValue != null && Double.TryParse(secondsValue,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsedSeconds))
                {
                    seconds = parsedSeconds;
                }
                GameMode mapMode = GameMode.Battle;
                string? modeValue = ValueAfter(args, "mode");
                if (modeValue != null && Enum.TryParse(modeValue, ignoreCase: true, out GameMode parsedMode))
                {
                    mapMode = parsedMode;
                }
                // The HUD is drawn to the window, not to the offscreen
                // target every other capture reads, so seeing it needs a real
                // window and a read from its buffer.
                Network.MapAudit.ShowWindow = HasFlag(args, "hudshots");
                Network.MapAudit.TeamProbe = HasFlag(args, "teamprobe");
                // -hunter H puts that hunter in slot 0, whose HUD every
                // capture is taken through. Each of the eight lays its
                // readouts out differently, so a HUD picture with no hunter
                // named is a picture of Samus's and of nobody else's.
                Network.MapAudit.MainHunter = ValueAfter(args, "hunter") != null
                    ? ParseHunter(args)
                    : null;
                // -drawrate N draws each simulation step N times, which is
                // what a 144 Hz screen does to a 60 Hz game. It is how the
                // decoupled loop is checked from a box with no display.
                string? drawRate = ValueAfter(args, "drawrate");
                if (drawRate != null && Int32.TryParse(drawRate, out int parsedDrawRate)
                    && parsedDrawRate > 0)
                {
                    Network.MapAudit.DrawRate = parsedDrawRate;
                }
                // -size WxH, so a HUD capture can be taken at a window shape
                // other than the one this happens to default to.
                string? sizeValue = ValueAfter(args, "size");
                if (sizeValue != null)
                {
                    string[] parts = sizeValue.ToLowerInvariant().Split('x');
                    if (parts.Length == 2 && Int32.TryParse(parts[0], out int sizeWidth)
                        && Int32.TryParse(parts[1], out int sizeHeight)
                        && sizeWidth > 0 && sizeHeight > 0)
                    {
                        Network.MapAudit.WindowSize = new OpenTK.Mathematics.Vector2i(sizeWidth, sizeHeight);
                    }
                }
                Environment.ExitCode = Network.MapAudit.Run(mapTest, players, seconds, mapMode,
                    bots: HasFlag(args, "bots"), shotDirectory: ValueAfter(args, "shots"),
                    renderProbe: HasFlag(args, "renderprobe"),
                    allNodes: HasFlag(args, "allnodes"),
                    itemProbe: HasFlag(args, "itemshots"));
                return true;
            }

            // The window's memory, both halves, without anybody watching a
            // window.
            //
            // It is a feature whose only failure mode is silent -- a window
            // that opens at the default size looks exactly like a window that
            // was never resized -- and the half that cannot be checked any
            // other way is the save, since it runs as the window closes and a
            // scripted run has no way to press a close button.
            //
            // Nothing is written: the preference is put back before this
            // returns, so running the check is not how somebody's window size
            // changes.
            if (HasFlag(args, "windowcheck"))
            {
                Environment.ExitCode = WindowMemoryCheck() ? 0 : 1;
                return true;
            }

            // The download behind the create-server screen's install mark, on
            // its own. "The button did nothing" is otherwise unanswerable from
            // a machine with no display, and the two halves that can fail --
            // finding the asset and unpacking it -- fail silently into a
            // screen that only says the package could not be installed.
            if (HasFlag(args, "installserver"))
            {
                Console.WriteLine($"[server] this platform takes the "
                    + $"\"{Update.UpdateCheck.ServerRid()}\" package");
                if (!LocalServer.CanInstall)
                {
                    Console.WriteLine("[server] no server package is published for it");
                    Environment.ExitCode = 1;
                    return true;
                }
                int lastPercent = -1;
                bool installed = LocalServer.Install(fraction =>
                {
                    int percent = (int)(fraction * 100);
                    if (percent >= lastPercent + 10)
                    {
                        lastPercent = percent;
                        Console.WriteLine($"[server] {percent}%");
                    }
                });
                if (!installed)
                {
                    Console.WriteLine($"[server] {LocalServer.LastError}");
                    Environment.ExitCode = 1;
                    return true;
                }
                Console.WriteLine($"[server] installed {LocalServer.InstalledTag} "
                    + $"into {LocalServer.Directory}");
                return true;
            }

            // The launcher's create-server screen, Dedicated half, with no
            // launcher: start a server on this machine from this build and
            // report where it landed.
            //
            // It exists because that is the one path in the feature that
            // cannot be checked by rendering a screen -- it spawns a process,
            // writes a rotation, copies paths.txt and waits for a socket, and
            // every one of those fails differently on a box with no display.
            string? hostLocal = ValueAfter(args, "hostlocal");
            if (hostLocal != null)
            {
                var localMaps = new List<(string RoomKey, GameMode Mode)>();
                GameMode localMode = GameMode.Battle;
                string? localModeValue = ValueAfter(args, "mode");
                if (localModeValue != null && Enum.TryParse(localModeValue,
                    ignoreCase: true, out GameMode parsedLocalMode))
                {
                    localMode = parsedLocalMode;
                }
                foreach (string entry in hostLocal.Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    localMaps.Add((entry, localMode));
                }
                ServerBinary? binary = LocalServer.Available();
                Console.WriteLine(binary == null
                    ? "[hostlocal] nothing on this machine can run a server"
                    : $"[hostlocal] using {binary.Value.Describe()} "
                        + $"({binary.Value.Executable})");
                double holdSeconds = 8;
                string? holdValue = ValueAfter(args, "seconds");
                if (holdValue != null && Double.TryParse(holdValue,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsedHold))
                {
                    holdSeconds = parsedHold;
                }
                int localPort = LocalServer.Start(
                    ValueAfter(args, "servername") ?? "Local test server",
                    localMaps, maxPlayers: PlayerEntity.SlotCapacity,
                    timeLimit: 7 * 60, pointGoal: 7,
                    masterHost: ValueAfter(args, "master") ?? NetMasterConfig.DefaultHost,
                    masterPort: ParseMasterPort(args),
                    listed: !HasFlag(args, "nomaster"));
                if (localPort < 0)
                {
                    Console.WriteLine($"[hostlocal] {LocalServer.LastError}");
                    Environment.ExitCode = 1;
                    return true;
                }
                Console.WriteLine($"[hostlocal] listening on 127.0.0.1:{localPort}");
                ServerStatus localStatus = NetStatus.Query("127.0.0.1", localPort,
                    allowJoinProbe: false);
                Console.WriteLine($"[hostlocal] it answers: {localStatus.RoomKey} "
                    + $"({NetStatus.ModeName(localStatus.Mode)}), "
                    + $"{localStatus.Players}/{localStatus.MaxPlayers} players");
                System.Threading.Thread.Sleep((int)(holdSeconds * 1000));
                LocalServer.Stop();
                Console.WriteLine("[hostlocal] stopped");
                return true;
            }

            // Ask the directory to run a match and join it. The launcher's
            // "Online, no setup" in one command -- and the only way to host
            // from a machine with no launcher, which is every machine that is
            // not Windows.
            string? hostGame = ValueAfter(args, "hostgame");
            if (hostGame != null)
            {
                string masterHost = ValueAfter(args, "master") ?? NetMasterConfig.DefaultHost;
                int masterPort = NetMasterConfig.DefaultPort;
                string? masterPortValue = ValueAfter(args, "masterport");
                if (masterPortValue != null && Int32.TryParse(masterPortValue, out int parsedMaster))
                {
                    masterPort = parsedMaster;
                }
                GameMode hostMode = GameMode.Battle;
                string? hostModeValue = ValueAfter(args, "mode");
                if (hostModeValue != null
                    && Enum.TryParse(hostModeValue, ignoreCase: true, out GameMode parsedHostMode))
                {
                    hostMode = parsedHostMode;
                }
                string hostName = ParseName(args);
                // The rest of the cycle, comma separated, for a hosted game
                // that is meant to outlast one match -- the command-line half
                // of what the launcher's create-server screen asks for. The
                // map named by -hostgame is always first, so the two ways of
                // saying it cannot disagree about what starts.
                var hostRotation = new List<(string RoomKey, GameMode Mode)>
                {
                    (hostGame, hostMode)
                };
                string? rotationValue = ValueAfter(args, "maprotation");
                if (rotationValue != null)
                {
                    foreach (string entry in rotationValue.Split(',',
                        StringSplitOptions.RemoveEmptyEntries
                        | StringSplitOptions.TrimEntries))
                    {
                        if (!String.Equals(entry, hostGame, StringComparison.OrdinalIgnoreCase))
                        {
                            hostRotation.Add((entry, hostMode));
                        }
                    }
                }
                Console.WriteLine($"[net] asking {masterHost}:{masterPort} to run {hostGame}"
                    + (hostRotation.Count > 1 ? $" and {hostRotation.Count - 1} more" : ""));
                HostedGame game = NetMasterClient.RequestGame(masterHost, masterPort,
                    hostGame, hostMode, timeLimit: 7 * 60, pointGoal: 7,
                    maxPlayers: PlayerEntity.SlotCapacity, serverName: $"{hostName}'s game",
                    rotation: hostRotation);
                if (!game.Started)
                {
                    Console.WriteLine($"[net] it would not: {game.Reason}");
                    Environment.ExitCode = 1;
                    return true;
                }
                Console.WriteLine($"[net] running on {game.Host}:{game.Port}; joining it");
                Network.NetConnectCommand.Run(game.Host, game.Port, hostName,
                    ParseHunter(args), ParseRecolor(args));
                return true;
            }

            // Join a server from the command line, with no launcher dialog.
            // The only way to start a client on a platform without WinForms,
            // and the only practical way to start two of them side by side --
            // which is the arrangement every bug in this feature has needed.
            string? connect = ValueAfter(args, "connect");
            if (connect != null)
            {
                Network.NetConnectCommand.Run(connect, ParsePort(args), ParseName(args),
                    ParseHunter(args), ParseRecolor(args));
                return true;
            }

            // The same client, running to a script and reporting what it saw.
            string? check = ValueAfter(args, "netcheck");
            if (check != null)
            {
                string? shots = ValueAfter(args, "shots");
                double seconds = 30;
                string? secondsValue = ValueAfter(args, "seconds");
                if (secondsValue != null && Double.TryParse(secondsValue,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsedSeconds))
                {
                    seconds = parsedSeconds;
                }
                // Watching instead of playing. -spectate on its own means
                // "from the moment the map is up"; with a number it is the
                // second to stop playing at, and -rejoin the second to come
                // back. Spectating is the one player state the scripted tour
                // cannot reach on its own -- the tour exists to drive a
                // hunter, and this is a player who has stopped driving one.
                double spectateAt = -1;
                double rejoinAt = -1;
                if (HasFlag(args, "spectate"))
                {
                    spectateAt = 0;
                    string? spectateValue = ValueAfter(args, "spectate");
                    if (spectateValue != null && Double.TryParse(spectateValue,
                        System.Globalization.CultureInfo.InvariantCulture, out double parsedSpectate))
                    {
                        spectateAt = parsedSpectate;
                    }
                }
                string? rejoinValue = ValueAfter(args, "rejoin");
                if (rejoinValue != null && Double.TryParse(rejoinValue,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsedRejoin))
                {
                    rejoinAt = parsedRejoin;
                }
                // Vote on the results screen's map ballot. Off unless asked,
                // because a scripted client that votes changes what a real
                // server plays next -- see NetCheckClient.MapVoteRow.
                // A real window and a picture of it, for the half of this
                // client's output that is HUD -- see NetCheckClient.ShowWindow.
                Network.NetCheckClient.ShowWindow = HasFlag(args, "hudshots");
                string? mapVote = ValueAfter(args, "mapvote");
                if (mapVote != null && Int32.TryParse(mapVote, out int mapVoteRow))
                {
                    Network.NetCheckClient.MapVoteRow = Math.Max(0, mapVoteRow);
                }
                Environment.ExitCode = Network.NetCheckClient.Run(check, ParsePort(args),
                    ParseName(args), ParseHunter(args), seconds, shots, width, height,
                    recordDemo: HasFlag(args, "recorddemo"),
                    spectateAt: spectateAt, rejoinAt: rejoinAt,
                    // -recolor N is a suit, and the harness needs to be able to
                    // ask for one: two clients asking for the same suit on the
                    // same hunter is precisely the case PlayerColors exists for.
                    color: ValueAfter(args, "recolor") != null ? ParseRecolor(args) : -1);
                return true;
            }

#if MPHREAD_AVALONIA
            if (ValueAfter(args, "replayshot") is string replayShots && ValueAfter(args, "demo") is string replayFile)
            {
                Environment.ExitCode = Launcher.Gui.UiCapture.RunReplay(replayShots, replayFile);
                return true;
            }
#endif
            if (ValueAfter(args, "replaydeterminism") is string replayPath)
            {
                Environment.ExitCode = Network.ReplayDeterminism.Run(replayPath, ValueAfter(args, "replayhashout"));
                return true;
            }
            if (ValueAfter(args, "replayclipcheck") is string clipSource
                && ValueAfter(args, "clip") is string clipPath)
            {
                if (!UInt32.TryParse(ValueAfter(args, "start"), out uint sourceStart))
                {
                    Console.WriteLine("[replayclipcheck] -start FRAME is required.");
                    Environment.ExitCode = 1;
                    return true;
                }
                Environment.ExitCode = Network.ReplayClipFidelity.Run(
                    clipSource, clipPath, sourceStart);
                return true;
            }
            // What a recorded match actually contains. Reads the file and
            // nothing else -- no room, no window, no game files.
            string? demoInfo = ValueAfter(args, "demoinfo");
            if (demoInfo != null)
            {
                Environment.ExitCode = Network.DemoInfo.Print(demoInfo,
                    replay: HasFlag(args, "replay"));
                return true;
            }

            // Worker invocation: capture the rooms this process was given and
            // exit. This is what ThumbnailBatch spawns -- a share of the
            // batch rather than one room, so the runtime that starts and the
            // code that JITs are paid for once across several pictures -- and
            // a single -thumbnail still works by hand to re-shoot one map.
            List<string> share = ValuesAfter(args, "thumbnail");
            if (share.Count > 0)
            {
                int captured = ThumbnailCapture.CaptureRooms(share, width, height);
                Console.WriteLine($"[thumbnails] captured {captured}/{share.Count}");
                return true;
            }

            if (HasFlag(args, "thumbnails"))
            {
                GenerateThumbnails(args, width, height);
                return true;
            }
            return false;
        }

        private static void GenerateThumbnails(string[] args, int width, int height)
        {
            bool force = HasFlag(args, "force");
            IReadOnlyList<string> rooms = force
                ? ThumbnailGenerator.MultiplayerRooms()
                : ThumbnailGenerator.MissingThumbnails();
            if (rooms.Count == 0)
            {
                Console.WriteLine("[thumbnails] all previews already present in "
                    + ThumbnailGenerator.CacheDirectory);
                Console.WriteLine("[thumbnails] pass -force to re-render them");
                return;
            }
            int jobs = ThumbnailBatch.DefaultParallelism;
            string? jobsValue = ValueAfter(args, "jobs");
            if (jobsValue != null && Int32.TryParse(jobsValue, out int parsedJobs))
            {
                jobs = parsedJobs;
            }
            Console.WriteLine($"[thumbnails] rendering {rooms.Count} preview(s) at "
                + $"{width}x{height}, {jobs} at a time");
            Console.WriteLine($"[thumbnails] output: {ThumbnailGenerator.CacheDirectory}");
            int written = ThumbnailBatch.Run(rooms, jobs, width, height);
            Console.WriteLine($"[thumbnails] done -- {written}/{rooms.Count} written");
        }

        private static (int Width, int Height) ParseSize(string[] args)
        {
            string? value = ValueAfter(args, "size");
            if (value != null)
            {
                string[] parts = value.Split('x', 'X');
                if (parts.Length == 2
                    && Int32.TryParse(parts[0], out int w)
                    && Int32.TryParse(parts[1], out int h)
                    && w > 0 && h > 0)
                {
                    return (w, h);
                }
                Console.WriteLine($"[thumbnails] ignoring -size {value} (expected e.g. 1920x1440)");
            }
            return (ThumbnailGenerator.ThumbnailWidth, ThumbnailGenerator.ThumbnailHeight);
        }

        private static int ParsePort(string[] args)
        {
            string? value = ValueAfter(args, "port");
            return value != null && Int32.TryParse(value, out int port) ? port : NetConfig.DefaultPort;
        }

        private static string ParseName(string[] args)
        {
            return ValueAfter(args, "name") ?? Environment.MachineName;
        }

        private static Hunter ParseHunter(string[] args)
        {
            string? value = ValueAfter(args, "hunter");
            return value != null && Enum.TryParse(value, ignoreCase: true, out Hunter hunter)
                ? hunter
                : Hunter.Samus;
        }

        private static int ParseRecolor(string[] args)
        {
            string? value = ValueAfter(args, "recolor");
            return value != null && Int32.TryParse(value, out int recolor) ? recolor : 0;
        }

        /// <summary>
        /// Render-option overrides from the command line, applied for every
        /// invocation before anything draws.
        ///
        /// The settings file is the launcher's, and the paths that never open
        /// one -- <c>-thumbnail</c>, <c>-maptest</c>, <c>-connect</c> -- had no
        /// way to ask for cel shading at all. That made the one mode whose
        /// whole point is what the picture looks like the one mode no
        /// screenshot command could turn on.
        /// </summary>
        private static void ApplyRenderOverrides(string[] args)
        {
            string? cel = ValueAfter(args, "cel");
            if (cel != null && !cel.StartsWith('-'))
            {
                RenderOptions.CelShading = RenderOptions.ParseOnOff(cel, RenderOptions.CelShading);
            }
            else if (HasFlag(args, "cel"))
            {
                // a bare -cel, with the next word belonging to another option
                RenderOptions.CelShading = true;
            }
            string? fog = ValueAfter(args, "fog");
            if (fog != null && !fog.StartsWith('-'))
            {
                RenderOptions.Fog = RenderOptions.ParseOnOff(fog, RenderOptions.Fog);
            }
            // How wide the view is, for the same paths -- and for a sharper
            // reason than most of them: what a field of view does to a picture
            // is the picture, so a screenshot is the only way to compare two
            // answers to it.
            string? fov = ValueAfter(args, "fov");
            if (fov != null && !fov.StartsWith('-'))
            {
                RenderOptions.FieldOfView = RenderOptions.ParseFov(fov,
                    RenderOptions.FieldOfView);
            }
            string? fps = ValueAfter(args, "fps");
            if (fps != null && !fps.StartsWith('-'))
            {
                RenderOptions.ShowFps = RenderOptions.ParseOnOff(fps, RenderOptions.ShowFps);
            }
            else if (HasFlag(args, "fps"))
            {
                RenderOptions.ShowFps = true;
            }
            // The frame rate, for the paths that never open a launcher --
            // which is every screenshot command and every scripted run. The
            // simulation is not affected by either of these: it is pinned at
            // 60 Hz in Mods/Render/FrameTiming.cs and these only decide how
            // often, and how smoothly, it is drawn.
            string? fpsCap = ValueAfter(args, "fpscap");
            if (fpsCap != null && !fpsCap.StartsWith('-'))
            {
                Render.FrameTiming.FrameRateCap = Render.FrameTiming.ParseCap(fpsCap,
                    Render.FrameTiming.FrameRateCap);
            }
            string? bands = ValueAfter(args, "celbands");
            if (bands != null && Int32.TryParse(bands, out int bandCount))
            {
                RenderOptions.CelBands = bandCount;
            }
            string? edge = ValueAfter(args, "celedge");
            if (edge != null && Int32.TryParse(edge.TrimEnd('%'), out int edgePercent))
            {
                RenderOptions.CelEdge = edgePercent / 100f;
            }
            // The whole competitive HUD, for the same paths and the same
            // reason: it is a mode whose point is what the picture looks like,
            // and every command that can photograph one opens no launcher.
            string? proHud = ValueAfter(args, "prohud");
            if (proHud != null && !proHud.StartsWith('-'))
            {
                Features.ProHud = RenderOptions.ParseOnOff(proHud, Features.ProHud);
            }
            else if (HasFlag(args, "prohud"))
            {
                Features.ProHud = true;
            }
            // Where the gun and the crosshair sit under it -- Quake's static
            // pair or the DS game's drifting one. The same reason again, and a
            // sharper one: the two answers differ mainly in what the middle of
            // the picture is doing, so a screenshot is how the difference is
            // checked at all.
            string? weaponStyle = ValueAfter(args, "weaponstyle");
            if (weaponStyle != null && !weaponStyle.StartsWith('-'))
            {
                if (weaponStyle.Equals("static", StringComparison.OrdinalIgnoreCase)
                    || weaponStyle.Equals("quake", StringComparison.OrdinalIgnoreCase))
                {
                    Features.ProHudFixedWeapon = true;
                    Features.FixedWeapon = true;
                }
                else if (weaponStyle.Equals("dynamic", StringComparison.OrdinalIgnoreCase)
                    || weaponStyle.Equals("metroid", StringComparison.OrdinalIgnoreCase))
                {
                    Features.ProHudFixedWeapon = false;
                    Features.FixedWeapon = false;
                }
            }
            // Which crosshair that HUD draws, and how big. Same reason again:
            // a screenshot command opens no launcher, and the crosshair is the
            // one thing in the middle of every one of those pictures.
            string? crosshair = ValueAfter(args, "crosshair");
            if (crosshair != null && !crosshair.StartsWith('-'))
            {
                Render.Crosshair.Style = Render.Crosshair.ParseStyle(crosshair,
                    Render.Crosshair.Style);
            }
            string? crosshairSize = ValueAfter(args, "crosshairsize");
            if (crosshairSize != null && !crosshairSize.StartsWith('-'))
            {
                Render.Crosshair.Size = Render.Crosshair.ParseSize(crosshairSize,
                    Render.Crosshair.Size);
            }
            // The round radar overlay. On by default and reachable from
            // Settings -> Game -> HUD; these are for the screenshot
            // commands, which open no launcher.
            string? radar = ValueAfter(args, "radar");
            if (radar != null && !radar.StartsWith('-'))
            {
                Render.Radar.Enabled = RenderOptions.ParseOnOff(radar, Render.Radar.Enabled);
            }
            else if (HasFlag(args, "radar"))
            {
                Render.Radar.Enabled = true;
            }
            // The dial's background and its rings/cone outline,
            // independently -- both off leaves only the blips and the
            // centre marker, which stays regardless of either.
            string? radarBackground = ValueAfter(args, "radarbackground");
            if (radarBackground != null && !radarBackground.StartsWith('-'))
            {
                Render.Radar.ShowBackground = RenderOptions.ParseOnOff(radarBackground,
                    Render.Radar.ShowBackground);
            }
            string? radarOutlines = ValueAfter(args, "radaroutlines");
            if (radarOutlines != null && !radarOutlines.StartsWith('-'))
            {
                Render.Radar.ShowOutlines = RenderOptions.ParseOnOff(radarOutlines, Render.Radar.ShowOutlines);
            }
        }

        private static bool HasFlag(string[] args, string name)
        {
            return args.Any(a => a.TrimStart('-').Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Kept out of <see cref="TryHandle"/> and told not to inline.
        ///
        /// The runtime loads the assemblies a method needs when it first
        /// *enters* that method, not when it reaches the call -- so naming
        /// UiCapture directly in TryHandle made every command load Avalonia,
        /// including `-server`. On a machine without it that is not a missing
        /// feature, it is the dedicated server aborting at startup with a
        /// FileNotFoundException, which is exactly what the netcheck clients
        /// did against a bin/ that had not been refreshed.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int RunShellCapture(string directory)
        {
#if MPHREAD_SHELL
            Launcher.Gui.Shell.RequestShots(directory);
            if (!Launcher.Gui.GuiLauncher.TryRun())
            {
                return 1;
            }
            // A step that could not press what it named is the check failing,
            // not a note in the log: what it proves is that a click reaches
            // the control it was aimed at, and a predicate matching nothing
            // proves that of nothing.
            int misses = Launcher.Gui.Shell.ShotMisses;
            if (misses > 0)
            {
                Console.WriteLine($"[shellshot] {misses} step(s) found nothing to press");
                return 1;
            }
            return 0;
#else
            Console.WriteLine("[shellshot] this build has no launcher");
            return 1;
#endif
        }

        /// <summary>
        /// Kept out of <see cref="TryHandle"/> and told not to inline. Same
        /// reason as the capture below it.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int RunUiCapture(string directory)
        {
#if MPHREAD_AVALONIA
            try
            {
                return Launcher.Gui.UiCapture.Run(directory);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[uishot] no launcher toolkit here: {ex.Message}");
                return 1;
            }
#else
            Console.WriteLine("[uishot] this build has no Avalonia launcher");
            return 1;
#endif
        }

        /// <summary>
        /// What the screens cost to redraw: `-uibench [screen]`. Same shape as
        /// the capture above it, and not inlined for the same reason.
        ///
        /// Desktop only, unlike the captures: what it measures is the surface
        /// the screens are drawn into, and Android has a real one.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int RunUiBench(string[] args)
        {
#if MPHREAD_SHELL
            try
            {
                Launcher.Gui.UiBench.Slow = HasFlag(args, "uibenchslow");
                Launcher.Gui.UiBench.AsAndroid = HasFlag(args, "uibenchandroid");
                Launcher.Gui.DeckTile.CacheChrome = !HasFlag(args, "uibenchnochrome");
                Launcher.Gui.UiBench.FreeFrames = HasFlag(args, "uibenchfree");
                Launcher.Gui.UiBench.OnlySize = ValueAfter(args, "uibenchsize");
                Launcher.Gui.UiBench.OnlyMove = ValueAfter(args, "uibenchonly");
                Launcher.Gui.UiBench.Shot = ValueAfter(args, "uibenchshot");
                if (Double.TryParse(ValueAfter(args, "uibenchscale"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                {
                    Launcher.Gui.UiBench.ScaleOverride = parsed;
                }
                return Launcher.Gui.UiBench.Run(ValueAfter(args, "uibench"));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[uibench] no launcher toolkit here: {ex.Message}");
                return 1;
            }
#else
            Console.WriteLine("[uibench] this build has no launcher surface to measure");
            return 1;
#endif
        }

        /// <summary>
        /// The layout studies: `-uidesign DIR`. Same shape as the capture
        /// above it, and not inlined for the same reason.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int RunUiDesigns(string directory)
        {
#if MPHREAD_AVALONIA
            try
            {
                return Launcher.Gui.UiDesigns.Run(directory);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[uidesign] no launcher toolkit here: {ex.Message}");
                return 1;
            }
#else
            Console.WriteLine("[uidesign] this build has no Avalonia launcher");
            return 1;
#endif
        }

        /// <summary>
        /// The tap-versus-scroll rule on its own: `-tapcheck`. The rule has no
        /// toolkit in it, but the check that drives it is written in Avalonia's
        /// coordinate types and lives under Mods/Launcher/Gui/, which a server
        /// build does not compile at all -- so the call goes through here for
        /// the same reason the captures above do.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int RunTapCheck()
        {
#if MPHREAD_AVALONIA
            return Launcher.Gui.TapCheck.Run();
#else
            Console.WriteLine("[tapcheck] this build has no launcher");
            return 1;
#endif
        }

        /// <summary>Where an option appears, or -1. For the ones read by position.</summary>
        private static int IndexOfFlag(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].TrimStart('-').Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        private static string? ValueAfter(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].TrimStart('-').Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return null;
        }

        private static string? ValueAfterAny(string[] args, params string[] names)
        {
            foreach (string raw in args)
            {
                string option = raw.TrimStart('-');
                foreach (string name in names)
                {
                    string prefix = name + "=";
                    if (option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return option[prefix.Length..];
                }
            }
            foreach (string name in names)
            {
                string? value = ValueAfter(args, name);
                if (value != null) return value;
            }
            return null;
        }

        private static int IntOption(string[] args, int fallback, int minimum,
            int maximum, params string[] names)
        {
            string? value = ValueAfterAny(args, names);
            if (value == null) return fallback;
            if (Int32.TryParse(value, out int parsed) && parsed >= minimum
                && parsed <= maximum)
            {
                return parsed;
            }
            Console.WriteLine($"[server] ignoring -{names[0]} {value} "
                + $"(expected {minimum}-{maximum})");
            return fallback;
        }

        private static bool TryParseOnOff(string value, out bool result)
        {
            if (Boolean.TryParse(value, out result)) return true;
            switch (value.Trim().ToLowerInvariant())
            {
                case "on":
                case "yes":
                case "1":
                    result = true;
                    return true;
                case "off":
                case "no":
                case "0":
                    result = false;
                    return true;
                default:
                    result = false;
                    return false;
            }
        }

        /// <summary>
        /// Every value given for a repeated option, in order. A thumbnail
        /// worker is handed a whole share of rooms this way rather than one.
        /// </summary>
        private static List<string> ValuesAfter(string[] args, string name)
        {
            var values = new List<string>();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].TrimStart('-').Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    values.Add(args[i + 1]);
                }
            }
            return values;
        }
    }
}
