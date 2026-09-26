using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MphRead.Mods.Chat;
using MphRead.Mods.Network;
using MphRead.Mods.Multiplayer;

namespace MphRead.Mods.Launcher.Gui
{
    internal sealed class LobbyScreen : UserControl
    {
        private static readonly (string Label, GameMode Free, GameMode Team, bool TeamOnly, bool FfaOnly)[] _gameTypes =
        {
            ("Battle", GameMode.Battle, GameMode.BattleTeams, false, false),
            ("Insta-Gib", GameMode.InstaGib, GameMode.InstaGib, false, true),
            ("Survival", GameMode.Survival, GameMode.SurvivalTeams, false, false),
            ("Bounty", GameMode.Bounty, GameMode.BountyTeams, false, false),
            ("Defender", GameMode.Defender, GameMode.DefenderTeams, false, false),
            ("Nodes", GameMode.Nodes, GameMode.NodesTeams, false, false),
            ("Capture", GameMode.Capture, GameMode.Capture, true, false),
            ("Prime Hunter", GameMode.PrimeHunter, GameMode.PrimeHunter, false, true)
        };

        private static readonly (string Label, MatchFormat Format)[] _matchups =
        {
            ("FFA", MatchFormat.FreeForAll),
            ("Teams", MatchFormat.Auto),
            ("1v1", MatchFormat.OneVsOne),
            ("2v2", MatchFormat.TwoVsTwo),
            ("3v3", MatchFormat.ThreeVsThree),
            ("4v4", MatchFormat.FourVsFour),
            ("2v2v2v2", MatchFormat.TwoVsTwoVsTwoVsTwo),
            ("Custom", MatchFormat.Custom)
        };

        public event EventHandler<LaunchPlan>? MatchRequested;
        public event EventHandler? HubRequested;
        public event EventHandler<string>? Closed;

        private readonly Grid _root = new();
        private readonly Control _mainPage;
        private readonly StackPanel _players = new() { Spacing = 1 };
        private bool _rosterTeams;
        private string[] _targetNames = Array.Empty<string>();
        private readonly StackPanel _ownerControls = new() { Spacing = 2 };
        private readonly Note _status = new("");
        private readonly Note _chat = new("", lines: 0);
        public PrimeOverlayHost? Overlays { get; set; }
        private readonly Border _startOverlay;
        private readonly TextBlock _startCountdown;
        private readonly TextBlock _startDetail;
        private readonly ScrollViewer _chatHistory;
        private readonly ChoiceRow _hunter, _suit, _team, _mode, _format;
        private readonly ChoiceRow _target;
        private readonly PickRow _map, _customTeams;
        private readonly ButtonToggleRow _fire, _affinity, _freeze, _requireReady, _join;
        private readonly ButtonToggleRow _lockTeams, _opponentHealth, _disablePowerups, _spawnProtection;
        private readonly ButtonToggleRow _vanillaDuelResources;
        private readonly Note _layoutSummary = new("");
        private readonly Note _teamSummary = new("", lines: 1);
        private readonly FieldRow _time, _goal;
        private readonly TextBox _chatEntry = new()
        {
            PlaceholderText = "Message",
            MaxLength = ChatPacket.MaxTextBytes,
            Height = 28,
            MinHeight = 28,
            FontFamily = HubTheme.Ui,
            FontSize = 11,
            Foreground = HubTheme.TextBrush,
            Background = HubTheme.PanelBrush,
            BorderBrush = HubTheme.EdgeBrush,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        private readonly HubNavButton _leave, _mainMenu, _ready, _start;
        private readonly HubNavButton _closeLobby, _transferButton, _kickButton;
        private readonly HubNavButton[] _teamAssign = new HubNavButton[5];
        private readonly Image _preview = new() { Height = 124, Stretch = Stretch.UniformToFill };
        private readonly string[] _rooms;
        private readonly List<byte> _targetSlots = new();

        private MatchDefinition? _shownMatch;
        private MatchDefinition? _submittedMatch;
        private SessionRules _submittedRules;
        private ushort? _shownRevision;
        private uint? _shownRosterRevision;
        private int _chatRevision = -1, _rosterCount;
        private double _nextPingRefresh;
        private SessionRules _shownRules;
        private string _draftRoom = "";
        private string _teamChoiceKey = "";
        private TeamLayout _customLayout = new(2, 2, 2);
        private bool _syncing, _suspended, _closed, _draftDirty, _closingLobby, _startAfterSave, _matchRequestIssued;
        private bool _saveFailed, _goalCustomized;
        private GameMode _goalMode = GameMode.Battle;
        private uint _draftVersion, _submittedDraftVersion;
        private double _draftChangedAt;
        private Bitmap? _bitmap;

        public LobbyScreen(IReadOnlyList<string> rooms, MphRead.Mods.Launcher.LobbyContext? context = null)
        {
            _rooms = rooms.ToArray();
            Focusable = true;

            _hunter = new ChoiceRow("Hunter",
                Enumerable.Range(0, Hunters.Playable).Select(i => ((Hunter)i).ToString()).ToArray(),
                (int)NetSession.LocalHunter);
            _suit = new ChoiceRow("Suit", new[] { "1", "2", "3", "4" }, NetSession.LocalColor);
            _team = new ChoiceRow("Team", new[] { "Auto", "Team A", "Team B" });
            _hunter.Changed += (_, _) => Identify();
            _suit.Changed += (_, _) => Identify();
            _team.Changed += (_, _) =>
            {
                if (!_syncing && NetSession.LocalSlot >= 0)
                    NetSession.SendLobbyCommand(LobbyCommandType.SetTeam, (byte)NetSession.LocalSlot,
                        (sbyte)(_team.Index - 1));
            };

            _map = new PickRow("Map");
            _map.Clicked += (_, _) => OpenMapPicker();
            _mode = new ChoiceRow("Game type", _gameTypes.Select(m => m.Label).ToArray());
            _format = new ChoiceRow("Matchup", _matchups.Select(m => m.Label).ToArray());
            _mode.Changed += (_, _) => MatchChoiceChanged(resetGoal: true);
            _format.Changed += (_, _) => MatchChoiceChanged(resetGoal: false);

            _customTeams = new PickRow("Custom teams") { IsVisible = false };
            _customTeams.Clicked += (_, _) => OpenCustomTeams();

            _time = new FieldRow("Time limit", "7:00", 92);
            _goal = new FieldRow("Score goal", "7", 92);
            _time.Box.PlaceholderText = "7:00";
            _goal.Box.PlaceholderText = "25";
            WireRuleField(_time);
            WireRuleField(_goal);

            _fire = Toggle("Friendly fire");
            _affinity = Toggle("Affinity weapons");
            _freeze = Toggle("Shadow freeze");
            _requireReady = Toggle("Require ready");
            _join = Toggle("Join in progress");
            _lockTeams = Toggle("Lock teams");
            _opponentHealth = Toggle("Opponent health");
            _disablePowerups = Toggle("Disable powerups", on: true);
            _spawnProtection = Toggle("Spawn protection (3s)", on: true);
            _vanillaDuelResources = Toggle("Vanilla 1v1 spawns/pickups");
            foreach (ButtonToggleRow toggle in new[]
            {
                _fire, _affinity, _freeze, _opponentHealth, _requireReady, _join, _lockTeams,
                _disablePowerups, _spawnProtection
            })
                toggle.Changed += (_, _) => DraftChanged();
            _vanillaDuelResources.Changed += (_, _) =>
            {
                if (!_syncing) DraftChanged();
            };
            // Three visual regions over the existing authoritative lobby
            // controls: roster, arena, and match/rule administration.
            var arena = new StackPanel { Spacing = 4 };
            arena.Children.Add(new Border
            {
                Background = HubTheme.InkBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Child = _preview,
                MinHeight = 96
            });
            arena.Children.Add(_map);
            arena.Children.Add(_mode);
            arena.Children.Add(_format);
            arena.Children.Add(_customTeams);

            var limits = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                ColumnSpacing = 8
            };
            limits.Children.Add(_time);
            Grid.SetColumn(_goal, 1);
            limits.Children.Add(_goal);

            var toggles = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
                ColumnSpacing = 24,
                RowSpacing = 10
            };
            Control[] toggleRows =
            {
                _fire, _affinity, _freeze, _opponentHealth,
                _requireReady, _join, _lockTeams, _disablePowerups,
                _spawnProtection, _vanillaDuelResources
            };
            for (int i = 0; i < toggleRows.Length; i++)
            {
                Grid.SetColumn(toggleRows[i], i % 2);
                Grid.SetRow(toggleRows[i], i / 2);
                toggles.Children.Add(toggleRows[i]);
            }

