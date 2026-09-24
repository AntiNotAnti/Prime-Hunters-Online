using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MphRead.Entities;
using MphRead.Mods;
using MphRead.Mods.Network;
using MphRead.Mods.Update;

namespace MphRead.Mods.Launcher
{
    /// <summary>
    /// The front screen for every platform WinForms does not reach.
    ///
    /// `HomeForm` is 1600 lines of custom-painted WinForms and cannot be run
    /// anywhere but Windows; what it *offers*, though, is five entries and a
    /// handful of choices, and none of that needs a window. This is those five
    /// entries as text, over the same <see cref="LauncherPrefs"/>, the same
    /// <see cref="GameFiles"/> setup and the same <see cref="MatchStart"/> --
    /// so a Linux player gets the launcher's behaviour, and the two screens
    /// cannot drift apart in what they actually start.
    ///
    /// It is not a port of the window and does not try to look like one. A
    /// terminal is what a Linux build already had (`-connect`, `-servers`,
    /// `-hostgame`); the gap this closes is that those are separate commands
    /// with addresses to copy between them, nothing remembers what you chose
    /// last time, and an offline match against bots -- the launcher's second
    /// entry -- had no command-line spelling at all.
    /// </summary>
    public static class TextLauncher
    {
        public static void Run()
        {
            LauncherPrefs.Load();
            if (LauncherPrefs.AutoUpdate)
            {
                // Started in the background and then waited on briefly. This
                // screen is printed once and then blocks on a keypress, so a
                // check that lands afterwards has no line to appear on until
                // the menu is drawn again.
                Update.Updater.CheckInBackground(_ => { });
                Update.Updater.WaitForCheck(TimeSpan.FromSeconds(2));
            }
            if (GameFiles.Ready)
            {
                // Upstream's CheckSetup does this before anything runs; the
                // launcher is dispatched before that check, so it does it here
                // -- and tolerates the files being absent, which is the whole
                // reason it goes first.
                GameFiles.ApplyPaths();
                // A map added after the install was set up has no picture and
                // no sweep coming to give it one.
                Mods.ThumbnailGenerator.EnsureCustomPreviews();
            }
            IReadOnlyList<string> rooms = Array.Empty<string>();

            // One launcher, then a match, then the launcher again, the same
            // loop the window runs. Settings and preferences are re-read each
            // time round because a match can commit its own copy of both.
            while (true)
            {
                MenuSettings settings = GameState.LoadSettings();
                Mods.GameSettings.Apply(settings);
                LauncherPrefs.Load();
                if (!Mods.WindowMode.StartupForced)
                {
                    Mods.WindowMode.Startup = LauncherPrefs.WindowMode;
                }
                if (rooms.Count == 0 && GameFiles.Ready)
                {
                    // Needs the game files: the room list is read out of them.
                    // Deferred rather than done up front so a fresh install can
                    // reach the entry that fixes that.
                    rooms = ThumbnailGenerator.MultiplayerRooms();
                }
                // See GuiLauncher: the roll behind "Random" is held for one
                // launch, and this is where a launch begins.
                Hunters.Reroll();
                if (!Home(settings, rooms, out LaunchPlan plan))
                {
                    return;
                }
                if (plan.Kind == LaunchKind.None)
                {
                    continue;
                }
                try
                {
                    MatchStart.Launch(settings, plan);
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    Console.WriteLine($"The game could not start: {ex.Message}");
                    Console.WriteLine(ex.StackTrace);
                    return;
                }
                finally
                {
                    // Both own a worker thread and a bound socket; a crash in
                    // the game must not leave either behind.
                    NetSession.Stop();
                    NetHostSession.Stop();
                }
                // No PauseMenu.QuitProgram check, unlike the window's loop:
                // the pause menu is WinForms and cannot run in a build that
                // reaches this screen, so the flag could only ever be false.
                // Quitting is [q] here.
            }
        }

        /// <summary>
        /// "Update now": open the release page and say what to fetch.
        ///
        /// The program does not install it. On a machine with no desktop --
        /// which is most of the ones that get this screen -- there is no
        /// browser to open either, so the address is printed and that is the
        /// whole of it.
        /// </summary>
        private static void UpdateNow(UpdateInfo update)
        {
            Console.WriteLine();
            Console.WriteLine($"  {Update.Updater.Describe(update)}");
            Console.WriteLine($"  {update.PageUrl}");
            if (Update.Updater.OpenPage(update))
            {
                Console.WriteLine("  Opened in your browser.");
            }
            Console.WriteLine("  Download it there and unpack it over this one.");
            Console.WriteLine();
        }

