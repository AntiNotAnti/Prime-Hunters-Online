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
                PrimeRoute.News => new NewsWorkspace(r => shell!.Router.Navigate(r), shell!.Overlays),
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
                players.Children.Add(new LobbyPlayerRow(roster, i, 0));
            }
            return lobby;
        }
        public static int Run(string? directory)
        {
            if (!GuiLauncher.EnsureSetup(requireDisplay: false)) return 1;
            _checks = 0; bool previousStill = Deck.Still; Deck.Still = true;
            try
            {
                Dispatcher.UIThread.Invoke(() =>
                {
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
                    shell.Overlays.Show(new PrimePanel(PrimeChrome.Stack(new PrimeButton("CANCEL", shell.Overlays.Close))), PrimeModalSize.Small);
                    Drain(window);
                    Check(ControllerNav.ModalRoot(shell) == shell.Overlays, "controller focus is trapped in modal");
                    foreach (var direction in new[] { Mods.Input.UiAction.Up, Mods.Input.UiAction.Down, Mods.Input.UiAction.Left, Mods.Input.UiAction.Right })
                    {
                        FocusNavigator.Move(shell, direction);
                        Check(FocusNavigator.Focused(shell.Overlays) != null, "directional focus remains in modal");
                    }
                    FocusNavigator.Key(FocusNavigator.Ensure(shell)!, Key.Escape); Drain(window);
                    Check(!shell.Overlays.IsOpen && shell.Router.Current == PrimeRoute.Play, "Escape closes modal first");
                    Check(join.IsFocused, "modal close restores focus");
                    bool resumed = false;
                    shell.Overlays.Show(new PrimePanel(PrimeChrome.Text("pause")), cancel: () => { shell.Overlays.Close(); resumed = true; });
                    shell.Back(); Check(resumed && !shell.Overlays.IsOpen, "Back dispatches overlay cancellation semantics");
                    shell.Router.Navigate(PrimeRoute.Settings); Drain(window);
                    var settings = (SettingsView)shell.Workspaces.Content!;
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
                    }
                    shell.Dispose();
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