            _target = new ChoiceRow("Player", Array.Empty<string>());

            // Team management is a one-click action now. The previous flow was:
            // select a player, select a destination, then press MOVE. In a full
            // lobby that turns basic team setup into menu bookkeeping. The owner
            // picks a player once and then hits Auto/A/B/C/D directly.
            var administration = new StackPanel { Spacing = 4 };
            administration.Children.Add(LobbySubhead("MANAGE PLAYER"));
            administration.Children.Add(_target);
            administration.Children.Add(_teamSummary);

            var teamButtons = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"),
                ColumnSpacing = 4
            };
            for (int i = 0; i < _teamAssign.Length; i++)
            {
                int team = i - 1;
                string label = i == 0 ? "AUTO" : ((char)('A' + team)).ToString();
                _teamAssign[i] = SmallButton(label, () => AssignSelectedTeam((sbyte)team),
                    i == 0 ? HubTheme.TextDim : HubTheme.Accent);
                Grid.SetColumn(_teamAssign[i], i);
                teamButtons.Children.Add(_teamAssign[i]);
            }
            administration.Children.Add(teamButtons);

            administration.Children.Add(LobbySubhead("LOBBY CONTROL"));
            var adminButtons = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                ColumnSpacing = 5
            };
            _transferButton = SmallButton("TRANSFER OWNER",
                () => Confirm("TRANSFER LOBBY OWNERSHIP?", () => Admin(LobbyCommandType.TransferOwner)), HubTheme.Warm);
            adminButtons.Children.Add(_transferButton);
            _kickButton = SmallButton("KICK",
                () => Confirm("KICK SELECTED PLAYER?", () => Admin(LobbyCommandType.KickPlayer)), HubTheme.Danger);
            Grid.SetColumn(_kickButton, 1);
            adminButtons.Children.Add(_kickButton);
            administration.Children.Add(adminButtons);

            _closeLobby = SmallButton("CLOSE LOBBY", () => Confirm("CLOSE LOBBY FOR EVERYONE?", () =>
            {
                if (NetSession.SendLobbyCommand(LobbyCommandType.CloseLobby))
                {
                    _closingLobby = true;
                    _status.Text = "Closing lobby...";
                }
            }), HubTheme.Danger);
            _closeLobby.HorizontalAlignment = HorizontalAlignment.Stretch;
            administration.Children.Add(_closeLobby);

            _chatHistory = new ScrollViewer
            {
                Content = _chat,
                Height = 32,
                MinHeight = 32,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var chatInput = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 6,
                Height = 28
            };
            chatInput.Children.Add(_chatEntry);
            var send = new HubNavButton("SEND", compact: true);
            send.Click += (_, _) => SendChat();
            Grid.SetColumn(send, 1);
            chatInput.Children.Add(send);
            _chatEntry.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    SendChat();
                    e.Handled = true;
                }
            };
            var chatBody = new Grid
            {
                RowDefinitions = new RowDefinitions("32,28"),
                RowSpacing = 4
            };
            chatBody.Children.Add(_chatHistory);
            Grid.SetRow(chatInput, 1);
            chatBody.Children.Add(chatInput);

            _leave = ActionButton("LEAVE", () => Confirm("LEAVE LOBBY?", () => Leave("")), accent: HubTheme.Danger);
            ControllerNav.Identify(_leave, "lobby.leave", initial: true);

            _mainMenu = ActionButton("MAIN MENU",
                () => HubRequested?.Invoke(this, EventArgs.Empty),
                accent: HubTheme.Accent);
            ControllerNav.Identify(_mainMenu, "lobby.menu");

            _ready = ActionButton("READY", () =>
            {
                if (NetSession.LocalSlot >= 0)
                    NetSession.SendLobbyCommand(LobbyCommandType.SetReady,
                        ready: !NetSession.SlotLobbyReady[NetSession.LocalSlot]);
            }, accent: HubTheme.Accent);
            ControllerNav.Identify(_ready, "lobby.ready");

            _start = ActionButton("START MATCH", StartMatchRequested,
                primary: true);
            ControllerNav.Identify(_start, "lobby.start");

            _leave.SetValue(ControllerNav.NavRightProperty, "lobby.menu");
            _mainMenu.SetValue(ControllerNav.NavLeftProperty, "lobby.leave");
            _mainMenu.SetValue(ControllerNav.NavRightProperty, "lobby.ready");
            _ready.SetValue(ControllerNav.NavLeftProperty, "lobby.menu");
            _ready.SetValue(ControllerNav.NavRightProperty, "lobby.start");
            _start.SetValue(ControllerNav.NavLeftProperty, "lobby.ready");

            var frame = new Grid
            {
                MaxWidth = 1320,
                Margin = new Thickness(18, 14, 18, 22),
                RowDefinitions = new RowDefinitions("Auto,*"),
                RowSpacing = 12
            };
            string lobbyTitle = context?.ServerName is { Length: > 0 } serverName
                ? serverName.ToUpperInvariant()
                : "CUSTOM MATCH";
            var header = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };
            var heading = new StackPanel { Spacing = 1 };
            heading.Children.Add(new TextBlock
            {
                Text = lobbyTitle,
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.Bold,
                FontSize = 24,
                Foreground = HubTheme.TextBrush
            });
            heading.Children.Add(new TextBlock
            {
                Text = context?.Endpoint is { Length: > 0 } endpoint
                    ? $"PLAY  /  MULTIPLAYER  /  LOBBY  /  {endpoint}"
                    : "PLAY  /  MULTIPLAYER  /  LOBBY",
                FontFamily = HubTheme.Data,
                FontSize = 8.5,
                Foreground = HubTheme.AccentBrush
            });
            header.Children.Add(heading);
            var live = new TextBlock
            {
                Text = NetSession.Active ? "● CONNECTED" : "● NO ACTIVE SESSION",
                FontFamily = HubTheme.DataBold,
                FontSize = 8.5,
                Foreground = HubTheme.GoodBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(live, 1);
            header.Children.Add(live);
            frame.Children.Add(header);


            // Primary tactical layout. Advanced controls keep their existing command
            // handlers and open as sheets over this same workspace.
            arena.Children.Remove(_mode); arena.Children.Remove(_format); arena.Children.Remove(_customTeams);
            _preview.Height = 145;
            var advanced = PrimeChrome.Stack(toggles, _customTeams, _layoutSummary);
            var parameters = PrimeChrome.Stack(_mode, _format, limits,
                new PrimeButton("ADVANCED RULES", () => ShowSheet("LOBBY RULES", advanced)),
                new PrimeButton("TEAMS & ADMINISTRATION", () => ShowSheet("TEAM MANAGEMENT", administration)));
            _ownerControls.Children.Add(parameters);
            foreach (var element in new Control[] { _players, _hunter, _suit, _team, arena, _ownerControls, administration, chatBody })
                if (element.Parent is Panel parent) parent.Children.Remove(element);
            var nativeLeft = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 12 };
            nativeLeft.Children.Add(new PrimePanel(PrimeChrome.Stack(PrimeChrome.Title("ROSTER MANIFEST"), _players)));
            var loadout = new PrimePanel(PrimeChrome.Stack(PrimeChrome.Title("OPERATIVE LOADOUT"), _hunter, _suit, _team));
            Grid.SetRow(loadout, 1); nativeLeft.Children.Add(loadout);
            var nativeMiddle = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 12 };
            nativeMiddle.Children.Add(new PrimePanel(PrimeChrome.Stack(PrimeChrome.Title("DEPLOYMENT ZONE"), arena)));
            var matchParameters = new PrimePanel(PrimeChrome.Stack(PrimeChrome.Title("MATCH PARAMETERS"), _ownerControls));
            Grid.SetRow(matchParameters, 1); nativeMiddle.Children.Add(matchParameters);
            _chatHistory.Height = double.NaN; _chatHistory.MinHeight = 80;
            chatBody.RowDefinitions = new("*,Auto"); chatInput.Height = 44;
            var comms = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), RowSpacing = 12 };
            comms.Children.Add(PrimeChrome.Title("COMMS TERMINAL"));
            Grid.SetRow(chatBody, 1); comms.Children.Add(chatBody);
            Grid.SetRow(_status, 2); comms.Children.Add(_status);
            _start.MinHeight = 64;
            var sessionActions = PrimeChrome.Stack(_start, _ready,
                PrimeChrome.Columns("*,*,*", new PrimeButton("INVITE", Invite), _mainMenu, _leave));
            Grid.SetRow(sessionActions, 3); comms.Children.Add(sessionActions);
            var nativeBody = PrimeChrome.Columns("1.04*,1.05*,1*", nativeLeft, nativeMiddle, new PrimePanel(comms));
            Grid.SetRow(nativeBody, 1); frame.Children.Add(nativeBody);
            _mainPage = frame;
            _root.Children.Add(_mainPage);

            _startCountdown = new TextBlock
            {
                Text = "...",
                FontFamily = HubTheme.DataBold,
                FontSize = 52,
                Foreground = HubTheme.TextBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _startDetail = new TextBlock
            {
                Text = "PREPARING MATCH",
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.SemiBold,
                FontSize = 11,
                Foreground = HubTheme.AccentBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var startPanel = new StackPanel
            {
                Spacing = 5,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            startPanel.Children.Add(_startCountdown);
            startPanel.Children.Add(_startDetail);
            _startOverlay = new Border
            {
                IsVisible = false,
                IsHitTestVisible = false,
                Background = HubTheme.InkBrush,
                BorderBrush = HubTheme.AccentBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(34, 20),
                MinWidth = 300,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = startPanel
            };
            _root.Children.Add(_startOverlay);
            Content = _root;


        }

        private static ButtonToggleRow Toggle(string label, bool on = false)
        {
            var row = new ButtonToggleRow(label, on, compact: true);
            row.Changed += (_, _) => { };
            return row;
        }

        private static HubNavButton ActionButton(string label, Action action,
            bool primary = false, Color? accent = null)
        {
            var button = new PrimeButton(label, primary: primary, danger: accent == HubTheme.Danger)
            {
                MinWidth = 92
            };
            button.Click += (_, _) => action();
            return button;
        }

        private static HubNavButton SmallButton(string label, Action action, Color accent)
        {
            var button = new HubNavButton(label, compact: true, accent: accent);
            button.Click += (_, _) => action();
            return button;
        }

        private static TextBlock LobbySubhead(string text) => new()
        {
            Text = text,
            FontFamily = HubTheme.DataBold,
            FontSize = 8,
            Foreground = HubTheme.TextDimBrush
        };

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            LauncherBackdrop.Set(LauncherBackdropScene.Lobby,
                _draftRoom.Length > 0 ? _draftRoom : null);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
        }

        public void Resume()
        {
            _suspended = false;
            if (NetSession.IsInLobby) _matchRequestIssued = false;
            _shownRevision = null;
        }

        public void Suspend()
        {
            _suspended = true;
        }

        private void Invite()
        {
            string endpoint = $"{LauncherPrefs.ServerAddress}:{LauncherPrefs.ServerPort}";
            var status = PrimeChrome.Text(LauncherPrefs.ServerAddress is "127.0.0.1" or "localhost" or "::1"
                ? "Hosted locally: replace the loopback host with your LAN or public address before sharing. Players join through Play / Direct Connect."
                : "Share this address with another player. They can use Play / Direct Connect.", 13);
            var content = PrimeChrome.Stack(new PrimeBadge("LOBBY ADDRESS"), PrimeChrome.Text(endpoint, 18, data: true), status,
                new PrimeButton("COPY INVITE ADDRESS", async () =>
                {
                    if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                    { await clipboard.SetTextAsync(endpoint); status.Text = "Address copied."; }
                }));
            ShowSheet("INVITE PLAYER", content);
        }

        internal bool IsSuspended => _suspended;

        internal void SessionTick(bool foreground)
        {
            if (_closed || !CheckConnection()) return;
            if (!_suspended) Tick(foreground);
            else if (NetSession.IsStarting) RefreshStartPresentation();
            RequestMatchLoadIfNeeded();
        }

        internal void RefreshStartPresentation()
        {
            if (!_closed && NetSession.ServerSession != null)
                Refresh();
        }

        public void Leave(string reason)
        {
            if (_closed) return;
            _closed = true;
            _bitmap?.Dispose();
            NetSession.Stop();
            NetHostSession.Stop();
            Closed?.Invoke(this, reason);
        }

        private void Tick(bool foreground)
        {
            if (_suspended || _closed) return;
            if (!CheckConnection()) return;
            if (foreground) Refresh();
            if (_closingLobby && !NetSession.LobbyCommandPending
                && NetSession.LobbyMessage.Length > 0)
            {
                // A denied close leaves the lobby alive. Keep the server's reason
                // visible and restore normal disconnect semantics.
                _closingLobby = false;
            }
            TryAutoApply();
            if (_startAfterSave && !NetSession.LobbyCommandPending)
            {
                if (NetSession.LobbyMessage.Length > 0)
                {
                    _startAfterSave = false;
                }
                else if (NetSession.CanEditLobby)
                {
                    _startAfterSave = false;
                    NetSession.SendLobbyCommand(LobbyCommandType.StartMatch);
                }
            }
            RequestMatchLoadIfNeeded();
        }

        private bool CheckConnection()
        {
            if (!NetSession.Refused && !NetSession.SessionTimedOut && NetSession.Active)
                return true;

            Leave(_closingLobby && !NetSession.Active
                ? "Lobby closed."
                : NetSession.Refused
                    ? NetSession.RefusedReason.Describe("Server")
                    : "The connection to the server was lost.");
            return false;
        }

        private void RequestMatchLoadIfNeeded()
        {
            if (_matchRequestIssued || !NetSession.ShouldLoadMatch) return;

            // The coordinator hydrates a newly assigned lobby immediately. During
            // join-in-progress that tick can arrive while StartScreen is still
            // wiring the gameplay handoff. Do not consume the one-shot request
            // until somebody is actually listening for it.
            EventHandler<LaunchPlan>? handler = MatchRequested;
            if (handler == null) return;

            _matchRequestIssued = true;
            _startAfterSave = false;
            _status.Text = NetSession.IsPlaying
                ? "Joining match in progress..."
                : "Loading match... waiting for all players.";
            _ready.IsEnabled = false;
            _start.IsEnabled = false;
            Suspend();
            MatchDefinition match = NetSession.ActiveMatchDefinition!.Value;
            handler.Invoke(this, new LaunchPlan
            {
                Kind = LaunchKind.Online,
                Hunter = NetSession.LocalHunter,
                PlayerName = NetSession.PlayerName,
                RoomKey = match.RoomKey,
                Mode = match.Mode
            });
        }

        private void Refresh()
        {
            if (NetSession.ServerSession is not { } session) return;
            AcceptSubmittedRules(session);
            _syncing = true;
            _hunter.Index = (int)NetSession.LocalHunter;
            _suit.Index = NetSession.LocalColor;
            RosterPacket roster = NetSession.LobbyRoster();
            _rosterCount = roster.Count;

            if (_shownRevision != session.Revision
                || _shownRosterRevision != roster.Revision
                || NetSession.Clock >= _nextPingRefresh)
            {
                _shownRevision = session.Revision;
                _shownRosterRevision = roster.Revision;
                _nextPingRefresh = NetSession.Clock + 1;
                byte selected = _target.Index < _targetSlots.Count
                    ? _targetSlots[_target.Index]
                    : byte.MaxValue;
                bool teamMode = GameState.IsTeamMode(session.Match.Mode);
                bool rebuild = _rosterTeams != teamMode || !_targetSlots.SequenceEqual(roster.Slots.Take(roster.Count));
                _rosterTeams = teamMode;
                if (rebuild) _players.Children.Clear();
                _targetSlots.Clear();
                var names = new List<string>();
                for (int i = 0; i < roster.Count; i++)
                {
                    byte slot = roster.Slots[i];
                    if (rebuild)
                    {
                    var playerRow = new LobbyPlayerRow(roster, i, session.OwnerSlot,
                        showTeam: teamMode, selected: slot == selected,
                        changeTeam: direction => CyclePlayerTeam(slot, direction));
                    playerRow.Cursor = new Cursor(StandardCursorType.Hand);
                    playerRow.PointerPressed += (_, e) =>
                    {
                        if (!NetSession.LocalIsLobbyOwner) return;
                        int index = _targetSlots.IndexOf(slot);
                        if (index >= 0)
                        {
                            _target.Index = index;
                            _shownRosterRevision = null;
                            e.Handled = true;
                        }
                    };
                    _players.Children.Add(playerRow);
                    }
                    else ((LobbyPlayerRow)_players.Children[i]).Update(roster, i, session.OwnerSlot, slot == selected);
                    _targetSlots.Add(slot);
                    string team = roster.Teams[i] < 0
                        ? "AUTO"
                        : $"TEAM {(char)('A' + roster.Teams[i])}";
                    names.Add($"{roster.Names[i]}  //  {team}");
                }
                if (!_targetNames.SequenceEqual(names))
                {
                    _targetNames = names.ToArray();
                    _target.SetItems(names, Math.Max(0, _targetSlots.IndexOf(selected)));
                }
            }

            if (_shownMatch != session.Match || _shownRules != session.RuleFlags)
            {
                _shownMatch = session.Match;
                _shownRules = session.RuleFlags;
                _draftRoom = session.Match.RoomKey;
                _map.Set(RoomName(_draftRoom));
                SetPreview(_draftRoom);
                _mode.Index = BaseModeIndex(session.Match.Mode);
                _format.Index = MatchupIndex(session.Match);
                if (!_draftDirty)
                {
                    _time.Value = DurationDisplay(session.Match.TimeLimitSeconds);
                    _goal.Label = GoalLabel(session.Match.Mode);
                    _goal.Value = GoalDisplay(session.Match.Mode, session.Match.PointGoal);
                    _goalMode = session.Match.Mode;
                    _goalCustomized = session.Match.PointGoal != MatchGoalRules.DefaultValue(session.Match.Mode);
                }
                _fire.On = session.Match.FriendlyFire;
                _affinity.On = session.Match.AffinityWeapons;
                _freeze.On = session.Match.ShadowFreeze;
                _opponentHealth.On = !session.Match.HideOpponentHealth;
                _disablePowerups.On = session.Match.DisablePowerups;
                _spawnProtection.On = session.Match.SpawnProtection;
                _vanillaDuelResources.On = session.Match.VanillaDuelResources;
                _requireReady.On = session.RequireReady;
                _join.On = session.AllowJoinInProgress;
                _lockTeams.On = PlayerChoosesTeam(session.Match) && session.LockTeams;

                TeamLayout layout = LobbyRules.ResolveTeamLayout(session.Match);
                _customLayout = session.Match.CustomTeams.IsValid
                    ? session.Match.CustomTeams
                    : layout.IsValid ? layout : new TeamLayout(2, 2, 2);
                _customTeams.Set(_customLayout.ToString());

            }

            _ownerControls.IsEnabled = NetSession.CanEditLobby && !NetSession.LobbyCommandPending;
            foreach (var toggle in new[] { _fire, _affinity, _freeze, _opponentHealth, _requireReady, _join, _lockTeams, _disablePowerups, _spawnProtection, _vanillaDuelResources })
                toggle.IsEnabled = _ownerControls.IsEnabled;
            bool vanillaDuelAvailable = session.Match.Format == MatchFormat.OneVsOne
                && session.Match.Mode == GameMode.BattleTeams;
            _vanillaDuelResources.IsVisible = vanillaDuelAvailable;
            _closeLobby.IsEnabled = NetSession.CanEditLobby && !NetSession.LobbyCommandPending;
            TeamLayout activeLayout = LobbyRules.ResolveTeamLayout(session.Match);
            bool chooseTeams = PlayerChoosesTeam(session.Match);
            RefreshTeamOrganizer(session, roster, activeLayout, chooseTeams);
            _hunter.IsEnabled = _suit.IsEnabled = NetSession.IsInLobby && !NetSession.LobbyCommandPending;
            _mainMenu.IsEnabled = NetSession.IsInLobby;
            _team.IsEnabled = chooseTeams && _hunter.IsEnabled
                && (!session.LockTeams || NetSession.LocalIsLobbyOwner);

            // Ready is a real gate only when the room requires it. Leaving a
            // meaningless READY button on screen when the rule is disabled
            // made players think they still had to use it, and controller
            // navigation could land on an action the server ignores for start.
            _ready.IsVisible = session.RequireReady;
            _ready.IsEnabled = session.RequireReady && NetSession.IsInLobby
                && !NetSession.LobbyCommandPending;
            _ready.Label = NetSession.LocalSlot >= 0 && NetSession.SlotLobbyReady[NetSession.LocalSlot]
                ? "UNREADY" : "READY";

            LobbyResultCode valid = LobbyRules.Validate(session.Match, roster,
                session.RequireReady, out string reason);
            _start.IsVisible = NetSession.LocalIsLobbyOwner;
            _start.IsEnabled = NetSession.CanEditLobby
                && valid == LobbyResultCode.Ok
                && !NetSession.LobbyCommandPending;

            bool showStart = _start.IsVisible;
            if (session.RequireReady)
            {
                _leave.SetValue(ControllerNav.NavRightProperty, "lobby.menu");
                _mainMenu.SetValue(ControllerNav.NavRightProperty, "lobby.ready");
                _ready.SetValue(ControllerNav.NavLeftProperty, "lobby.menu");
                _ready.SetValue(ControllerNav.NavRightProperty,
                    showStart ? "lobby.start" : "lobby.leave");
                _start.SetValue(ControllerNav.NavLeftProperty, "lobby.ready");
            }
            else
            {
                _leave.SetValue(ControllerNav.NavRightProperty, "lobby.menu");
                _mainMenu.SetValue(ControllerNav.NavRightProperty,
                    showStart ? "lobby.start" : "lobby.leave");
                _start.SetValue(ControllerNav.NavLeftProperty, "lobby.menu");
            }

            RefreshStartOverlay(session);
            _status.Text = NetSession.ConnectionLost
                ? "Connection lost, retrying..."
                : NetSession.LobbyMessage.Length > 0
                    ? NetSession.LobbyMessage
                    : session.Phase == SessionPhase.Lobby
                        ? reason
                        : NetSession.StartCountdownRemainingSeconds > 0
                            ? $"Match starts in {Math.Max(1, (int)Math.Ceiling(NetSession.StartCountdownRemainingSeconds))}..."
                            : session.StartStage == StartStage.Synchronizing
                                ? $"Synchronizing world: {CountParticipants(session.WorldReadyParticipants)}/{CountParticipants(session.ExpectedParticipants)} ready..."
                                : $"Loading world: {CountParticipants(session.LoadedParticipants)}/{CountParticipants(session.ExpectedParticipants)} loaded...";

            if (_chatRevision != NetChat.Revision)
            {
                _chatRevision = NetChat.Revision;
                _chat.Text = String.Join("\n", NetChat.History.TakeLast(12));
                Dispatcher.UIThread.Post(() => _chatHistory.ScrollToEnd(), DispatcherPriority.Loaded);
            }

            _syncing = false;
            RefreshDraft();
        }

        private void RefreshStartOverlay(SessionStatePacket session)
        {
            bool starting = session.Phase == SessionPhase.Starting;
            _startOverlay.IsVisible = starting;
            if (!starting) return;

            int loaded = CountParticipants(session.LoadedParticipants);
            int expected = CountParticipants(session.ExpectedParticipants);
            double remaining = NetSession.StartCountdownRemainingSeconds;
            if (remaining > 0)
            {
                _startCountdown.Text = Math.Max(1, (int)Math.Ceiling(remaining))
                    .ToString(CultureInfo.InvariantCulture);
                _startDetail.Text = "READY  //  MATCH STARTING";
            }
            else
            {
                _startCountdown.Text = "...";
                _startDetail.Text = session.StartStage == StartStage.Synchronizing
                    ? $"SYNCHRONIZING WORLD  //  {CountParticipants(session.WorldReadyParticipants)}/{expected} READY"
                    : $"LOADING WORLD  //  {loaded}/{expected} LOADED";
            }
        }

        private static int CountParticipants(byte mask)
        {
            int count = 0;
            while (mask != 0)
            {
                count += mask & 1;
                mask >>= 1;
            }
            return count;
        }

        private void Identify()
        {
            if (_syncing || !NetSession.IsInLobby) return;
            NetSession.LocalHunter = (Hunter)_hunter.Index;
            NetSession.LocalColor = _suit.Index;
            LauncherPrefs.LastHunter = NetSession.LocalHunter;
            LauncherPrefs.LastColor = NetSession.LocalColor;
            LauncherPrefs.Save();
            NetSession.SendIdentify();
        }

        private void SendChat()
        {
            NetChat.Send(_chatEntry.Text ?? "");
            _chatEntry.Text = "";
        }

        private byte SelectedTargetSlot() =>
            _target.Index >= 0 && _target.Index < _targetSlots.Count
                ? _targetSlots[_target.Index]
                : byte.MaxValue;

        private static bool CanChangePlayerTeam(SessionStatePacket session, byte slot) =>
            PlayerChoosesTeam(session.Match) && NetSession.IsInLobby && !NetSession.LobbyCommandPending
            && (NetSession.LocalIsLobbyOwner || (slot == NetSession.LocalSlot && !session.LockTeams));

        private void CyclePlayerTeam(byte slot, int direction)
        {
            if (NetSession.ServerSession is not { } session || !CanChangePlayerTeam(session, slot)) return;
            var roster = NetSession.LobbyRoster();
            var layout = LobbyRules.ResolveTeamLayout(session.Match);
            if (LobbyPlayerRow.NextTeam(roster, slot, layout, direction) is { } team)
                NetSession.SendLobbyCommand(LobbyCommandType.SetTeam, slot, team);
            Refresh();
        }

        private void AssignSelectedTeam(sbyte team)
        {
            byte target = SelectedTargetSlot();
            if (target != byte.MaxValue)
                NetSession.SendLobbyCommand(LobbyCommandType.SetTeam, target, team);
        }

        private void Admin(LobbyCommandType type)
        {
            byte target = SelectedTargetSlot();
            if (target != byte.MaxValue)
                NetSession.SendLobbyCommand(type, target, -1);
        }

        private void RefreshTeamOrganizer(SessionStatePacket session, RosterPacket roster,
            TeamLayout layout, bool chooseTeams)
        {
            _team.IsVisible = chooseTeams;
            _teamSummary.IsVisible = chooseTeams;

            int[] counts = new int[4];
            for (int i = 0; i < roster.Count; i++)
            {
                int team = roster.Teams[i];
                if (team >= 0 && team < counts.Length) counts[team]++;
            }

            for (int i = 0; i < roster.Count && i < _players.Children.Count; i++)
            {
                byte slot = roster.Slots[i];
                bool canChange = CanChangePlayerTeam(session, slot);
                ((LobbyPlayerRow)_players.Children[i]).SetTeamAvailability(
                    canChange && LobbyPlayerRow.NextTeam(roster, slot, layout, -1).HasValue,
                    canChange && LobbyPlayerRow.NextTeam(roster, slot, layout, 1).HasValue);
            }

            string[] choices = Enumerable.Range(0, layout.TeamCount)
                .Select(team =>
                {
                    int capacity = layout.Capacity(team);
                    return $"Team {(char)('A' + team)}  {counts[team]}/{capacity}";
                })
                .Prepend("Auto balance").ToArray();

            int localTeam = NetSession.LocalSlot >= 0
                ? NetSession.SlotTeamIndex[NetSession.LocalSlot] + 1
                : 0;
            localTeam = Math.Clamp(localTeam, 0, Math.Max(0, choices.Length - 1));
            string choiceKey = String.Join('|', choices);
            if (choiceKey != _teamChoiceKey)
            {
                _teamChoiceKey = choiceKey;
                _team.SetItems(choices, localTeam);
            }
            else if (!NetSession.LobbyCommandPending)
            {
                // Do not visually snap a just-chosen team back to the previous
                // authoritative value while its SetTeam command is in flight.
                _team.Index = localTeam;
            }

            _teamSummary.Text = chooseTeams
                ? String.Join("   ", Enumerable.Range(0, layout.TeamCount)
                    .Select(team => $"{(char)('A' + team)} {counts[team]}/{layout.Capacity(team)}"))
                : "";

            byte selected = SelectedTargetSlot();
            sbyte selectedTeam = -1;
            for (int i = 0; i < roster.Count; i++)
                if (roster.Slots[i] == selected)
                    selectedTeam = roster.Teams[i];

            _teamAssign[0].IsVisible = chooseTeams;
            _teamAssign[0].IsEnabled = chooseTeams && selected != byte.MaxValue
                && NetSession.CanEditLobby && !NetSession.LobbyCommandPending;
            _teamAssign[0].Label = "AUTO";

            for (int i = 1; i < _teamAssign.Length; i++)
            {
                int team = i - 1;
                bool visible = chooseTeams && team < layout.TeamCount;
                _teamAssign[i].IsVisible = visible;
                if (!visible) continue;
                int capacity = layout.Capacity(team);
                _teamAssign[i].Label = $"{(char)('A' + team)} {counts[team]}/{capacity}";
                _teamAssign[i].IsEnabled = selected != byte.MaxValue
                    && NetSession.CanEditLobby && !NetSession.LobbyCommandPending
                    && (selectedTeam == team || counts[team] < capacity);
            }

            bool targetIsOther = selected != byte.MaxValue && selected != NetSession.LocalSlot;
            _transferButton.IsEnabled = targetIsOther && NetSession.CanEditLobby
                && !NetSession.LobbyCommandPending;
            _kickButton.IsEnabled = targetIsOther && NetSession.CanEditLobby
                && !NetSession.LobbyCommandPending;
        }

        private void WireRuleField(FieldRow field)
        {
            field.Box.MaxLength = 8;
            field.Box.TextChanged += (_, _) =>
            {
                if (!_syncing && ReferenceEquals(field, _goal))
                    _goalCustomized = true;
                DraftChanged();
            };
            field.Box.LostFocus += (_, _) => TryAutoApply(force: true);
            field.Box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    TryAutoApply(force: true);
                }
            };
        }

        private void StartMatchRequested()
        {
            if (!NetSession.CanEditLobby) return;
            if (NetSession.LobbyCommandPending)
            {
                // Clicking Start can move focus out of a rule field, which sends
                // its save just before this click handler runs. Queue the start
                // behind that save instead of swallowing the click.
                if (_submittedMatch != null)
                    _startAfterSave = true;
                return;
            }
            if (_draftDirty)
            {
                if (!TryBuildMatch(out _, out string reason))
                {
                    _layoutSummary.Text = reason;
                    return;
                }
                _startAfterSave = true;
                TryAutoApply(force: true);
                return;
            }
            NetSession.SendLobbyCommand(LobbyCommandType.StartMatch);
        }

        private void MatchChoiceChanged(bool resetGoal)
        {
            if (_syncing) return;
            GameMode previousGoalMode = _goalMode;
            bool preserveCustomGoal = _goalCustomized;
            _syncing = true;
            (string _, GameMode _, GameMode _, bool teamOnly, bool ffaOnly) = _gameTypes[_mode.Index];
            MatchFormat format = SelectedFormat();
            int target = _format.Index;
            if (ffaOnly && format != MatchFormat.FreeForAll)
                target = MatchupIndex(MatchFormat.FreeForAll);
            else if (teamOnly && (format == MatchFormat.FreeForAll
                || format == MatchFormat.TwoVsTwoVsTwoVsTwo))
                target = MatchupIndex(MatchFormat.Auto);
            if (target != _format.Index) _format.Index = target;
            MatchDefinition draft = DraftMatch();
            _goal.Label = GoalLabel(draft.Mode);
            bool sameGoalKind = MatchGoalRules.UsesLives(previousGoalMode) == MatchGoalRules.UsesLives(draft.Mode)
                && MatchGoalRules.UsesTimeTarget(previousGoalMode) == MatchGoalRules.UsesTimeTarget(draft.Mode);
            if (resetGoal && (!preserveCustomGoal || !sameGoalKind))
            {
                _goal.Value = GoalDisplay(draft.Mode, MatchGoalRules.DefaultValue(draft.Mode));
                _goalCustomized = false;
            }
            _goalMode = draft.Mode;
            _syncing = false;
            DraftChanged();
        }

        private MatchDefinition DraftMatch()
        {
            MatchFormat format = SelectedFormat();
            var type = _gameTypes[Math.Clamp(_mode.Index, 0, _gameTypes.Length - 1)];
            bool teams = format != MatchFormat.FreeForAll;
            GameMode mode = type.FfaOnly ? type.Free
                : type.TeamOnly ? type.Team
                : teams ? type.Team : type.Free;
            return new MatchDefinition
            {
                RoomKey = _draftRoom,
                Mode = mode,
                Format = format,
                CustomTeams = _customLayout
            };
        }

        private void DraftChanged()
        {
            if (_syncing) return;
            _draftDirty = true;
            _saveFailed = false;
            _draftVersion++;
            _draftChangedAt = NetSession.Clock;
            RefreshDraft();
        }

        private void RefreshDraft()
        {
            if (_syncing) return;
            MatchDefinition draft = DraftMatch();
            _goal.Label = GoalLabel(draft.Mode);
            _goal.Box.PlaceholderText = MatchGoalRules.UsesTimeTarget(draft.Mode)
                ? "1:30"
                : MatchGoalRules.UsesLives(draft.Mode) ? "3" : "25";
            _customTeams.IsVisible = draft.Format == MatchFormat.Custom;
            bool vanillaDuelAvailable = draft.Format == MatchFormat.OneVsOne
                && draft.Mode == GameMode.BattleTeams;
            _vanillaDuelResources.IsVisible = vanillaDuelAvailable;
            bool chooseTeams = PlayerChoosesTeam(draft);
            _lockTeams.IsVisible = chooseTeams;
            _layoutSummary.IsVisible = draft.Format != MatchFormat.OneVsOne;
            bool valid = TryBuildMatch(out MatchDefinition configured, out string reason);
            TeamLayout layout = LobbyRules.ResolveTeamLayout(configured);

            _layoutSummary.Text = !valid
                ? reason
                : _submittedMatch != null
                    ? "Saving changes..."
                    : _saveFailed
                        ? (NetSession.LobbyMessage.Length > 0
                            ? NetSession.LobbyMessage
                            : "The server did not accept the rule changes.")
                        : _draftDirty
                            ? "Changes save when you finish editing."
                            : layout.TeamCount == 0
                                ? "Free for all"
                                : $"Teams: {layout} · up to {layout.TotalPlayers} players · flexible start";
            _customTeams.Set(_customLayout.ToString());
            if (!valid) _start.IsEnabled = false;
        }

        private bool TryBuildMatch(out MatchDefinition match, out string reason)
        {
            match = DraftMatch();
            if (LobbyRules.ValidateDefinition(match, out reason) != LobbyResultCode.Ok)
                return false;

            TeamLayout layout = LobbyRules.ResolveTeamLayout(match);
            if (layout.TeamCount > 0
                && (layout.TotalPlayers < _rosterCount
                    || (LobbyRules.ExactTeams(match)
                        && layout.TotalPlayers > (NetSession.ServerSession?.MaxPlayers ?? 8))))
            {
                reason = "The matchup must fit the connected players and server limit.";
                return false;
            }
            if (!TryTimeSeconds(out ushort seconds))
            {
                reason = "Time limit must be minutes (7) or m:ss (7:00).";
                return false;
            }
            if (!TryGoalValue(match.Mode, out ushort goal, out reason))
                return false;

            bool vanillaDuelResources = match.Format == MatchFormat.OneVsOne
                && match.Mode == GameMode.BattleTeams && _vanillaDuelResources.On;
            match = match with
            {
                TimeLimitSeconds = seconds,
                PointGoal = goal,
                FriendlyFire = _fire.On,
                AffinityWeapons = _affinity.On,
                ShadowFreeze = _freeze.On,
                HideOpponentHealth = !_opponentHealth.On,
                DisablePowerups = _disablePowerups.On,
                SpawnProtection = _spawnProtection.On,
                VanillaDuelResources = vanillaDuelResources
            };
            return true;
        }

        private void TryAutoApply(bool force = false)
        {
            if (_submittedMatch != null)
            {
                // The command result and authoritative SessionState are separate
                // UDP packets. Keep the draft alive until the server publishes
                // the exact accepted rules, rather than letting an older state
                // snap the controls back to its defaults while the save is in flight.
                if (!NetSession.LobbyCommandPending && NetSession.LobbyMessage.Length > 0)
                {
                    _submittedMatch = null;
                    _saveFailed = true;
                }
                else
                {
                    return;
                }
            }
            if (_saveFailed && !force) return;
            if (force) _saveFailed = false;
            if (!_draftDirty || NetSession.LobbyCommandPending || !NetSession.CanEditLobby
                || (!force && (_time.Box.IsFocused || _goal.Box.IsFocused))
                || (!force && NetSession.Clock - _draftChangedAt < 0.35)
                || NetSession.ServerSession is not { } config)
                return;
            if (!TryBuildMatch(out MatchDefinition match, out string reason))
            {
                if (force) _layoutSummary.Text = reason;
                return;
            }

            config.Match = match;
            config.RuleFlags = match.Rules
                | (_requireReady.On ? SessionRules.RequireReady : 0)
                | (_join.On ? SessionRules.AllowJoinInProgress : 0)
                | (PlayerChoosesTeam(match) && _lockTeams.On ? SessionRules.LockTeams : 0);
            if (NetSession.SendLobbyCommand(LobbyCommandType.UpdateMatch, configuration: config))
            {
                _submittedMatch = match;
                _submittedRules = config.RuleFlags;
                _submittedDraftVersion = _draftVersion;
                _layoutSummary.Text = "Saving changes...";
            }
        }

        private void AcceptSubmittedRules(SessionStatePacket session)
        {
            if (_submittedMatch is not { } submitted
                || session.Match != submitted
                || session.RuleFlags != _submittedRules)
                return;

            // Persist only after the server publishes the exact submitted match.
            // This makes a completely new hosted/dedicated lobby start with the
            // last accepted clock/goal instead of hardcoded 7:00/7 defaults.
            LauncherPrefs.LastLobbyMode = submitted.Mode;
            LauncherPrefs.LastLobbyTimeLimitSeconds = submitted.TimeLimitSeconds;
            LauncherPrefs.LastLobbyGoal = submitted.PointGoal;
            LauncherPrefs.Save();

            _submittedMatch = null;
            _saveFailed = false;
            // A player can make a newer edit while the previous command is
            // crossing the network. Only clear dirty for the exact draft that
            // produced this authoritative state; otherwise the newer edit is
            // still waiting to be saved.
            if (_submittedDraftVersion == _draftVersion)
                _draftDirty = false;
        }

        private void OpenMapPicker()
        {
            if (!NetSession.CanEditLobby || NetSession.LobbyCommandPending) return;
            var picker = new MapCardPicker(_rooms, _draftRoom);
            picker.Done += (_, room) =>
            {
                _draftRoom = room;
                _map.Set(RoomName(_draftRoom));
                SetPreview(_draftRoom);
                DraftChanged();
                ClosePage();
            };
            picker.Cancelled += (_, _) => ClosePage();
            OpenPage(picker);
        }

        private void OpenCustomTeams()
        {
            if (!NetSession.CanEditLobby || NetSession.LobbyCommandPending) return;
            var picker = new CustomTeamPicker(_customLayout,
                NetSession.ServerSession?.MaxPlayers ?? 8);
            picker.Done += (_, layout) =>
            {
                _customLayout = layout;
                _customTeams.Set(layout.ToString());
                DraftChanged();
                ClosePage();
            };
            picker.Cancelled += (_, _) => ClosePage();
            OpenPage(picker);
        }

        private void ShowSheet(string title, Control content)
        {
            if (Overlays == null) return;
            // Sheets are reused too: release the previous frame's child before
            // attaching the rule controls to their next presentation.
            if (content.Parent is Panel old) old.Children.Remove(content);
            Overlays.Show(new PrimePanel(PrimeChrome.Stack(PrimeChrome.Title(title), content,
                new PrimeButton("DONE", Overlays.Close))), PrimeModalSize.Medium);
        }
        private void Confirm(string title, Action action)
        {
            if (Overlays == null) { action(); return; }
            Overlays.Show(new PrimePanel(PrimeChrome.Stack(PrimeChrome.Title(title),
                PrimeChrome.Columns("*,*", new PrimeButton("CANCEL", Overlays.Close),
                    new PrimeButton("CONFIRM", () => { Overlays.Close(); action(); }, danger: true)))), PrimeModalSize.Small);
        }

        private void OpenPage(Control page)
        {
            if (Overlays != null) { Overlays.Show(page); return; }
            _root.Children.Clear();
            _root.Children.Add(page);
            Dispatcher.UIThread.Post(() => page.Focus(), DispatcherPriority.Background);
        }

        private void ClosePage()
        {
            if (Overlays != null) { Overlays.Close(); return; }
            _root.Children.Clear();
            _root.Children.Add(_mainPage);
            Dispatcher.UIThread.Post(() => _map.Focus(), DispatcherPriority.Background);
        }

        private void SetPreview(string room)
        {
            if (!String.IsNullOrWhiteSpace(room))
                LauncherBackdrop.Set(LauncherBackdropScene.Lobby, room);
            _preview.Source = null;
            _bitmap?.Dispose();
            _bitmap = null;
            try
            {
                string path = ThumbnailGenerator.PathFor(room);
                if (!String.IsNullOrWhiteSpace(room) && File.Exists(path))
                    _bitmap = new Bitmap(path);
            }
            catch (Exception)
            {
                // A thumbnail is presentation only; map validation belongs to the server.
            }
            _preview.Source = _bitmap;
            _preview.IsVisible = _bitmap != null;
        }

        private static string RoomName(string room)
        {
            return Metadata.GetRoomByName(room).Item1?.InGameName ?? room;
        }

        private static string DurationDisplay(ushort seconds) =>
            $"{seconds / 60}:{seconds % 60:00}";

        private static bool TryDuration(string text, bool allowZero, out ushort seconds)
        {
            seconds = 0;
            text = text.Trim();
            if (text.Length == 0) return false;

            int colon = text.IndexOf(':');
            if (colon >= 0)
            {
                if (text.IndexOf(':', colon + 1) >= 0
                    || !int.TryParse(text[..colon], NumberStyles.None,
                        CultureInfo.InvariantCulture, out int minutes)
                    || !int.TryParse(text[(colon + 1)..], NumberStyles.None,
                        CultureInfo.InvariantCulture, out int remainder)
                    || minutes < 0 || remainder < 0 || remainder >= 60)
                    return false;
                long total = minutes * 60L + remainder;
                if (total > UInt16.MaxValue || (!allowZero && total == 0))
                    return false;
                seconds = (ushort)total;
                return true;
            }

            if (!double.TryParse(text, NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out double decimalMinutes)
                || !Double.IsFinite(decimalMinutes)
                || decimalMinutes < 0 || (!allowZero && decimalMinutes <= 0)
                || decimalMinutes * 60 > UInt16.MaxValue)
                return false;
            seconds = (ushort)Math.Round(decimalMinutes * 60,
                MidpointRounding.AwayFromZero);
            return allowZero || seconds > 0;
        }

        private bool TryTimeSeconds(out ushort seconds) =>
            TryDuration(_time.Value, allowZero: true, out seconds);

        private static bool PlayerChoosesTeam(MatchDefinition match)
        {
            // Every team mode exposes the player's team choice, including 1v1.
            // The server still enforces the configured per-team capacity.
            return GameState.IsTeamMode(match.Mode);
        }

        private static string GoalLabel(GameMode mode) => mode switch
        {
            GameMode.Survival or GameMode.SurvivalTeams => "Lives",
            GameMode.Bounty or GameMode.BountyTeams => "Bounty goal",
            GameMode.Capture => "Captures",
            GameMode.Defender or GameMode.DefenderTeams => "Hold time",
            GameMode.Nodes or GameMode.NodesTeams => "Node score",
            GameMode.PrimeHunter => "Prime time",
            _ => "Score goal"
        };

        private static string GoalDisplay(GameMode mode, ushort value)
        {
            if (MatchGoalRules.UsesLives(mode))
                return ((int)value + 1).ToString(CultureInfo.InvariantCulture);
            if (MatchGoalRules.UsesTimeTarget(mode))
                return DurationDisplay(value);
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private bool TryGoalValue(GameMode mode, out ushort value, out string reason)
        {
            value = 0;
            reason = "";
            if (MatchGoalRules.UsesLives(mode))
            {
                if (!int.TryParse(_goal.Value, NumberStyles.None, CultureInfo.InvariantCulture,
                        out int lives) || lives < 1 || lives > UInt16.MaxValue + 1)
                {
                    reason = "Lives must be a whole number from 1 to 65536.";
                    return false;
                }
                value = (ushort)(lives - 1);
                return true;
            }
            if (MatchGoalRules.UsesTimeTarget(mode))
            {
                if (!TryDuration(_goal.Value, allowZero: false, out value))
                {
                    reason = $"{GoalLabel(mode)} must be minutes (1.5) or m:ss (1:30).";
                    return false;
                }
                return true;
            }
            if (!ushort.TryParse(_goal.Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out value))
            {
                reason = $"{GoalLabel(mode)} must be a whole number from 0 to 65535.";
                return false;
            }
            return true;
        }

        private MatchFormat SelectedFormat()
        {
            return _matchups[Math.Clamp(_format.Index, 0, _matchups.Length - 1)].Format;
        }

        private static int MatchupIndex(MatchFormat format)
        {
            int index = Array.FindIndex(_matchups, m => m.Format == format);
            return index < 0 ? 0 : index;
        }

        private static int MatchupIndex(MatchDefinition match)
        {
            if (match.Format == MatchFormat.Auto && !GameState.IsTeamMode(match.Mode))
                return MatchupIndex(MatchFormat.FreeForAll);
            return MatchupIndex(match.Format);
        }

        private static int BaseModeIndex(GameMode mode)
        {
            int index = Array.FindIndex(_gameTypes, m => m.Free == mode || m.Team == mode);
            return index < 0 ? 0 : index;
        }
    }

    /// <summary>
    /// Advanced team capacities live off the main lobby so the common match
    /// setup remains one screen. Only Custom opens this page.
    /// </summary>
    internal sealed class CustomTeamPicker : UserControl
    {
        public event EventHandler<TeamLayout>? Done;
        public event EventHandler? Cancelled;

        private readonly ChoiceRow _count;
        private readonly ChoiceRow[] _sizes = new ChoiceRow[4];
        private readonly Note _note = new("");
        private readonly UiMark _use;
        private readonly int _maxPlayers;

        public CustomTeamPicker(TeamLayout current, int maxPlayers)
        {
            _maxPlayers = Math.Clamp(maxPlayers, 2, 8);
            int count = current.IsValid ? current.TeamCount : 2;
            _count = new ChoiceRow("Teams", new[] { "2", "3", "4" }, count - 2);
            var form = new StackPanel { Spacing = 2 };
            form.Children.Add(_count);
            for (int team = 0; team < 4; team++)
            {
                int initial = current.IsValid && team < current.TeamCount
                    ? Math.Max(1, (int)current.Capacity(team)) - 1
                    : 1;
                _sizes[team] = new ChoiceRow($"Team {(char)('A' + team)} size",
                    Enumerable.Range(1, 8).Select(n => n.ToString()).ToArray(), initial);
                _sizes[team].Changed += (_, _) => Refresh();
                form.Children.Add(_sizes[team]);
            }
            form.Children.Add(_note);
            _count.Changed += (_, _) => Refresh();

            var back = new UiMark(UiMark.Shape.Cancel, "back");
            back.Click += (_, _) => Cancelled?.Invoke(this, EventArgs.Empty);
            _use = new UiMark(UiMark.Shape.Accept, "use teams");
            _use.Click += (_, _) =>
            {
                TeamLayout layout = Value();
                if (layout.IsValid && layout.TotalPlayers <= _maxPlayers)
                    Done?.Invoke(this, layout);
            };
            Content = UiLayout.Page(overGame: false, UiLayout.WellSettings,
                "custom teams", strip: null, body: form, no: back, yes: _use);
            Refresh();
        }

        private TeamLayout Value()
        {
            int count = _count.Index + 2;
            return new TeamLayout(
                (byte)count,
                (byte)(_sizes[0].Index + 1),
                (byte)(_sizes[1].Index + 1),
                count > 2 ? (byte)(_sizes[2].Index + 1) : (byte)0,
                count > 3 ? (byte)(_sizes[3].Index + 1) : (byte)0);
        }

        private void Refresh()
        {
            int count = _count.Index + 2;
            for (int team = 0; team < 4; team++)
                _sizes[team].IsVisible = team < count;
            TeamLayout layout = Value();
            bool valid = layout.IsValid && layout.TotalPlayers <= _maxPlayers;
            _note.Text = valid
                ? $"{layout} · {layout.TotalPlayers} player slots"
                : $"Custom teams must use no more than {_maxPlayers} player slots.";
            _note.Foreground = valid ? GuiTheme.TextDimBrush : GuiTheme.WarmBrush;
            _use.IsEnabled = valid;
        }
    }
}