        /// <summary>
        /// The entries. Returns false when the answer was "quit"; a true with
        /// <see cref="LaunchKind.None"/> means "nothing to launch, show the
        /// screen again", which is what Settings, Credits and Game files do.
        /// </summary>
        private static bool Home(MenuSettings settings, IReadOnlyList<string> rooms,
            out LaunchPlan plan)
        {
            plan = default;
            string? problem = GameFiles.Problem();
            while (true)
            {
                Console.WriteLine();
                Console.WriteLine($"  {Mods.Branding.NameAndVersion}");
                Console.WriteLine("  --------------------------------------------");
                Console.WriteLine($"  Game files : {GameFiles.Describe()}");
                Console.WriteLine($"  Player     : {LauncherPrefs.PlayerName}"
                    + $" as {LauncherPrefs.LastHunter}");
                Console.WriteLine();
                // Before there is anything to play, the setup is the whole
                // screen. A menu that offers five things and refuses four of
                // them is a worse first impression than one that asks for the
                // one thing it needs.
                if (problem != null)
                {
                    Console.WriteLine($"  {problem}.");
                    Console.WriteLine();
                    Console.WriteLine("  [1] Game files       point this at your .nds dump");
                    Console.WriteLine("  [q] Quit");
                    Console.WriteLine();
                    Console.WriteLine($"  {Mods.Credits.Summary}");
                    Console.WriteLine();
                    string only = Ask("  Choose", "1").ToLowerInvariant();
                    if (only == "q" || only == "quit" || InputEnded)
                    {
                        return false;
                    }
                    SetUpGameFiles();
                    problem = GameFiles.Problem();
                    if (problem == null)
                    {
                        // Straight into the launcher proper: there are map
                        // previews to show now, and every entry works.
                        return true;
                    }
                    continue;
                }
                if (Update.Updater.Available != null)
                {
                    Console.WriteLine($"  {Update.Updater.Describe(Update.Updater.Available.Value)}");
                    Console.WriteLine();
                }
                Console.WriteLine("  [1] Adventure        the story, from a save slot");
                Console.WriteLine("  [2] Play online      join a server");
                Console.WriteLine("  [3] Play offline     a match against bots");
                Console.WriteLine("  [4] Host a game      run a server and play on it");
                Console.WriteLine("  [5] Settings         name, hunter, window, addresses");
                Console.WriteLine("  [6] Game files       point this at your .nds dump");
                if (Update.Updater.Available != null)
                {
                    Console.WriteLine("  [u] Update now       open the download page");
                }
                Console.WriteLine("  [q] Quit");
                Console.WriteLine();
                Console.WriteLine($"  {Mods.Credits.Summary} -credits for the full list.");
                Console.WriteLine();
                string choice = Ask("  Choose", "1").ToLowerInvariant();
                if (choice == "q" || choice == "quit" || InputEnded)
                {
                    return false;
                }
                if (choice == "5")
                {
                    Settings();
                    continue;
                }
                if (choice == "6")
                {
                    SetUpGameFiles();
                    problem = GameFiles.Problem();
                    return true;
                }
                if (choice == "u" && Update.Updater.Available != null)
                {
                    UpdateNow(Update.Updater.Available.Value);
                    continue;
                }
                if (problem != null)
                {
                    Console.WriteLine();
                    Console.WriteLine($"  {problem}. Use [6] first.");
                    continue;
                }
                switch (choice)
                {
                    case "1":
                        if (Adventure(out plan))
                        {
                            return true;
                        }
                        continue;
                    case "2":
                        if (PlayOnline(out plan))
                        {
                            return true;
                        }
                        continue;
                    case "3":
                        if (PlayOffline(settings, rooms, out plan))
                        {
                            return true;
                        }
                        continue;
                    case "4":
                        if (HostGame(settings, rooms, out plan))
                        {
                            return true;
                        }
                        continue;
                    default:
                        continue;
                }
            }
        }

