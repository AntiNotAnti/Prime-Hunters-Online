#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MphRead.Mods.Launcher;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// The Project Prime Hunter License. The visual hierarchy follows the
    /// supplied license-card reference while staying inside the FPS hub's flat
    /// tactical language and existing controller navigation.
    /// </summary>
    internal sealed class LicenseWorkspace : UserControl
    {
        private enum Face
        {
            Overview,
            Stats,
            History,
            Achievements,
            Emblems,
            Titles,
            Comparison,
            Account
        }

        private readonly ContentControl _page = new();
        private readonly TextBlock _player = new();
        private readonly TextBlock _hunterId = new();
        private readonly TextBlock _status = new();
        private readonly TextBlock _accountBadge = new();
        private readonly HunterStand _stand;
        private readonly Dictionary<Face, HubNavButton> _nav = new();
        private readonly HubNavButton _accountButton;
        private HunterLicenseSnapshot _snapshot = HunterLicenseClient.LocalSnapshot();
        private Face _face;
        private readonly Dictionary<Face, Control> _tabCache = new();
        private bool _loaded;
        private CancellationTokenSource? _load;
        private string _pendingEmail = "";
        private string _pendingPassword = "";
        private string _securityMessage = "";
        private bool _securityBusy;

        public event EventHandler? Closed;

        public LicenseWorkspace(bool loadProfile = true)
        {
            Focusable = true;
            Background = Brushes.Transparent;

            var root = new Grid
            {
                Margin = new Thickness(24, 20, 24, 30),
                RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
                RowSpacing = 14
            };
            root.Children.Add(HubChrome.Header(
                "HOME  /  HUNTER LICENSE",
                "HUNTER'S LICENSE",
                "YOUR PROFILE. YOUR LEGACY.",
                "CAREER",
                HubTheme.AccentBrush));

            var body = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("230,*"),
                ColumnSpacing = 12,
                MinHeight = 0
            };
            Grid.SetRow(body, 2);
            root.Children.Add(body);

            var railPanel = new StackPanel { Spacing = 7 };
            _stand = new HunterStand
            {
                Height = 290,
                MinWidth = 180,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Name2 = HunterName(_snapshot.Profile.FavoriteHunter),
                Suit = Math.Clamp(LauncherPrefs.LastColor, 0, 3)
            };
            railPanel.Children.Add(new Border
            {
                Height = 300,
                Background = HubTheme.PanelStrongBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Child = _stand
            });

            var identity = new Border
            {
                Background = HubTheme.PanelBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8)
            };
            var identityCopy = new StackPanel { Spacing = 2 };
            _player.FontFamily = PrimeTypography.Display;
            _player.FontWeight = FontWeight.Bold;
            _player.FontSize = 16;
            _player.Foreground = HubTheme.TextBrush;
            _hunterId.FontFamily = HubTheme.Data;
            _hunterId.FontSize = 9;
            _hunterId.Foreground = HubTheme.TextDimBrush;
            _accountBadge.FontFamily = HubTheme.DataBold;
            _accountBadge.FontSize = 8;
            _accountBadge.Margin = new Thickness(0, 4, 0, 0);
            identityCopy.Children.Add(_player);
            identityCopy.Children.Add(_hunterId);
            identityCopy.Children.Add(_accountBadge);
            identity.Child = identityCopy;
            railPanel.Children.Add(identity);

            AddNav(railPanel, Face.Overview, "OVERVIEW", initial: true);
            AddNav(railPanel, Face.Stats, "STATS");
            AddNav(railPanel, Face.History, "MATCH HISTORY");
            AddNav(railPanel, Face.Achievements, "ACHIEVEMENTS");
            AddNav(railPanel, Face.Emblems, "EMBLEMS");
            AddNav(railPanel, Face.Titles, "TITLES");
            AddNav(railPanel, Face.Comparison, "COMPARISON");
            _accountButton = AddNav(railPanel, Face.Account, "SECURE LICENSE",
                accent: HubTheme.Warm);

            var categoryTabs = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var tab in _nav.Values)
            {
                railPanel.Children.Remove(tab);
                tab.Margin = new Thickness(0, 0, 6, 0);
                categoryTabs.Children.Add(tab);
            }
            Grid.SetRow(categoryTabs, 1); root.Children.Add(categoryTabs);
            var rail = new Border
            {
                Background = HubTheme.InkBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = railPanel
                }
            };
            body.Children.Add(rail);

            var contentFrame = new Border
            {
                Background = HubTheme.InkBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16),
                Child = _page
            };
            Grid.SetColumn(contentFrame, 1);
            body.Children.Add(contentFrame);

            var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
            var refresh = new PrimeButton("SYNC LICENSE", () =>
            {
                if (!loadProfile) return;
                _load?.Cancel(); _load?.Dispose(); _load = new CancellationTokenSource();
                _ = RefreshAsync(_load.Token);
            });
            Grid.SetColumn(refresh, 1); footer.Children.Add(refresh);
            _status.FontFamily = HubTheme.DataBold;
            _status.FontSize = 8.5;
            _status.Foreground = HubTheme.TextDimBrush;
            _status.VerticalAlignment = VerticalAlignment.Center;
            footer.Children.Add(_status);
            var back = new HubNavButton("BACK", compact: true);
            ControllerNav.Identify(back, "hunter-license.back");
            back.Click += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
            Grid.SetColumn(back, 2);
            footer.Children.Add(back);
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            Content = root;
            ApplySnapshot(_snapshot);
            Show(Face.Overview);

            AttachedToVisualTree += (_, _) =>
            {
                LauncherBackdrop.Set(LauncherBackdropScene.Home);
                if (_loaded || !loadProfile) return;
                _load?.Cancel();
                _load = new CancellationTokenSource();
                _ = RefreshAsync(_load.Token);
            };
            DetachedFromVisualTree += (_, _) => _load?.Cancel();
        }

        private HubNavButton AddNav(StackPanel panel, Face face, string label,
            bool initial = false, Color? accent = null)
        {
            var button = new PrimeTabButton(label, () => Show(face));
            ControllerNav.Identify(button, "hunter-license." + face.ToString().ToLowerInvariant(),
                initial: initial);
            _nav.Add(face, button);
            panel.Children.Add(button);
            return button;
        }

        private async System.Threading.Tasks.Task RefreshAsync(CancellationToken token)
        {
            HunterLicenseSnapshot snapshot;
            try
            {
                snapshot = await HunterLicenseClient.LoadAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (token.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _loaded = true;
                _tabCache.Clear();
                _snapshot = snapshot;
                ApplySnapshot(snapshot);
                Show(_face);
            });
        }

        private void ApplySnapshot(HunterLicenseSnapshot snapshot)
        {
            HunterLicenseProfile p = snapshot.Profile;
            _player.Text = (p.DisplayName.Length == 0 ? "PLAYER" : p.DisplayName).ToUpperInvariant();
            _hunterId.Text = "HUNTER ID  " + HunterId(p.PlayerId);
            _status.Text = snapshot.Status;
            _status.Foreground = snapshot.Connected ? HubTheme.GoodBrush : HubTheme.WarmBrush;
            _accountBadge.Text = snapshot.Account.IsSecure ? "SECURED LICENSE" : "GUEST LICENSE";
            _accountBadge.Foreground = snapshot.Account.IsSecure
                ? HubTheme.GoodBrush : HubTheme.WarmBrush;
            _accountButton.Label = snapshot.Account.IsSecure ? "ACCOUNT" : "SECURE LICENSE";
            _stand.Name2 = HunterName(p.FavoriteHunter);
            _stand.Suit = Math.Clamp(LauncherPrefs.LastColor, 0, 3);
        }

        private void Show(Face face)
        {
            _face = face;
            foreach ((Face key, HubNavButton button) in _nav)
                button.Selected = key == face;

            if (face != Face.Account && _tabCache.TryGetValue(face, out var cached))
            { _page.Content = cached; return; }
            Control page = face switch
            {
                Face.Overview => Overview(),
                Face.Stats => Stats(),
                Face.History => History(),
                Face.Achievements => Achievements(),
                Face.Emblems => Catalog("EMBLEMS",
                    "Emblem inventory is reserved for the identity catalog. Career data and cosmetic loadouts are already connected."),
                Face.Titles => Catalog("TITLES",
                    "Title inventory is reserved for the identity catalog. The license profile is ready to display it when the catalog lands."),
                Face.Comparison => Comparison(),
                _ => Account()
            };
            if (face != Face.Account) _tabCache[face] = page;
            _page.Content = page;
        }

        private Control Account()
        {
            HunterLicenseAccount account = _snapshot.Account;
            var root = new StackPanel { Spacing = 10 };
            root.Children.Add(SectionTitle(
                account.IsSecure ? "SECURED HUNTER LICENSE" : "GUEST HUNTER LICENSE",
                account.IsSecure
                    ? "This license has a recoverable sign-in identity. You can add more sign-in methods without changing the Hunter ID or career."
                    : "This license works normally on this install, but its anonymous credential can be lost if app data is cleared. Secure it without creating a separate profile."));

            var statusCard = new StackPanel { Spacing = 5 };
            statusCard.Children.Add(HubChrome.Kicker(
                account.IsSecure ? "RECOVERY ENABLED" : "LOCAL CREDENTIAL ONLY",
                account.IsSecure ? HubTheme.GoodBrush : HubTheme.WarmBrush));
            statusCard.Children.Add(new TextBlock
            {
                Text = account.Email.Length > 0 ? account.Email : "NO EMAIL LINKED",
                FontFamily = HubTheme.DataBold,
                FontSize = 11,
                Foreground = HubTheme.TextBrush
            });
            statusCard.Children.Add(new TextBlock
            {
                Text = account.Providers.Count == 0
                    ? "SIGN-IN METHODS  //  NONE"
                    : "SIGN-IN METHODS  //  " + String.Join("  /  ",
                        account.Providers.Select(p => p.ToUpperInvariant())),
                FontFamily = HubTheme.Data,
                FontSize = 8.5,
                Foreground = HubTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap
            });
            root.Children.Add(new Border
            {
                Background = account.IsSecure
                    ? HubTheme.AccentPanel(HubTheme.Good, 20) : HubTheme.PanelBrush,
                BorderBrush = account.IsSecure ? HubTheme.GoodBrush : HubTheme.WarmBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10),
                Child = statusCard
            });

            string defaultEmail = _pendingEmail.Length > 0 ? _pendingEmail : account.Email;
            var email = new FieldRow("Email", defaultEmail, boxWidth: 280);
            email.Box.MaxLength = 254;
            var password = new FieldRow("Password", _pendingPassword, boxWidth: 280);
            password.Box.PasswordChar = '*';
            password.Box.MaxLength = 128;
            var confirm = new FieldRow("Confirm", _pendingPassword, boxWidth: 280);
            confirm.Box.PasswordChar = '*';
            confirm.Box.MaxLength = 128;

            var emailBlock = new StackPanel { Spacing = 7 };
            emailBlock.Children.Add(HubChrome.Kicker(
                account.IsSecure ? "EMAIL + PASSWORD" : "SECURE THIS LICENSE"));
            emailBlock.Children.Add(new TextBlock
            {
                Text = account.IsSecure
                    ? "Link or change the recovery email, or set a new password. Email changes are verified by Supabase before they take effect."
                    : "Your Hunter ID stays exactly the same. Supabase verifies the email first; the password is only set after verification.",
                FontFamily = HubTheme.Ui,
                FontSize = 9.5,
                Foreground = HubTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap
            });
            emailBlock.Children.Add(email);
            emailBlock.Children.Add(password);
            emailBlock.Children.Add(confirm);

            var emailActions = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                ColumnSpacing = 7
            };
            var send = new HubNavButton(
                account.Email.Length > 0 ? "SEND EMAIL CHANGE" : "SEND VERIFICATION",
                compact: true, accent: HubTheme.Accent);
            send.IsEnabled = _snapshot.Connected && !_securityBusy;
            send.Click += async (_, _) =>
            {
                if (_securityBusy) return;
                string candidateEmail = email.Value.Trim();
                string candidatePassword = password.Value;
                if (candidatePassword.Length < 8 || candidatePassword != confirm.Value)
                {
                    SetSecurityMessage("Password must be at least 8 characters and both password fields must match.");
                    Show(Face.Account);
                    return;
                }

                _pendingEmail = candidateEmail;
                _pendingPassword = candidatePassword;
                _securityBusy = true;
                Show(Face.Account);
                HunterLicenseActionResult result = await HunterLicenseClient.BeginEmailLinkAsync(
                    _pendingEmail, CancellationToken.None);
                _securityBusy = false;
                SetSecurityMessage(result.Message);
                Show(Face.Account);
            };
            emailActions.Children.Add(send);

            var finish = new HubNavButton(
                account.IsSecure ? "SET / CHANGE PASSWORD" : "I VERIFIED // FINISH",
                compact: true, accent: account.IsSecure ? HubTheme.Good : HubTheme.Warm);
            finish.IsEnabled = _snapshot.Connected && !_securityBusy;
            finish.Click += async (_, _) =>
            {
                if (_securityBusy) return;
                _pendingPassword = password.Value;
                if (_pendingPassword.Length < 8 || _pendingPassword != confirm.Value)
                {
                    SetSecurityMessage("Password must be at least 8 characters and both password fields must match.");
                    Show(Face.Account);
                    return;
                }

                _securityBusy = true;
                Show(Face.Account);
                HunterLicenseActionResult result = await HunterLicenseClient.SetPasswordAsync(
                    _pendingPassword, CancellationToken.None);
                _securityBusy = false;
                SetSecurityMessage(result.Message);
                if (result.Success)
                {
                    _pendingPassword = "";
                    await RefreshAsync(CancellationToken.None);
                }
                else Show(Face.Account);
            };
            Grid.SetColumn(finish, 1);
            emailActions.Children.Add(finish);
            emailBlock.Children.Add(emailActions);

            root.Children.Add(new Border
            {
                Background = HubTheme.PanelStrongBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10),
                Child = emailBlock
            });

            var providerBlock = new StackPanel { Spacing = 7 };
            providerBlock.Children.Add(HubChrome.Kicker("LINK SIGN-IN PROVIDER"));
            providerBlock.Children.Add(new TextBlock
            {
                Text = "Optional. Link Google, GitHub or Discord to this same Supabase user. Finish the provider flow in your browser, then refresh the status here.",
                FontFamily = HubTheme.Ui,
                FontSize = 9.5,
                Foreground = HubTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap
            });
            var providers = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*,*"),
                ColumnSpacing = 7
            };
            string[] providerNames = { "google", "github", "discord" };
            for (int i = 0; i < providerNames.Length; i++)
            {
                string provider = providerNames[i];
                bool linked = account.Providers.Contains(provider, StringComparer.OrdinalIgnoreCase);
                var button = new HubNavButton(
                    linked ? provider.ToUpperInvariant() + "  LINKED" : "LINK " + provider.ToUpperInvariant(),
                    compact: true, accent: linked ? HubTheme.Good : HubTheme.Accent);
                button.IsEnabled = _snapshot.Connected && !_securityBusy && !linked;
                button.Click += async (_, _) =>
                {
                    if (_securityBusy) return;
                    _securityBusy = true;
                    Show(Face.Account);
                    HunterLicenseActionResult result = await HunterLicenseClient.StartOAuthLinkAsync(
                        provider, CancellationToken.None);
                    _securityBusy = false;
                    SetSecurityMessage(result.Message);
                    Show(Face.Account);
                };
                Grid.SetColumn(button, i);
                providers.Children.Add(button);
            }
            providerBlock.Children.Add(providers);
            var refreshLinks = new HubNavButton("REFRESH LINK STATUS", compact: true);
            refreshLinks.IsEnabled = _snapshot.Connected && !_securityBusy;
            refreshLinks.Click += async (_, _) =>
            {
                if (_securityBusy) return;
                _securityBusy = true;
                Show(Face.Account);
                await RefreshAsync(CancellationToken.None);
                _securityBusy = false;
                SetSecurityMessage(_snapshot.Account.IsSecure
                    ? "Identity status refreshed. This license is recoverable."
                    : "Still a guest license. Complete the email/provider verification, then refresh again.");
                Show(Face.Account);
            };
            providerBlock.Children.Add(refreshLinks);
            root.Children.Add(new Border
            {
                Background = HubTheme.PanelStrongBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10),
                Child = providerBlock
            });

            if (!account.IsSecure)
            {
                var recovery = new StackPanel { Spacing = 7 };
                recovery.Children.Add(HubChrome.Kicker("RECOVER AN EXISTING LICENSE"));
                recovery.Children.Add(new TextBlock
                {
                    Text = _snapshot.Stats.GamesPlayed > 0
                        ? "This guest already has career history. Secure this license first instead of switching identities and stranding its local credential."
                        : "On a second device, enter the email and password of a secured license above and switch this empty guest to it.",
                    FontFamily = HubTheme.Ui,
                    FontSize = 9.5,
                    Foreground = _snapshot.Stats.GamesPlayed > 0
                        ? HubTheme.WarmBrush : HubTheme.TextDimBrush,
                    TextWrapping = TextWrapping.Wrap
                });
                var recover = new HubNavButton("SIGN IN // RECOVER LICENSE",
                    compact: true, accent: HubTheme.Good);
                recover.IsEnabled = _snapshot.Connected && !_securityBusy
                    && _snapshot.Stats.GamesPlayed == 0;
                recover.Click += async (_, _) =>
                {
                    if (_securityBusy) return;
                    if (password.Value != confirm.Value)
                    {
                        SetSecurityMessage("Both password fields must match before recovery.");
                        Show(Face.Account);
                        return;
                    }
                    _pendingEmail = email.Value.Trim();
                    _pendingPassword = password.Value;
                    _securityBusy = true;
                    Show(Face.Account);
                    HunterLicenseActionResult result = await HunterLicenseClient.RecoverWithPasswordAsync(
                        _pendingEmail, _pendingPassword,
                        CancellationToken.None);
                    _securityBusy = false;
                    SetSecurityMessage(result.Message);
                    if (result.Success)
                    {
                        _pendingPassword = "";
                        await RefreshAsync(CancellationToken.None);
                    }
                    else Show(Face.Account);
                };
                recovery.Children.Add(recover);
                root.Children.Add(new Border
                {
                    Background = HubTheme.PanelBrush,
                    BorderBrush = HubTheme.EdgeBrush,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 10),
                    Child = recovery
                });
            }

            if (_securityMessage.Length > 0)
            {
                root.Children.Add(new TextBlock
                {
                    Text = _securityMessage,
                    FontFamily = HubTheme.Data,
                    FontSize = 9,
                    Foreground = _securityMessage.StartsWith("License secured", StringComparison.OrdinalIgnoreCase)
                        || _securityMessage.StartsWith("Recovered", StringComparison.OrdinalIgnoreCase)
                        ? HubTheme.GoodBrush : HubTheme.WarmBrush,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            root.Children.Add(new TextBlock
            {
                Text = "PRIVACY  //  Project Prime stores the Supabase refresh credential locally. Passwords are never written to disk by the game.",
                FontFamily = HubTheme.Data,
                FontSize = 8,
                Foreground = HubTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap
            });

            return Scroll(root);
        }

        private void SetSecurityMessage(string message)
        {
            _securityMessage = message.Trim();
        }

        private Control Overview()
        {
            var s = _snapshot.Stats;
            var p = _snapshot.Profile;
            Control Metric(string label, string value, string detail, IBrush? color = null)
            {
                var content = PrimeChrome.Stack(PrimeChrome.Text(label, 11, PrimeTheme.TextSecondaryBrush, true),
                    PrimeChrome.Text(value, 26, color ?? PrimeTheme.PrimaryBrush, true),
                    PrimeChrome.Text(detail, 11, PrimeTheme.TextSecondaryBrush, true));
                content.Spacing = 3;
                return new PrimePanel(content) { Padding = new Thickness(12, 8) };
            }
            var metrics = new Grid { ColumnDefinitions = new("*,*"), RowDefinitions = new("*,*"), ColumnSpacing = 10, RowSpacing = 10 };
            Control[] cards = {
                Metric("WIN RATE", Percent(s.Wins, s.GamesPlayed), $"{s.Wins:N0} W / {s.Losses:N0} L", PrimeTheme.GreenBrush),
                Metric("K / D RATIO", Ratio(s.Kills, s.Deaths), $"{s.Kills:N0} K / {s.Deaths:N0} D"),
                Metric("HEADSHOTS", s.Headshots.ToString("N0"), "CAREER TOTAL"),
                Metric("LONGEST STREAK", s.LongestKillStreak.ToString("N0"), "CONSECUTIVE ELIMINATIONS", PrimeTheme.TextBrush)
            };
            for (int i = 0; i < cards.Length; i++) { Grid.SetRow(cards[i], i / 2); Grid.SetColumn(cards[i], i % 2); metrics.Children.Add(cards[i]); }
            var telemetry = PrimeChrome.Stack(PrimeChrome.Title("COMBAT TELEMETRY LOG"));
            telemetry.Spacing = 2;
            AddStat(telemetry, "GAMES PLAYED", s.GamesPlayed.ToString("N0"));
            AddStat(telemetry, "TOTAL DAMAGE", s.Damage.ToString("N0"));
            AddStat(telemetry, "OCTOLITH SCORES", s.OctolithScores.ToString("N0"));
            AddStat(telemetry, "NODES CAPTURED", s.NodesCaptured.ToString("N0"));
            AddStat(telemetry, "ASSISTS", s.Assists.ToString("N0"));
            AddStat(telemetry, "TIME PLAYED", FormatTicks(s.PlayedTicks));
            var center = PrimeChrome.Stack(metrics, new PrimePanel(telemetry));
            var history = PrimeChrome.Stack(new PrimePanel(PrimeChrome.Stack(new PrimeBadge("LICENSE RANK"),
                PrimeChrome.Title(RankText(p)), PrimeChrome.Text($"{p.RatingPoints:N0} RATING POINTS", 12, PrimeTheme.GreenBrush, true))),
                PrimeChrome.Title("MATCH HISTORY"));
            if (_snapshot.Matches.Count == 0)
                history.Children.Add(PrimeChrome.Text("No accepted matches yet. Complete an eligible online match to start your career record.", 13, PrimeTheme.TextSecondaryBrush));
            foreach (var match in _snapshot.Matches.Take(4))
            {
                string result = match.Tied ? "TIE" : match.Won ? "VICTORY" : "DEFEAT";
                string map = Metadata.RoomMetadata.TryGetValue(match.RoomKey, out var metadata) ? metadata.InGameName ?? match.RoomKey : match.RoomKey;
                history.Children.Add(new PrimePanel(PrimeChrome.Stack(
                    PrimeChrome.Text(map.ToUpperInvariant(), 15),
                    PrimeChrome.Text(result, 11, match.Won ? PrimeTheme.GreenBrush : PrimeTheme.TextSecondaryBrush, true),
                    PrimeChrome.Text($"{match.Kills} K / {match.Deaths} D // {FormatTicks(match.PlayedTicks)}", 11, data: true))));
            }
            return PrimeChrome.Columns("1.65*,1*", Scroll(center), Scroll(history));
        }

        private Control Stats()
        {
            HunterLicenseStats s = _snapshot.Stats;
            var root = new StackPanel { Spacing = 12 };
            root.Children.Add(SectionTitle("CAREER STATS",
                "Server-accepted, career-eligible match data from the authoritative projection."));

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                ColumnSpacing = 10
            };
            var career = StatCard("CAREER");
            AddStat((StackPanel)career.Child!, "MATCHES", s.GamesPlayed.ToString("N0"));
            AddStat((StackPanel)career.Child!, "WINS", s.Wins.ToString("N0"));
            AddStat((StackPanel)career.Child!, "LOSSES", s.Losses.ToString("N0"));
            AddStat((StackPanel)career.Child!, "TIES", s.Ties.ToString("N0"));
            AddStat((StackPanel)career.Child!, "WIN RATE", Percent(s.Wins, s.GamesPlayed));
            grid.Children.Add(career);

            var combat = StatCard("COMBAT");
            AddStat((StackPanel)combat.Child!, "KILLS", s.Kills.ToString("N0"));
            AddStat((StackPanel)combat.Child!, "DEATHS", s.Deaths.ToString("N0"));
            AddStat((StackPanel)combat.Child!, "ASSISTS", s.Assists.ToString("N0"));
            AddStat((StackPanel)combat.Child!, "HEADSHOTS", s.Headshots.ToString("N0"));
            AddStat((StackPanel)combat.Child!, "K/D", Ratio(s.Kills, s.Deaths));
            AddStat((StackPanel)combat.Child!, "DAMAGE", s.Damage.ToString("N0"));
            AddStat((StackPanel)combat.Child!, "BEST KILL STREAK", s.LongestKillStreak.ToString("N0"));
            Grid.SetColumn(combat, 1);
            grid.Children.Add(combat);
            root.Children.Add(grid);
            root.Children.Add(StatCard("STREAKS + OBJECTIVES",
                ("CURRENT WIN STREAK", s.CurrentWinStreak.ToString("N0")),
                ("LONGEST WIN STREAK", s.LongestWinStreak.ToString("N0")),
                ("OCTOLITH SCORES", s.OctolithScores.ToString("N0")),
                ("NODES CAPTURED", s.NodesCaptured.ToString("N0")),
                ("KILLS AS PRIME", s.KillsAsPrime.ToString("N0"))));
            root.Children.Add(StatCard("PLAY TIME", ("TOTAL", FormatTicks(s.PlayedTicks)),
                ("AVG / MATCH", s.GamesPlayed == 0 ? "0m" : FormatSeconds(s.PlayedTicks / 60 / s.GamesPlayed))));
            return Scroll(root);
        }

        private Control History()
        {
            var root = new StackPanel { Spacing = 8 };
            root.Children.Add(SectionTitle("MATCH HISTORY",
                "Your 25 most recent accepted matches. Career eligibility is shown per result."));
            if (_snapshot.Matches.Count == 0)
            {
                root.Children.Add(Empty("NO ACCEPTED MATCHES YET",
                    "Once the authoritative match pipeline accepts a result, it appears here automatically."));
                return Scroll(root);
            }

            foreach (HunterLicenseMatch match in _snapshot.Matches)
            {
                string result = match.Tied ? "TIE" : match.Won ? "WIN" : "LOSS";
                IBrush resultBrush = match.Tied ? HubTheme.WarmBrush
                    : match.Won ? HubTheme.GoodBrush : HubTheme.DangerBrush;
                var card = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("88,*,Auto"),
                    Background = HubTheme.PanelBrush
                };
                card.Children.Add(new Border
                {
                    BorderBrush = resultBrush,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 9),
                    Child = new TextBlock
                    {
                        Text = result,
                        FontFamily = HubTheme.DataBold,
                        FontSize = 10,
                        Foreground = resultBrush,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                });
                var middle = new StackPanel { Spacing = 2, Margin = new Thickness(8, 7) };
                middle.Children.Add(new TextBlock
                {
                    Text = $"{RoomName(match.RoomKey)}  //  {ModeName(match.Mode)}",
                    FontFamily = HubTheme.Ui,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = HubTheme.TextBrush
                });
                middle.Children.Add(new TextBlock
                {
                    Text = $"{match.Kills} K  /  {match.Deaths} D  /  {match.Assists} A    {FormatTicks(match.PlayedTicks)}",
                    FontFamily = HubTheme.Data,
                    FontSize = 8.5,
                    Foreground = HubTheme.TextDimBrush
                });
                Grid.SetColumn(middle, 1);
                card.Children.Add(middle);
                var right = new TextBlock
                {
                    Text = match.Eligible && match.CareerEligible ? "CAREER" : "UNRANKED",
                    FontFamily = HubTheme.DataBold,
                    FontSize = 8,
                    Foreground = match.Eligible && match.CareerEligible
                        ? HubTheme.AccentBrush : HubTheme.TextDimBrush,
                    Margin = new Thickness(8),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(right, 2);
                card.Children.Add(right);
                root.Children.Add(new Border
                {
                    BorderBrush = HubTheme.EdgeBrush,
                    BorderThickness = new Thickness(1),
                    Child = card
                });
            }
            return Scroll(root);
        }

        private Control Achievements()
        {
            HunterLicenseStats s = _snapshot.Stats;
            var root = new StackPanel { Spacing = 8 };
            root.Children.Add(SectionTitle("ACHIEVEMENTS",
                "Career milestones are derived from accepted match totals, so they stay deterministic across devices."));

            var achievements = new (string Name, string Detail, bool Earned)[]
            {
                ("FIRST HUNT", "Complete 1 career match", s.GamesPlayed >= 1),
                ("FIRST VICTORY", "Win 1 career match", s.Wins >= 1),
                ("CENTURION", "Reach 100 career kills", s.Kills >= 100),
                ("HEADHUNTER", "Land 50 career headshot kills", s.Headshots >= 50),
                ("ACE", "Win 25 career matches", s.Wins >= 25),
                ("VETERAN", "Complete 50 career matches", s.GamesPlayed >= 50),
                ("LEGACY", "Complete 250 career matches", s.GamesPlayed >= 250)
            };
            foreach (var achievement in achievements)
            {
                root.Children.Add(new Border
                {
                    Background = achievement.Earned
                        ? HubTheme.AccentPanel(HubTheme.Good, 24) : HubTheme.PanelBrush,
                    BorderBrush = achievement.Earned ? HubTheme.GoodBrush : HubTheme.EdgeBrush,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 9),
                    Child = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                        Children =
                        {
                            new StackPanel
                            {
                                Spacing = 2,
                                Children =
                                {
                                    new TextBlock
                                    {
                                        Text = achievement.Name,
                                        FontFamily = HubTheme.DataBold,
                                        FontSize = 10,
                                        Foreground = achievement.Earned ? HubTheme.GoodBrush : HubTheme.TextBrush
                                    },
                                    new TextBlock
                                    {
                                        Text = achievement.Detail,
                                        FontFamily = HubTheme.Ui,
                                        FontSize = 9,
                                        Foreground = HubTheme.TextDimBrush
                                    }
                                }
                            },
                            new TextBlock
                            {
                                Text = achievement.Earned ? "UNLOCKED" : "LOCKED",
                                FontFamily = HubTheme.DataBold,
                                FontSize = 8,
                                Foreground = achievement.Earned ? HubTheme.GoodBrush : HubTheme.TextDimBrush,
                                HorizontalAlignment = HorizontalAlignment.Right,
                                VerticalAlignment = VerticalAlignment.Center
                            }
                        }
                    }
                });
                Grid.SetColumn(((Grid)((Border)root.Children[^1]).Child!).Children[1], 1);
            }
            return Scroll(root);
        }

        private Control Catalog(string title, string detail)
        {
            var root = new StackPanel { Spacing = 10 };
            root.Children.Add(SectionTitle(title, detail));
            root.Children.Add(new Border
            {
                Background = HubTheme.PanelStrongBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 7,
                    Children =
                    {
                        HubChrome.Kicker("IDENTITY CATALOG"),
                        new TextBlock
                        {
                            Text = "The current Prime schema already stores per-hunter skin, armor-effect and death-effect loadouts. Emblems and titles can layer onto the same profile without touching authoritative career totals.",
                            FontFamily = HubTheme.Ui,
                            FontSize = 10,
                            Foreground = HubTheme.TextDimBrush,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = $"{_snapshot.Cosmetics.Count} COSMETIC LOADOUT{(_snapshot.Cosmetics.Count == 1 ? "" : "S")} ON THIS PROFILE",
                            FontFamily = HubTheme.DataBold,
                            FontSize = 9,
                            Foreground = HubTheme.AccentBrush
                        }
                    }
                }
            });
            return Scroll(root);
        }

        private Control Comparison()
        {
            HunterLicenseStats s = _snapshot.Stats;
            double games = Math.Max(1, s.GamesPlayed);
            var root = new StackPanel { Spacing = 10 };
            root.Children.Add(SectionTitle("COMPARISON",
                "A privacy-safe career benchmark now; public Hunter ID lookup can be added when profile visibility rules are defined."));
            root.Children.Add(StatCard("PER-MATCH BENCHMARK",
                ("KILLS / MATCH", (s.Kills / games).ToString("0.00", CultureInfo.InvariantCulture)),
                ("DEATHS / MATCH", (s.Deaths / games).ToString("0.00", CultureInfo.InvariantCulture)),
                ("ASSISTS / MATCH", (s.Assists / games).ToString("0.00", CultureInfo.InvariantCulture)),
                ("DAMAGE / MATCH", (s.Damage / games).ToString("N0", CultureInfo.InvariantCulture)),
                ("WIN RATE", Percent(s.Wins, s.GamesPlayed))));
            return Scroll(root);
        }

        private static Border SectionTitle(string title, string subtitle) => new()
        {
            Background = HubTheme.PanelStrongBrush,
            BorderBrush = HubTheme.EdgeBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10),
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontFamily = PrimeTypography.Display,
                        FontWeight = FontWeight.Bold,
                        FontSize = 16,
                        Foreground = HubTheme.TextBrush
                    },
                    new TextBlock
                    {
                        Text = subtitle,
                        FontFamily = HubTheme.Ui,
                        FontSize = 9.5,
                        Foreground = HubTheme.TextDimBrush,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };

        private static Border StatCard(string title, params (string Label, string Value)[] values)
        {
            var rows = new StackPanel { Spacing = 0 };
            rows.Children.Add(HubChrome.Kicker(title));
            rows.Children.Add(HubChrome.Divider());
            foreach ((string label, string value) in values) AddStat(rows, label, value);
            return new Border
            {
                Background = HubTheme.PanelBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 8),
                Child = rows
            };
        }

        private static Border StatCard(string title)
            => StatCard(title, Array.Empty<(string Label, string Value)>());

        private static void AddStat(StackPanel rows, string label, string value)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                MinHeight = 31
            };
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontFamily = HubTheme.Data,
                FontSize = 9,
                Foreground = HubTheme.TextDimBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            var number = new TextBlock
            {
                Text = value,
                FontFamily = HubTheme.DataBold,
                FontSize = 11,
                Foreground = HubTheme.TextBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(number, 1);
            row.Children.Add(number);
            rows.Children.Add(row);
            rows.Children.Add(new Border { Height = 1, Background = HubTheme.EdgeBrush, Opacity = 0.6 });
        }

        private static ScrollViewer Scroll(Control content) => new()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content
        };

        private static Border Empty(string title, string detail) => new()
        {
            Background = HubTheme.PanelBrush,
            BorderBrush = HubTheme.EdgeBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    HubChrome.Kicker(title, HubTheme.WarmBrush),
                    new TextBlock
                    {
                        Text = detail,
                        FontFamily = HubTheme.Ui,
                        FontSize = 10,
                        Foreground = HubTheme.TextDimBrush,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };

        private static string HunterId(string id)
        {
            if (id.Length == 0) return "#--------";
            string compact = id.Replace("-", "", StringComparison.Ordinal);
            return "#" + compact[..Math.Min(8, compact.Length)].ToUpperInvariant();
        }

        private static string HunterName(int value)
        {
            int clamped = Math.Clamp(value, 0, 6);
            return ((Hunter)clamped).ToString();
        }

        private static string RankText(HunterLicenseProfile profile)
            => profile.RatingTier.HasValue
                ? $"TIER {profile.RatingTier.Value}  //  {profile.RatingPoints:N0} RP"
                : profile.RatingPoints > 0 ? $"{profile.RatingPoints:N0} RP" : "UNRANKED";

        private static string Ratio(long kills, long deaths)
            => deaths == 0 ? (kills == 0 ? "0.00" : "∞")
                : ((double)kills / deaths).ToString("0.00", CultureInfo.InvariantCulture);

        private static string Percent(long value, long total)
            => total == 0 ? "0.0%"
                : ((double)value / total).ToString("P1", CultureInfo.InvariantCulture);

        private static string FormatTicks(long ticks) => FormatSeconds(Math.Max(0, ticks) / 60);

        private static string FormatSeconds(long seconds)
        {
            var span = TimeSpan.FromSeconds(seconds);
            if (span.TotalHours >= 1)
                return $"{(int)span.TotalHours}h {span.Minutes:00}m";
            return $"{span.Minutes}m {span.Seconds:00}s";
        }

        private static string ModeName(int mode)
            => Enum.IsDefined(typeof(GameMode), mode) ? ((GameMode)mode).ToString() : $"MODE {mode}";

        private static string RoomName(string key)
        {
            try
            {
                return Metadata.GetRoomByName(key).Item1?.InGameName ?? key;
            }
            catch
            {
                return key.Length == 0 ? "UNKNOWN MAP" : key;
            }
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
    }
}
#endif
