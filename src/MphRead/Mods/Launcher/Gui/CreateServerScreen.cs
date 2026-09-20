using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MphRead.Entities;
using MphRead.Mods;
using MphRead.Mods.Network;
using MphRead.Mods.Update;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Run a server, from the browser that lists them.
    ///
    /// Hosting used to be a row on the *offline* face -- "Where: Local or
    /// Online" -- which is the wrong screen twice over: offline is the one
    /// face of Play that is about not being online, and the row's second
    /// answer produced a server, which is a thing other people join rather
    /// than a variant of a match against bots. It is here now, beside the
    /// servers it is an alternative to joining.
    ///
    /// Two kinds of server, and the difference is whose machine runs it:
    ///
    /// - **Hosted** -- a directory starts an ordinary
    ///   <see cref="DedicatedServer"/> on its own box and answers with the
    ///   port. Nothing has to reach into the player's network, so there is no
    ///   router to configure and no firewall to open, and it is the default
    ///   for exactly that reason. It lives as long as somebody is in it.
    /// - **Dedicated** -- the server runs *here*, as its own process, and the
    ///   player joins it over the loopback. Their own rotation, their own
    ///   uptime, nobody else's port range -- and a router that has to forward
    ///   UDP before anybody outside can reach it, which is the warning this
    ///   screen exists to put in front of somebody before they pick it rather
    ///   than after.
    ///
    /// The player is joined either way, without the launcher closing: a
    /// dedicated server is dialled on 127.0.0.1 rather than on whatever this
    /// machine's public address is, because the loopback is the one address
    /// that is certain to reach a server on this box -- a router that hairpins
    /// badly is a thing that exists, and a player watching their own server
    /// fail to admit them would have no way to tell that apart from a server
    /// that did not start.
    /// </summary>
    internal sealed class CreateServerScreen : UserControl
    {
        /// <summary>Backed out without creating anything.</summary>
        public event EventHandler? Closed;

        /// <summary>The server is up and this player is in it.</summary>
        public event EventHandler<LaunchPlan>? Launched;

        private static readonly (string Label, GameMode Mode)[] _modes =
        {
            ("Battle", GameMode.Battle),
            ("Battle teams", GameMode.BattleTeams),
            ("Survival", GameMode.Survival),
            ("Survival teams", GameMode.SurvivalTeams),
            ("Capture", GameMode.Capture),
            ("Bounty", GameMode.Bounty),
            ("Bounty teams", GameMode.BountyTeams),
            ("Defender", GameMode.Defender),
            ("Defender teams", GameMode.DefenderTeams),
            ("Nodes", GameMode.Nodes),
            ("Nodes teams", GameMode.NodesTeams),
            ("Prime hunter", GameMode.PrimeHunter)
        };

        private static readonly string[] _hunters =
            Enumerable.Range(0, Hunters.Playable).Select(i => ((Hunter)i).ToString())
                .Append(Hunter.Random.ToString()).ToArray();

        /// <summary>Hosted first: it is the one that needs nothing opening.</summary>
        private static readonly string[] _kinds = { "Hosted lobby", "Dedicated server" };

        /// <summary>
        /// Whether running the server on *this* machine is an answer at all.
        ///
        /// It is not on Android, and not for one reason but for four, any one
        /// of which is enough. The package holds no server binary and there is
        /// none published for the platform, so <see cref="LocalServer.Ready"/>
        /// is false and the install mark has nothing to fetch. A dedicated
        /// server is a second process that outlives the client, which is not
        /// something an app may start on Android. A phone is behind carrier
        /// NAT, so there is no port to forward even if it could. And the
        /// process would be killed the moment the player switched away.
        ///
        /// So the row is not drawn there rather than drawn and refused. A
        /// choice with one answer is not a choice, and the rest of this
        /// launcher does not draw rows that can never be used -- which is the
        /// same rule <see cref="Refresh"/> applies to the Host on row.
        /// </summary>
        internal static bool CanRunHere => !OperatingSystem.IsAndroid();

        private readonly List<string> _rooms;

        /// <summary>The maps, in the order they will be played.</summary>
        private readonly List<string> _rotation = new();

        private readonly Panel _root = new();
        private readonly StackPanel _form = new() { Spacing = 2 };
        private readonly Note _note = new("");
        private readonly ProgressRow _progress = new();

        private readonly FieldRow _name;
        private readonly ChoiceRow _mode;
        private readonly ChoiceRow _hunter;
        private readonly ChoiceRow _kind;
        private readonly PickRow _host;
        private readonly PickRow _maps;

        private readonly UiMark _back;
        private readonly UiMark _go;
        private readonly UiMark _fetch;

        private readonly Control _page;

        /// <summary>Every machine that was asked, whether or not it will host.</summary>
        private readonly List<HostCandidate> _candidates = new();

        /// <summary>The one picked, or null while nothing is.</summary>
        private HostCandidate? _chosen;

        /// <summary>The host page while it is open, so answers still arriving reach it.</summary>
        private HostPicker? _picker;

        private bool _finished;
        private bool _busy;

        /// <summary>
        /// True while the directories are being asked, which takes a round
        /// trip and up to a second and a half of timeout.
        ///
        /// Tracked because the screen is built and drawn before the answer
        /// arrives, and "no directory has offered to run a game" is a
        /// different sentence from "nobody has answered yet" -- the first
        /// tells a player to go and pick Dedicated, and saying it while the
        /// question is still in flight sends them off to solve a problem that
        /// is about to not exist.
        /// </summary>
        private bool _asking = true;
        private CancellationTokenSource? _work;

        public CreateServerScreen(IReadOnlyList<string> rooms, string? firstMap = null)
        {
            _rooms = new List<string>(rooms);
            Background = Brushes.Transparent;
            Focusable = true;

            string player = LauncherPrefs.PlayerName.Trim();
            _name = new FieldRow("Lobby name",
                (player.Length > 0 ? player : "Player") + "'s lobby", boxWidth: 230);
            _mode = new ChoiceRow("Game type", _modes.Select(m => m.Label).ToArray());
            _hunter = new ChoiceRow("Your hunter", _hunters,
                Math.Max(0, Array.IndexOf(_hunters, LauncherPrefs.LastHunter.ToString())));
            _host = new PickRow("Host on");
            _host.Set("asking...");
            _host.Clicked += (_, _) => OpenHosts();
            _maps = new PickRow("Map rotation");
            _maps.Clicked += (_, _) => OpenMaps();
            _kind = new ChoiceRow("Hosting", _kinds, 0);
            _kind.Changed += (_, _) => Refresh();

            _form.Children.Add(_name);
            _form.Children.Add(_mode);
            _form.Children.Add(_hunter);
            _form.Children.Add(_maps);
            _form.Children.Add(_host);
            if (CanRunHere)
            {
                _form.Children.Add(_kind);
            }
            _form.Children.Add(_progress);
            _form.Children.Add(_note);

            _back = new UiMark(UiMark.Shape.Cancel, "back");
            _back.Click += (_, _) => Leave();
            _go = new UiMark(UiMark.Shape.Accept, "continue");
            _go.Click += (_, _) => Go();
            // Only ever drawn when a dedicated server cannot be started as
            // things stand. It is not a second way to do the same thing: the
            // tick is refused while it is up, because there is nothing behind
            // the tick to run.
            _fetch = new UiMark(UiMark.Shape.Fetch, "files required -- install")
            {
                IsVisible = false
            };
            _fetch.Click += (_, _) => Fetch();

            var body = new ScrollViewer
            {
                Content = _form,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _page = UiLayout.Page(overGame: false, UiLayout.WellSettings,
                "create lobby", strip: null, body: body, no: _back, yes: _go,
                extra: _fetch);
            _root.Children.Add(_page);
            Content = _root;

            // A rotation of one, so the screen opens on something playable and
            // the map row is never empty on a screen that refuses to continue
            // without one. The map they last played rather than the first in
            // the list: somebody who has been playing one map and now wants to
            // run a server for it should not have to pick it again.
            string? start = firstMap != null && _rooms.Contains(firstMap)
                ? firstMap
                : _rooms.Count > 0 ? _rooms[0] : null;
            if (start != null)
            {
                _rotation.Add(start);
            }
            _maps.Set(Describe());
            Refresh();
            AskDirectories();
        }

        /// <summary>
        /// Open on the dedicated answer, for the capture that photographs it.
        /// Nothing else calls it: a player picks the row.
        /// </summary>
        public void ShowDedicated()
        {
            _kind.Index = 1;
            Refresh();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Dispatcher.UIThread.Post(() => _name.Box.Focus(), DispatcherPriority.Background);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _work?.Cancel();
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Leave();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        private bool Dedicated => CanRunHere && _kind.Index == 1;

        private void Leave()
        {
            if (_finished || _busy)
            {
                return;
            }
            _finished = true;
            _work?.Cancel();
            Closed?.Invoke(this, EventArgs.Empty);
        }

        // ---------------------------------------------------------- the shape

        /// <summary>
        /// What the two kinds of server make true, applied in one place.
        ///
        /// A row that can never be used is not drawn -- the rule the rest of
        /// the launcher already follows -- so the list of directories goes away
        /// for a server that runs here, and the install mark appears only when
        /// there is nothing here to run one with.
        /// </summary>
        private void Refresh()
        {
            _host.IsVisible = !Dedicated;
            if (!Dedicated)
            {
                _fetch.IsVisible = false;
                _go.IsEnabled = !_busy;
                if (_asking)
                {
                    Say("Asking who can run one...", GuiTheme.TextDim);
                }
                else if (_chosen != null)
                {
                    Say($"{_chosen.Value.Label} opens the match on its own machine. "
                        + "Nothing to forward here.", GuiTheme.TextDim);
                }
                else
                {
                    Say(CanRunHere
                        ? "No server will open one for you. Pick Dedicated server to run "
                            + "it here."
                        // Nothing for the player to do about it on a phone,
                        // which cannot run one itself -- so say what is true
                        // rather than name a row that is not on the screen.
                        : "No server will open one for you. Try again in a moment, or "
                            + "join somebody else's from the browser.", GuiTheme.Warm);
                }
                return;
            }
            bool ready = LocalServer.Ready;
            _fetch.IsVisible = !ready && !_busy;
            _go.IsEnabled = ready && !_busy;
            if (!ready)
            {
                Say(LocalServer.CanInstall
                    ? $"{UpdateCheck.ServerBinaryName()} is not here yet -- install it below."
                    : "No server package is published for this platform. Use Hosted.",
                    GuiTheme.Warm);
                return;
            }
            // The one thing somebody has to do outside this program, said
            // before they commit rather than after they wonder why nobody
            // joins -- and said in one line, because a paragraph of warning is
            // a paragraph nobody reads.
            Say($"Runs here, in its own window. Forward UDP {NetConfig.DefaultPort} to this "
                + "PC for anyone outside to join; you join over 127.0.0.1 either way.",
                GuiTheme.Warm);
        }

        private void Say(string text, Color colour)
        {
            _note.Text = text;
            _note.Foreground = new SolidColorBrush(colour);
        }

        private string Describe()
        {
            if (_rotation.Count == 0)
            {
                return "none picked";
            }
            (RoomMetadata? meta, _) = Metadata.GetRoomByName(_rotation[0]);
            string first = meta?.InGameName ?? _rotation[0];
            return _rotation.Count == 1
                ? first
                : $"{first} +{(_rotation.Count - 1).ToString(CultureInfo.InvariantCulture)} more";
        }

        // -------------------------------------------------------- directories

        /// <summary>
        /// Who can run a match, asked rather than assumed -- and every box
        /// that was asked is kept, not just the ones that said yes.
        ///
        /// A list containing only the usable answers cannot distinguish "Japan
        /// was not asked" from "Japan cannot host", which are the same absence
        /// and completely different problems. The picker shows the whole
        /// fleet with a reason against each, and only the ones that will run a
        /// match can be pressed.
        /// </summary>
        private void AskDirectories()
        {
            _asking = true;
            _candidates.Clear();
            _chosen = null;
            NetMasterClient.FindHosts(LauncherPrefs.MasterHost, LauncherPrefs.MasterPort,
                onFound: candidate => Dispatcher.UIThread.Post(() => Arrived(candidate)),
                onDone: () => Dispatcher.UIThread.Post(() =>
                {
                    if (_finished)
                    {
                        return;
                    }
                    _asking = false;
                    DebugLog.Line("net", $"{_candidates.Count} host(s) asked, "
                        + $"{_candidates.Count(c => c.WillHost)} can run a match");
                    if (_chosen == null)
                    {
                        _host.Set("nobody");
                    }
                    Refresh();
                }));
        }

        /// <summary>
        /// One machine answered. Shown now rather than with the rest: the
        /// configured directory normally replies in tens of milliseconds and
        /// the row is usable the moment it does, while a box with no
        /// directory costs the whole timeout and has nothing worth waiting
        /// for.
        /// </summary>
        private void Arrived(HostCandidate candidate)
        {
            if (_finished)
            {
                return;
            }
            NetMasterClient.Merge(_candidates, candidate);
            // Merge may have replaced a row rather than added one -- the same
            // machine answering on its other port, with a better answer -- so
            // the choice is re-read off the list instead of being tracked
            // alongside it.
            HostCandidate? best = null;
            foreach (HostCandidate entry in _candidates)
            {
                if (entry.WillHost)
                {
                    best = entry;
                    break;
                }
            }
            if (best != null && (_chosen == null || !_chosen.Value.WillHost))
            {
                _chosen = best;
                _host.Set(best.Value.Label);
                // The tick works from here on, without waiting for the boxes
                // that are never going to reply.
                _asking = false;
                Refresh();
            }
            if (_picker != null)
            {
                _picker.Show(_candidates, _asking);
            }
        }

        /// <summary>The fleet, as a page: which machine runs your match.</summary>
        private void OpenHosts()
        {
            if (_busy || Dedicated)
            {
                return;
            }
            var picker = new HostPicker(_candidates, _asking);
            picker.Done += (_, host) =>
            {
                _chosen = host;
                _host.Set(host.Label);
                ClosePage();
                Refresh();
            };
            picker.Cancelled += (_, _) => ClosePage();
            picker.RefreshRequested += (_, _) =>
            {
                _host.Set("asking...");
                AskDirectories();
                picker.Show(_candidates, asking: true);
            };
            _picker = picker;
            OpenPage(picker);
        }

        // ------------------------------------------------------- the rotation

        /// <summary>
        /// The map list, over this screen rather than beside it.
        ///
        /// Swapped into the same frame instead of pushed onto the launcher's
        /// stack, because it is not a screen of its own: it is this screen's
        /// answer to one of its own rows, and going back from it has to land
        /// on the half-filled form rather than on the browser.
        /// </summary>
        private void OpenMaps()
        {
            if (_busy)
            {
                return;
            }
            var picker = new MapRotationPicker(_rooms, _rotation);
            picker.Done += (_, picked) =>
            {
                _rotation.Clear();
                _rotation.AddRange(picked);
                _maps.Set(Describe());
                ClosePage();
            };
            picker.Cancelled += (_, _) => ClosePage();
            OpenPage(picker);
        }

        private void OpenPage(Control page)
        {
            _root.Children.Clear();
            _root.Children.Add(page);
            Dispatcher.UIThread.Post(() => page.Focus(), DispatcherPriority.Background);
        }

        private void ClosePage()
        {
            _picker = null;
            _root.Children.Clear();
            _root.Children.Add(_page);
            Dispatcher.UIThread.Post(() => _name.Box.Focus(), DispatcherPriority.Background);
        }

        // --------------------------------------------------------- installing

        private async void Fetch()
        {
            if (_busy || !LocalServer.CanInstall)
            {
                return;
            }
            Busy(true, "installing");
            _progress.Set(0, "Fetching the dedicated-server package");
            var cancel = new CancellationTokenSource();
            _work = cancel;
            bool ok = await Task.Run(() => LocalServer.Install(fraction =>
                Dispatcher.UIThread.Post(() => _progress.Set(
                    fraction < 0 ? 0 : fraction, "Fetching the dedicated-server package")),
                cancel.Token));
            _progress.IsVisible = false;
            if (!ok)
            {
                Fail(LocalServer.LastError ?? "the package could not be installed");
                return;
            }
            Busy(false, "continue");
            Refresh();
        }

        /// <summary>
        /// Come back from a failed attempt: the marks go back to what the
        /// current answers allow, and *then* the reason is written over
        /// whatever <see cref="Refresh"/> had to say.
        ///
        /// That order is the whole point. Refresh owns the note as well as the
        /// marks, so saying why first and restoring after silently replaces
        /// the error with the standing advice -- which is a screen that
        /// refuses to do anything and will not say why.
        /// </summary>
        private void Fail(string? why)
        {
            Busy(false, "continue");
            Refresh();
            Say(why ?? "that did not work", GuiTheme.Bad);
        }

        private void Busy(bool busy, string label)
        {
            _busy = busy;
            _go.Label = label;
            _go.IsEnabled = !busy;
            _back.IsEnabled = !busy;
            _fetch.IsEnabled = !busy;
            if (busy)
            {
                _fetch.IsVisible = false;
            }
        }

        // ------------------------------------------------------------ the act

        private async void Go()
        {
            if (_finished || _busy)
            {
                return;
            }
            if (_rotation.Count == 0)
            {
                Say("Pick at least one map first.", GuiTheme.Warm);
                return;
            }
            string name = _name.Value.Trim();
            if (name.Length == 0)
            {
                name = "Fruity lobby";
            }
            GameMode mode = _modes[_mode.Index].Mode;
            var hunter = (Hunter)Enum.Parse(typeof(Hunter), _hunter.Value);
            var maps = _rotation.Select(room => (room, mode)).ToList();
            string player = LauncherPrefs.PlayerName.Trim();
            if (player.Length == 0)
            {
                player = "Player";
            }
            LauncherPrefs.LastHunter = hunter;
            LauncherPrefs.Save();

            if (Dedicated)
            {
                await StartHere(name, player, hunter, maps);
                return;
            }
            await StartOnServer(name, player, hunter, mode, maps);
        }

        /// <summary>
        /// Start a server on this machine and join it over the loopback.
        /// </summary>
        private async Task StartHere(string name, string player, Hunter hunter,
            List<(string RoomKey, GameMode Mode)> maps)
        {
            Busy(true, "starting");
            Say($"Starting {name} on this machine...", GuiTheme.TextDim);
            var cancel = new CancellationTokenSource();
            _work = cancel;
            int port = await Task.Run(() => LocalServer.Start(name, maps,
                maxPlayers: PlayerEntity.SlotCapacity, timeLimit: 7 * 60,
                pointGoal: MatchGoalRules.DefaultValue(maps[0].Mode),
                masterHost: LauncherPrefs.MasterHost, masterPort: LauncherPrefs.MasterPort,
                listed: LauncherPrefs.ListHostedGame, cancel: cancel.Token, lobby: true));
            if (port < 0)
            {
                Fail(LocalServer.LastError ?? "the server would not start");
                return;
            }
            Say($"Joining your server on 127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}...",
                GuiTheme.TextDim);
            bool joined = await Task.Run(() =>
                NetLaunch.Connect("127.0.0.1", port, player, hunter, ownerToken: LocalServer.OwnerToken));
            if (!joined)
            {
                // The server is up and this client could not get into it. It
                // is left running rather than killed: the player can still be
                // told what happened, and a server other people may already be
                // joining is not this screen's to take away on a failure that
                // was local to it.
                NetSession.Stop();
                Fail(NetLaunch.LastJoinError
                    + " -- the server is running; try joining 127.0.0.1:"
                    + port.ToString(CultureInfo.InvariantCulture) + " from the browser.");
                return;
            }
            LauncherPrefs.ServerAddress = "127.0.0.1";
            LauncherPrefs.ServerPort = port;
            LauncherPrefs.LastKind = (int)LaunchKind.Online;
            LauncherPrefs.Save();
            Finish(new LaunchPlan
            {
                Kind = LaunchKind.Online,
                Hunter = hunter,
                PlayerName = player,
                RoomKey = "",
                Mode = maps[0].Mode,
                Port = port,
                Lobby = new LobbyContext(name, $"Local server · port {port}", CreatedLocally: true)
            });
        }

        /// <summary>
        /// Ask the chosen server to open the match beside itself, and join
        /// what it answers with.
        ///
        /// The request goes to that server's own port -- the one the browser
        /// pings it on. It is the machine that will run the match, so there is
        /// nothing in between to ask.
        /// </summary>
        private async Task StartOnServer(string name, string player, Hunter hunter,
            GameMode mode, List<(string RoomKey, GameMode Mode)> maps)
        {
            if (_chosen == null)
            {
                Say(CanRunHere
                    ? "No server will open one for you. Pick Dedicated server to run it "
                        + "here."
                    : "No server will open one for you. Try again in a moment, or join "
                        + "somebody else's from the browser.", GuiTheme.Warm);
                return;
            }
            string host = _chosen.Value.Host;
            int port = _chosen.Value.Port;
            Busy(true, "starting");
            Say($"Asking {host} to open your lobby...",
                GuiTheme.TextDim);
            HostedGame game = await Task.Run(() => NetMasterClient.RequestGame(host, port,
                maps[0].RoomKey, mode, timeLimit: 7 * 60,
                pointGoal: MatchGoalRules.DefaultValue(mode),
                maxPlayers: PlayerEntity.SlotCapacity, serverName: name,
                rotation: maps, policy: ServerSessionPolicy.Lobby));
            if (!game.Started)
            {
                Fail(game.Reason.Length > 0 ? game.Reason
                    : $"{host} would not open a game");
                return;
            }
            bool joined = await Task.Run(() =>
                NetLaunch.Connect(game.Host, game.Port, player, hunter, ownerToken: game.OwnerToken));
            if (!joined)
            {
                NetSession.Stop();
                NetHostSession.Stop();
                Fail(NetLaunch.LastJoinError);
                return;
            }
            LauncherPrefs.ServerAddress = game.Host;
            LauncherPrefs.ServerPort = game.Port;
            LauncherPrefs.LastKind = (int)LaunchKind.Host;
            LauncherPrefs.Save();
            Finish(new LaunchPlan
            {
                Kind = LaunchKind.Host,
                Hunter = hunter,
                PlayerName = player,
                RoomKey = "",
                Mode = mode,
                Port = game.Port,
                Lobby = new LobbyContext(name, $"{game.Host}:{game.Port}")
            });
        }

        private void Finish(LaunchPlan plan)
        {
            if (_finished)
            {
                return;
            }
            _finished = true;
            _busy = false;
            Launched?.Invoke(this, plan);
        }
    }

    /// <summary>
    /// A row whose answer is too big to cycle through with two arrows, so it
    /// opens a page instead: the map rotation, and which machine to host on.
    ///
    /// Drawn like a <see cref="ChoiceRow"/> rather than like a list, because
    /// on this screen it is one of six answers and not the subject: the
    /// subject is the server. The chevron is the whole of the difference --
    /// arrows mean "there are a few of these", a chevron means "there is a
    /// page of them".
    /// </summary>
    internal sealed class PickRow : Control
    {
        public event EventHandler? Clicked;

        private readonly string _label;
        private string _value = "";
        private bool _hot;

        private readonly Tap _tap = new();

        public PickRow(string label)
        {
            _label = label;
            Height = 34;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        public void Set(string value)
        {
            _value = value;
            InvalidateVisual();
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            _hot = true;
            InvalidateVisual();
            base.OnPointerEntered(e);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            _hot = false;
            _tap.Cancel();
            InvalidateVisual();
            base.OnPointerExited(e);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            _tap.Press(e, this);
            base.OnPointerPressed(e);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            _tap.Moved(e, this);
            base.OnPointerMoved(e);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            // See Tap: the press has to have landed here and stayed, or a
            // scroll that ends over this row opens its page.
            if (_tap.Release(e, this))
            {
                Focus();
                Clicked?.Invoke(this, EventArgs.Empty);
            }
            base.OnPointerReleased(e);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            _tap.Cancel();
            base.OnPointerCaptureLost(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Space || e.Key == Key.Right)
            {
                Clicked?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            InvalidateVisual();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            InvalidateVisual();
            base.OnLostFocus(e);
        }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Brushes.Transparent,
                new Rect(0, 0, Bounds.Width, Bounds.Height));
            bool lit = _hot || IsFocused;
            var label = new FormattedText(_label, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, GuiTheme.Face(bold: false), 13,
                GuiTheme.TextDimBrush);
            context.DrawText(label, new Point(4, (Bounds.Height - label.Height) / 2));
            var value = new FormattedText(_value + "   >", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, GuiTheme.Face(bold: true), 13,
                new SolidColorBrush(lit ? GuiTheme.Accent : GuiTheme.Text));
            context.DrawText(value, new Point(Bounds.Width - value.Width - 4,
                (Bounds.Height - value.Height) / 2));
        }
    }

    /// <summary>
    /// Which machine runs your match, as a page.
    ///
    /// Every box the directory knows about is on it, not only the ones that
    /// can run a game -- because the question a player actually has is "why
    /// can I not host on Japan", and a list Japan is simply missing from
    /// cannot answer it. A row that cannot host says why and refuses to be
    /// pressed.
    ///
    /// The candidates are discovered rather than configured
    /// (<see cref="NetMasterClient.FindHosts"/>): every server on the
    /// directory's list is also asked on the directory port, so a box in the
    /// fleet becomes hostable the moment it gets a directory on a reachable
    /// 27889, with nothing to change or ship here.
    /// </summary>
    internal sealed class HostPicker : UserControl
    {
        public event EventHandler<HostCandidate>? Done;
        public event EventHandler? Cancelled;

        /// <summary>Ask everybody again -- something that was down may be up.</summary>
        public event EventHandler? RefreshRequested;

        private readonly UiList _list = new();
        private readonly Note _note = new("");

        public HostPicker(IReadOnlyList<HostCandidate> candidates, bool asking)
        {
            Background = Brushes.Transparent;
            Focusable = true;

            var back = new UiMark(UiMark.Shape.Cancel, "back");
            back.Click += (_, _) => Cancelled?.Invoke(this, EventArgs.Empty);
            var again = new UiMark(UiMark.Shape.Add, "ask again");
            again.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);

            var body = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
            Grid.SetRow(_list, 0);
            body.Children.Add(_list);
            Grid.SetRow(_note, 1);
            body.Children.Add(_note);

            Content = UiLayout.Page(overGame: false, UiLayout.WellPlay,
                "host on", strip: null, body: body, no: back, yes: null, extra: again);
            Show(candidates, asking);
        }

        /// <summary>
        /// Draw what is known so far.
        ///
        /// Called again for every answer that arrives while the page is open,
        /// rather than once with a finished list: the boxes are asked in
        /// parallel and reply over a spread of seconds, so a page built once
        /// is a page that is either empty or late. Rebuilt rather than
        /// appended to because the rows are re-sorted as answers land -- the
        /// ones that can run a match come first.
        /// </summary>
        public void Show(IReadOnlyList<HostCandidate> candidates, bool asking)
        {
            var ordered = new List<HostCandidate>(candidates);
            ordered.Sort((a, b) => a.WillHost == b.WillHost ? 0 : a.WillHost ? -1 : 1);
            _list.Clear();
            int usable = 0;
            foreach (HostCandidate candidate in ordered)
            {
                var row = new UiListRow(candidate.Label, candidate.Describe())
                {
                    Choice = candidate
                };
                if (candidate.WillHost)
                {
                    usable++;
                    HostCandidate picked = candidate;
                    _list.Add(row);
                    row.Clicked += (_, _) => Done?.Invoke(this, picked);
                }
                else
                {
                    // Added straight to the list rather than through
                    // UiList.Add: a row that cannot be chosen must not take
                    // the keyboard, or arrowing down the list stops on things
                    // that do nothing.
                    row.Focusable = false;
                    _list.Add(row);
                }
            }
            if (asking)
            {
                _list.AddNote("asking the rest...", GuiTheme.TextDim);
            }
            else if (ordered.Count == 0)
            {
                _list.AddNote("The directory did not answer, so there is no list of "
                    + "servers to ask.", GuiTheme.Warm);
            }
            _note.Text = usable > 0
                ? $"{usable.ToString(CultureInfo.InvariantCulture)} can run a match for you. "
                    + "A server can open one when its admin allows it a port range."
                : asking ? "" :
                    CreateServerScreen.CanRunHere
                        ? "None of these hosts are configured to create lobbies. The server "
                            + "admin can enable hosted lobbies with -hostports FIRST-LAST, "
                            + "or Dedicated server can run one on this machine."
                        : "None of these hosts are configured to create lobbies. The server "
                            + "admin must enable a hosted-game port range.";
            _note.Foreground = usable > 0 ? GuiTheme.TextDimBrush : GuiTheme.WarmBrush;
            // Only the first time. This is called again for every answer that
            // lands while the page is open, and taking the keyboard back to
            // the top on each one would drag the selection out from under
            // somebody arrowing down the list.
            if (!_focused && usable > 0)
            {
                _focused = true;
                _list.FocusFirst();
            }
        }

        private bool _focused;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _list.FocusFirst();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Cancelled?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (_list.HandleKey(e.Key))
            {
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }
    }

    /// <summary>
    /// Every map, picked one at a time, in the order they are picked.
    ///
    /// Order is the whole reason this is not a column of tick boxes: a
    /// rotation is a sequence, and the sequence a player wants is the one they
    /// built. So a press appends, a press on a map already in the list takes
    /// it back out, and the number beside each row is where it comes in the
    /// cycle -- which makes the list its own confirmation that nothing has to
    /// be read back from somewhere else.
    /// </summary>
    internal sealed class MapRotationPicker : UserControl
    {
        public event EventHandler<IReadOnlyList<string>>? Done;
        public event EventHandler? Cancelled;

        private readonly UiList _list = new();
        private readonly Note _note = new("");
        private readonly List<string> _picked;
        private readonly List<string> _rooms;
        private readonly Dictionary<string, UiListRow> _byRoom = new();
        private readonly bool _single;

        public MapRotationPicker(IReadOnlyList<string> rooms, IReadOnlyList<string> picked,
            bool single = false)
        {
            _rooms = new List<string>(rooms);
            _picked = new List<string>(picked);
            _single = single;
            Background = Brushes.Transparent;
            Focusable = true;

            var back = new UiMark(UiMark.Shape.Cancel, "back");
            back.Click += (_, _) => Cancelled?.Invoke(this, EventArgs.Empty);
            var done = new UiMark(UiMark.Shape.Accept, single ? "use this map" : "use these maps");
            done.Click += (_, _) => Commit();

            var body = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
            Grid.SetRow(_list, 0);
            body.Children.Add(_list);
            Grid.SetRow(_note, 1);
            body.Children.Add(_note);

            Content = UiLayout.Page(overGame: false, UiLayout.WellPlay,
                single ? "choose map" : "map rotation", strip: null, body: body, no: back, yes: done);
            Fill();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _list.FocusFirst();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Cancelled?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (_list.HandleKey(e.Key))
            {
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        private void Fill()
        {
            _list.Clear();
            _byRoom.Clear();
            if (_rooms.Count == 0)
            {
                _list.AddNote("No multiplayer rooms were found. Set the game files up "
                    + "from Settings.", GuiTheme.Warm);
                return;
            }
            foreach (string room in _rooms)
            {
                (RoomMetadata? meta, _) = Metadata.GetRoomByName(room);
                var row = new UiListRow(meta?.InGameName ?? room, "") { Choice = room };
                _byRoom[room] = row;
                // The press, not the list's own `activate` hook. That hook is
                // the row's side effect of *becoming the selection*, and
                // UiList fires it for the first row the moment it is added --
                // which on a list whose side effect is "put this map in the
                // rotation" means opening the picker silently picks map one,
                // or un-picks the one that was already there.
                _list.Add(row);
                row.Clicked += (_, _) => Toggle(room);
            }
            Mark();
        }

        private void Toggle(string room)
        {
            if (_single)
            {
                _picked.Clear();
                _picked.Add(room);
                Mark();
                return;
            }
            if (!_picked.Remove(room))
            {
                if (_picked.Count >= HostRequestPacket.MaxRotation)
                {
                    _note.Text = $"{HostRequestPacket.MaxRotation.ToString(CultureInfo.InvariantCulture)}"
                        + " maps is as long as a rotation can be sent.";
                    _note.Foreground = GuiTheme.WarmBrush;
                    return;
                }
                _picked.Add(room);
            }
            Mark();
        }

        /// <summary>Say where each picked map comes in the cycle, and how many there are.</summary>
        private void Mark()
        {
            foreach ((string room, UiListRow row) in _byRoom)
            {
                int at = _picked.IndexOf(room);
                row.Detail = at < 0 ? ""
                    : $"#{(at + 1).ToString(CultureInfo.InvariantCulture)}";
            }
            _note.Foreground = GuiTheme.TextDimBrush;
            _note.Text = _picked.Count == 0
                ? (_single ? "Pick a map." : "Press maps to build the cycle. They are played in the order you press them.")
                : _single
                    ? $"Selected: {Metadata.GetRoomByName(_picked[0]).Item1?.InGameName ?? _picked[0]}"
                    : String.Join("  >  ", _picked.Select(room =>
                        Metadata.GetRoomByName(room).Item1?.InGameName ?? room));
        }

        private void Commit()
        {
            if (_picked.Count == 0)
            {
                _note.Text = _single ? "Pick a map." : "Pick at least one map.";
                _note.Foreground = GuiTheme.WarmBrush;
                return;
            }
            Done?.Invoke(this, _picked);
        }
    }
}