        /// <summary>
        /// Join a server: pick one from the directory or type an address, then
        /// connect here rather than in the match.
        ///
        /// Connecting on this screen is what the window does too, and for the
        /// same reason: "could not join, it may be off or UDP may be blocked"
        /// belongs where somebody is still looking, not after a room has been
        /// loaded.
        /// </summary>
        private static bool PlayOnline(out LaunchPlan plan)
        {
            plan = default;
            string address = LauncherPrefs.ServerAddress;
            int port = LauncherPrefs.ServerPort;
            Console.WriteLine();
            Console.WriteLine($"  [b] browse the servers on {LauncherPrefs.MasterHost}");
            Console.WriteLine($"  [enter] use {address}:{port}");
            Console.WriteLine("  [c] cancel");
            string answer = Ask("  Server", "").ToLowerInvariant();
            if (answer == "c")
            {
                return false;
            }
            if (answer == "b")
            {
                if (!Browse(ref address, ref port))
                {
                    return false;
                }
            }
            else if (answer.Length > 0)
            {
                if (!ParseEndpoint(answer, ref address, ref port))
                {
                    Console.WriteLine("  That is not a host or host:port.");
                    return false;
                }
            }
            ServerStatus status = NetStatus.Query(address, port, allowJoinProbe: true);
            if (status.Online)
            {
                Console.WriteLine($"  {Describe(status)}");
            }
            else
            {
                Console.WriteLine("  That server did not answer. Joining anyway.");
            }
            string name = AskName();
            Hunter hunter = AskHunter();
            LauncherPrefs.ServerAddress = address;
            LauncherPrefs.ServerPort = port;
            LauncherPrefs.LastKind = (int)LaunchKind.Online;
            LauncherPrefs.Save();
            Console.WriteLine($"  Connecting to {address}:{port}...");
            if (!NetLaunch.Join(address, port, name, hunter))
            {
                Console.WriteLine($"  {NetLaunch.LastJoinError}");
                NetSession.Stop();
                return false;
            }
            plan = new LaunchPlan
            {
                Kind = LaunchKind.Online,
                Hunter = hunter,
                PlayerName = name,
                RoomKey = "",
                Mode = GameMode.Battle,
                Port = port
            };
            return true;
        }

        /// <summary>
        /// The server browser: what the directory lists, then each server asked
        /// directly. Both calls are the ones the window's browser makes, and
        /// the ones `-servers` already prints -- this adds picking one.
        /// </summary>
        private static bool Browse(ref string address, ref int port)
        {
            Console.WriteLine($"  Asking {LauncherPrefs.MasterHost}:{LauncherPrefs.MasterPort}...");
            MasterListResult result = NetMasterClient.Query(LauncherPrefs.MasterHost,
                LauncherPrefs.MasterPort);
            if (!result.Answered)
            {
                Console.WriteLine("  The directory did not answer; it may be down, "
                    + "or UDP may not reach it.");
                return false;
            }
            if (result.Servers.Count == 0)
            {
                Console.WriteLine("  The directory is up and has nobody listed.");
                return false;
            }
            var listed = new List<MasterListing>(result.Servers);
            Console.WriteLine();
            for (int i = 0; i < listed.Count; i++)
            {
                MasterListing listing = listed[i];
                // Directly, not through the directory: the round trip that
                // matters is this machine's, and an answer also proves the
                // server is reachable from here rather than only from there.
                ServerStatus status = NetStatus.Query(listing.Address, listing.Port,
                    allowJoinProbe: false);
                string name = status.ServerName.Length > 0
                    ? status.ServerName
                    : listing.ServerName.Length > 0 ? listing.ServerName : listing.Endpoint;
                Console.WriteLine($"  [{i + 1}] {name,-24} {listing.Endpoint,-24} "
                    + (status.Online ? Describe(status) : "did not answer"));
            }
            Console.WriteLine();
            string answer = Ask("  Which one", "1");
            if (!Int32.TryParse(answer, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int index) || index < 1 || index > listed.Count)
            {
                return false;
            }
            address = listed[index - 1].Address;
            port = listed[index - 1].Port;
            return true;
        }

        private static string Describe(ServerStatus status)
        {
            string players = status.MaxPlayers > 0
                ? $"{status.Players}/{status.MaxPlayers}"
                : status.Players.ToString(CultureInfo.InvariantCulture);
            string ping = status.Latency >= 0
                ? $"{status.Latency.ToString(CultureInfo.InvariantCulture)} ms"
                : "-- ms";
            return $"{status.RoomKey} ({NetStatus.ModeName(status.Mode)}) {players} {ping}";
        }

