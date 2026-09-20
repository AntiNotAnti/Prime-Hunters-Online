#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MphRead.Mods.Network;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Screenshots of the front screen, without a screen.
    ///
    /// The launcher is the one part of this program that could not be looked
    /// at from here: the game renders through GL and can be read back
    /// (ScreenCapture), but the launcher is Avalonia, and checking a change to
    /// it meant opening a window on a machine with a display and looking. On a
    /// headless box, or over SSH, or in CI, there was no way to see what a
    /// layout change had actually done -- which is how a control that moves
    /// under the pointer ships.
    ///
    /// Avalonia can measure, arrange and draw a control into a bitmap with no
    /// window involved, which is all a screenshot of a layout needs. So
    /// `-uishot DIR` builds each screen at a fixed size, renders it, and
    /// writes a PNG.
    ///
    /// What this does *not* prove: that a real window manager gives the window
    /// the size asked for, that the fonts on another machine are these ones,
    /// or that anything is clickable. It proves the layout -- which is what
    /// every report about this screen has been about.
    /// </summary>
    internal static class UiCapture
    {
        /// <summary>
        /// The size the screens are photographed at. Close to what the game
        /// window gives them at its own startup size, which is what they are
        /// laid out against (see UiSurface.Scale).
        /// </summary>
        // 16:9 exactly. It was 940x560, which is 1.68 and not the ratio the
        // game window or any monitor has -- so every screen was being laid out
        // with thirty points of height nothing would ever give it.
        private static readonly Size _windowSize = new Size(940, 528);

        /// <summary>
        /// A Galaxy S21's two orientations, in CSS points: 360x800 and the
        /// same panel turned. The launcher is one layout sized off the width
        /// it is given, so these are not a second design -- they are the same
        /// screens asked the question the Android head asks them, and the
        /// only way to see the answer without an emulator.
        /// </summary>
        private static readonly Size _phonePortrait = new Size(360, 800);
        private static readonly Size _phoneLandscape = new Size(800, 360);

        public static int RunReplay(string directory, string replay)
        {
            if (!GuiLauncher.EnsureSetup()) return 1;
            Directory.CreateDirectory(directory);
            if (!DemoPlayback.Join(replay)) { Console.WriteLine(DemoPlayback.LastError); return 1; }
            int written = 0;
            try
            {
                Dispatcher.UIThread.Invoke(() =>
                {
                    foreach (var (name, size) in new[] { ("desktop", new Size(1280, 720)), ("phone", new Size(960, 540)), ("short", new Size(800, 400)) })
                    {
                        if (Capture(new PauseMenuView(offerWindowMode: true), Path.Combine(directory, "replay-controls-" + name + ".png"), size)) written++;
                        if (Capture(new PlayScreen(new MenuSettings(), RoomList(), PlayScreen.Face.Clips), Path.Combine(directory, "replay-library-" + name + ".png"), size)) written++;
                    }
                });
            }
            finally { DemoPlayback.Stop(); NetSession.Stop(); }
            Console.WriteLine($"[replayshot] {written} layouts written to {directory}");
            return written == 6 ? 0 : 1;
        }

        public static int Run(string directory)
        {
            if (!GuiLauncher.EnsureSetup())
            {
                Console.WriteLine("[uishot] no Avalonia backend on this machine; nothing captured");
                return 1;
            }
            Directory.CreateDirectory(directory);
            // The front screen's Share button only exists where something can
            // receive a file, which today is Android alone -- so without a
            // stand-in the one corner this tool was made to check could never
            // be photographed as a phone draws it. Same reason as SampleDemos
            // below, and it is still only offered when real logs exist.
            Mods.LogShare.Current ??= new CaptureLogShare();
            // Springs jump straight to where they were going, and nothing
            // bobs. See Deck.Still for why this is a correctness fix and not
            // only a tidiness one.
            Deck.Still = true;
            int written = 0;
            // On the toolkit's own thread, and drained afterwards: the views
            // post work to the dispatcher as they are built (the front screen
            // focuses its first control that way), and a render before that
            // has run is a picture of a half-built screen.
            Dispatcher.UIThread.Invoke(() =>
            {
                var settings = new MenuSettings();
                List<string> rooms = RoomList();
                foreach ((string name, Control view, Size size) in Screens(settings, rooms))
                {
                    string path = Path.Combine(directory, $"{name}.png");
                    // The em is read off a different curve on a phone (see
                    // Deck.EmFor), and which curve is a property of the head
                    // rather than of the box -- so a desktop photographing a
                    // phone has to say so. Set before the render, not before
                    // the construction: the screens read it in a measure pass.
                    Deck.Phone = size == _phonePortrait || size == _phoneLandscape;
                    PlayScreen.Sample = _browser;
                    if (Capture(view, path, size))
                    {
                        written++;
                        Console.WriteLine($"[uishot] {path}");
                    }
                }
            });
            Deck.Phone = OperatingSystem.IsAndroid();
            Deck.Still = false;
            PlayScreen.Sample = null;
            Console.WriteLine($"[uishot] {written} screen(s) written to {directory}");
            return written > 0 ? 0 : 1;
        }

        private static List<string> RoomList()
        {
            var rooms = new List<string>();
            try
            {
                foreach (RoomMetadata meta in Metadata.RoomMetadata.Values)
                {
                    if (meta.Multiplayer)
                    {
                        rooms.Add(meta.Name);
                    }
                }
            }
            catch (Exception)
            {
                // No game files here. The screens still lay out; the map rows
                // are simply empty, which is itself worth being able to see.
            }
            rooms.Sort(StringComparer.OrdinalIgnoreCase);
            return rooms;
        }

        private static IEnumerable<(string, Control, Size)> Screens(MenuSettings settings,
            IReadOnlyList<string> rooms)
        {
            yield return ("start", new StartScreen(settings, rooms), _windowSize);
            yield return ("start-phone-portrait",
                new StartScreen(settings, rooms), _phonePortrait);
            yield return ("start-phone-landscape",
                new StartScreen(settings, rooms), _phoneLandscape);
            yield return ("hub-home", new HubHomeView(), _windowSize);
            yield return ("hub-home-phone-portrait", new HubHomeView(), _phonePortrait);
            yield return ("hub-home-phone-landscape", new HubHomeView(), _phoneLandscape);
            yield return ("play", new HubPlayView(), _windowSize);
            yield return ("play-phone-portrait", new HubPlayView(), _phonePortrait);
            yield return ("play-phone-landscape", new HubPlayView(), _phoneLandscape);
            yield return ("multiplayer", new HubMultiplayerView(), _windowSize);
            yield return ("multiplayer-phone-landscape",
                new HubMultiplayerView(), _phoneLandscape);
            yield return ("map-editor-placeholder",
                new HubPlaceholderView("MAP EDITOR", "WORKSHOP PLACEHOLDER",
                    "A visual custom-map editor is planned for this hub."),
                _windowSize);
            yield return ("quick-play", new HubQuickPlayView(preview: true), _windowSize);
            yield return ("hub-servers",
                new HubServerBrowserView(HubBrowserSample()), _windowSize);
            yield return ("hub-servers-phone-landscape",
                new HubServerBrowserView(HubBrowserSample()), _phoneLandscape);
            yield return ("hub-settings", new HubSettingsView(), _windowSize);
            yield return ("hub-settings-phone-portrait", new HubSettingsView(), _phonePortrait);
            // Every face of the one screen that replaced seven. They share a
            // layout and nothing else -- the list, the settings beside it and
            // the word on the tick are different on each -- so one picture of
            // it would prove nothing about the other three.
            yield return ("play-online",
                new PlayScreen(settings, rooms, PlayScreen.Face.Online), _windowSize);
            // The browser in the two shapes a phone gives it. It is the one
            // screen whose row drops columns below a 560-point frame, and the
            // only way to see that happen without an emulator.
            yield return ("play-online-phone-portrait",
                new PlayScreen(settings, rooms, PlayScreen.Face.Online), _phonePortrait);
            yield return ("play-online-phone-landscape",
                new PlayScreen(settings, rooms, PlayScreen.Face.Online), _phoneLandscape);
            yield return ("offline", new HubOfflineView(settings, rooms), _windowSize);
            yield return ("offline-phone-landscape",
                new HubOfflineView(settings, rooms), _phoneLandscape);
            yield return ("adventure", new HubAdventureView(), _windowSize);
            yield return ("adventure-phone-portrait",
                new HubAdventureView(), _phonePortrait);
            yield return ("replay-studio", new HubReplayStudioView(), _windowSize);
            yield return ("replay-studio-phone-landscape",
                new HubReplayStudioView(), _phoneLandscape);
            yield return ("play-vote",
                new PlayScreen(settings, rooms, PlayScreen.Face.Vote, overGame: true),
                _windowSize);
            // Both faces of creating a server, and the map list it opens.
            // The dedicated one is a separate picture because the rows it
            // hides and the warning it raises are the whole difference between
            // the two, and neither shows on the other.
            yield return ("custom-match",
                new CreateServerScreen(rooms, discoverHosts: false), _windowSize);
            yield return ("custom-match-phone-landscape",
                new CreateServerScreen(rooms, discoverHosts: false), _phoneLandscape);
            var dedicated = new CreateServerScreen(rooms, discoverHosts: false);
            dedicated.ShowDedicated();
            yield return ("custom-match-dedicated", dedicated, _windowSize);
            yield return ("custom-match-rotation",
                new MapRotationPicker(rooms, Array.Empty<string>()), _windowSize);
            yield return ("custom-match-hosts", new HostPicker(Fleet(), asking: false),
                _windowSize);
            yield return ("settings", new SettingsView(settings), _windowSize);
            var credits = new SettingsView(settings);
            credits.ShowSection("Profile");
            yield return ("settings-player", credits, _windowSize);
            var controls = new SettingsView(settings);
            controls.ShowSection("Controls");
            yield return ("settings-controls", controls, _windowSize);
            // The pad's own sub-page, photographed on purpose: its rows are
            // built with the page hidden and are the ones that used to take
            // the process down when Controls was opened. A shot of the
            // Keyboard sub-page alone proves nothing about them.
            var pad = new SettingsView(settings);
            pad.ShowSection("Controls", sub: 1);
            yield return ("settings-gamepad", pad, _windowSize);
            // The results panel, on its own. The scoreboard beside it in a
            // real match is the engine's and is not drawn here -- this is the
            // half the theme owns.
            yield return ("end-panel", new EndPanelView(), _windowSize);
            var endHunter = new EndPanelView();
            endHunter.ShowHunter();
            yield return ("end-panel-hunter", endHunter, _windowSize);
            yield return ("setup", new SetupScreen(), _windowSize);
            yield return ("confirm",
                new ConfirmScreen($"Quit {Mods.Branding.Name}?"), _windowSize);
            yield return ("pausemenu", new PauseMenuView(offerWindowMode: true), _windowSize);
            // Deliberately shorter than the menu's own content, and shorter
            // than the game window is now allowed to be. The pause menu is
            // laid over the game window, so its host is whatever size the
            // player dragged that to, and entries drawn off the bottom edge
            // are a player who cannot leave the match. This is the check that
            // the column shrinks to carry them.
            yield return ("pausemenu-small", new PauseMenuView(offerWindowMode: true),
                new Size(560, 320));
            // The same menu where Android shows it: on the front screen's own
            // stack rather than in the game window. It is the one screen that
            // draws no ground of its own, so what is behind it is whatever the
            // front screen left there -- which is how the main menu's
            // photograph, its moving layer and its three faces ended up behind
            // a pause menu on that head and on no other.
            var paused = new StartScreen(settings, rooms);
            paused.ShowPauseMenu(() => { }, () => { }, () => { });
            yield return ("pausemenu-phone", paused, _phoneLandscape);
            yield return ("serverbrowser", ServerList(), _windowSize);
        }

        private static IReadOnlyList<ServerBrowserEntry> HubBrowserSample()
        {
            var entries = new List<ServerBrowserEntry>();
            foreach ((string name, string endpoint, ServerStatus status) in _browser)
            {
                string address = endpoint;
                int port = NetConfig.DefaultPort;
                int colon = endpoint.LastIndexOf(':');
                if (colon > 0 && Int32.TryParse(endpoint[(colon + 1)..], out int parsed))
                {
                    address = endpoint[..colon];
                    port = parsed;
                }
                entries.Add(new ServerBrowserEntry(
                    new MasterListing
                    {
                        Address = address,
                        Port = port,
                        ServerName = name,
                        RoomKey = status.RoomKey,
                        Mode = status.Mode,
                        Players = status.Players,
                        MaxPlayers = status.MaxPlayers,
                        Protocol = status.Protocol
                    },
                    status));
            }
            return entries;
        }

        /// <summary>
        /// The fleet as the host picker draws it, without asking the network:
        /// one that will run a match, one too old to say so, and one with no
        /// directory at all. The three states are the whole point of the
        /// screen, and a capture that queried the real directory would
        /// photograph whichever of them happened to be true that morning.
        /// </summary>
        private static List<HostCandidate> Fleet()
        {
            return new List<HostCandidate>
            {
                new() { Label = "net.livetek.fr", Host = "net.livetek.fr", Port = 27889,
                    Answered = true, CanHost = true, Latency = 3 },
                new() { Label = "Fruity Prime - West Europe", Host = "20.16.135.109",
                    Port = 27889, Answered = true, CanHost = null, Latency = 39 },
                new() { Label = "Fruity Prime - Japan", Host = "13.78.14.98", Port = 27889,
                    Answered = false, CanHost = null, Latency = -1 }
            };
        }

        /// <summary>
        /// Somewhere for the Share button to point while it is being
        /// photographed. Nothing is built and nothing is sent: a capture has
        /// nobody to press it.
        /// </summary>
        private sealed class CaptureLogShare : Mods.ILogShare
        {
            public string StagingPath(string fileName) =>
                Path.Combine(Path.GetTempPath(), fileName);

            public bool Share(string path, string subject, out string error)
            {
                error = "there is nothing to share to on this platform";
                return false;
            }
        }

        /// <summary>
        /// The browser's table, at the width the panel gives it, with rows
        /// standing in for servers that are not up.
        ///
        /// Built here rather than reached through the play screen because that
        /// one only fills in when a directory answers -- and the fault this is
        /// for (a map name wrapping onto the row below, headings running into
        /// each other) is a property of the columns and the width, not of any
        /// real server. Both widths are drawn, so a narrow row is checked too.
        /// </summary>
        private static Control ServerList()
        {
            var stack = new StackPanel { Spacing = 18, Margin = new Thickness(12) };
            foreach (double width in new[] { 600.0, 400.0 })
            {
                var list = new StackPanel { Spacing = 2, Width = width };
                foreach ((string name, string endpoint, ServerStatus status) in _browser)
                {
                    var row = new ServerRow(name, endpoint);
                    row.SetStatus(status);
                    list.Children.Add(row);
                }
                stack.Children.Add(list);
            }
            return stack;
        }

        /// <summary>
        /// The browser with servers in it, with no directory and no network.
        ///
        /// <para>
        /// A photograph of the browser is a photograph of its columns, its
        /// flags and its three ping bands, and a capture run against the real
        /// directory shows whichever of those happened to be true that
        /// morning -- usually thirteen rows of "asking...", which is the one
        /// state that says nothing about the other two.
        /// </para>
        ///
        /// <para>
        /// The endpoints are <b>real addresses</b>, because the flag is read
        /// out of the address (<see cref="GeoCountry"/>) and a list of sample
        /// rows on 203.0.113.x would photograph the same badge thirteen times.
        /// One is deliberately on this network and one deliberately never
        /// answers: those are the two cases that must not invent a country.
        /// The pings straddle both band edges (60 and 120) on purpose.
        /// </para>
        /// </summary>
        private static readonly IReadOnlyList<(string, string, ServerStatus)> _browser =
            Build();

        private static List<(string, string, ServerStatus)> Build()
        {
            var rows = new List<(string, string, ServerStatus)>();
            void Add(string name, string endpoint, string room, GameMode mode,
                int players, int max, int ping)
            {
                rows.Add((name, endpoint, new ServerStatus
                {
                    Online = ping >= 0,
                    RoomKey = room,
                    Mode = mode,
                    Players = players,
                    MaxPlayers = max,
                    Latency = ping
                }));
            }
            Add("Combat Hall 24/7", "82.66.14.9:27888", "MP3 PROVING GROUND",
                GameMode.Battle, 4, 8, 24);
            Add("Prime EU #1", "85.214.228.188:27888", "AD2 MAGMA VENTS",
                GameMode.BountyTeams, 7, 8, 48);
            Add("Arcterra Pickup", "83.19.146.2:27890", "MP9 CRYOCHASM",
                GameMode.Survival, 2, 6, 91);
            Add("MPH Speedrun", "51.140.44.19:27888", "AD1 TRANSFER LOCK DM",
                GameMode.PrimeHunter, 1, 4, 168);
            Add("hunters.us.west", "34.94.2.11:27888", "MP8 FIRE CONTROL",
                GameMode.BattleTeams, 6, 8, 212);
            Add("Sic Transit 24/7", "91.198.174.192:27888", "MP12 SIC TRANSIT",
                GameMode.Capture, 3, 8, 57);
            Add("LAN - Pi 4", "192.168.1.42:27888", "MP2 HARVESTER",
                GameMode.Capture, 0, 8, 3);
            Add("Ice Hive Rotation", "80.50.24.3:27888", "MP9 CRYOCHASM",
                GameMode.Nodes, 4, 8, 74);
            Add("Head Shot only", "92.222.10.7:27888", "MP6 HEADSHOT",
                GameMode.Battle, 5, 6, 31);
            Add("Elder Passage.CTF", "54.230.11.9:27888", "MP4 HIGHGROUND - EXPANDED",
                GameMode.Capture, 5, 8, 143);
            Add("Data Shrine.1v1", "217.160.0.153:27888", "MP1 SANCTORUS",
                GameMode.Battle, 2, 2, 39);
            Add("Fuel Stack Rotation", "51.148.31.4:27888", "MP13 ACCELERATOR",
                GameMode.Bounty, 2, 8, 66);
            Add("old.vesper", "45.33.32.156:27888", "", GameMode.Battle, 0, 0, -1);
            return rows;
        }

        /// <summary>
        /// Render one screen.
        ///
        /// Through a real <see cref="Window"/>, not by laying the control out
        /// on its own. Avalonia resolves styles through the visual tree's
        /// style host, and a control with no window above it has none: it
        /// measures, arranges and renders perfectly happily and comes out a
        /// flat rectangle of the background colour, which is exactly what the
        /// first attempt at this produced. The window is what connects the
        /// tree to the Application's styles.
        ///
        /// It is shown, because a window that has never been shown has no
        /// layout pass behind it -- but shown *off the side of the display*
        /// and without taking focus, so a capture run does not steal the
        /// pointer or flash a window per screen.
        /// </summary>
        /// <summary>
        /// Every control's box, beside the picture of it.
        ///
        /// A screenshot says two layouts differ; it does not say by how much,
        /// and "looks close" is how a port stops three points short on every
        /// screen and nobody can say which three. This writes what the toolkit
        /// actually arranged -- type, label, x, y, width, height, in the
        /// window's own coordinates -- so a reference layout and this one can
        /// be diffed as numbers and the difference fixed rather than eyeballed.
        ///
        /// Beside the PNG and with the same stem, because the two are only
        /// useful together.
        /// </summary>
        private static void DumpBounds(Visual root, string path)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[\n");
            bool first = true;
            Walk(root, root, sb, ref first);
            sb.Append("\n]\n");
            File.WriteAllText(path, sb.ToString());
        }

        private static void Walk(Visual node, Visual root,
            System.Text.StringBuilder sb, ref bool first)
        {
            foreach (Visual child in node.GetVisualChildren())
            {
                if (child.Bounds.Width > 0 && child.Bounds.Height > 0)
                {
                    Point origin = child.TranslatePoint(new Point(0, 0), root)
                        ?? new Point(0, 0);
                    if (!first)
                    {
                        sb.Append(",\n");
                    }
                    first = false;
                    sb.Append(" {\"type\":\"").Append(child.GetType().Name)
                      .Append("\",\"label\":\"").Append(Escape(LabelOf(child)))
                      .Append("\",\"x\":").Append(Math.Round(origin.X, 1))
                      .Append(",\"y\":").Append(Math.Round(origin.Y, 1))
                      .Append(",\"w\":").Append(Math.Round(child.Bounds.Width, 1))
                      .Append(",\"h\":").Append(Math.Round(child.Bounds.Height, 1))
                      .Append('}');
                }
                Walk(child, root, sb, ref first);
            }
        }

        private static string LabelOf(Visual v)
        {
            return v switch
            {
                TextBlock t => t.Text ?? "",
                TextBox b => b.Text ?? "",
                _ => ""
            };
        }

        private static string Escape(string text)
        {
            return text.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\n", " ").Replace("\r", "");
        }

        internal static bool Capture(Control view, string path, Size size)
        {
            Window? window = null;
            try
            {
                window = new Window
                {
                    Width = size.Width,
                    Height = size.Height,
                    Background = GuiTheme.PanelBrush,
                    RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
                    WindowDecorations = WindowDecorations.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Position = new PixelPoint(-4000, -4000),
                    Content = view
                };
                window.Show();
                // The views post work to the dispatcher as they are built --
                // the front screen focuses its first control that way, and the
                // map picker loads its pictures -- and a render before that has
                // run is a picture of a half-built screen. Several passes,
                // because one job can queue another.
                for (int i = 0; i < 8; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                }
                window.Measure(size);
                window.Arrange(new Rect(size));
                Dispatcher.UIThread.RunJobs();
                var bitmap = new RenderTargetBitmap(
                    new PixelSize((int)size.Width, (int)size.Height),
                    new Vector(96, 96));
                bitmap.Render(window);
                bitmap.Save(path, PngBitmapEncoderOptions.Default);
                DumpBounds(window, Path.ChangeExtension(path, ".json"));
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[shot] {Path.GetFileName(path)} could not be rendered: {ex.Message}");
                return false;
            }
            finally
            {
                window?.Close();
            }
        }

    }
}
#endif
