#if MPHREAD_SHELL
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MphRead.Mods.Network;
using MphRead.Mods.Multiplayer;
namespace MphRead.Mods.Launcher.Gui
{
    internal static class PrimeUiChecks
    {
        private static int _checks;
        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); _checks++; }
        private static readonly Size[] Sizes = { new(1920,1080), new(1600,900), new(1440,900), new(1366,768), new(1280,720), new(830,390) };
        internal static PrimeShell Create()
        {
            var settings = new MenuSettings();
            string[] rooms = { "MP1 SANCTORUS", "MP3 PROVING GROUND" };
            PrimeShell? shell = null;
            Control Create(PrimeRoute route) => route switch
            {
                PrimeRoute.News => new NewsWorkspace(shell!.Overlays),
                PrimeRoute.Play => new PlayWorkspace(new[] {
                    new ServerBrowserEntry(new MasterListing { Address = "127.0.0.1", Port = 27888, ServerName = "LOCAL TEST ARENA" },
                        new ServerStatus { Online = true, Protocol = NetConfig.ProtocolVersion, RoomKey = rooms[0],
                            Mode = GameMode.Battle, Players = 3, MaxPlayers = 8, Latency = 24 }),
                    new ServerBrowserEntry(new MasterListing { Address = "127.0.0.2", Port = 27888, ServerName = "FULL TEST ARENA" },
                        new ServerStatus { Online = true, Protocol = NetConfig.ProtocolVersion, RoomKey = rooms[1],
                            Mode = GameMode.BattleTeams, Players = 8, MaxPlayers = 8, Latency = 42 })
                }) { Overlays = shell!.Overlays },
                PrimeRoute.HunterLicense => new LicenseWorkspace(loadProfile: false),
                PrimeRoute.Settings => new SettingsView(settings, shell: true),
                PrimeRoute.Offline => new OfflineWorkspace(settings, rooms, shell!.Overlays),
                PrimeRoute.Theatre => new TheatreWorkspace(manageStorage: false),
                PrimeRoute.Forge => new MapStudioScreen(shell!.Overlays, preview: true),
                PrimeRoute.Lobby => LobbyFixture(rooms, shell!.Overlays),
                _ => throw new ArgumentOutOfRangeException(nameof(route))
            };
            shell = new PrimeShell(Create, () => { }); shell.Start(); return shell;
        }
        private static LobbyScreen LobbyFixture(string[] rooms, PrimeOverlayHost overlays)
        {
            var lobby = new LobbyScreen(rooms) { Overlays = overlays };
            var roster = RosterPacket.Create(); roster.Count = 8;
            var players = (StackPanel)typeof(LobbyScreen).GetField("_players", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(lobby)!;
            for (int i = 0; i < roster.Count; i++)
            {
                roster.Slots[i] = (byte)i; roster.Names[i] = "PLAYER " + (i + 1);
                roster.Hunters[i] = (byte)(i % 7); roster.Teams[i] = (sbyte)(i % 2);
                roster.LobbyReady[i] = true; roster.Pings[i] = (ushort)(18 + i * 3);
                players.Children.Add(new LobbyPlayerRow(roster, i, 0, changeTeam: _ => { }));
            }
            return lobby;
        }
        private static void CheckSavedLobbyLimits()
        {
            GameMode previousMode = LauncherPrefs.LastLobbyMode;
            int previousTime = LauncherPrefs.LastLobbyTimeLimitSeconds;
            int previousGoal = LauncherPrefs.LastLobbyGoal;
            try
            {
                LauncherPrefs.LastLobbyMode = GameMode.Battle;
                LauncherPrefs.LastLobbyTimeLimitSeconds = 10 * 60;
                LauncherPrefs.LastLobbyGoal = 25;

                var saved = CreateServerScreen.InitialMatchLimits(GameMode.Battle);
                Check(saved.TimeLimit == 10 * 60 && saved.PointGoal == 25,
                    "new lobby reuses the last accepted time and goal");

                var differentMode = CreateServerScreen.InitialMatchLimits(GameMode.Capture);
                Check(differentMode.TimeLimit == 10 * 60
                    && differentMode.PointGoal == MatchGoalRules.DefaultValue(GameMode.Capture),
                    "new lobby keeps the clock but does not leak an incompatible goal across modes");
            }
            finally
            {
                LauncherPrefs.LastLobbyMode = previousMode;
                LauncherPrefs.LastLobbyTimeLimitSeconds = previousTime;
                LauncherPrefs.LastLobbyGoal = previousGoal;
            }
        }

        private static void CheckInProgressAdmission()
        {
            // Connect already knows the server is InMatch before creating the
            // lobby screen. Repeat after disconnect to cover same-match rejoin.
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    NetSession.StartClient("127.0.0.1", 9);
                    NetSession.ApplySessionState(new SessionStatePacket
                    {
                        Policy = ServerSessionPolicy.Lobby, Phase = SessionPhase.InMatch,
                        MatchId = 1, AuthorityEpoch = 1, StartGeneration = 1, Revision = 1,
                        Match = new MatchDefinition { RoomKey = "MP1 SANCTORUS", Mode = GameMode.Battle }
                    });
                    var lobby = new LobbyScreen(new[] { "MP1 SANCTORUS" });
                    using var coordinator = new LobbySessionCoordinator { Screen = lobby };
                    int requests = 0;
                    lobby.MatchRequested += (_, plan) =>
                    {
                        Check(plan.Kind == LaunchKind.Online && plan.RoomKey == "MP1 SANCTORUS",
                            "in-progress admission loads the active match");
                        requests++;
                    };
                    coordinator.Tick();
                    Check(requests == 1 && lobby.IsSuspended,
                        "in-progress join/rejoin hands the lobby connection to gameplay");
                    coordinator.Tick();
                    Check(requests == 1, "gameplay handoff does not request a duplicate scene load");
                    NetSession.Stop();
                }
            }
            finally { NetSession.Stop(); }
        }
        public static int Run(string? directory)
        {
            if (!GuiLauncher.EnsureSetup(requireDisplay: false)) return 1;
            _checks = 0; bool previousStill = Deck.Still; Deck.Still = true;
            try
            {
                Dispatcher.UIThread.Invoke(() =>
                {
                    foreach (string asset in new[] {
                        "avares://ProjectPrime/Assets/Fonts/Rajdhani-SemiBold.ttf",
                        "avares://ProjectPrime/Assets/Fonts/Rajdhani-Bold.ttf",
                        "avares://ProjectPrime/Assets/Fonts/Rajdhani-OFL.txt" })
                        Check(Avalonia.Platform.AssetLoader.Exists(new Uri(asset)), "bundled tactical font asset " + asset);
                    Check(new Avalonia.Media.Typeface(PrimeTypography.Display, weight: Avalonia.Media.FontWeight.Bold)
                        .GlyphTypeface.FamilyName.Contains("Rajdhani", StringComparison.OrdinalIgnoreCase), "display resolves to bundled Rajdhani");
                    Check(new Avalonia.Media.Typeface(PrimeTypography.Label, weight: Avalonia.Media.FontWeight.SemiBold)
                        .GlyphTypeface.FamilyName.Contains("Rajdhani", StringComparison.OrdinalIgnoreCase), "controls resolve to bundled Rajdhani");
                    foreach (var mode in Enum.GetValues<WindowStartMode>())
                        Check(WindowMode.Parse(WindowMode.Serialize(mode), WindowStartMode.Windowed) == mode,
                            "window mode preference round trip: " + mode);
                    Check(WindowMode.Parse("borderless", WindowStartMode.Windowed) == WindowStartMode.BorderlessFullscreen
                        && WindowMode.Parse("1", WindowStartMode.Windowed) == WindowStartMode.BorderlessFullscreen,
                        "legacy borderless preferences remain valid");
                    CheckSavedLobbyLimits();
                    CheckInProgressAdmission();
                    var shell = Create();
                    var window = new Window { Width = 1280, Height = 720, Content = shell, ShowInTaskbar = false,
                        Position = new PixelPoint(-4000,-4000), WindowStartupLocation = WindowStartupLocation.Manual };
                    window.Show(); Drain(window);
                    var header = shell.Header; var footer = shell.Footer;
                    var retained = new Dictionary<PrimeRoute, Control>();
                    foreach (var route in PrimeRouter.Tabs)
                    {
                        shell.Router.Navigate(route); Drain(window);
                        Check(ReferenceEquals(header, shell.Header) && ReferenceEquals(footer, shell.Footer), "persistent chrome: " + route);
                        Check(FocusNavigator.Ensure(shell.Workspaces) != null, "reachable workspace control: " + route);
                        retained[route] = (Control)shell.Workspaces.Content!;
                    }
                    foreach (var (route, view) in retained)
                    { shell.Router.Navigate(route); Drain(window); Check(ReferenceEquals(view, shell.Workspaces.Content), "retained workspace " + route); }
                    shell.Router.Navigate(PrimeRoute.News); shell.Router.PreviousRoute();
                    Check(shell.Router.Current == PrimeRoute.Settings, "previous tab wraps");
                    shell.Router.NextRoute(); Check(shell.Router.Current == PrimeRoute.News, "next tab wraps");
                    shell.Router.Navigate(PrimeRoute.Play); Drain(window);
                    var play = shell.Workspaces.Get(PrimeRoute.Play);
                    var quick = ControllerNav.Find(play,"multiplayer.quick")!;
                    quick.Focus(); FocusNavigator.Key(quick, Key.Enter); Drain(window);
                    var join = ControllerNav.Find(play,"multiplayer.join")!;
                    Check(join.IsEnabled, "Quick Play selects an open compatible fixture");
                    join.Focus(); shell.Router.Navigate(PrimeRoute.News); shell.Router.Navigate(PrimeRoute.Play); Drain(window);
                    Check(ReferenceEquals(play, shell.Workspaces.Content) && join.IsEnabled, "selected server survives navigation");
                    Check(join.IsFocused, "workspace focus is restored");
                    FocusNavigator.Key(join, Key.Up); Drain(window);
                    Check(FocusNavigator.Focused(shell) != join, "keyboard arrows move spatial focus");
                    join.Focus();
                    var stand = play.GetVisualDescendants().OfType<HunterStand>().First();
                    Check(stand.CanPresentPreview(), "visible workspace hunter can present");
                    shell.Overlays.Show(new PrimePanel(PrimeChrome.Stack(new PrimeButton("CANCEL", shell.Overlays.Close))), PrimeModalSize.Small);
                    Drain(window);
                    Check(!stand.CanPresentPreview(), "workspace hunter cannot paint over a modal");
                    Check(ControllerNav.ModalRoot(shell) == shell.Overlays, "controller focus is trapped in modal");
                    foreach (var direction in new[] { Mods.Input.UiAction.Up, Mods.Input.UiAction.Down, Mods.Input.UiAction.Left, Mods.Input.UiAction.Right })
                    {
                        FocusNavigator.Move(shell, direction);
                        Check(FocusNavigator.Focused(shell.Overlays) != null, "directional focus remains in modal");
                    }
                    FocusNavigator.Key(FocusNavigator.Ensure(shell)!, Key.Escape); Drain(window);
                    Check(!shell.Overlays.IsOpen && shell.Router.Current == PrimeRoute.Play, "Escape closes modal first");
                    Check(join.IsFocused, "modal close restores focus");
                    Check(stand.CanPresentPreview(), "hunter presentation resumes after modal closes");
                    bool resumed = false;
                    shell.Overlays.Show(new PrimePanel(PrimeChrome.Text("pause")), cancel: () => { shell.Overlays.Close(); resumed = true; });
                    shell.Back(); Check(resumed && !shell.Overlays.IsOpen, "Back dispatches overlay cancellation semantics");
                    shell.Router.Navigate(PrimeRoute.Settings); Drain(window);
                    var settings = (SettingsView)shell.Workspaces.Content!;
                    Check(settings.GetVisualDescendants().OfType<PrimeTabButton>().Count() == 7
                        && !settings.GetVisualDescendants().OfType<Control>().Any(c =>
                            c.GetValue(ControllerNav.NavIdProperty)?.StartsWith("settings.detail.Display", StringComparison.OrdinalIgnoreCase) == true),
                        "shell settings has one category strip without a duplicate sidebar");
                    CheckTeamCycling();
                    var slider = settings.GetVisualDescendants().OfType<SliderRow>().First();
                    int value = slider.Value; slider.Value = value == 0 ? 1 : value - 1;
                    Check(settings.IsDirty, "settings edit marks draft dirty");
                    settings.ShowSection("Audio"); settings.ShowSection("Display");
                    Check(settings.IsDirty, "settings category retains draft");
                    settings.DiscardDraft(); Check(slider.Value == value && !settings.IsDirty, "Discard restores controls and clean state");
                    var previousRuntime = Mods.Input.GamepadRuntimeConfig.Current;
                    try
                    {
                        Mods.Input.GamepadRuntimeConfig.Current = new Mods.Input.GamepadRuntimeConfig();
                        var defaults = new SettingsDraft(new Panel());
                        Mods.Input.PadBindings.ApplyPreset("Southpaw");
                        defaults.Discard();
                        Check(!defaults.IsDirty, "default controller preset survives Discard");
                    }
                    finally { Mods.Input.GamepadRuntimeConfig.Current = previousRuntime; }
                    var rows = play.GetVisualDescendants().OfType<ServerRow>().ToArray();
                    Check(rows.Length == 2 && rows[0].CanJoin && !rows[1].CanJoin, "full server cannot be joined");
                    shell.Footer.SetStatus("DOWNLOADING 42%"); shell.Refresh();
                    Check(shell.Footer.Version.Label == "DOWNLOADING 42%", "telemetry refresh preserves update progress");
                    shell.Footer.SetStatus("SIM: 60 HZ // BUILD LOCAL");
                    var played = new LaunchPlan { Kind = LaunchKind.Offline, RoomKey = "MP1 SANCTORUS",
                        Mode = GameMode.Battle, Bots = 5, BotLevel = 3, Hunter = Hunter.Trace, PlayerName = "CHECK" };
                    Check(OfflineRematch.TryPlan(played, "", out var same) && same.RoomKey == played.RoomKey,
                        "unselected bot results repeat current arena");
                    Check(OfflineRematch.TryPlan(played, "MP3 PROVING GROUND", out var next)
                        && next.RoomKey == "MP3 PROVING GROUND" && next.Bots == 5 && next.BotLevel == 3
                        && next.Hunter == Hunter.Trace && next.Mode == played.Mode && next.PlayerName == played.PlayerName,
                        "next arena retains bot match configuration");
                    Check(!OfflineRematch.TryPlan(played with { Kind = LaunchKind.Online }, "MP3 PROVING GROUND", out _)
                        && !OfflineRematch.TryPlan(played with { Kind = LaunchKind.Adventure }, "", out _)
                        && !OfflineRematch.TryPlan(played with { IsPlaytest = true }, "", out _),
                        "local rematch excludes network and Adventure sessions");
                    string? picked = null;
                    var picker = new MapCardPicker(new[] { "MP1 SANCTORUS", "MP3 PROVING GROUND" }, "MP1 SANCTORUS");
                    picker.Done += (_, room) => picked = room;
                    shell.Overlays.Show(picker); Drain(window);
                    var search = (TextBox)ControllerNav.Find(picker, "map-picker.search")!;
                    search.Text = "PROVING"; Drain(window);
                    var cards = picker.GetVisualDescendants().OfType<DeckTile>().ToArray();
                    Check(cards.Count(c => c.IsVisible) == 1, "map picker filters arena cards");
                    var card = cards.Single(c => c.IsVisible); card.Focus(); FocusNavigator.Key(card, Key.Enter);
                    var use = ControllerNav.Find(picker, "map-picker.use")!;
                    use.Focus(); FocusNavigator.Key(use, Key.Enter);
                    Check(picked == "MP3 PROVING GROUND", "map picker confirms selected arena");
                    shell.Overlays.Close();
                    window.Content = null; window.Close();
                    if (directory != null)
                    {
                        Directory.CreateDirectory(directory);
                        foreach (var size in Sizes)
                        foreach (var route in Enum.GetValues<PrimeRoute>())
                        {
                            shell.Router.Navigate(route);
                            string path = Path.Combine(directory, $"prime-{route.ToString().ToLowerInvariant()}-{size.Width:0}x{size.Height:0}.png");
                            Check(UiCapture.Capture(shell, path, size), "capture " + path);
                            Geometry(shell, size, route);
                        }
                        shell.Router.Navigate(PrimeRoute.Settings);
                        settings.ShowSection("Credits");
                        Check(UiCapture.Capture(shell, Path.Combine(directory, "prime-credits-1280x720.png"), new Size(1280,720)), "credits capture");
                        settings.ShowSection("Display");
                        shell.Router.Navigate(PrimeRoute.Lobby);
                        var lobby = (Control)shell.Workspaces.Content!;
                        var rules = lobby.GetVisualDescendants().OfType<PrimeButton>().Single(b => b.Label == "ADVANCED RULES");
                        rules.Focus(); FocusNavigator.Key(rules, Key.Enter);
                        Check(shell.Overlays.IsOpen, "lobby rules open");
                        Check(UiCapture.Capture(shell, Path.Combine(directory, "prime-lobby-rules-1280x720.png"), new Size(1280,720)), "lobby rules capture");
                        foreach (var toggle in shell.Overlays.GetVisualDescendants().OfType<ButtonToggleRow>().Where(t => t.IsVisible))
                        foreach (var button in toggle.GetVisualDescendants().OfType<PrimeButton>())
                            Check(button.Bounds.Width >= 50 && button.Bounds.Height >= 38, "rule toggle has a readable hit target");
                        shell.Overlays.Close();
                        shell.Router.Navigate(PrimeRoute.Play);
                        shell.Overlays.Show(new MapCardPicker(new[] { "MP1 SANCTORUS", "MP3 PROVING GROUND" }, "MP1 SANCTORUS"));
                        Check(UiCapture.Capture(shell, Path.Combine(directory, "prime-map-picker-1280x720.png"), new Size(1280,720)), "map picker capture");
                        shell.Overlays.Close();
                    }
                    shell.Dispose();
                    CheckStartup(directory);
                    Deck.Still = false;
                    var animated = new PrimePanel(PrimeChrome.Text("motion"));
                    HubMotion.Enter(animated); Dispatcher.UIThread.RunJobs();
                    Check(animated.Opacity == 1 && animated.RenderTransform == null, "headless frame clock completes route motion");
                    Deck.Still = true;
                    int pulses = 0;
                    using var pulse = new PrimeUiPulse(TimeSpan.FromMilliseconds(10), () =>
                    { Check(Dispatcher.UIThread.CheckAccess(), "wall-clock pulse runs on UI thread"); pulses++; });
                    pulse.Start();
                    // A busy CI worker may not schedule the thread-pool timer
                    // within one short sleep. Pump until delivery or a bounded deadline.
                    var deadline = System.Diagnostics.Stopwatch.StartNew();
                    while (pulses == 0 && deadline.Elapsed < TimeSpan.FromSeconds(5))
                    { System.Threading.Thread.Sleep(10); Dispatcher.UIThread.RunJobs(); }
                    Check(pulses > 0, "embedded shell pulse advances without a native event loop");
                    pulse.Stop(); int stopped = pulses;
                    System.Threading.Thread.Sleep(30); Dispatcher.UIThread.RunJobs();
                    Check(pulses == stopped, "detached shell pulse stops");
                });
                Console.WriteLine($"[primeuicheck] PASS: {_checks} checks"); return 0;
            }
            catch (Exception ex) { Console.WriteLine("[primeuicheck] FAIL: " + ex); return 1; }
            finally { Deck.Still = previousStill; }
        }
        private static void Drain(Window window)
        { for (int i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); } }
        private static void CheckStartup(string? directory)
        {
            bool autoUpdate = LauncherPrefs.AutoUpdate;
            LauncherPrefs.AutoUpdate = false;
            try
            {
                using var front = new StartScreen(new MenuSettings(), new[] { "MP1 SANCTORUS" });
                var shell = front.Prime;
                var header = shell.Header;
                if (directory != null)
                    foreach (var size in Sizes)
                        Check(UiCapture.Capture(front, Path.Combine(directory,
                            $"prime-startup-{size.Width:0}x{size.Height:0}.png"), size), "startup landscape capture");
                var window = new Window { Width = 1280, Height = 720, Content = front,
                    ShowInTaskbar = false, Position = new PixelPoint(-4000,-4000) };
                try
                {
                    window.Show(); Drain(window);
                    var startup = front.GetVisualDescendants().OfType<PrimeStartupScreen>().Single();
                    Check(startup.IsFocused && !shell.IsVisible && !shell.Overlays.IsOpen,
                        "startup owns focus and defers setup prompts");
                    shell.Router.Navigate(PrimeRoute.Settings);
                    Check(shell.Router.Current == PrimeRoute.News, "startup blocks shell routes");
                    FocusNavigator.Key(startup, Key.Escape); FocusNavigator.Key(startup, Key.E);
                    Check(startup.IsVisible && !startup.Leaving, "startup consumes back and tab navigation");
                    FocusNavigator.Key(startup, Key.Enter); Drain(window);
                    Check(shell.IsVisible && shell.IsEnabled && ReferenceEquals(header, shell.Header)
                        && !front.GetVisualDescendants().OfType<PrimeStartupScreen>().Any(),
                        "Enter reveals the same mounted shell and removes startup");
                    shell.Overlays.Clear();
                    shell.Router.Navigate(PrimeRoute.News);
                    FocusNavigator.Key(FocusNavigator.Ensure(shell)!, Key.Escape);
                    Drain(window);
                    Check(shell.Overlays.GetVisualDescendants().OfType<ConfirmScreen>().Any(),
                        "Escape on the main screen opens quit confirmation");
                    FocusNavigator.Key(FocusNavigator.Ensure(shell.Overlays)!, Key.Escape);
                    Drain(window);
                    Check(!shell.Overlays.IsOpen && shell.Router.Current == PrimeRoute.News,
                        "cancelling quit stays on the main screen");
                    startup.Continue(); front.Reset(new MenuSettings()); Drain(window);
                    Check(shell.IsVisible && !front.GetVisualDescendants().OfType<PrimeStartupScreen>().Any(),
                        "repeat input and match return never reopen startup");
                }
                finally { window.Content = null; window.Close(); }
            }
            finally { LauncherPrefs.AutoUpdate = autoUpdate; }
        }
        private static void CheckTeamCycling()
        {
            var roster = RosterPacket.Create(); roster.Count = 3;
            for (int i = 0; i < 3; i++) { roster.Slots[i] = (byte)i; roster.Teams[i] = (sbyte)i; }
            var layout = new TeamLayout(3, 2, 1, 2);
            Check(LobbyPlayerRow.NextTeam(roster, 0, layout, 1) == 2, "team arrows skip full destinations");
            Check(LobbyPlayerRow.NextTeam(roster, 0, layout, -1) == 2, "previous team wraps");
            Check(LobbyPlayerRow.NextTeam(roster, 2, layout, 1) == 0, "next team wraps");
            Check(LobbyPlayerRow.NextTeam(roster, 0, new TeamLayout(3, 1, 1, 1), 1) == null,
                "full teams cannot be selected");
            Check(LobbyPlayerRow.NextTeam(roster, 7, layout, 1) == null, "departed player cannot change team");
            Check(LobbyPlayerRow.NextTeam(roster, 0, default, 1) == null, "FFA has no team destinations");
            roster.Teams[0] = -1;
            Check(LobbyPlayerRow.NextTeam(roster, 0, layout, 1) == 0, "unassigned player can choose first team");
            Check(LobbyPlayerRow.NextTeam(roster, 0, layout, -1) == 2, "unassigned previous chooses last team");
        }

        private static void Geometry(PrimeShell shell, Size size, PrimeRoute route)
        {
            foreach (var element in shell.GetVisualDescendants().OfType<Control>())
            {
                Check(double.IsFinite(element.Bounds.Width) && double.IsFinite(element.Bounds.Height)
                    && element.Bounds.Width >= 0 && element.Bounds.Height >= 0, "finite geometry: " + element.GetType().Name);
            }
            Check(shell.Header.Bounds.Height > 0 && shell.Footer.Bounds.Height > 0, "chrome visible " + route);
            if (route == PrimeRoute.Lobby)
            {
                var players = shell.GetVisualDescendants().OfType<LobbyPlayerRow>().ToArray();
                Check(players.Length == 8, "eight-player roster rendered");
                var bottom = players[^1].TranslatePoint(new Point(0, players[^1].Bounds.Height), shell);
                Check(bottom.HasValue && bottom.Value.Y < size.Height - 20, "eight-player roster fits");
                var rosterPanel = players[^1].GetVisualAncestors().OfType<PrimePanel>().First();
                var rosterBottom = players[^1].TranslatePoint(new Point(0, players[^1].Bounds.Height), rosterPanel);
                Check(rosterBottom.HasValue && rosterBottom.Value.Y <= rosterPanel.Bounds.Height,
                    "eight-player roster fits inside its panel above loadout");
            }
            string? cta = route switch { PrimeRoute.Play => "multiplayer.join", PrimeRoute.Offline => "offline.start",
                PrimeRoute.Lobby => "lobby.start", PrimeRoute.Settings => "settings.detail.save", _ => null };
            if (cta != null)
            {
                var control = ControllerNav.Find(shell, cta);
                Check(control != null && control.Bounds.Height > 0, "CTA laid out " + cta);
                Point? origin = control!.TranslatePoint(new Point(0,0), shell);
                Point? bottom = control!.TranslatePoint(new Point(control!.Bounds.Width, control.Bounds.Height), shell);
                Check(origin != null && bottom != null && origin.Value.Y >= 0 && origin.Value.X >= 0
                    && bottom.Value.Y <= size.Height - 15 && bottom.Value.X <= size.Width + 1, "CTA inside canvas " + cta);
            }
        }
    }
}
#endif