        /// <summary>
        /// A match against bots. The entry with no command-line spelling
        /// before this: `-room` opens the model viewer's room path with no
        /// bots, and `-maptest` is the test harness driving them to a script.
        /// </summary>
        /// <summary>
        /// The story. Pick a slot, then continue it or start it over.
        ///
        /// Choosing a slot is not presentation: saving is gated on
        /// <see cref="Menu.SaveSlot"/>, so a session started without one runs
        /// perfectly and then loses everything. See <see cref="AdventureSave"/>.
        /// </summary>
        private static bool Adventure(out LaunchPlan plan)
        {
            plan = default;
            while (true)
            {
                IReadOnlyList<AdventureSave.SlotInfo> slots = AdventureSave.ReadAll();
                Console.WriteLine();
                Console.WriteLine("  Adventure");
                Console.WriteLine("  --------------------------------------------");
                for (int i = 0; i < slots.Count; i++)
                {
                    Console.WriteLine($"  [{i + 1}] Slot {slots[i].Slot}"
                        + $"           {slots[i].Describe()}");
                }
                Console.WriteLine("  [b] Back");
                Console.WriteLine();
                string choice = Ask("  Choose a slot", "1").ToLowerInvariant();
                if (choice == "b" || choice == "back" || InputEnded)
                {
                    return false;
                }
                if (!Int32.TryParse(choice, out int index)
                    || index < 1 || index > slots.Count)
                {
                    continue;
                }
                AdventureSave.SlotInfo slot = slots[index - 1];
                bool newGame = !slot.Used;
                if (slot.Used)
                {
                    Console.WriteLine();
                    Console.WriteLine($"  Slot {slot.Slot}: {slot.Describe()}");
                    Console.WriteLine("  [1] Continue");
                    Console.WriteLine("  [2] New game    (overwrites this slot once you save)");
                    Console.WriteLine("  [b] Back");
                    Console.WriteLine();
                    string what = Ask("  Choose", "1").ToLowerInvariant();
                    if (what == "b" || what == "back")
                    {
                        continue;
                    }
                    if (what == "2")
                    {
                        newGame = true;
                    }
                    else if (what != "1")
                    {
                        continue;
                    }
                }
                Hunter hunter = AskHunter();
                LauncherPrefs.LastKind = (int)LaunchKind.Adventure;
                LauncherPrefs.Save();
                plan = new LaunchPlan
                {
                    Kind = LaunchKind.Adventure,
                    Hunter = hunter,
                    PlayerName = LauncherPrefs.PlayerName,
                    SaveSlot = slot.Slot,
                    NewGame = newGame
                };
                return true;
            }
        }

        private static bool PlayOffline(MenuSettings settings, IReadOnlyList<string> rooms,
            out LaunchPlan plan)
        {
            plan = default;
            if (!AskRoom(settings, rooms, out string roomKey))
            {
                return false;
            }
            GameMode mode = AskMode();
            int bots = AskInt("  Bots (0-7)", LauncherPrefs.Bots, 0,
                PlayerEntity.SlotCapacity - 1);
            int level = AskInt("  Bot skill (0 easy, 1 normal, 2 hard, 3 insane)",
                LauncherPrefs.BotLevel, 0, 3);
            Hunter hunter = AskHunter();
            LauncherPrefs.Bots = bots;
            LauncherPrefs.BotLevel = level;
            LauncherPrefs.LastKind = (int)LaunchKind.Offline;
            LauncherPrefs.Save();
            plan = new LaunchPlan
            {
                Kind = LaunchKind.Offline,
                Hunter = hunter,
                PlayerName = LauncherPrefs.PlayerName,
                RoomKey = roomKey,
                Mode = mode,
                Bots = bots,
                BotLevel = level
            };
            return true;
        }

