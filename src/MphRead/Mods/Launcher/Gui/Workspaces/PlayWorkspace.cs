#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MphRead.Mods.Network;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Multiplayer is one workspace, not three menu cards: Quick Play,
    /// the live server browser, manual join and lobby creation all meet here.
    /// </summary>
    internal sealed class PlayWorkspace : UserControl
    {
        private static readonly string[] _hunters =
            Enumerable.Range(0, Hunters.Playable).Select(i => ((Hunter)i).ToString())
                .Append(Hunter.Random.ToString()).ToArray();

        private readonly UiList _servers = new()
        {
            AutoSelectFirst = false,
            SpacingEms = 0.32
        };
        private readonly DeckField _name;
        private readonly DeckField _address;
        private readonly ChoiceRow _hunter;
        private readonly ChoiceRow _suit;
        private readonly HunterStand _stand;
        private readonly Image _mapPreview;
        private readonly TextBlock _detailName;
        private readonly TextBlock _detailMeta;
        private readonly TextBlock _summary;
        private readonly IReadOnlyList<ServerBrowserEntry>? _sample;
        private readonly HubNavButton _quick;
        private readonly HubNavButton _refresh;
        private readonly HubNavButton _join;
        private readonly PrimeButton _favorite;
        private readonly PrimeButton _spectate;
        private string? _selectedEndpoint;
        private CancellationTokenSource? _discover;
        private CancellationTokenSource? _quickSearch;
        private string _backdropRoom = "";
        private bool _joining;
        private int _replied, _live;

        public event EventHandler? Closed;
        public event EventHandler? CreateLobbyRequested;
        public event EventHandler<LaunchPlan>? Launched;

        public PlayWorkspace(IReadOnlyList<ServerBrowserEntry>? sample = null)
        {
            _sample = sample;
            Focusable = true;
            Background = Brushes.Transparent;

            _name = new DeckField(PlayerName(), widthEms: 8);
            _address = new DeckField(
                $"{LauncherPrefs.ServerAddress}:{LauncherPrefs.ServerPort}",
                widthEms: 0, watermark: "host:port");

            _hunter = new ChoiceRow("Hunter", _hunters,
                Math.Max(0, Array.IndexOf(_hunters, LauncherPrefs.LastHunter.ToString())));
            _suit = new ChoiceRow("Suit", new[] { "1", "2", "3", "4" },
                Math.Clamp(LauncherPrefs.LastColor, 0, 3));

            _stand = new HunterStand
            {
                Height = 128,
                MinHeight = 110,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Name2 = _hunters[_hunter.Index],
                Suit = _suit.Index
            };
            _hunter.Changed += (_, _) => RefreshHunter();
            _suit.Changed += (_, _) => RefreshHunter();

            _mapPreview = new Image
            {
                Height = 126,
                Stretch = Stretch.UniformToFill
            };

            _detailName = new TextBlock
            {
                Text = "SELECT A SERVER",
                FontFamily = PrimeTypography.Display,
                FontWeight = FontWeight.Bold,
                FontSize = 18,
                Foreground = HubTheme.TextBrush,
                TextWrapping = TextWrapping.Wrap
            };
            _detailMeta = new TextBlock
            {
                Text = "Pick a live server to inspect its map, mode, population and latency.",
                FontFamily = HubTheme.Ui,
                FontSize = 10.5,
                Foreground = HubTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap
            };
            _summary = new TextBlock
            {
                Text = "CONTACTING DIRECTORY",
                FontFamily = HubTheme.Data,
                FontSize = 9,
                Foreground = HubTheme.TextDimBrush,
                VerticalAlignment = VerticalAlignment.Center
            };

            _servers.SelectionChanged += (_, row) => SelectionChanged(row);
            _servers.Activated += (_, row) =>
            {
                if (row is ServerRow server && server.CanJoin)
                    _ = JoinAsync();
            };

            var root = new Grid
            {
                Margin = PrimeMetrics.PageMargin,
                RowDefinitions = new("Auto,Auto,*"), RowSpacing = 12
            };
            _quick = new PrimeButton("QUICK PLAY", primary: true);
            ControllerNav.Identify(_quick, "multiplayer.quick", initial: true);
            _quick.Click += (_, _) => _ = QuickPlayAsync();
            var create = new PrimeButton("CREATE LOBBY", () => CreateLobbyRequested?.Invoke(this, EventArgs.Empty));
            var direct = new PrimeButton("DIRECT CONNECT", DirectConnect);
            ControllerNav.Identify(create, "multiplayer.create");
            var browserTab = new PrimeTabButton("SERVER BROWSER", () => _servers.FocusFirst()) { Selected = true };
            root.Children.Add(PrimeChrome.Columns("Auto,Auto,Auto,Auto,*", _quick, browserTab, create, direct, _summary));
            _refresh = new PrimeButton("REFRESH");
            ControllerNav.Identify(_refresh, "multiplayer.refresh");
            _refresh.Click += (_, _) => RefreshServers();
            var identity = PrimeChrome.Columns("210,*,Auto", _name, _address, _refresh);
            Grid.SetRow(identity, 1); root.Children.Add(identity);
            _join = new PrimeButton("ENGAGE & JOIN", primary: true) { IsEnabled = false };
            ControllerNav.Identify(_join, "multiplayer.join");
            _spectate = new PrimeButton("SPECTATE", () => _ = JoinAsync(spectate: true)) { IsEnabled = false };
            ToolTip.SetTip(_spectate, "Join an available player slot and watch using the spectator camera.");
            _join.Click += (_, _) => _ = JoinAsync();
            var copy = new PrimeButton("COPY ADDRESS", async () =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                    await clipboard.SetTextAsync(_address.Value);
            });
            _favorite = new PrimeButton("FAVORITE", () =>
            {
                if (_selectedEndpoint == null) return;
                if (!LauncherPrefs.FavoriteServers.Add(_selectedEndpoint)) LauncherPrefs.FavoriteServers.Remove(_selectedEndpoint);
                LauncherPrefs.Save(); RefreshFavorite(); FilterServers(_searchText, _filterIndex);
            }) { IsEnabled = false };
            var filter = new ChoiceRow("Directory", new[] { "All", "Joinable", "Low ping (<80 ms)", "Favorites" });
            var search = new DeckField("", 0, watermark: "Search servers or maps");
            filter.Changed += (_, _) => FilterServers(search.Value, filter.Index);
            search.Box.TextChanged += (_, _) => FilterServers(search.Value, filter.Index);
            var left = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 10 };
            left.Children.Add(PrimeChrome.Columns("*,*", search, filter));
            Grid.SetRow(_servers, 1); left.Children.Add(_servers);
            var inspector = new PrimePanel(PrimeChrome.Stack(new PrimeBadge("SELECTED SESSION"), _detailName, _detailMeta,
                PrimeChrome.Columns("Auto,Auto,Auto,*", copy, _favorite, _spectate, _join)));
            Grid.SetRow(inspector, 2); left.Children.Add(inspector);
            _stand.Height = 190;
            _mapPreview.Height = 100;
            var loadout = new PrimePanel(PrimeChrome.Stack(new PrimeBadge("DEPLOYMENT TELEMETRY"),
                PrimeChrome.Title("HUNTER LOADOUT"), _stand, _hunter, _suit,
                PrimeChrome.Text("ARENA PREVIEW", 11, PrimeTheme.TextSecondaryBrush, true), _mapPreview));
            var body = PrimeChrome.Columns("1.6*,1*", left, loadout);
            Grid.SetRow(body, 2); root.Children.Add(body); Content = root;
            AttachedToVisualTree += (_, _) =>
            {
                LauncherBackdrop.Set(LauncherBackdropScene.Multiplayer,
                    _backdropRoom.Length > 0 ? _backdropRoom : null);
                if (!_loaded) { _loaded = true; RefreshServers(); }
            };
            DetachedFromVisualTree += (_, _) => CancelWork();
        }

        public PrimeOverlayHost? Overlays { get; set; }
        public Func<bool>? CanLaunch { get; set; }
        private bool _loaded;
        private readonly List<ServerRow> _rows = new();
        private CancellationTokenSource? _connect;
        private TextBlock? _connectionStatus;
        private bool _progressOpen;
        private string _searchText = "";
        private int _filterIndex;
        private void FilterServers(string search, int filter)
        {
            _searchText = search; _filterIndex = filter;
            foreach (var row in _rows)
                row.IsVisible = (row.DisplayName + " " + row.MapName).Contains(search, StringComparison.OrdinalIgnoreCase)
                    && (filter == 0 || filter == 3 || row.CanJoin)
                    && (filter != 3 || LauncherPrefs.FavoriteServers.Contains(row.Endpoint))
                    && (filter != 2 || (int.TryParse(row.PingText, out int ping) && ping < 80));
        }
        private void DirectConnect()
        {
            if (Overlays == null || _joining || NetSession.Active) return;
            var endpoint = new DeckField(_address.Value, 0, watermark: "host:port");
            var error = PrimeChrome.Text("", 12, PrimeTheme.DangerBrush);
            Overlays.Show(new PrimePanel(PrimeChrome.Stack(PrimeChrome.Title("DIRECT CONNECT"),
                PrimeChrome.Text("Host or IP address and port", 13), endpoint, error,
                PrimeChrome.Columns("*,*", new PrimeButton("CANCEL", Overlays.Close),
                    new PrimeButton("CONNECT", () =>
                    {
                        if (!ServerBrowserService.TryParseEndpoint(endpoint.Value, LauncherPrefs.ServerAddress,
                            LauncherPrefs.ServerPort, out _, out _)) { error.Text = "Enter a valid host:port (1–65535)."; return; }
                        _address.Value = endpoint.Value; Overlays.Close(); _ = JoinAsync();
                    }, true)))), PrimeModalSize.Medium);
        }
        private void ShowProgress(string message)
        {
            if (Overlays == null) return;
            if (_progressOpen) { if (_connectionStatus != null) _connectionStatus.Text = message; return; }
            _connectionStatus = PrimeChrome.Text(message);
            _progressOpen = true;
            void Cancel()
            {
                _connect?.Cancel(); CancelQuickSearch();
                _quick.IsEnabled = true; _refresh.IsEnabled = true;
                _summary.Text = "CONNECTION CANCELLED";
                CloseProgress();
            }
            Overlays.Show(new PrimePanel(PrimeChrome.Stack(PrimeChrome.Title("ESTABLISHING UPLINK"),
                _connectionStatus, new PrimeButton("CANCEL", Cancel))), PrimeModalSize.Small, Cancel);
        }
        private void CloseProgress()
        {
            if (_progressOpen) { _progressOpen = false; Overlays?.Close(); }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Closed?.Invoke(this, EventArgs.Empty);
                return;
            }
            base.OnKeyDown(e);
        }

        private void RefreshHunter()
        {
            _stand.Name2 = _hunters[_hunter.Index];
            _stand.Suit = Math.Clamp(_suit.Index, 0, 3);
        }

        private void SelectionChanged(Control row)
        {
            if (row is not ServerRow server)
                return;

            _selectedEndpoint = server.Endpoint; RefreshFavorite();
            _address.Value = server.Endpoint;
            _detailName.Text = server.DisplayName.ToUpperInvariant();
            _detailMeta.Text = server.IsLive
                ? $"{server.MapName}\n{server.ModeName}  /  {server.PlayerCount} PLAYERS  /  {server.PingText} MS\n{server.Endpoint}"
                : $"NO RESPONSE\n{server.Endpoint}";
            _mapPreview.Source = server.IsLive ? MapShot.For(server.RoomKey) : null;
            if (server.IsLive && server.RoomKey.Length > 0)
            {
                _backdropRoom = server.RoomKey;
                LauncherBackdrop.Set(LauncherBackdropScene.Multiplayer, _backdropRoom);
            }
            _join.IsEnabled = _spectate.IsEnabled = server.CanJoin && !_joining;
        }

        private void RefreshFavorite()
        {
            _favorite.IsEnabled = _selectedEndpoint != null;
            _favorite.Label = _selectedEndpoint != null && LauncherPrefs.FavoriteServers.Contains(_selectedEndpoint) ? "★ FAVORITED" : "☆ FAVORITE";
        }
        private void CancelDiscovery()
        {
            _discover?.Cancel();
            _discover?.Dispose();
            _discover = null;
        }

        private void CancelQuickSearch()
        {
            _quickSearch?.Cancel();
            _quickSearch?.Dispose();
            _quickSearch = null;
        }

        private void CancelWork()
        {
            CancelDiscovery();
            CancelQuickSearch();
            _quick.IsEnabled = !_joining; _refresh.IsEnabled = !_joining;
        }

        private async void RefreshServers()
        {
            CancelWork();
            _servers.Clear();
            _rows.Clear();
            _selectedEndpoint = null; RefreshFavorite();
            _replied = 0;
            _live = 0;
            _join.IsEnabled = _spectate.IsEnabled = false;
            _mapPreview.Source = null;

            if (_sample != null)
            {
                foreach (ServerBrowserEntry entry in _sample)
                    AddEntry(entry);
                _summary.Text = $"{_live} LIVE  /  {_replied} CHECKED";
                _summary.Foreground = _live > 0
                    ? HubTheme.GoodBrush : HubTheme.WarmBrush;
                return;
            }

            _summary.Text = "CONTACTING DIRECTORY";
            _summary.Foreground = HubTheme.TextDimBrush;
            _detailName.Text = "SELECT A SERVER";
            _detailMeta.Text = "Waiting for live directory results.";
            var cancel = new CancellationTokenSource();
            _discover = cancel;

            ServerDiscoveryResult result = await ServerBrowserService.DiscoverAsync(entry =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (cancel.IsCancellationRequested || TopLevel.GetTopLevel(this) == null)
                        return;
                    AddEntry(entry);
                });
            }, cancel.Token);

            if (cancel.IsCancellationRequested || TopLevel.GetTopLevel(this) == null)
                return;
            _summary.Text = result.Message.ToUpperInvariant();
            _summary.Foreground = result.DirectoryAnswered
                ? (_live > 0 ? HubTheme.GoodBrush : HubTheme.WarmBrush)
                : HubTheme.DangerBrush;
        }

        private void AddEntry(ServerBrowserEntry entry)
        {
            var row = new ServerRow(entry.Name, entry.Endpoint) { Tactical = true, Height = 72 };
            row.SetStatus(entry.Status);
            _servers.Add(row, _ => _address.Value = entry.Endpoint);
            _rows.Add(row);
            FilterServers(_searchText, _filterIndex);
            _replied++;
            if (entry.Live)
                _live++;
            _summary.Text = $"{_live} LIVE  /  {_replied} CHECKED";
        }

        private async Task QuickPlayAsync()
        {
            if (CanLaunch?.Invoke() == false) return;
            if (_joining || _quickSearch != null || NetSession.Active)
                return;

            CancelDiscovery();
            _quick.IsEnabled = false;
            _refresh.IsEnabled = false;
            _join.IsEnabled = _spectate.IsEnabled = false;
            _summary.Text = "QUICK PLAY  /  SEARCHING";
            _summary.Foreground = HubTheme.AccentBrush;

            if (_sample != null)
            {
                ServerBrowserEntry[] candidates = _sample
                    .Where(entry => entry.Live && entry.Compatible
                        && (entry.Status.MaxPlayers <= 0
                            || entry.Status.Players < entry.Status.MaxPlayers))
                    .OrderBy(entry => entry.Status.Latency < 0
                        ? Int32.MaxValue : entry.Status.Latency)
                    .ToArray();
                _quick.IsEnabled = true;
                _refresh.IsEnabled = true;
                if (candidates.Length > 0)
                {
                    ServerBrowserEntry preview = candidates[0];
                    ShowEntry(preview);
                    _summary.Text = $"QUICK PLAY  /  {preview.Name}".ToUpperInvariant();
                    _summary.Foreground = HubTheme.GoodBrush;
                }
                else
                {
                    _summary.Text = "NO COMPATIBLE OPEN SERVER";
                    _summary.Foreground = HubTheme.WarmBrush;
                }
                return;
            }

            ShowProgress("Searching for a compatible open server…");
            var cancel = new CancellationTokenSource();
            _quickSearch = cancel;
            QuickPlaySearchResult result = await ServerBrowserService.FindBestAsync(cancel.Token);
            if (cancel.IsCancellationRequested || TopLevel.GetTopLevel(this) == null)
                return;
            _quickSearch.Dispose();
            _quickSearch = null;
            _quick.IsEnabled = true;
            _refresh.IsEnabled = true;

            if (!result.Found)
            {
                CloseProgress();
                _summary.Text = result.Message.ToUpperInvariant();
                _summary.Foreground = HubTheme.WarmBrush;
                return;
            }

            ShowEntry(result.Entry);
            _summary.Text = $"QUICK PLAY  /  {result.Message}".ToUpperInvariant();
            _summary.Foreground = HubTheme.GoodBrush;
            await JoinAsync();
        }

        private void ShowEntry(ServerBrowserEntry entry)
        {
            _selectedEndpoint = entry.Endpoint; RefreshFavorite();
            _address.Value = entry.Endpoint;
            string room = entry.Status.RoomKey;
            string mapName = Metadata.RoomMetadata.TryGetValue(room, out RoomMetadata? meta)
                && !String.IsNullOrWhiteSpace(meta.InGameName)
                    ? meta.InGameName!
                    : room;
            string players = entry.Status.MaxPlayers > 0
                ? $"{entry.Status.Players}/{entry.Status.MaxPlayers}"
                : entry.Status.Players.ToString();
            string ping = entry.Status.Latency >= 0
                ? $"{entry.Status.Latency} MS" : "PING --";

            _detailName.Text = entry.Name.ToUpperInvariant();
            _detailMeta.Text =
                $"{mapName}\n{NetStatus.ModeName(entry.Status.Mode)}  /  {players} PLAYERS  /  {ping}\n{entry.Endpoint}";
            _mapPreview.Source = MapShot.For(room);
            if (room.Length > 0)
            {
                _backdropRoom = room;
                LauncherBackdrop.Set(LauncherBackdropScene.Multiplayer, _backdropRoom);
            }
            _join.IsEnabled = _spectate.IsEnabled = entry.Live && !_joining;
        }

        private async Task JoinAsync(bool spectate = false)
        {
            if (CanLaunch?.Invoke() == false) return;
            if (_joining || NetSession.Active)
                return;

            if (!ServerBrowserService.TryParseEndpoint(_address.Value,
                LauncherPrefs.ServerAddress, LauncherPrefs.ServerPort,
                out string host, out int port))
            {
                _summary.Text = "INVALID SERVER ADDRESS";
                _summary.Foreground = HubTheme.DangerBrush;
                return;
            }

            string player = _name.Value.Trim();
            Hunter hunter = Enum.TryParse(_hunter.Value, true, out Hunter parsed)
                ? parsed : LauncherPrefs.LastHunter;
            int suit = Math.Clamp(_suit.Index, 0, 3);

            _joining = true;
            _connect = new CancellationTokenSource();
            ShowProgress($"Connecting to {host}:{port}…");
            _quick.IsEnabled = false;
            _refresh.IsEnabled = false;
            _join.IsEnabled = _spectate.IsEnabled = false;
            _summary.Text = $"CONNECTING TO {host}:{port}";
            _summary.Foreground = HubTheme.AccentBrush;
            CancelDiscovery();

            OnlineJoinResult result = await ServerBrowserService.JoinAsync(
                host, port, player, hunter, suit, _connect.Token);

            bool cancelled = _connect.IsCancellationRequested;
            _connect.Dispose(); _connect = null;
            if (cancelled && result.Joined) { NetSession.Stop(); result = new OnlineJoinResult(false, default, "Join cancelled."); }
            CloseProgress();
            _joining = false;
            _quick.IsEnabled = true;
            _refresh.IsEnabled = true;
            if (!result.Joined)
            {
                _summary.Text = result.Error.ToUpperInvariant();
                _summary.Foreground = HubTheme.DangerBrush;
                _join.IsEnabled = _spectate.IsEnabled = true;
                return;
            }
            Launched?.Invoke(this, result.Plan with { Spectate = spectate });
        }

        public void SessionEnded(string reason)
        {
            _joining = false;
            _summary.Text = reason.Length > 0 ? reason.ToUpperInvariant() : "SESSION ENDED";
            _summary.Foreground = HubTheme.DangerBrush;
            _quick.IsEnabled = true;
            _refresh.IsEnabled = true;
            RefreshServers();
        }

        private static string PlayerName()
        {
            string name = LauncherPrefs.PlayerName.Trim();
            return name.Length > 0 ? name : "Player";
        }
    }
}
#endif