        /// <summary>
        /// Run a server and play on it. Either on this machine -- which needs
        /// a forwarded UDP port for anybody outside to reach it -- or by asking
        /// the directory to run it, which needs nothing forwarded anywhere and
        /// is why it is the default.
        /// </summary>
        private static bool HostGame(MenuSettings settings, IReadOnlyList<string> rooms,
            out LaunchPlan plan)
        {
            plan = default;
            if (!AskRoom(settings, rooms, out string roomKey))
            {
                return false;
            }
            GameMode mode = AskMode();
            string name = AskName();
            Hunter hunter = AskHunter();
            bool onMaster = AskYesNo("  Let the directory run it (no port forwarding)",
                LauncherPrefs.HostOnMaster);
            LauncherPrefs.HostOnMaster = onMaster;
            LauncherPrefs.LastKind = (int)LaunchKind.Host;

            if (onMaster)
            {
                Console.WriteLine($"  Asking {LauncherPrefs.MasterHost}:"
                    + $"{LauncherPrefs.MasterPort} to run {roomKey}...");
                HostedGame game = NetMasterClient.RequestGame(LauncherPrefs.MasterHost,
                    LauncherPrefs.MasterPort, roomKey, mode, timeLimit: 7 * 60,
                    pointGoal: 7, maxPlayers: PlayerEntity.SlotCapacity,
                    serverName: $"{name}'s game");
                if (!game.Started)
                {
                    Console.WriteLine($"  It would not: {game.Reason}");
                    return false;
                }
                LauncherPrefs.Save();
                Console.WriteLine($"  Running on {game.Host}:{game.Port}; joining it.");
                if (!NetLaunch.Join(game.Host, game.Port, name, hunter))
                {
                    Console.WriteLine("  The game started but could not be joined.");
                    NetSession.Stop();
                    return false;
                }
            }
            else
            {
                int port = AskInt("  Port", LauncherPrefs.HostPort, 1, 65535);
                bool listed = AskYesNo("  List it so others can find it",
                    LauncherPrefs.ListHostedGame);
                LauncherPrefs.HostPort = port;
                LauncherPrefs.ListHostedGame = listed;
                LauncherPrefs.Save();
                Console.WriteLine($"  Starting a server on port {port}...");
                if (!NetHostSession.StartAndJoin(port, name, hunter, roomKey, mode,
                    timeLimit: 7 * 60, pointGoal: 7,
                    listing: listed
                        ? (LauncherPrefs.MasterHost, LauncherPrefs.MasterPort, $"{name}'s game")
                        : null))
                {
                    Console.WriteLine("  The server would not start: "
                        + (NetHostSession.LastError ?? "the port may be in use"));
                    return false;
                }
                Console.WriteLine($"  Hosting on port {port}. Friends join with:");
                Console.WriteLine($"    {Mods.Branding.Executable} -connect <your address> -port {port}");
            }
            plan = new LaunchPlan
            {
                Kind = LaunchKind.Host,
                Hunter = hunter,
                PlayerName = name,
                RoomKey = roomKey,
                Mode = mode,
                Port = LauncherPrefs.HostPort
            };
            return true;
        }

        /// <summary>
        /// The handful of settings this screen owns. Everything else --
        /// volumes, match rules, cheats, bugfixes -- is upstream's console
        /// menu (`-menu`), which reads and writes the same settings.json this
        /// screen has already applied.
        /// </summary>
        private static void Settings()
        {
            Console.WriteLine();
            LauncherPrefs.PlayerName = AskName();
            LauncherPrefs.LastHunter = AskHunter();
            LauncherPrefs.WindowMode = AskYesNo("  Start fullscreen",
                LauncherPrefs.WindowMode != WindowStartMode.Windowed)
                ? WindowStartMode.Fullscreen
                : WindowStartMode.Windowed;
            string endpoint = Ask("  Default server",
                $"{LauncherPrefs.ServerAddress}:{LauncherPrefs.ServerPort}");
            string address = LauncherPrefs.ServerAddress;
            int port = LauncherPrefs.ServerPort;
            if (ParseEndpoint(endpoint, ref address, ref port))
            {
                LauncherPrefs.ServerAddress = address;
                LauncherPrefs.ServerPort = port;
            }
            string master = Ask("  Server directory",
                $"{LauncherPrefs.MasterHost}:{LauncherPrefs.MasterPort}");
            string masterHost = LauncherPrefs.MasterHost;
            int masterPort = LauncherPrefs.MasterPort;
            if (ParseEndpoint(master, ref masterHost, ref masterPort))
            {
                LauncherPrefs.MasterHost = masterHost;
                LauncherPrefs.MasterPort = masterPort;
            }
            LauncherPrefs.Save();
            Console.WriteLine("  Saved.");
            Console.WriteLine("  Volumes, controls and match rules are in -menu.");
        }

        /// <summary>
        /// First run. The window opens a file picker; here the path is typed,
        /// and the extraction is the same child process either way.
        /// </summary>
        private static void SetUpGameFiles()
        {
            Console.WriteLine();
            Console.WriteLine($"  {Mods.Branding.Name} needs your own Metroid Prime Hunters cartridge");
            Console.WriteLine("  dump. It unpacks what it needs next to this program and");
            Console.WriteLine("  leaves the file alone. No game data is included or");
            Console.WriteLine("  downloaded.");
            Console.WriteLine();
            string path = Ask("  Path to the .nds file (blank to cancel)", "");
            if (path.Length == 0)
            {
                return;
            }
            // A path pasted from a file manager often arrives quoted, and the
            // quotes are not part of it.
            path = path.Trim().Trim('"', '\'');
            if (!System.IO.File.Exists(path))
            {
                Console.WriteLine($"  There is no file at {path}");
                return;
            }
            Console.WriteLine();
            var progress = new SetupProgress();
            bool redraw = !Console.IsOutputRedirected;
            bool ok = GameFiles.RunSetup(path, line =>
            {
                if (!progress.Observe(line))
                {
                    return;
                }
                if (redraw)
                {
                    // Over the top of itself, so a five-minute extraction is
                    // one line rather than a thousand. Only when the output is
                    // a terminal: into a pipe or a log, carriage returns just
                    // make an unreadable file.
                    Console.Write($"\r  {progress.Bar()}  {progress.Stage,-22}");
                }
                else
                {
                    Console.WriteLine($"  {progress.Bar()}  {progress.Stage}");
                }
            });
            progress.Finish(ok);
            if (redraw)
            {
                Console.Write($"\r  {progress.Bar()}  {progress.Stage,-22}");
            }
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine(ok ? "  Ready to play." : "  Setup did not finish.");
            if (ok)
            {
                GenerateMissingThumbnails();
            }
        }

        private static void GenerateMissingThumbnails()
        {
            IReadOnlyList<string> missing = ThumbnailGenerator.MissingThumbnails();
            if (missing.Count == 0)
            {
                return;
            }
            Console.WriteLine("  Rendering map previews...");
            bool redraw = !Console.IsOutputRedirected;
            ThumbnailBatch.Run(missing, ThumbnailBatch.DefaultParallelism,
                ThumbnailGenerator.ThumbnailWidth, ThumbnailGenerator.ThumbnailHeight,
                line =>
                {
                    if (redraw)
                    {
                        Console.Write($"\r  {line,-70}");
                    }
                    else
                    {
                        Console.WriteLine($"  {line}");
                    }
                });
            if (redraw)
            {
                Console.WriteLine();
            }
        }

        private static bool AskRoom(MenuSettings settings, IReadOnlyList<string> rooms,
            out string roomKey)
        {
            roomKey = settings.RoomKey;
            if (rooms.Count == 0)
            {
                Console.WriteLine("  No multiplayer rooms were found.");
                return false;
            }
            // The one the window would have shown selected: last played if it
            // is still a multiplayer room, else the first.
            int current = 0;
            for (int i = 0; i < rooms.Count; i++)
            {
                if (rooms[i] == roomKey)
                {
                    current = i;
                    break;
                }
            }
            Console.WriteLine();
            for (int i = 0; i < rooms.Count; i++)
            {
                Console.WriteLine($"  [{i + 1,2}] {rooms[i]}");
            }
            Console.WriteLine();
            string answer = Ask("  Map", (current + 1).ToString(CultureInfo.InvariantCulture));
            if (!Int32.TryParse(answer, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int index) || index < 1 || index > rooms.Count)
            {
                return false;
            }
            roomKey = rooms[index - 1];
            settings.RoomKey = roomKey;
            return true;
        }

        private static GameMode AskMode()
        {
            // The modes a multiplayer match can be started in. Not every
            // GameMode value is one -- SinglePlayer and None are in the enum
            // too -- so this is the list rather than the enum.
            GameMode[] modes =
            {
                GameMode.Battle, GameMode.BattleTeams, GameMode.Survival,
                GameMode.SurvivalTeams, GameMode.Capture, GameMode.Bounty,
                GameMode.BountyTeams, GameMode.Defender, GameMode.DefenderTeams,
                GameMode.Nodes, GameMode.NodesTeams, GameMode.PrimeHunter
            };
            Console.WriteLine();
            for (int i = 0; i < modes.Length; i++)
            {
                Console.WriteLine($"  [{i + 1,2}] {NetStatus.ModeName(modes[i])}");
            }
            Console.WriteLine();
            string answer = Ask("  Mode", "1");
            return Int32.TryParse(answer, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int index) && index >= 1 && index <= modes.Length
                ? modes[index - 1]
                : GameMode.Battle;
        }

        private static string AskName()
        {
            string name = Ask("  Your name", LauncherPrefs.PlayerName);
            if (name.Length == 0)
            {
                name = LauncherPrefs.PlayerName;
            }
            LauncherPrefs.PlayerName = name;
            return name;
        }

        private static Hunter AskHunter()
        {
            // Seven playable hunters plus Random, which is what the picker on
            // the window offers; the enum carries entries past those.
            var hunters = new List<Hunter>();
            for (int i = 0; i < 7; i++)
            {
                hunters.Add((Hunter)i);
            }
            hunters.Add(Hunter.Random);
            int current = Math.Max(0, hunters.IndexOf(LauncherPrefs.LastHunter));
            Console.WriteLine();
            Console.WriteLine("  " + String.Join("  ", hunters.Select(
                (h, i) => $"[{i + 1}] {h}")));
            string answer = Ask("  Hunter", (current + 1).ToString(CultureInfo.InvariantCulture));
            Hunter hunter = Int32.TryParse(answer, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int index)
                && index >= 1 && index <= hunters.Count
                ? hunters[index - 1]
                : LauncherPrefs.LastHunter;
            LauncherPrefs.LastHunter = hunter;
            return hunter;
        }

        private static bool AskYesNo(string prompt, bool current)
        {
            string answer = Ask($"{prompt} (y/n)", current ? "y" : "n").ToLowerInvariant();
            return answer.Length > 0 ? answer[0] == 'y' : current;
        }

        private static int AskInt(string prompt, int current, int min, int max)
        {
            string answer = Ask(prompt, current.ToString(CultureInfo.InvariantCulture));
            return Int32.TryParse(answer, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int value)
                ? Math.Clamp(value, min, max)
                : current;
        }

        /// <summary>
        /// Whether stdin has ended.
        ///
        /// Taking EOF as the default answer is right for one question and
        /// wrong for a menu: the default there is an entry, the entry comes
        /// back to the menu, and the menu asks again -- a process with nobody
        /// at it printing its front screen forever. That is what a Windows or
        /// Linux build does today when the graphical launcher fails to open
        /// and the fall-back inherits a stdin that is a pipe. Every menu reads
        /// this and leaves.
        /// </summary>
        public static bool InputEnded { get; private set; }

        /// <summary>
        /// Prompt, showing the remembered answer, and take a blank line to mean
        /// "keep it". A null from ReadLine means stdin closed -- a piped or
        /// backgrounded run -- and must not become an endless loop over EOF, so
        /// it reads as the default too, and <see cref="InputEnded"/> says so.
        /// </summary>
        private static string Ask(string prompt, string fallback)
        {
            Console.Write(fallback.Length > 0 ? $"{prompt} [{fallback}]: " : $"{prompt}: ");
            string? line = Console.ReadLine();
            if (line == null)
            {
                InputEnded = true;
                Console.WriteLine();
                return fallback;
            }
            line = line.Trim();
            return line.Length == 0 ? fallback : line;
        }

        /// <summary>host, or host:port. Leaves both alone and returns false on
        /// anything else, so a typo does not silently change the address.</summary>
        private static bool ParseEndpoint(string text, ref string host, ref int port)
        {
            text = text.Trim();
            if (text.Length == 0)
            {
                return false;
            }
            int colon = text.LastIndexOf(':');
            if (colon <= 0)
            {
                host = text;
                return true;
            }
            if (!Int32.TryParse(text[(colon + 1)..], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int parsed)
                || parsed < 1 || parsed > 65535)
            {
                return false;
            }
            host = text[..colon];
            port = parsed;
            return true;
        }
    }
}
