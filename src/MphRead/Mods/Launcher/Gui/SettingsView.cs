using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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
using MphRead.Mods.Render;
using FrameTiming = MphRead.Mods.Render.FrameTiming;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Settings, in the same language as every other screen: the same picture,
    /// the same strip of names across the top, the same two marks in the
    /// bottom corners.
    ///
    /// Eight pages: Display, Graphics, Audio, Controls, Replays, Profile, Maintenance, Credits. There is no
    /// "Match rules" page -- point goal, time limit, damage, team play,
    /// friendly fire, hunter radar, affinity weapons and shadow freeze are
    /// not exposed here at all any more, and stay at whatever
    /// <see cref="MenuSettings"/> already defaults them to.
    ///
    /// The same view opens from the front screen, from the pause menu inside a
    /// match, and from the Android head -- which is why nothing here needs a
    /// restart to take effect except the window mode, and why it is a
    /// <see cref="UserControl"/> rather than a <see cref="Window"/>: there is
    /// no second window to open it in on any platform now. The desktop pushes
    /// it onto <see cref="StartScreen"/>'s stack or, over a match, onto
    /// <see cref="InGameMenu"/>'s.
    /// </summary>
    internal sealed class SettingsView : UserControl
    {
        private readonly MenuSettings _settings;
        private readonly bool _inGame;
        private readonly bool _shell;
        private SettingsDraft? _draft;
        public bool IsDirty => _draft?.IsDirty == true;
        internal void TrackControllerDraft() => _draft?.TrackController();
        public bool ApplyDraft()
        {
            try { Commit(); _saveError.IsVisible = false; return true; }
            catch (Exception ex) { _saveError.Text = "Could not save: " + ex.Message; _saveError.IsVisible = true; return false; }
        }
        public void DiscardDraft()
        {
            _draft?.Discard();
            RenderOptions.FieldOfView = RenderOptions.ParseFov(_settings.FieldOfView, RenderOptions.DefaultFov);
            _gamepadSettings.Reload();
            InvalidateVisual();
        }
        private readonly ScenePlayerRegistry? _players;

        private readonly Panel _pages = new();
        private readonly List<(string Name, Control Page)> _sections = new();
        private UiTabs _tabs = null!;
        private readonly List<HubNavButton> _sectionNav = new();
        private readonly List<PrimeTabButton> _categoryTabs = new();
        private StackPanel _sectionNavStack = null!;
        private ScrollViewer _sectionNavScroll = null!;
        private Border _sectionNavHost = null!;
        private Border _sectionContentHost = null!;
        private Grid _settingsBody = null!;
        private TextBlock _sectionTitle = null!;
        private TextBlock _sectionDetail = null!;
        private bool _compactShell;

        /// <summary>Raised when this view is finished with, saved or not.</summary>
        public event EventHandler? Closed;

        /// <summary>
        /// The player asked for the game-files screen, which lives on the
        /// front screen because extracting a ROM is a thing you do before
        /// there is anything to configure. Raised, not acted on: this view
        /// does not know what is behind it.
        /// </summary>
        public event EventHandler? GameFilesRequested;

        private ChoiceRow? _windowRow;
        private ChoiceRow? _clipPostRollRow;
        private ChoiceRow? _clipSecondsRow;
        private ChoiceRow? _replayStorageRow;
        private ToggleRow? _replayAutoPruneRow;
        private ToggleRow? _replayDeleteClipsRow;
        private ToggleRow? _spectatorNameTagsRow;
        private ToggleRow? _killCamRow;
        private ToggleRow? _finalKillCamRow;
        private static readonly int[] _replayStorageStops = { 0, 5, 10, 25, 50 };
        private SliderRow _resolutionScale = null!;
        private ToggleRow _lightingRow = null!;
        private ToggleRow _fogRow = null!;
        private ToggleRow _filteringRow = null!;
        private ToggleRow _mipmapRow = null!;
        private ChoiceRow _anisotropyRow = null!;
        private ChoiceRow _graphicsPresetRow = null!;
        private ChoiceRow _antiAliasingRow = null!;
        private SliderRow _sharpenRow = null!;
        private ToggleRow _bloomRow = null!;
        private SliderRow _bloomIntensityRow = null!;
        private ChoiceRow _colorGradeRow = null!;
        private SliderRow _gammaRow = null!;
        private SliderRow _contrastRow = null!;
        private SliderRow _saturationRow = null!;
        private ToggleRow _enhancedLightingRow = null!;
        private ChoiceRow _ambientOcclusionRow = null!;
        private ToggleRow _contactShadowsRow = null!;
        private ToggleRow _enhancedFogRow = null!;
        private ToggleRow _volumetricFogRow = null!;
        private ToggleRow _internalHdrRow = null!;
        private ToggleRow _reflectionsRow = null!;
        private ToggleRow _dynamicGlowRow = null!;
        private ToggleRow _textureReplacementsRow = null!;
        private ToggleRow _celRow = null!;
        private SliderRow _celBandsRow = null!;
        private SliderRow _celEdgeRow = null!;
        private ChoiceRow _brightSkinsRow = null!;
        private ChoiceRow _playerOutlineRow = null!;
        private SliderRow _playerOutlineWidthRow = null!;
        private ToggleRow _fpsRow = null!;
        private ToggleRow _reduceMotion = null!;

        /// <summary>
        /// The stops the FPS limit slides over, and the cap each one means.
        ///
        /// Stops rather than a free number, because a slider dragged across a
        /// free range lands on 143 and 167 as easily as on 144 and 165, and a
        /// limit that is one frame under the monitor's rate is the one number
        /// nobody wants. Every common refresh rate is here up to 240.
        ///
        /// "Display" is VSync at the monitor's own rate and is the default: it
        /// is the only tear-free setting, and on a 144 Hz screen it *is* 144.
        /// An explicit number turns VSync off, because asking for 120 on a
        /// 144 Hz screen with VSync on gets 72.
        ///
        /// None of them move the simulation, which runs at 60 Hz on every
        /// setting -- see Mods/Render/FrameTiming.cs.
        /// </summary>
        private static readonly int[] _anisotropyStops = { 1, 2, 4, 8, 16 };

        private static int AnisotropyIndex(int value)
        {
            int index = Array.IndexOf(_anisotropyStops, value);
            if (index >= 0)
            {
                return index;
            }
            int best = 0;
            for (int i = 1; i < _anisotropyStops.Length; i++)
            {
                if (_anisotropyStops[i] <= value)
                {
                    best = i;
                }
            }
            return best;
        }

        private static readonly (string Label, int Cap)[] _fpsLimitStops = new[]
        {
            ("Display (VSync)", FrameTiming.DisplayRate),
            ("30 fps", 30),
            ("60 fps", 60),
            ("75 fps", 75),
            ("90 fps", 90),
            ("100 fps", 100),
            ("120 fps", 120),
            ("144 fps", 144),
            ("165 fps", 165),
            ("180 fps", 180),
            ("200 fps", 200),
            ("240 fps", 240),
            ("Unlimited", FrameTiming.MaxCap)
        };

        private static int FpsLimitStopIndex(int cap)
        {
            int index = Array.FindIndex(_fpsLimitStops, stop => stop.Cap == cap);
            if (index >= 0)
            {
                return index;
            }
            // A settings.json written by hand, or by a build with a different
            // table: land on the nearest stop that does not exceed what was
            // asked for, rather than silently jumping to the default.
            int best = 0;
            for (int i = 1; i < _fpsLimitStops.Length; i++)
            {
                if (_fpsLimitStops[i].Cap <= cap)
                {
                    best = i;
                }
            }
            return best;
        }
        private SliderRow _fpsLimitRow = null!;
        private SliderRow _fovRow = null!;
        private ToggleRow _proHud = null!;
        private ToggleRow _smoothNativeHud = null!;
        private ToggleRow _killFeed = null!;
        private ChoiceRow _crosshairSizeRow = null!;
        private ChoiceRow _crosshairStyleRow = null!;
        private ChoiceRow _weaponStyleRow = null!;
        private ToggleRow _radarRow = null!;
        private ToggleRow _radarBackgroundRow = null!;
        private ToggleRow _radarOutlinesRow = null!;
        private SliderRow _sfxVolume = null!;
        private SliderRow _musicVolume = null!;
        private SliderRow _combatFeedbackVolume = null!;
        private ToggleRow _combatNotificationsVisible = null!;
        private readonly Dictionary<Mods.Sound.CombatFeedbackCue, ChoiceRow> _combatFeedbackRows = new();
        private readonly Dictionary<Mods.Sound.CombatFeedbackCue, Mods.Sound.CombatFeedbackOption[]> _combatFeedbackOptions = new();
        private readonly List<HubNavButton> _audioNav = new();
        private readonly List<Control> _audioPages = new();
        private int _audioPageIndex;
        private ChoiceRow _languageRow = null!;
        private SliderRow _sensitivity = null!;
        private SliderRow _imperialistZoomSensitivity = null!;
        private SliderRow _imperialistZoomAmount = null!;
        private SliderRow _altSwipeSensitivity = null!;
        private ToggleRow _invertY = null!;
        private ToggleRow _invertX = null!;
        private ToggleRow? _mouseMovementBoost;
        private ToggleRow? _mouseAltFormMovement;
        private ToggleRow? _stylusMovementBoost;
        private ToggleRow _penTablet = null!;
        private ToggleRow _scrollAllWeapons = null!;
        private GamepadSettingsPanel _gamepadSettings = null!;
        private readonly List<HubNavButton> _controlNav = new();
        private readonly List<Control> _controlPages = new();
        private int _controlPageIndex;
        private ToggleRow? _repositionFilter;
        private StackPanel? _stylusAdvanced;
        private HubNavButton? _stylusAdvancedButton;
        private bool _stylusAdvancedOpen;
        private FieldRow _playerName = null!;
        private ChoiceRow _hunterRow = null!;
        private ChoiceRow _colorRow = null!;
        private FieldRow _serverRow = null!;
        private FieldRow _masterRow = null!;
        private ToggleRow _autoUpdate = null!;
        private Note _saveError = null!;

        /// <summary>True when the user pressed save rather than closing.</summary>
        public bool Saved { get; private set; }

        /// <summary>What a frame around this should be titled.</summary>
        public string WindowTitle => $"{Mods.Branding.Name} settings";

        /// <summary>True when this was opened over a match rather than the launcher.</summary>
        public bool InGame => _inGame;

        public SettingsView(MenuSettings settings, bool inGame = false, ScenePlayerRegistry? players = null, bool shell = false)
        {
            _shell = shell;
            _settings = settings;
            _inGame = inGame;
            _players = players;

            Background = Brushes.Transparent;
            Focusable = true;

            BuildPages();

            // Keep UiTabs only as the tiny state object used by ShowSection and
            // existing screenshot automation. The player-facing navigation is
            // the same hub rail used everywhere else in Project Prime.
            _tabs = new UiTabs(_sections.ConvertAll(s => s.Name));
            _tabs.Changed += (_, _) => ShowPage(_tabs.Index);

            var contentRoot = new Grid
            {
                Margin = new Thickness(28, 24, 28, 30),
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 14
            };
            contentRoot.Children.Add(HubChrome.Header(
                inGame ? "MATCH  /  SETTINGS" : "HOME  /  SETTINGS",
                "SETTINGS",
                "Tune Project Prime without leaving the command hub.",
                inGame ? "LIVE MATCH" : "CONFIGURATION",
                inGame ? HubTheme.WarmBrush : HubTheme.AccentBrush));

            _settingsBody = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("190,*"),
                ColumnSpacing = 12
            };
            Grid.SetRow(_settingsBody, 1);
            contentRoot.Children.Add(_settingsBody);

            _sectionNavStack = BuildSectionNavigation();
            _sectionNavScroll = new ScrollViewer
            {
                Content = _sectionNavStack,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _sectionNavHost = new Border
            {
                Background = HubTheme.PanelStrongBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(7),
                Child = _sectionNavScroll
            };
            _settingsBody.Children.Add(_sectionNavHost);

            var sectionHeader = new StackPanel
            {
                Spacing = 2,
                Margin = new Thickness(2, 0, 2, 8)
            };
            _sectionTitle = new TextBlock
            {
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.Bold,
                FontSize = 20,
                Foreground = HubTheme.TextBrush
            };
            _sectionDetail = new TextBlock
            {
                FontFamily = HubTheme.Ui,
                FontSize = 9.5,
                Foreground = HubTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap
            };
            sectionHeader.Children.Add(_sectionTitle);
            sectionHeader.Children.Add(_sectionDetail);
            sectionHeader.Children.Add(HubChrome.Divider());

            var contentGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*")
            };
            contentGrid.Children.Add(sectionHeader);
            Grid.SetRow(_pages, 1);
            contentGrid.Children.Add(_pages);
            _sectionContentHost = new Border
            {
                Background = HubTheme.PanelStrongBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 11),
                Child = contentGrid
            };
            Grid.SetColumn(_sectionContentHost, 1);
            _settingsBody.Children.Add(_sectionContentHost);

            var footer = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                ColumnSpacing = 10
            };
            var back = new HubNavButton(shell ? "DISCARD" : "BACK", compact: true);
            ControllerNav.Identify(back, "settings.detail.back");
            back.Click += (_, _) => { if (_shell) DiscardDraft(); else Close(); };
            footer.Children.Add(back);

            _saveError = new Note("", HubTheme.Warm)
            {
                IsVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_saveError, 1);
            footer.Children.Add(_saveError);

            var save = new PrimeButton(shell ? "APPLY CHANGES" : inGame ? "APPLY" : "SAVE", primary: true);
            ControllerNav.Identify(save, "settings.detail.save");
            save.Click += (_, _) => TryCommit();
            Grid.SetColumn(save, 2);
            footer.Children.Add(save);
            Grid.SetRow(footer, 2);
            contentRoot.Children.Add(footer);

            if (shell)
            {
                // Same settings controls and persistence, arranged inside the shell.
                contentRoot.RowDefinitions = new("Auto,Auto,*");
                Grid.SetRow(_settingsBody, 2);
                contentRoot.Children.Remove(footer);
                var tabs = new Grid { ColumnSpacing = 4 };
                for (int i = 0; i < _sections.Count; i++)
                {
                    int index = i;
                    tabs.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                    var tab = new PrimeTabButton(_sections[i].Name.ToUpperInvariant(), () =>
                    { _tabs.Index = index; ShowPage(index); });
                    Grid.SetColumn(tab, i); tabs.Children.Add(tab); _categoryTabs.Add(tab);
                }
                Grid.SetRow(tabs, 1); contentRoot.Children.Add(tabs);
                _settingsBody.Children.Remove(_sectionNavHost);
                _settingsBody.ColumnDefinitions = new("*,320");
                Grid.SetColumn(_sectionContentHost, 0);
                var calibration = new PrimeFovPreview(() => _fovRow.Value);
                _fovRow.ValueChanged += (_, _) => calibration.InvalidateVisual();
                var diagnostics = PrimeChrome.Stack(new PrimeBadge("CALIBRATION VIEWPORT"), calibration,
                    PrimeChrome.Text("HARDWARE DIAGNOSTICS", 12, PrimeTheme.HighlightBrush, true),
                    PrimeChrome.Text($"PLATFORM // {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n"
                        + $"PROCESS // {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\n"
                        + $"LOGICAL CORES // {Environment.ProcessorCount}\nSIMULATION // 60 HZ", 11, data: true));
                footer.Children.Clear();
                var command = PrimeChrome.Stack(save, back, _saveError);
                var right = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 12 };
                right.Children.Add(new PrimePanel(diagnostics));
                var commandPanel = new PrimePanel(command);
                Grid.SetRow(commandPanel, 1); right.Children.Add(commandPanel);
                Grid.SetColumn(right, 1); _settingsBody.Children.Add(right);
                Content = contentRoot;
            }
            else
            {
                Panel backdrop = UiLayout.Backdrop(inGame);
                backdrop.Children.Add(contentRoot); Content = backdrop;
            }
            if (shell) _draft = new SettingsDraft(_pages);
            SizeChanged += (_, e) => ApplyShellResponsive(e.NewSize);
            ShowPage(0);
            ApplyShellResponsive(new Size(960, 600));
        }

        /// <summary>
        /// Put the keyboard on the strip as the view appears.
        ///
        /// Without it the screen opens with nothing focused, and the first Tab
        /// goes to whatever the tree happens to offer first rather than to the
        /// strip -- which is the difference between a screen that can be
        /// driven from the keyboard and one that can be driven from the
        /// keyboard once you have found out how.
        /// </summary>
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Dispatcher.UIThread.Post(() =>
            {
                if (_shell && _categoryTabs.Count > 0)
                {
                    _categoryTabs[Math.Clamp(_tabs.Index, 0, _categoryTabs.Count - 1)].Focus();
                }
                else if (_sectionNav.Count > 0)
                {
                    _sectionNav[Math.Clamp(_tabs.Index, 0, _sectionNav.Count - 1)].Focus();
                }
            }, DispatcherPriority.Background);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        /// <summary>
        /// Leaving without saving.
        ///
        /// The field of view is applied as it is dragged -- it is a question
        /// about how the room behind this screen looks, and there is no
        /// answering it from a number -- so it is the one row here that has
        /// changed something by the time cancel is pressed. Put back from the
        /// file, which is what every other row has been doing all along by
        /// simply not writing anything.
        /// </summary>
        private void Close()
        {
            if (_shell) { Closed?.Invoke(this, EventArgs.Empty); return; }
            if (!Saved)
            {
                RenderOptions.FieldOfView = RenderOptions.ParseFov(_settings.FieldOfView,
                    RenderOptions.DefaultFov);
            }
            Closed?.Invoke(this, EventArgs.Empty);
        }

        // ----------------------------------------------------------- structure

        private StackPanel AddSection(string name)
        {
            var page = new StackPanel { Spacing = 2 };
            // The inset is the page's margin rather than the scroll viewer's
            // padding: padding is not taken off the width the content is
            // measured with, so every wrapped note ran off the right edge of
            // the window by exactly that much.
            page.Margin = new Thickness(0);
            var scroll = new ScrollViewer
            {
                Content = page,
                IsVisible = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            _pages.Children.Add(scroll);
            _sections.Add((name, scroll));
            return page;
        }

        /// <summary>
        /// Open on a named page rather than the first.
        ///
        /// For <c>-uishot</c>, which is the only way any of these can be
        /// looked at from a machine with no display.
        /// </summary>
        internal void ShowSection(string name, int sub = 0)
        {
            for (int i = 0; i < _sections.Count; i++)
            {
                if (String.Equals(_sections[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    _tabs.Index = i;
                    ShowPage(i);
                    if (String.Equals(name, "Audio", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowAudioPage(sub);
                    }
                    else if (String.Equals(name, "Controls", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowControlPage(sub);
                    }
                    return;
                }
            }
        }

        private void ShowPage(int index)
        {
            if (_sections.Count == 0)
            {
                return;
            }
            index = Math.Clamp(index, 0, _sections.Count - 1);
            for (int i = 0; i < _sections.Count; i++)
            {
                _sections[i].Page.IsVisible = i == index;
                if (i < _sectionNav.Count)
                {
                    _sectionNav[i].Selected = i == index;
                }
            }
            for (int i = 0; i < _categoryTabs.Count; i++) _categoryTabs[i].Selected = i == index;
            if (_sectionTitle != null)
            {
                string name = _sections[index].Name;
                _sectionTitle.Text = name.ToUpperInvariant();
                _sectionDetail.Text = SectionDescription(name);
            }
        }

        private StackPanel BuildSectionNavigation()
        {
            var nav = new StackPanel { Spacing = 5 };
            for (int i = 0; i < _sections.Count; i++)
            {
                int at = i;
                string name = _sections[i].Name;
                var button = new HubNavButton(name.ToUpperInvariant(),
                    compact: true, accent: SectionAccent(name));
                string id = $"settings.detail.{name.ToLowerInvariant()}";
                ControllerNav.Identify(button, id, initial: i == 0);
                button.Click += (_, _) =>
                {
                    _tabs.Index = at;
                    ShowPage(at);
                };
                _sectionNav.Add(button);
                nav.Children.Add(button);
            }
            for (int i = 0; i < _sectionNav.Count; i++)
            {
                string prev = _sections[(i + _sections.Count - 1) % _sections.Count].Name.ToLowerInvariant();
                string next = _sections[(i + 1) % _sections.Count].Name.ToLowerInvariant();
                _sectionNav[i].SetValue(ControllerNav.NavUpProperty, $"settings.detail.{prev}");
                _sectionNav[i].SetValue(ControllerNav.NavDownProperty, $"settings.detail.{next}");
            }
            return nav;
        }

        private static Color SectionAccent(string name) => name switch
        {
            "Graphics" => Color.FromRgb(0x55, 0xe0, 0xd2),
            "Audio" => HubTheme.Good,
            "Controls" => Color.FromRgb(0x86, 0xb8, 0xff),
            "Replays" => Color.FromRgb(0xa7, 0x9b, 0xf5),
            "Profile" => HubTheme.Warm,
            "Maintenance" => Color.FromRgb(0xf0, 0xb4, 0x63),
            "Credits" => HubTheme.TextDim,
            _ => HubTheme.Accent
        };

        private static string SectionDescription(string name) => name switch
        {
            "Display" => "Window, view, frame pacing, HUD and accessibility.",
            "Graphics" => "Render resolution, supersampling and scene-quality controls.",
            "Audio" => "Sound, music and language.",
            "Controls" => "Keyboard, mouse, controller, touch and stylus.",
            "Replays" => "Instant clips, replay storage and playback controls.",
            "Profile" => "Player identity, hunter, servers, updates and game files.",
            "Maintenance" => "Verify the install, clean reproducible caches and diagnose performance.",
            "Credits" => "Project attribution, community contributors and technology.",
            _ => ""
        };

        private void ApplyShellResponsive(Size size)
        {
            if (_shell) return;
            bool compact = size.Width < 760 || size.Height < 500;
            if (compact == _compactShell)
            {
                return;
            }
            _compactShell = compact;
            if (compact)
            {
                _settingsBody.ColumnDefinitions = new ColumnDefinitions("*");
                _settingsBody.RowDefinitions = new RowDefinitions("Auto,*");
                _settingsBody.ColumnSpacing = 0;
                _settingsBody.RowSpacing = 8;
                Grid.SetColumn(_sectionNavHost, 0);
                Grid.SetRow(_sectionNavHost, 0);
                Grid.SetColumn(_sectionContentHost, 0);
                Grid.SetRow(_sectionContentHost, 1);
                _sectionNavStack.Orientation = Orientation.Horizontal;
                _sectionNavScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                _sectionNavScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                foreach (HubNavButton button in _sectionNav)
                {
                    button.Width = 118;
                    button.MinHeight = 40;
                }
            }
            else
            {
                _settingsBody.ColumnDefinitions = new ColumnDefinitions("190,*");
                _settingsBody.RowDefinitions = new RowDefinitions("*");
                _settingsBody.ColumnSpacing = 12;
                _settingsBody.RowSpacing = 0;
                Grid.SetColumn(_sectionNavHost, 0);
                Grid.SetRow(_sectionNavHost, 0);
                Grid.SetColumn(_sectionContentHost, 1);
                Grid.SetRow(_sectionContentHost, 0);
                _sectionNavStack.Orientation = Orientation.Vertical;
                _sectionNavScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                _sectionNavScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                foreach (HubNavButton button in _sectionNav)
                {
                    button.Width = Double.NaN;
                    button.MinHeight = 42;
                }
            }
        }

        private static Caption Heading(StackPanel page, string text)
        {
            var caption = new Caption(text) { Height = 30, Margin = new Thickness(0, 8, 0, 4) };
            page.Children.Add(caption);
            return caption;
        }

        private static Note Explain(StackPanel page, string text, Color? color = null)
        {
            var note = new Note(text, color);
            page.Children.Add(note);
            return note;
        }

        private static T Add<T>(StackPanel page, T control) where T : Control
        {
            page.Children.Add(control);
            return control;
        }

        private static HubNavButton AddAdvancedToggle(StackPanel page, StackPanel advanced, string navId)
        {
            var button = new HubNavButton("ADVANCED", compact: true)
            {
                Width = 170,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 4)
            };
            ControllerNav.Identify(button, navId);
            button.Click += (_, _) => advanced.IsVisible = !advanced.IsVisible;
            page.Children.Add(button);
            page.Children.Add(advanced);
            return button;
        }

        /// <summary>
        /// Eight pages: display, graphics, audio, controls, replays, profile, maintenance and credits.
        /// </summary>
        private void BuildPages()
        {
            BuildDisplay(AddSection("Display"));
            BuildGraphics(AddSection("Graphics"));
            BuildAudio(AddSection("Audio"));
            BuildControls(AddSection("Controls"));
            BuildReplays(AddSection("Replays"));
            BuildLauncher(AddSection("Profile"));
            BuildMaintenance(AddSection("Maintenance"));
            BuildCredits(AddSection("Credits"));
        }

        private void BuildMaintenance(StackPanel page)
        {
            Heading(page, "Installation");
            var status = Explain(page, Maintenance.PerformanceSummary(_settings));
            var storage = Explain(page, Maintenance.StorageSummary());

            var verify = new HubNavButton("VERIFY INSTALLATION",
                "Check every release-owned file without touching player data", compact: true)
            {
                MinHeight = 48,
                Margin = new Thickness(0, 6, 0, 4)
            };
            ControllerNav.Identify(verify, "settings.maintenance.verify");
            verify.Click += (_, _) =>
            {
                status.Text = Update.DesktopUpdate.VerifyInstallation();
            };
            page.Children.Add(verify);

            var clean = new HubNavButton("CLEAN TEMPORARY CACHE",
                "Prune old map builds, replay materializations and stale previews", compact: true)
            {
                MinHeight = 48,
                Margin = new Thickness(0, 4, 0, 4)
            };
            ControllerNav.Identify(clean, "settings.maintenance.clean");
            clean.Click += async (_, _) =>
            {
                clean.IsEnabled = false;
                status.Text = "Cleaning reproducible caches...";
                MaintenanceReport report = await Task.Run(Maintenance.CleanReproducibleData);
                status.Text = report.Summary;
                storage.Text = Maintenance.StorageSummary();
                clean.IsEnabled = true;
            };
            page.Children.Add(clean);

            var clearMaps = new HubNavButton("CLEAR MAP BUILD CACHE",
                "Remove compiled map binaries; they are rebuilt on demand", compact: true)
            {
                MinHeight = 48,
                Margin = new Thickness(0, 4, 0, 4)
            };
            ControllerNav.Identify(clearMaps, "settings.maintenance.clear-map-cache");
            clearMaps.Click += async (_, _) =>
            {
                clearMaps.IsEnabled = false;
                MaintenanceReport report = await Task.Run(Maintenance.ClearMapBuildCache);
                status.Text = report.Summary;
                storage.Text = Maintenance.StorageSummary();
                clearMaps.IsEnabled = true;
            };
            page.Children.Add(clearMaps);

            var rebuildThumbs = new HubNavButton("REBUILD MAP THUMBNAILS",
                "Clear previews and render every available arena again", compact: true)
            {
                MinHeight = 48,
                Margin = new Thickness(0, 4, 0, 4),
                IsEnabled = !_inGame && ThumbnailHost.CanRender
            };
            ControllerNav.Identify(rebuildThumbs, "settings.maintenance.rebuild-thumbnails");
            rebuildThumbs.Click += async (_, _) =>
            {
                rebuildThumbs.IsEnabled = false;
                MaintenanceReport cleared = await Task.Run(Maintenance.ClearThumbnails);
                status.Text = cleared.Summary + " Rebuilding previews...";
                status.Text = await RunThumbnailRebuild();
                storage.Text = Maintenance.StorageSummary();
                rebuildThumbs.IsEnabled = !_inGame && ThumbnailHost.CanRender;
            };
            page.Children.Add(rebuildThumbs);
            Explain(page, "Cleanup never deletes settings, controls, saves, extracted cartridge data, real replays, custom maps or user media.");

            Heading(page, "Performance");
            var reset = new HubNavButton("RESET PERFORMANCE SETTINGS",
                "Return rendering and frame pacing to clean-install defaults", compact: true)
            {
                MinHeight = 48,
                Margin = new Thickness(0, 6, 0, 4)
            };
            ControllerNav.Identify(reset, "settings.maintenance.reset-performance");
            reset.Click += (_, _) =>
            {
                _graphicsPresetRow.Index = (int)GraphicsPreset.Original;
                ApplyGraphicsPresetDraft(GraphicsPreset.Original);
                _resolutionScale.Value = 100;
                _fovRow.Value = RenderOptions.DefaultFov;
                _lightingRow.On = true;
                _fogRow.On = true;
                _filteringRow.On = false;
                _mipmapRow.On = false;
                _anisotropyRow.Index = 0;
                _fpsRow.On = false;
                _smoothNativeHud.On = true;
                _fpsLimitRow.Value = FpsLimitStopIndex(FrameTiming.DisplayRate);
                _celRow.On = false;
                _celBandsRow.Value = 8;
                _celEdgeRow.Value = 50;
                ShowTextureQualityRows();
                ShowCelRows();
                status.Text = "Performance settings reset in this draft. Apply changes to save them.";
            };
            page.Children.Add(reset);

            var benchmark = new HubNavButton("RUN PERFORMANCE DIAGNOSTIC",
                "20-second 1080p / 144 Hz-equivalent bot workload with JSON frame-time results", compact: true)
            {
                MinHeight = 48,
                Margin = new Thickness(0, 4, 0, 4),
                IsEnabled = !_inGame && !OperatingSystem.IsAndroid()
            };
            ControllerNav.Identify(benchmark, "settings.maintenance.perf");
            benchmark.Click += async (_, _) =>
            {
                benchmark.IsEnabled = false;
                status.Text = "Running performance diagnostic in an isolated process...";
                status.Text = await RunPerformanceDiagnostic();
                benchmark.IsEnabled = !_inGame && !OperatingSystem.IsAndroid();
                storage.Text = Maintenance.StorageSummary();
            };
            page.Children.Add(benchmark);
            if (_inGame)
            {
                Explain(page, "Performance diagnostics are available from the launcher so the benchmark does not compete with a live match.");
            }
            else if (OperatingSystem.IsAndroid())
            {
                Explain(page, "The isolated desktop performance harness is not available on Android; Android frame pacing continues to use its device-side diagnostics.");
            }

            Heading(page, "Current effective settings");
            Explain(page, Maintenance.PerformanceSummary(_settings));
            Explain(page, "Command-line equivalent: ProjectPrime -perfcheck \"MP3 PROVING GROUND\" -seconds 20 -hz 144");
        }

        private static async Task<string> RunThumbnailRebuild()
        {
            try
            {
                if (!ThumbnailHost.CanRender)
                    return "Thumbnail rendering is not available on this platform.";
                int written = await ThumbnailHost.RenderMissingAsync(
                    line => DebugLog.Line("thumbnails", line));
                int missing = ThumbnailGenerator.MissingThumbnails().Count;
                return missing == 0
                    ? $"Map thumbnails rebuilt ({written} rendered)."
                    : $"Rendered {written} thumbnail(s); {missing} still unavailable.";
            }
            catch (Exception ex)
            {
                return "Thumbnail rebuild failed: " + ex.Message;
            }
        }

        private static async Task<string> RunPerformanceDiagnostic()
        {
            try
            {
                string? executable = Environment.ProcessPath;
                if (String.IsNullOrEmpty(executable))
                {
                    return "Could not locate the Project Prime executable.";
                }
                string directory = Path.Combine(LauncherPrefs.Directory, "perf");
                Directory.CreateDirectory(directory);
                string output = Path.Combine(directory,
                    $"perf-manual-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                var start = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add("-perfcheck");
                start.ArgumentList.Add("MP3 PROVING GROUND");
                start.ArgumentList.Add("-seconds");
                start.ArgumentList.Add("20");
                start.ArgumentList.Add("-hz");
                start.ArgumentList.Add("144");
                start.ArgumentList.Add("-output");
                start.ArgumentList.Add(output);
                using Process? process = Process.Start(start);
                if (process == null) return "Could not start the performance diagnostic.";
                string stdout = await process.StandardOutput.ReadToEndAsync();
                string stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                {
                    string detail = stderr.Trim().Length > 0 ? stderr.Trim()
                        : stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
                    return "Performance diagnostic failed"
                        + (detail.Length > 0 ? ": " + detail : ".");
                }
                return File.Exists(output)
                    ? "Performance report saved to " + output
                    : "Performance diagnostic completed, but no JSON report was written.";
            }
            catch (Exception ex)
            {
                return "Performance diagnostic failed: " + ex.Message;
            }
        }

        /// <summary>
        /// Who this is built on, in full.
        ///
        /// It used to be four dim lines in the corner of the front screen,
        /// where it was the first thing the eye landed on and the last thing
        /// anybody needed while choosing a match. Here it is out of the way
        /// and, being a page rather than a corner, it can say what each person
        /// actually did.
        /// </summary>
        private void BuildCredits(StackPanel page)
        {
            Heading(page, "Project Prime");
            page.Children.Add(PrimeChrome.Text(Mods.Credits.Summary, 14, PrimeTheme.TextSecondaryBrush));

            void Credit(Mods.Credits.Entry entry)
            {
                page.Children.Add(PrimeChrome.Text(entry.Who, 16, PrimeTheme.HighlightBrush));
                page.Children.Add(PrimeChrome.Text(entry.What + (entry.Where.Length > 0 ? "\n" + entry.Where : ""),
                    14, PrimeTheme.TextSecondaryBrush));
            }

            Heading(page, "MphRead");
            Credit(Mods.Credits.Foundation);

            Heading(page, "Fruity Prime");
            page.Children.Add(PrimeChrome.Text(Mods.Credits.Author, 16, PrimeTheme.HighlightBrush));
            page.Children.Add(PrimeChrome.Text(Mods.Credits.ForkWork, 14, PrimeTheme.TextSecondaryBrush));

            Heading(page, "Additional upstream work & acknowledgements");
            foreach (Mods.Credits.Entry entry in Mods.Credits.Entries)
            {
                if (entry != Mods.Credits.Foundation) Credit(entry);
            }

        }

        // ------------------------------------------------------------- display

        private void BuildDisplay(StackPanel page)
        {
            // A phone has one window, it is already the whole screen, and it
            // has no F11. Everything in this group is about a desktop window.
            if (!OperatingSystem.IsAndroid())
            {
                Heading(page, "Window");
                _windowRow = Add(page, new ChoiceRow("Mode",
                    new[] { "Windowed", "Borderless", "Fullscreen" },
                    (int)LauncherPrefs.WindowMode));
            }

            // Its own heading, above the performance rows, because it is not
            // one: everything under Performance trades picture for frame rate,
            // and this trades neither. It is how wide the view is, which is
            // the first thing anybody who has played a shooter with a mouse
            // goes looking for.
            Heading(page, "View");
            _fovRow = Add(page, new SliderRow("Field of view",
                RenderOptions.FieldOfView,
                v => $"{v}{'\u00b0'}{(v == RenderOptions.DefaultFov ? " (DS)" : "")}",
                min: RenderOptions.MinFov, max: RenderOptions.MaxFov, keyStep: 1));
            // Live, while the slider is being dragged: the settings open over
            // the running match, so the row can be answered by looking at the
            // room behind it rather than by saving and coming back. Cancel
            // puts it back -- see Revert.
            _fovRow.ValueChanged += (_, _) => RenderOptions.FieldOfView = _fovRow.Value;

            Heading(page, "Frame pacing");
            _fpsLimitRow = Add(page, new SliderRow("FPS limit",
                FpsLimitStopIndex(FrameTiming.FrameRateCap),
                v => _fpsLimitStops[Math.Clamp(v, 0, _fpsLimitStops.Length - 1)].Label,
                min: 0, max: _fpsLimitStops.Length - 1, keyStep: 1));
            _fpsRow = Add(page, new ToggleRow("FPS counter", RenderOptions.ShowFps));

            // One switch, and none of what it drives.
            //
            // Pro mode is the whole HUD decision now: helmet and visor,
            // crosshair, weapon list and its size, and where energy, ammo and
            // the score are drawn. Off is the game as the DS drew it; on is
            // the competitive layout. The six settings underneath were six
            // ways to end up somewhere between the two, and a player who has
            // to answer six questions to get one look has been handed the
            // design problem. They keep working -- Features still holds them,
            // -nohelmet still sets two of them -- they simply are not asked
            // about here.
            Heading(page, "HUD");
            _proHud = Add(page, new ToggleRow("Pro mode HUD", Features.ProHud));
            _smoothNativeHud = Add(page, new ToggleRow("Smooth native HUD",
                RenderOptions.SmoothNativeHud));
            Explain(page, "Smooth native HUD uses filtered sampling for the original DS reticle, meters and weapon-menu sprites when they are enlarged on modern displays. Turn it off for the original hard pixel edges.");
            _killFeed = Add(page, new ToggleRow("Kill feed", Features.KillFeedEnabled));
            Explain(page, "Shows confirmed match deaths with attacker, weapon or cause, victim, headshots and team kills.");
            // Size applies to both the original DS reticle and the Pro/custom
            // crosshair. Type and weapon behavior remain Pro-mode choices.
            _crosshairSizeRow = Add(page, new ChoiceRow("Crosshair size",
                Crosshair.SizeNames, (int)Crosshair.Size));
            Explain(page, "Size applies to both the original DS reticle and Pro mode crosshairs.");
            _crosshairStyleRow = Add(page, new ChoiceRow("Crosshair type",
                Crosshair.StyleNames, (int)Crosshair.Style));
            _crosshairStyleRow.Preview = (context, area) => CrosshairPreview.Draw(context, area,
                (CrosshairStyle)_crosshairStyleRow.Index, (CrosshairSize)_crosshairSizeRow.Index);
            // The preview lives on the type row and answers both rows, so the
            // size row has to ask for it to be repainted.
            _crosshairSizeRow.Changed += (_, _) => _crosshairStyleRow.InvalidateVisual();
            // Where the gun sits, which is the one Pro-mode question with two
            // real answers rather than a right one. Static is Quake's: the
            // weapon is welded to the camera, the crosshair sits dead centre
            // and the HUD stops sliding around under the mouse. Dynamic is the
            // DS game's: the gun lags behind the aim point and settles after
            // it, and the crosshair moves around the screen with the aim while
            // the camera follows. Pro mode has always drawn the first, so that
            // stays the default -- this only makes the second reachable
            // without giving up the rest of the HUD.
            //
            // The crosshair is the half that used not to move. It was drawn
            // wherever Features.FixedCrosshair said, which Pro mode forces on
            // for a different reason -- the DS reticle animates as you fire
            // and a crosshair must not -- so answering Dynamic moved the gun
            // and left the thing the player is actually looking at welded to
            // the middle of the screen. See PlayerHud.UpdateReticle.
            _weaponStyleRow = Add(page, new ChoiceRow("Weapon",
                new[] { "Static (Quake)", "Dynamic (Metroid)" },
                Features.ProHudFixedWeapon ? 0 : 1));
            _proHud.Changed += (_, _) => ShowCrosshairRows();
            ShowCrosshairRows();

            // Independent of Pro mode: a round overlay under the FPS counter
            // showing nearby hunters, weapons and power-ups, not the DS HUD's
            // business either way. Called "Radar" on request; it used to be
            // "Motion tracker" specifically to avoid this, since "Hunter
            // radar" is already a match rule further down this same page --
            // two different things with the same name on one page is how a
            // player answers the wrong question, so the two are still worth
            // keeping apart by eye even though the label no longer does it.
            _radarRow = Add(page, new ToggleRow("Radar", Radar.Enabled));
            // Both default on; both off leaves only the hunter/weapon/
            // power-up blips on screen, with nothing drawn around them --
            // except the centre marker, which stays regardless of either.
            _radarBackgroundRow = Add(page, new ToggleRow("Radar background",
                Radar.ShowBackground));
            _radarOutlinesRow = Add(page, new ToggleRow("Radar outlines",
                Radar.ShowOutlines));
            _radarRow.Changed += (_, _) => ShowRadarRows();
            ShowRadarRows();

            Heading(page, "Accessibility");
            _brightSkinsRow = Add(page, new ChoiceRow("Player highlight",
                new[] { "Off", "Textured", "High contrast", "Solid" },
                !RenderOptions.BrightSkins ? 0 : RenderOptions.BrightSkinStyle switch
                {
                    PlayerSkinStyle.Textured => 1,
                    PlayerSkinStyle.HighContrastTextured => 2,
                    _ => 3
                }));
            Explain(page, "Local-only multiplayer visibility aid. Team modes use team colors; free-for-all uses suit colors.");
            _playerOutlineRow = Add(page, new ChoiceRow("Player outline",
                new[] { "Off", "Team color", "Bright red" },
                (int)RenderOptions.PlayerOutline));
            _playerOutlineWidthRow = Add(page, new SliderRow("Outline thickness",
                RenderOptions.PlayerOutlineWidth, value => $"{value} px",
                labelWidth: 160, min: 1, max: 8, keyStep: 1));
            _playerOutlineWidthRow.IsVisible = _playerOutlineRow.Index != 0;
            _playerOutlineRow.Changed += (_, _) =>
                _playerOutlineWidthRow.IsVisible = _playerOutlineRow.Index != 0;
            _reduceMotion = Add(page, new ToggleRow(
                "Reduce menu motion", LauncherPrefs.ReduceMotion));
        }

        private void BuildGraphics(StackPanel page)
        {
            Heading(page, "Quality preset");
            _graphicsPresetRow = Add(page, new ChoiceRow("Preset",
                new[] { "Original", "Performance", "Enhanced", "Ultra", "Extreme", "Custom" },
                (int)RenderOptions.Preset));
            Explain(page, "Original preserves the existing renderer. Enhanced and above add presentation-only processing after the 3D world is rendered and before the full-resolution HUD.");

            Heading(page, "Rendering");
            Explain(page, "100% is native framebuffer resolution. Above 100% supersamples the 3D world before resolving it to the display; 300% is an extreme 3x-per-axis mode that shades nine times as many scene pixels.");
            _resolutionScale = Add(page, new SliderRow("Render scale",
                RenderOptions.ResolutionScale,
                v => v == 100 ? "100% (native)"
                    : v > 100 ? $"{v}% (supersampled)" : $"{v}%",
                min: RenderOptions.MinScale, max: RenderOptions.MaxScale, keyStep: 5));
            _antiAliasingRow = Add(page, new ChoiceRow("Anti-aliasing",
                new[] { "Off", "FXAA", "FXAA High" }, (int)RenderOptions.AntiAliasing));
            _sharpenRow = Add(page, new SliderRow("Image sharpening",
                RenderOptions.SharpenStrength, v => $"{v}%", min: 0, max: 100, keyStep: 5));

            Heading(page, "Textures");
            _filteringRow = Add(page, new ToggleRow("Bilinear texture filtering",
                RenderOptions.TextureFiltering));
            _mipmapRow = Add(page, new ToggleRow("Trilinear mipmaps",
                RenderOptions.TextureMipmaps));
            _anisotropyRow = Add(page, new ChoiceRow("Anisotropic filtering",
                new[] { "Off", "2x", "4x", "8x", "16x" },
                AnisotropyIndex(RenderOptions.TextureAnisotropy)));
            _textureReplacementsRow = Add(page, new ToggleRow("HD texture replacements",
                RenderOptions.TextureReplacements));
            Explain(page, "Mipmaps reduce distant shimmer; anisotropic filtering sharpens oblique surfaces. HD replacements load optional user textures and fall back to the original assets when no replacement exists.");
            _filteringRow.Changed += (_, _) => ShowTextureQualityRows();
            ShowTextureQualityRows();

            Heading(page, "Lighting and depth");
            _lightingRow = Add(page, new ToggleRow("Original lighting", RenderOptions.Lighting));
            _enhancedLightingRow = Add(page, new ToggleRow("Enhanced per-pixel lighting",
                RenderOptions.EnhancedLighting));
            _ambientOcclusionRow = Add(page, new ChoiceRow("Ambient occlusion",
                new[] { "Off", "Low", "Medium", "High" }, (int)RenderOptions.AmbientOcclusion));
            _contactShadowsRow = Add(page, new ToggleRow("Contact shadows",
                RenderOptions.ContactShadows));
            _reflectionsRow = Add(page, new ToggleRow("Screen-space reflections",
                RenderOptions.Reflections));
            Explain(page, "Enhanced lighting, AO, contact shadows and reflections use the scene depth buffer only; they do not alter collision, networking or weapon simulation.");

            Heading(page, "Atmosphere and glow");
            _fogRow = Add(page, new ToggleRow("Original fog", RenderOptions.Fog));
            _enhancedFogRow = Add(page, new ToggleRow("Enhanced atmospheric fog",
                RenderOptions.EnhancedFog));
            _volumetricFogRow = Add(page, new ToggleRow("Volumetric fog detail",
                RenderOptions.VolumetricFog));
            _bloomRow = Add(page, new ToggleRow("Bloom", RenderOptions.Bloom));
            _bloomIntensityRow = Add(page, new SliderRow("Bloom intensity",
                RenderOptions.BloomIntensity, v => $"{v}%", min: 0, max: 150, keyStep: 5));
            _dynamicGlowRow = Add(page, new ToggleRow("Dynamic energy glow",
                RenderOptions.DynamicGlow));
            _internalHdrRow = Add(page, new ToggleRow("HDR tone mapping",
                RenderOptions.InternalHdr));
            _bloomRow.Changed += (_, _) => ShowModernGraphicsRows();
            _enhancedFogRow.Changed += (_, _) => ShowModernGraphicsRows();

            Heading(page, "Color");
            _colorGradeRow = Add(page, new ChoiceRow("Color profile",
                new[] { "Original", "Enhanced", "Vibrant", "Cinematic" },
                (int)RenderOptions.ColorGrade));
            _gammaRow = Add(page, new SliderRow("Gamma", RenderOptions.Gamma,
                v => $"{v}%", min: 50, max: 150, keyStep: 5));
            _contrastRow = Add(page, new SliderRow("Contrast", RenderOptions.Contrast,
                v => $"{v}%", min: 50, max: 150, keyStep: 5));
            _saturationRow = Add(page, new SliderRow("Saturation", RenderOptions.Saturation,
                v => $"{v}%", min: 0, max: 200, keyStep: 5));

            var maxQuality = new HubNavButton("APPLY EXTREME PRESET",
                "300% supersampling plus every enhanced rendering effect", primary: true)
            {
                MinHeight = 50,
                Margin = new Thickness(0, 10, 0, 4)
            };
            ControllerNav.Identify(maxQuality, "settings.graphics.max");
            maxQuality.Click += (_, _) =>
            {
                _graphicsPresetRow.Index = (int)GraphicsPreset.Extreme;
                ApplyGraphicsPresetDraft(GraphicsPreset.Extreme);
            };
            page.Children.Add(maxQuality);
            Explain(page, "Extreme is deliberately excessive. Ultra is the practical high-end preset; Extreme keeps the old 300% supersampling ceiling for very fast GPUs.");

            Heading(page, "Cel shading");
            _celRow = Add(page, new ToggleRow("Cel shading", RenderOptions.CelShading));
            _celBandsRow = Add(page, new SliderRow("Shading bands", RenderOptions.CelBands,
                v => v.ToString(CultureInfo.InvariantCulture), min: 2, max: 8, keyStep: 1));
            _celEdgeRow = Add(page, new SliderRow("Outline strength",
                (int)MathF.Round(RenderOptions.CelEdge * 100),
                v => $"{v}%", min: 0, max: 100, keyStep: 5));
            _celRow.Changed += (_, _) => ShowCelRows();
            _graphicsPresetRow.Changed += (_, _) =>
                ApplyGraphicsPresetDraft((GraphicsPreset)Math.Clamp(_graphicsPresetRow.Index, 0, 5));
            ShowModernGraphicsRows();
            ShowCelRows();
        }

        private void ApplyGraphicsPresetDraft(GraphicsPreset preset)
        {
            int scale;
            AntiAliasingMode aa;
            int sharpen, bloom;
            bool bloomOn, enhancedLight, contacts, enhancedFog, volumeFog, hdr, reflections, glow;
            AmbientOcclusionQuality ao;
            ColorGradeProfile grade;
            int contrast, saturation, anisotropy;
            bool filtering, mipmaps;
            switch (preset)
            {
            case GraphicsPreset.Performance:
                scale = 85; aa = AntiAliasingMode.Fxaa; sharpen = 20; bloomOn = false; bloom = 45;
                grade = ColorGradeProfile.Enhanced; contrast = 104; saturation = 106;
                enhancedLight = false; ao = AmbientOcclusionQuality.Off; contacts = false;
                enhancedFog = true; volumeFog = false; hdr = false; reflections = false; glow = false;
                filtering = mipmaps = true; anisotropy = 4;
                break;
            case GraphicsPreset.Enhanced:
                scale = 100; aa = AntiAliasingMode.FxaaHigh; sharpen = 25; bloomOn = true; bloom = 60;
                grade = ColorGradeProfile.Enhanced; contrast = 108; saturation = 112;
                enhancedLight = true; ao = AmbientOcclusionQuality.Medium; contacts = true;
                enhancedFog = true; volumeFog = false; hdr = false; reflections = false; glow = true;
                filtering = mipmaps = true; anisotropy = 16;
                break;
            case GraphicsPreset.Ultra:
                scale = 150; aa = AntiAliasingMode.FxaaHigh; sharpen = 18; bloomOn = true; bloom = 80;
                grade = ColorGradeProfile.Cinematic; contrast = 110; saturation = 115;
                enhancedLight = true; ao = AmbientOcclusionQuality.High; contacts = true;
                enhancedFog = true; volumeFog = true; hdr = true; reflections = true; glow = true;
                filtering = mipmaps = true; anisotropy = 16;
                break;
            case GraphicsPreset.Extreme:
                scale = RenderOptions.MaxScale; aa = AntiAliasingMode.FxaaHigh; sharpen = 12;
                bloomOn = true; bloom = 95; grade = ColorGradeProfile.Cinematic;
                contrast = 112; saturation = 118; enhancedLight = true;
                ao = AmbientOcclusionQuality.High; contacts = true; enhancedFog = true;
                volumeFog = true; hdr = true; reflections = true; glow = true;
                filtering = mipmaps = true; anisotropy = 16;
                break;
            case GraphicsPreset.Original:
                scale = 100; aa = AntiAliasingMode.Off; sharpen = 0; bloomOn = false; bloom = 60;
                grade = ColorGradeProfile.Original; contrast = saturation = 100;
                enhancedLight = false; ao = AmbientOcclusionQuality.Off; contacts = false;
                enhancedFog = volumeFog = hdr = reflections = glow = false;
                filtering = mipmaps = false; anisotropy = 1;
                _textureReplacementsRow.On = false;
                break;
            default:
                return;
            }
            _resolutionScale.Value = scale;
            _antiAliasingRow.Index = (int)aa;
            _sharpenRow.Value = sharpen;
            _bloomRow.On = bloomOn;
            _bloomIntensityRow.Value = bloom;
            _colorGradeRow.Index = (int)grade;
            _gammaRow.Value = 100;
            _contrastRow.Value = contrast;
            _saturationRow.Value = saturation;
            _enhancedLightingRow.On = enhancedLight;
            _ambientOcclusionRow.Index = (int)ao;
            _contactShadowsRow.On = contacts;
            _enhancedFogRow.On = enhancedFog;
            _volumetricFogRow.On = volumeFog;
            _internalHdrRow.On = hdr;
            _reflectionsRow.On = reflections;
            _dynamicGlowRow.On = glow;
            _lightingRow.On = true;
            _fogRow.On = true;
            _filteringRow.On = filtering;
            _mipmapRow.On = mipmaps;
            _anisotropyRow.Index = AnisotropyIndex(anisotropy);
            ShowTextureQualityRows();
            ShowModernGraphicsRows();
        }

        private void ShowModernGraphicsRows()
        {
            _bloomIntensityRow.IsVisible = _bloomRow.On;
            _volumetricFogRow.IsVisible = _enhancedFogRow.On;
        }

        private void ShowTextureQualityRows()
        {
            _mipmapRow.IsVisible = _filteringRow.On;
            _anisotropyRow.IsVisible = _filteringRow.On;
        }

        private void ShowCelRows()
        {
            _celBandsRow.IsVisible = _celRow.On;
            _celEdgeRow.IsVisible = _celRow.On;
        }

        private void ShowCrosshairRows()
        {
            _crosshairSizeRow.IsVisible = true;
            _crosshairStyleRow.IsVisible = _proHud.On;
            _weaponStyleRow.IsVisible = _proHud.On;
        }

        private void ShowRadarRows()
        {
            _radarBackgroundRow.IsVisible = _radarRow.On;
            _radarOutlinesRow.IsVisible = _radarRow.On;
        }

        // --------------------------------------------------------------- audio

        private void BuildAudio(StackPanel outer)
        {
            var general = new StackPanel { Spacing = 2 };
            var notifications = new StackPanel { Spacing = 2 };
            _audioPages.Clear();
            _audioNav.Clear();
            _audioPages.Add(general);
            _audioPages.Add(notifications);

            var subs = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                ColumnSpacing = 5,
                Margin = new Thickness(0, 0, 0, 8)
            };
            string[] names = { "GENERAL", "NOTIFICATIONS" };
            Color[] accents = { HubTheme.Good, HubTheme.Accent };
            for (int i = 0; i < names.Length; i++)
            {
                int at = i;
                var button = new HubNavButton(names[i], compact: true, accent: accents[i])
                {
                    MinHeight = 40
                };
                string id = $"settings.audio.{names[i].ToLowerInvariant()}";
                ControllerNav.Identify(button, id, initial: i == 0);
                button.Click += (_, _) => ShowAudioPage(at);
                Grid.SetColumn(button, i);
                subs.Children.Add(button);
                _audioNav.Add(button);
            }
            _audioNav[0].SetValue(ControllerNav.NavLeftProperty, "settings.audio.notifications");
            _audioNav[0].SetValue(ControllerNav.NavRightProperty, "settings.audio.notifications");
            _audioNav[1].SetValue(ControllerNav.NavLeftProperty, "settings.audio.general");
            _audioNav[1].SetValue(ControllerNav.NavRightProperty, "settings.audio.general");

            outer.Children.Add(subs);
            outer.Children.Add(general);
            outer.Children.Add(notifications);
            BuildAudioGeneral(general);
            BuildCombatNotifications(notifications);
            ShowAudioPage(0);
        }

        private void ShowAudioPage(int index)
        {
            if (_audioPages.Count == 0)
            {
                return;
            }
            _audioPageIndex = Math.Clamp(index, 0, _audioPages.Count - 1);
            for (int i = 0; i < _audioPages.Count; i++)
            {
                _audioPages[i].IsVisible = i == _audioPageIndex;
                if (i < _audioNav.Count)
                {
                    _audioNav[i].Selected = i == _audioPageIndex;
                }
            }
        }

        private void BuildAudioGeneral(StackPanel page)
        {
            Heading(page, "Volume");
            _sfxVolume = Add(page, new SliderRow("Sound effects",
                Percent(_settings.SfxVolume, 35)));
            _musicVolume = Add(page, new SliderRow("Music", Percent(_settings.MusicVolume, 50)));

            Heading(page, "Language");
            string[] languages = Enum.GetNames<Language>();
            _languageRow = Add(page, new ChoiceRow("Text", languages,
                Math.Max(0, Array.IndexOf(languages, _settings.Language))));
        }

        private void BuildCombatNotifications(StackPanel page)
        {
            Heading(page, "Combat notifications");
            _combatNotificationsVisible = Add(page, new ToggleRow("Visual notifications",
                LauncherPrefs.CombatNotificationsVisible));
            _combatFeedbackVolume = Add(page, new SliderRow("Notification volume",
                Math.Clamp((int)Math.Round(LauncherPrefs.CombatFeedbackVolume * 100), 0, 100)));
            Explain(page, "Rapid multi-kills use a 4-second window between confirmed kills. "
                + "Life-streak medals reset on death. Visual medals are local-only and do not "
                + "change scoring or network state.");

            Heading(page, "Match awards");
            AddCombatFeedbackCue(page, "First Blood (first match kill)",
                Mods.Sound.CombatFeedbackCue.FirstBlood);

            Heading(page, "Hit confirmation");
            AddCombatFeedbackCue(page, "Imperialist headshot",
                Mods.Sound.CombatFeedbackCue.ImperialistHeadshot);

            Heading(page, "Rapid multi-kills");
            AddCombatFeedbackCue(page, "Double Kill (2)",
                Mods.Sound.CombatFeedbackCue.DoubleKill);
            AddCombatFeedbackCue(page, "Triple Kill (3)",
                Mods.Sound.CombatFeedbackCue.TripleKill);
            AddCombatFeedbackCue(page, "Overkill (4)",
                Mods.Sound.CombatFeedbackCue.Overkill);
            AddCombatFeedbackCue(page, "Killtacular (5)",
                Mods.Sound.CombatFeedbackCue.Killtacular);
            AddCombatFeedbackCue(page, "Killtrocity (6)",
                Mods.Sound.CombatFeedbackCue.Killtrocity);
            AddCombatFeedbackCue(page, "Kilimanjaro (7)",
                Mods.Sound.CombatFeedbackCue.Kilimanjaro);
            AddCombatFeedbackCue(page, "Killtastrophe (8)",
                Mods.Sound.CombatFeedbackCue.Killtastrophe);
            AddCombatFeedbackCue(page, "Killpocalypse (9)",
                Mods.Sound.CombatFeedbackCue.Killpocalypse);
            AddCombatFeedbackCue(page, "Killionaire (10)",
                Mods.Sound.CombatFeedbackCue.Killionaire);

            Heading(page, "Kills in one life");
            AddCombatFeedbackCue(page, "Killing Spree (5)",
                Mods.Sound.CombatFeedbackCue.KillingSpree);
            AddCombatFeedbackCue(page, "Killing Frenzy (10)",
                Mods.Sound.CombatFeedbackCue.KillingFrenzy);
            AddCombatFeedbackCue(page, "Running Riot (15)",
                Mods.Sound.CombatFeedbackCue.RunningRiot);
            AddCombatFeedbackCue(page, "Rampage (20)",
                Mods.Sound.CombatFeedbackCue.Rampage);
            AddCombatFeedbackCue(page, "Untouchable (25)",
                Mods.Sound.CombatFeedbackCue.Untouchable);
            AddCombatFeedbackCue(page, "Invincible (30)",
                Mods.Sound.CombatFeedbackCue.Invincible);

            Explain(page, "Built-in cues ship inside Project Prime. Add WAV, MP3 or FLAC files to:\n"
                + Mods.Sound.CombatFeedbackAudio.CustomDirectory
                + "\nApply settings to refresh the custom-sound list for the next visit.");
        }

        private void AddCombatFeedbackCue(StackPanel page, string label,
            Mods.Sound.CombatFeedbackCue cue)
        {
            Mods.Sound.CombatFeedbackOption[] options =
                Mods.Sound.CombatFeedbackAudio.GetOptions(cue).ToArray();
            _combatFeedbackOptions[cue] = options;
            _combatFeedbackRows[cue] = Add(page, new ChoiceRow(label,
                options.Select(o => o.Label).ToArray(),
                CombatFeedbackIndex(options, Mods.Sound.CombatFeedbackAudio.GetSelection(cue), cue)));
        }

        private static int CombatFeedbackIndex(Mods.Sound.CombatFeedbackOption[] options,
            string selected, Mods.Sound.CombatFeedbackCue cue)
        {
            int index = Array.FindIndex(options, option =>
                option.Id.Equals(selected, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                return index;
            }
            string fallback = Mods.Sound.CombatFeedbackAudio.DefaultSelection(cue);
            index = Array.FindIndex(options, option =>
                option.Id.Equals(fallback, StringComparison.OrdinalIgnoreCase));
            return Math.Max(0, index);
        }

        private static string SelectedCombatFeedback(
            Mods.Sound.CombatFeedbackOption[] options, ChoiceRow row,
            Mods.Sound.CombatFeedbackCue cue)
        {
            if (options.Length == 0)
            {
                return Mods.Sound.CombatFeedbackAudio.DefaultSelection(cue);
            }
            return options[Math.Clamp(row.Index, 0, options.Length - 1)].Id;
        }

        private static int Percent(string stored, int fallback)
        {
            return Single.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture,
                out float parsed)
                ? Math.Clamp((int)Math.Round(parsed * 100), 0, 100)
                : fallback;
        }

        // ------------------------------------------------------------ controls

        /// <summary>
        /// Three devices, three focused pages. The old nested UiTabs strip was
        /// the last legacy navigation surface inside settings; these compact
        /// hub buttons now use the same focus/selection language as the outer
        /// Settings shell.
        /// </summary>
        private void BuildControls(StackPanel outer)
        {
            var keyboard = new StackPanel { Spacing = 2 };
            var gamepad = new StackPanel { Spacing = 2 };
            var stylus = new StackPanel { Spacing = 2 };
            _controlPages.Clear();
            _controlPages.Add(keyboard);
            _controlPages.Add(gamepad);
            _controlPages.Add(stylus);

            var subs = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*,*"),
                ColumnSpacing = 5,
                Margin = new Thickness(0, 0, 0, 8)
            };
            string[] names = { "KEYBOARD", "GAMEPAD", "STYLUS" };
            Color[] accents =
            {
                HubTheme.Accent,
                Color.FromRgb(0x86, 0xb8, 0xff),
                HubTheme.Warm
            };
            for (int i = 0; i < names.Length; i++)
            {
                int at = i;
                var button = new HubNavButton(names[i], compact: true, accent: accents[i])
                {
                    MinHeight = 40
                };
                string id = $"settings.controls.{names[i].ToLowerInvariant()}";
                ControllerNav.Identify(button, id, initial: i == 0);
                button.Click += (_, _) => ShowControlPage(at);
                Grid.SetColumn(button, i);
                subs.Children.Add(button);
                _controlNav.Add(button);
            }
            for (int i = 0; i < _controlNav.Count; i++)
            {
                string prev = names[(i + names.Length - 1) % names.Length].ToLowerInvariant();
                string next = names[(i + 1) % names.Length].ToLowerInvariant();
                _controlNav[i].SetValue(ControllerNav.NavLeftProperty,
                    $"settings.controls.{prev}");
                _controlNav[i].SetValue(ControllerNav.NavRightProperty,
                    $"settings.controls.{next}");
            }

            outer.Children.Add(subs);
            outer.Children.Add(keyboard);
            outer.Children.Add(gamepad);
            outer.Children.Add(stylus);
            BuildKeyboard(keyboard);
            BuildGamepad(gamepad);
            BuildStylus(stylus);
            ShowControlPage(0);
        }

        private void ShowControlPage(int index)
        {
            if (_controlPages.Count == 0)
            {
                return;
            }
            _controlPageIndex = Math.Clamp(index, 0, _controlPages.Count - 1);
            for (int i = 0; i < _controlPages.Count; i++)
            {
                _controlPages[i].IsVisible = i == _controlPageIndex;
                if (i < _controlNav.Count)
                {
                    _controlNav[i].Selected = i == _controlPageIndex;
                }
            }
        }

        private void BuildKeyboard(StackPanel page)
        {
            Heading(page, "Mouse");
            _sensitivity = Add(page, new SliderRow("Sensitivity",
                SensitivityToSlider(InputSettings.MouseSensitivity),
                v => $"{SliderToSensitivity(v).ToString("0.00", CultureInfo.InvariantCulture)}x",
                min: 1, max: 300, keyStep: 1));
            _imperialistZoomSensitivity = Add(page, new SliderRow("Imperialist zoom sensitivity",
                SensitivityToSlider(InputSettings.ImperialistZoomSensitivity),
                v => $"{SliderToSensitivity(v).ToString("0.00", CultureInfo.InvariantCulture)}x",
                min: (int)(InputSettings.MinImperialistZoomSensitivity * 100),
                max: (int)(InputSettings.MaxImperialistZoomSensitivity * 100), keyStep: 1));
            _imperialistZoomAmount = Add(page, new SliderRow("Imperialist zoom amount",
                (int)MathF.Round(InputSettings.ImperialistZoomAmount * 100),
                v => $"{v}%",
                min: (int)(InputSettings.MinImperialistZoomAmount * 100),
                max: (int)(InputSettings.MaxImperialistZoomAmount * 100), keyStep: 5));
            Explain(page, "Imperialist zoom sensitivity changes mouse speed while scoped. "
                + "Zoom amount changes magnification: 100% is the original scope, lower values zoom less, "
                + "and higher values zoom more.");
            _invertY = Add(page, new ToggleRow("Invert vertical aim", InputSettings.InvertMouseY));
            _invertX = Add(page, new ToggleRow("Invert horizontal aim", InputSettings.InvertMouseX));
            if (OperatingSystem.IsAndroid())
            {
                Heading(page, "Touch gestures");
                BuildAltSwipeSensitivity(page);
            }
            else
            {
                Heading(page, "Alt-form gestures");
                _mouseAltFormMovement = Add(page, new ToggleRow(
                    "Mouse movement controls rolling alt forms",
                    InputSettings.MouseAltFormMovement));
                BuildAltSwipeSensitivity(page);
            }

            var advanced = new StackPanel { Spacing = 2, IsVisible = false };
            _scrollAllWeapons = Add(advanced, new ToggleRow("Wheel cycles every weapon",
                InputSettings.ScrollAllWeapons));
            if (!OperatingSystem.IsAndroid())
            {
                _mouseMovementBoost = Add(advanced, new ToggleRow(
                    "Mouse movement can trigger morph boost", InputSettings.MouseMovementBoost));
                advanced.Children.Add(new Note(
                    "Turn this off if fast mouse movement should never boost Samus. "
                    + "Right-click/Zoom and the normal Boost binding still work."));
            }
            BuildTouchControls(advanced);
            AddAdvancedToggle(page, advanced, "keyboard.advanced");

            Heading(page, "Keys");
            var rows = new List<KeyRow>();
            rows.Add(Add(page, new KeyRow("Chat",
                () => InputSettings.ChatKey, k => InputSettings.ChatKey = k)));
            rows.Add(Add(page, new KeyRow("Save clip",
                () => InputSettings.ClipKey, k => InputSettings.ClipKey = k)));
            foreach (PropertyInfo property in InputSettings.Bindings)
            {
                rows.Add(Add(page, new KeyRow(property)));
            }
            _keyRows = rows;
        }

        // -------------------------------------------------------------- replays

        private ChoiceRow? _killCameraRow;
        private void BuildReplays(StackPanel page)
        {
            Heading(page, "Instant clips");
            Explain(page, "Save the moments around the clip key without recording a full match. "
                + "The rolling buffer stays in memory and only writes when you ask for a clip.");
            _clipSecondsRow = Add(page, new ChoiceRow("Clip length",
                Array.ConvertAll(Mods.Network.DemoClip.Lengths, n => $"{n} seconds"),
                Math.Max(0, Array.IndexOf(Mods.Network.DemoClip.Lengths,
                    Mods.Network.DemoClip.Seconds))));
            _clipPostRollRow = Add(page, new ChoiceRow("Clip post-roll",
                Array.ConvertAll(Mods.Network.DemoClip.PostRollLengths, n => $"{n} seconds"),
                Math.Max(0, Array.IndexOf(Mods.Network.DemoClip.PostRollLengths,
                    Mods.Network.DemoClip.PostRollSeconds))));

            Heading(page, "Kill cams");
            _killCamRow = Add(page, new ToggleRow("Kill cam after death",
                LauncherPrefs.KillCamEnabled));
            _finalKillCamRow = Add(page, new ToggleRow("Final kill cam",
                LauncherPrefs.FinalKillCamEnabled));
            _killCameraRow = Add(page, new ChoiceRow("Kill cam camera", new[] { "Chase", "First person", "Cinematic" }, LauncherPrefs.KillCamCamera));
            Explain(page, "Kill cams show 3.5 seconds before the confirmed kill and 1.5 seconds after it; "
                + "they never rewind the live network simulation. Release Fire, then press it again "
                + "to skip your personal kill cam.");

            Heading(page, "Replay library");
            Explain(page, "Full recordings, instant clips and recovered sessions appear in "
                + "REPLAY STUDIO on the main screen. Files are stored in:\n"
                + Mods.Network.DemoLibrary.Directory);

            int storageIndex = Array.FindIndex(_replayStorageStops,
                value => value == LauncherPrefs.ReplayStorageLimitGb);
            if (storageIndex < 0) storageIndex = 2;
            _replayStorageRow = Add(page, new ChoiceRow("Storage limit",
                Array.ConvertAll(_replayStorageStops,
                    value => value == 0 ? "Unlimited" : $"{value} GB"),
                storageIndex));
            _replayAutoPruneRow = Add(page, new ToggleRow("Auto-manage storage",
                LauncherPrefs.ReplayAutoPrune));
            _replayDeleteClipsRow = Add(page, new ToggleRow("Allow old clips to be pruned",
                LauncherPrefs.ReplayDeleteClips));
            Explain(page, "When the limit is reached, the oldest full-match recordings are "
                + "removed first. Favorites are always protected. Clips remain protected unless "
                + "you explicitly allow them to be pruned.");

            Heading(page, "Spectating");
            _spectatorNameTagsRow = Add(page, new ToggleRow("Free-camera player names",
                LauncherPrefs.SpectatorNameTags));
            Explain(page, "Shows player names above active hunters only while using the free camera. "
                + "Normal gameplay and player POV cameras are unchanged.");

            Heading(page, "Replay keyboard");
            Explain(page, "These keys control replay playback directly while the match is on screen.");
            _keyRows.Add(Add(page, new KeyRow("Play / pause",
                () => InputSettings.ReplayPlayPauseKey, k => InputSettings.ReplayPlayPauseKey = k)));
            _keyRows.Add(Add(page, new KeyRow("Step backward",
                () => InputSettings.ReplayStepBackKey, k => InputSettings.ReplayStepBackKey = k)));
            _keyRows.Add(Add(page, new KeyRow("Step forward",
                () => InputSettings.ReplayStepForwardKey, k => InputSettings.ReplayStepForwardKey = k)));
            _keyRows.Add(Add(page, new KeyRow("Seek back 5 seconds",
                () => InputSettings.ReplaySeekBackKey, k => InputSettings.ReplaySeekBackKey = k)));
            _keyRows.Add(Add(page, new KeyRow("Seek forward 5 seconds",
                () => InputSettings.ReplaySeekForwardKey, k => InputSettings.ReplaySeekForwardKey = k)));
            _keyRows.Add(Add(page, new KeyRow("Slower",
                () => InputSettings.ReplaySlowerKey, k => InputSettings.ReplaySlowerKey = k)));
            _keyRows.Add(Add(page, new KeyRow("Faster",
                () => InputSettings.ReplayFasterKey, k => InputSettings.ReplayFasterKey = k)));
            _keyRows.Add(Add(page, new KeyRow("Restart replay",
                () => InputSettings.ReplayRestartKey, k => InputSettings.ReplayRestartKey = k)));

            Heading(page, "Replay controller");
            Explain(page, "Replay controller bindings are separate from gameplay bindings, so A can "
                + "play/pause here while still being Jump during a match.");
            foreach (Mods.Input.PadAction action in Mods.Input.PadBindings.ReplayActions)
                _replayPadRows.Add(Add(page, new PadRow(action)));

            var resetReplay = new HubNavButton("RESET REPLAY CONTROLS",
                compact: true, accent: HubTheme.Warm)
            {
                Width = 230,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 10, 0, 0)
            };
            ControllerNav.Identify(resetReplay, "replay.controls.reset");
            resetReplay.Click += (_, _) =>
            {
                InputSettings.ResetReplayBindings();
                foreach (KeyRow row in _keyRows) row.InvalidateVisual();
                foreach (PadRow row in _replayPadRows) row.InvalidateVisual();
            };
            page.Children.Add(resetReplay);
        }

        /// <summary>Every key row, so Reset can redraw them from whichever page it is on.</summary>
        private List<KeyRow> _keyRows = new();
        private readonly List<PadRow> _replayPadRows = new();

        /// <summary>The pen tablet page keeps common setup visible and hides tuning.</summary>
        private void BuildStylus(StackPanel page)
        {
            Heading(page, "Pen tablet");
            _penTablet = Add(page, new ToggleRow("Stylus mode", Mods.Input.PointerInput.StylusMode));
            BuildStylusZone(page);
            _penTablet.Changed += (_, _) => ShowStylusRows();
            ShowStylusRows();
        }

        private void BuildGamepad(StackPanel page)
        {
            Heading(page, "Controller");
            _gamepadSettings = Add(page, new GamepadSettingsPanel());

            Heading(page, "Controller buttons");
            var padRows = new List<PadRow>();
            foreach (Mods.Input.PadAction action in Mods.Input.PadBindings.GameplayActions)
            {
                padRows.Add(Add(page, new PadRow(action)));
            }

            var reset = new HubNavButton("RESET TO DEFAULTS",
                compact: true, accent: HubTheme.Warm)
            {
                Width = 200,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 10, 0, 0)
            };
            ControllerNav.Identify(reset, "controller.reset");
            reset.Click += (_, _) =>
            {
                InputSettings.Reset();
                _sensitivity.Value = SensitivityToSlider(InputSettings.MouseSensitivity);
                _imperialistZoomSensitivity.Value = SensitivityToSlider(InputSettings.ImperialistZoomSensitivity);
                _imperialistZoomAmount.Value = (int)MathF.Round(InputSettings.ImperialistZoomAmount * 100);
                _altSwipeSensitivity.Value = (int)MathF.Round(InputSettings.AltSwipeSensitivity * 100);
                _invertY.On = InputSettings.InvertMouseY;
                _invertX.On = InputSettings.InvertMouseX;
                if (_mouseMovementBoost != null) _mouseMovementBoost.On = InputSettings.MouseMovementBoost;
                if (_mouseAltFormMovement != null) _mouseAltFormMovement.On = InputSettings.MouseAltFormMovement;
                if (_stylusMovementBoost != null) _stylusMovementBoost.On = InputSettings.StylusMovementBoost;
                _penTablet.On = Mods.Input.PointerInput.StylusMode;
                if (_repositionFilter != null) _repositionFilter.On = Mods.Input.PointerInput.GuardJumps;
                if (_stylusZone != null)
                {
                    _stylusZone.On = Mods.Input.StylusZone.Wanted;
                }
                if (_stylusNativeUi != null)
                    _stylusNativeUi.On = Mods.Input.StylusZone.NativeUi;
                if (_stylusNativeUiOpacity != null)
                    _stylusNativeUiOpacity.Value = (int)MathF.Round(Mods.Input.StylusZone.NativeUiOpacity * 100);
                if (_stylusCursorOpacity != null)
                    _stylusCursorOpacity.Value = (int)MathF.Round(Mods.Input.StylusZone.CursorOpacity * 100);
                if (_stylusOutlineOpacity != null)
                    _stylusOutlineOpacity.Value = (int)MathF.Round(Mods.Input.StylusZone.OutlineOpacity * 100);
                if (_stylusButtonOpacity != null)
                    _stylusButtonOpacity.Value = (int)MathF.Round(Mods.Input.StylusZone.ButtonOpacity * 100);
                ShowStylusRows();
                _scrollAllWeapons.On = InputSettings.ScrollAllWeapons;
                _gamepadSettings.Reload();
                foreach (PadRow row in padRows) row.InvalidateVisual();
                foreach (PadRow row in _replayPadRows) row.InvalidateVisual();
                foreach (KeyRow row in _keyRows) row.InvalidateVisual();
                if (_touchButtonsRow != null) _touchButtonsRow.On = Mods.Input.TouchSettings.ButtonsVisible;
                foreach ((Mods.Input.TouchControl control, ToggleRow row) in _touchRows)
                    row.On = Mods.Input.TouchSettings.IsEnabled(control);
            };
            page.Children.Add(reset);
        }

        private ToggleRow? _touchButtonsRow;

        private readonly List<(Mods.Input.TouchControl Control, ToggleRow Row)> _touchRows = new();

        private ToggleRow? _stylusZone;
        private ToggleRow? _stylusNativeUi;
        private SliderRow? _stylusNativeUiOpacity;
        private SliderRow? _stylusCursorOpacity;
        private SliderRow? _stylusOutlineOpacity;
        private SliderRow? _stylusButtonOpacity;
        private readonly List<Control> _stylusRows = new();

        private void ShowStylusRows()
        {
            bool enabled = _penTablet.On;
            foreach (Control row in _stylusRows) row.IsVisible = enabled;
            if (_stylusAdvanced != null)
                _stylusAdvanced.IsVisible = enabled && _stylusAdvancedOpen;
        }

        private void BuildStylusZone(StackPanel page)
        {
            if (OperatingSystem.IsAndroid()) return;

            _stylusZone = Add(page, new ToggleRow("DS touch-screen zone", Mods.Input.StylusZone.Wanted));
            _stylusRows.Add(_stylusZone);

            var place = new HubNavButton("CONFIGURE STYLUS ZONE",
                compact: true, accent: HubTheme.Accent)
            {
                Width = 220,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0)
            };
            ControllerNav.Identify(place, "stylus.configure_zone");
            place.Click += (_, _) =>
            {
                Mods.Input.StylusZone.BeginPlacement();
                StylusPlacementRequested?.Invoke(this, EventArgs.Empty);
            };
            page.Children.Add(place);
            _stylusRows.Add(place);

            _stylusAdvanced = new StackPanel { Spacing = 2, IsVisible = false };
            _repositionFilter = Add(_stylusAdvanced,
                new ToggleRow("Reposition filtering", Mods.Input.PointerInput.GuardJumps));
            _stylusMovementBoost = Add(_stylusAdvanced, new ToggleRow(
                "Stylus movement can trigger morph boost", InputSettings.StylusMovementBoost));
            _stylusNativeUi = Add(_stylusAdvanced, new ToggleRow(
                "Native bottom-screen UI", Mods.Input.StylusZone.NativeUi));
            _stylusNativeUiOpacity = Add(_stylusAdvanced, new SliderRow("Native UI opacity",
                (int)MathF.Round(Mods.Input.StylusZone.NativeUiOpacity * 100),
                v => $"{v}%", min: 0, max: 100, keyStep: 5));
            _stylusCursorOpacity = Add(_stylusAdvanced, new SliderRow("Cursor opacity",
                (int)MathF.Round(Mods.Input.StylusZone.CursorOpacity * 100),
                v => $"{v}%", min: 0, max: 100, keyStep: 5));
            _stylusOutlineOpacity = Add(_stylusAdvanced, new SliderRow("Guide rectangle opacity",
                (int)MathF.Round(Mods.Input.StylusZone.OutlineOpacity * 100),
                v => $"{v}%", min: 0, max: 100, keyStep: 5));
            _stylusButtonOpacity = Add(_stylusAdvanced, new SliderRow("Guide button opacity",
                (int)MathF.Round(Mods.Input.StylusZone.ButtonOpacity * 100),
                v => $"{v}%", min: 0, max: 100, keyStep: 5));
            _stylusAdvanced.Children.Add(new Note(
                "Native UI draws the hunter's original DS lower screen and automatically swaps "
                + "to the alt-form and weapon-select artwork. The guide rectangle and circular "
                + "buttons are used only when native art is off or unavailable; placement always "
                + "keeps a visible guide. Cursor opacity remains independent. Stylus movement "
                + "boost controls pen-motion morph boosts, while reposition filtering ignores "
                + "tablet jumps after lift/re-contact."));
            _stylusAdvancedButton = new HubNavButton("ADVANCED", compact: true)
            {
                Width = 170,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 4)
            };
            ControllerNav.Identify(_stylusAdvancedButton, "stylus.advanced");
            _stylusAdvancedButton.Click += (_, _) =>
            {
                _stylusAdvancedOpen = !_stylusAdvancedOpen;
                ShowStylusRows();
            };
            page.Children.Add(_stylusAdvancedButton);
            page.Children.Add(_stylusAdvanced);
            _stylusRows.Add(_stylusAdvancedButton);
        }

        /// <summary>
        /// The settings asking to be closed so the player can draw on the
        /// game. Raised by the one button above, which is built on the
        /// desktop only, so SettingsWindow closing itself is the whole of
        /// what answering this means.
        /// </summary>
        public event EventHandler? StylusPlacementRequested;

        /// <summary>
        /// Which on-screen buttons the phone draws.
        ///
        /// Only on a touch screen: on the desktop these decide nothing, and a
        /// page of eleven switches that do nothing is worse than no page. The
        /// master switch is first and takes the rest away with it, since
        /// "turn them all off" is the answer most people who come here want
        /// and it should not be eleven presses.
        /// </summary>
        private void BuildTouchControls(StackPanel page)
        {
            if (!OperatingSystem.IsAndroid())
            {
                return;
            }
            Heading(page, "On-screen buttons");
            _touchButtonsRow = Add(page, new ToggleRow("Show on-screen buttons",
                Mods.Input.TouchSettings.ButtonsVisible));
            Add(page, new Note("The stick, aiming, the double tap that jumps and the flick "
                + "that boosts are not buttons, so they keep working with every one of these off."));
            foreach ((Mods.Input.TouchControl control, string label) in Mods.Input.TouchSettings.Order)
            {
                ToggleRow row = Add(page, new ToggleRow(label,
                    Mods.Input.TouchSettings.IsEnabled(control)));
                _touchRows.Add((control, row));
            }
            void ShowTouchRows()
            {
                foreach ((_, ToggleRow row) in _touchRows)
                {
                    row.IsVisible = _touchButtonsRow.On;
                }
            }
            _touchButtonsRow.Changed += (_, _) => ShowTouchRows();
            ShowTouchRows();
        }

        private void BuildAltSwipeSensitivity(StackPanel page)
        {
            _altSwipeSensitivity = Add(page, new SliderRow("Alt swipe sensitivity",
                Math.Clamp((int)MathF.Round(InputSettings.AltSwipeSensitivity * 100),
                    (int)(InputSettings.MinAltSwipeSensitivity * 100),
                    (int)(InputSettings.MaxAltSwipeSensitivity * 100)),
                value => $"{(value / 100f).ToString("0.00", CultureInfo.InvariantCulture)}x",
                min: (int)(InputSettings.MinAltSwipeSensitivity * 100),
                max: (int)(InputSettings.MaxAltSwipeSensitivity * 100), keyStep: 5));
            Explain(page, "Controls alt-form swipe response. Higher values need less travel. "
                + "Touch/pen rolling movement reaches full deflection sooner; when mouse alt-form "
                + "movement is enabled, relative mouse steering responds more strongly too. "
                + "Desktop mouse/pen Samus boost and Spire attack flicks use the same multiplier. "
                + "Normal mouse aim sensitivity is unchanged.");
        }

        // Direct hundredths of the sensitivity itself, not an index over a
        // range: 1-300 covers 0.01x-3.00x with every integer step worth
        // exactly 0.01, on the keyboard and under the pointer alike.
        private static int SensitivityToSlider(float sensitivity)
        {
            return Math.Clamp((int)Math.Round(sensitivity * 100), 1, 300);
        }

        private static float SliderToSensitivity(int value)
        {
            return value / 100f;
        }

        // The pad's look runs 0.25x to 3x, which is 50 to 630 degrees a second
        // -- slower than anybody plays at one end and faster at the other.
        private static int LookToSlider(float look)
        {
            return Math.Clamp((int)Math.Round((look - 0.25f) / 2.75f * 100), 0, 100);
        }

        private static float SliderToLook(int value)
        {
            return 0.25f + value / 100f * 2.75f;
        }

        // Up to half the stick's travel. Past that a pad is broken rather than
        // worn, and a dead zone that large makes the game feel worse than the
        // drift it was hiding.
        private static int DeadZoneToSlider(float dead)
        {
            return Math.Clamp((int)Math.Round(dead / 0.5f * 100), 0, 100);
        }

        private static float SliderToDeadZone(int value)
        {
            return value / 100f * 0.5f;
        }

        /// <summary>
        /// Who you are and where you play: the launcher's own preferences,
        /// which live in launcher.txt rather than in the game's settings.json.
        ///
        /// They were on a card of the front screen while this window was
        /// Windows-only and the other platforms had nothing else. They belong
        /// here: the front screen asks the questions a session needs answering
        /// now, and a default server address is not one of them.
        /// </summary>
        private void BuildLauncher(StackPanel page)
        {
            Heading(page, "You");
            _playerName = Add(page, new FieldRow("Your name", LauncherPrefs.PlayerName,
                boxWidth: 200));
            // The seven playable hunters and Random, the same list the front
            // screen offers -- not every name in the enum, which also holds the
            // Guardian and the enemies' entries.
            string[] hunters = Enumerable.Range(0, 7)
                .Select(i => ((Hunter)i).ToString())
                .Append(Hunter.Random.ToString()).ToArray();
            _hunterRow = Add(page, new ChoiceRow("Hunter", hunters,
                Math.Max(0, Array.IndexOf(hunters, LauncherPrefs.LastHunter.ToString()))));
            // Every hunter model carries four suits, and until now the game
            // used the first for everybody. Numbered rather than named: each
            // hunter's four are their own colours, so "2" is the only label
            // that means the same thing on all seven. See PlayerColors, which
            // also moves two players off the same suit when they turn up on
            // the same hunter.
            string[] colors = Enumerable.Range(1, Mods.Network.PlayerColors.Count)
                .Select(i => i.ToString()).ToArray();
            _colorRow = Add(page, new ChoiceRow("Suit colour", colors,
                Mods.Network.PlayerColors.Clamp(LauncherPrefs.LastColor)));

            Heading(page, "Servers");
            _serverRow = Add(page, new FieldRow("Default server",
                $"{LauncherPrefs.ServerAddress}:{LauncherPrefs.ServerPort}", boxWidth: 220));
            _masterRow = Add(page, new FieldRow("Server directory",
                $"{LauncherPrefs.MasterHost}:{LauncherPrefs.MasterPort}", boxWidth: 220));
            _autoUpdate = Add(page, new ToggleRow("Check for updates automatically",
                LauncherPrefs.AutoUpdate));

            Heading(page, "Game files");
            var files = new UiWord("Game files", 15);
            files.Click += (_, _) =>
            {
                GameFilesRequested?.Invoke(this, EventArgs.Empty);
                Close();
            };
            page.Children.Add(files);
            page.Children.Add(new Note(GameFiles.Describe(),
                GameFiles.Ready ? GuiTheme.Good : GuiTheme.Warm));

            BuildDebugLogs(page);

            // Nothing behind it yet, and the row says so when pressed rather
            // than being absent until the day there is: a player who wonders
            // whether their times count anywhere gets an answer either way,
            // and "coming soon" is an answer.
            // The hunter you start as, and the hunter itself. A name in a
            // drop-down is not what anybody recognises a hunter by; the
            // silhouette is. The picker on the results screen is the one that
            // matters mid-session -- this is the one that answers "who am I by
            // default", which is a settings question.
            Heading(page, "Hunter");
            string[] standNames = HunterStand.Names;
            var hunterRow = Add(page, new ChoiceRow("Default hunter", standNames,
                Math.Max(0, Array.IndexOf(standNames, LauncherPrefs.LastHunter.ToString()))));
            var stand = new HunterStand
            {
                Height = 150,
                Margin = new Thickness(0, 4, 0, 4),
                Name2 = standNames[hunterRow.Index]
            };
            hunterRow.Changed += (_, _) => stand.Name2 = standNames[hunterRow.Index];
            page.Children.Add(stand);

            Heading(page, "Account");
            var signIn = new UiWord("Sign in", 15, colour: GuiTheme.Accent);
            var signInNote = new Note("") { IsVisible = false };
            signIn.Click += (_, _) =>
            {
                signInNote.Text = "Ranking feature will be coming soon!";
                signInNote.IsVisible = true;
            };
            page.Children.Add(signIn);
            page.Children.Add(signInNote);
        }

        /// <summary>
        /// The switch that turns the log file on.
        ///
        /// It used to be the smallest thing on the front screen, in the corner
        /// under the version. It is not something anybody came to the launcher
        /// for: it is what somebody is asked to turn on when they report a
        /// crash nobody else can reproduce, and it costs a growing directory
        /// and a lock on every line the program prints. The bottom of the page
        /// about this copy of the program is where that belongs -- findable
        /// when described over a chat window, and out of the way the rest of
        /// the time.
        ///
        /// It says where the file went once it is on, because "turn on logging
        /// and send me the file" has a second half.
        /// </summary>
        private void BuildDebugLogs(StackPanel page)
        {
            Heading(page, "Debugging");
            var row = new ToggleRow("Write a debugging log", LauncherPrefs.DebugLogs);
            var where = new Note("");
            row.Changed += (_, _) =>
            {
                LauncherPrefs.DebugLogs = row.On;
                LauncherPrefs.Save();
                if (row.On)
                {
                    // Straight away, so the run that is about to crash is the
                    // run in the file -- being asked to restart first is where
                    // a report like this is usually lost.
                    DebugLog.Attach();
                    DebugLog.Line("launcher", "debug logging turned on from the settings");
                }
                else
                {
                    DebugLog.Line("launcher", "debug logging turned off from the settings");
                    DebugLog.Detach();
                }
                where.Text = LogLocation();
                _shareLogs!.IsVisible = LogShare.Available;
            };
            where.Text = LogLocation();
            page.Children.Add(row);
            page.Children.Add(where);
            // Only where something can receive a file, which today is Android
            // alone -- the app's own directory being one no file manager will
            // browse.
            _shareLogs = new UiWord("\u2197 Share logs", 15, colour: GuiTheme.TextDim)
            {
                IsVisible = LogShare.Available
            };
            _shareLogs.Click += async (_, _) => await ShareLogs();
            _shareError = new Note("", GuiTheme.Warm) { IsVisible = false };
            page.Children.Add(_shareLogs);
            page.Children.Add(_shareError);
        }

        private UiWord? _shareLogs;
        private Note? _shareError;
        private bool _sharing;

        private static string LogLocation()
        {
            if (!LauncherPrefs.DebugLogs)
            {
                return "Everything this build can say about itself, written to a file "
                    + "for a bug report.";
            }
            return DebugLog.Path is string path
                ? $"Writing to {path}"
                : "Logging starts with the next run.";
        }

        /// <summary>
        /// Zip the logs and hand them over.
        ///
        /// Off the UI thread, because it reads and compresses however many
        /// files <c>DebugLog</c> is keeping. The chooser itself goes back on
        /// the UI thread: on Android it is an activity. Everything it can say,
        /// it says on the row -- a control that greys out and reports nothing
        /// is one people press again.
        /// </summary>
        private async Task ShareLogs()
        {
            if (_sharing || _shareLogs == null || LogShare.Current is not ILogShare sharer)
            {
                return;
            }
            _sharing = true;
            _shareLogs.Text = "\u2197 Zipping\u2026";
            string name = LogArchive.FileName();
            string path = "";
            string error = "";
            bool built = await Task.Run(() =>
            {
                try
                {
                    path = sharer.StagingPath(name);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
                return LogArchive.Create(path, out error);
            });
            if (built)
            {
                built = sharer.Share(path, name, out error);
            }
            _sharing = false;
            _shareLogs.Text = "\u2197 Share logs";
            _shareError!.Text = error;
            _shareError.IsVisible = !built;
        }

        /// <summary>host, or host:port. Leaves both alone on anything else, so a
        /// typo does not silently change the address.</summary>
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

        // -------------------------------------------------------------- saving

        /// <summary>
        /// Save, and say so on the window if it does not work.
        ///
        /// Writing settings.json touches the disk, and the disk is allowed to
        /// say no -- a read-only folder, a file open elsewhere, a full drive.
        /// That is worth a line on the screen, not an exception out of a window
        /// that may be sitting over a match still being played.
        /// </summary>
        private void TryCommit()
        {
            try
            {
                Commit();
            }
            catch (Exception ex)
            {
                _saveError.Text = $"Could not save: {ex.Message}";
                _saveError.IsVisible = true;
            }
        }

        private void Commit()
        {
            // Display
            if (_windowRow != null)
            {
                LauncherPrefs.WindowMode = (WindowStartMode)_windowRow.Index;
                WindowMode.Startup = LauncherPrefs.WindowMode;
                // Apply the selected mode, not a boolean toggle: Borderless ->
                // Fullscreen changes focus policy without leaving fullscreen.
                PauseMenu.RequestWindowMode(LauncherPrefs.WindowMode);
            }
            if (_clipSecondsRow != null)
            {
                Mods.Network.DemoClip.Seconds = Mods.Network.DemoClip.Lengths[
                    Math.Clamp(_clipSecondsRow.Index, 0, Mods.Network.DemoClip.Lengths.Length - 1)];
            }
            _settings.ResolutionScale = Math.Clamp(_resolutionScale.Value,
                RenderOptions.MinScale, RenderOptions.MaxScale)
                .ToString(CultureInfo.InvariantCulture);
            RenderOptions.FieldOfView = _fovRow.Value;
            _settings.FieldOfView = _fovRow.Value.ToString(CultureInfo.InvariantCulture);
            _settings.Lighting = RenderOptions.OnOff(_lightingRow.On);
            _settings.Fog = RenderOptions.OnOff(_fogRow.On);
            _settings.TextureFiltering = RenderOptions.OnOff(_filteringRow.On);
            _settings.TextureMipmaps = RenderOptions.OnOff(_mipmapRow.On);
            _settings.TextureAnisotropy = _anisotropyStops[
                Math.Clamp(_anisotropyRow.Index, 0, _anisotropyStops.Length - 1)]
                .ToString(CultureInfo.InvariantCulture);
            _settings.ShowFps = RenderOptions.OnOff(_fpsRow.On);
            RenderOptions.SmoothNativeHud = _smoothNativeHud.On;
            _settings.SmoothNativeHud = RenderOptions.OnOff(_smoothNativeHud.On);
            int cap = _fpsLimitStops[Math.Clamp(_fpsLimitRow.Value, 0,
                _fpsLimitStops.Length - 1)].Cap;
            FrameTiming.FrameRateCap = cap;
            _settings.FrameRateCap = FrameTiming.CapString(cap);
            _settings.CelShading = RenderOptions.OnOff(_celRow.On);
            _settings.CelBands = Math.Clamp(_celBandsRow.Value, 2, 8)
                .ToString(CultureInfo.InvariantCulture);
            _settings.CelEdge = Math.Clamp(_celEdgeRow.Value, 0, 100)
                .ToString(CultureInfo.InvariantCulture);
            _settings.GraphicsPreset = ((GraphicsPreset)Math.Clamp(_graphicsPresetRow.Index, 0, 5))
                .ToString().ToLowerInvariant();
            _settings.AntiAliasing = ((AntiAliasingMode)Math.Clamp(_antiAliasingRow.Index, 0, 2))
                .ToString().ToLowerInvariant();
            _settings.SharpenStrength = _sharpenRow.Value.ToString(CultureInfo.InvariantCulture);
            _settings.Bloom = RenderOptions.OnOff(_bloomRow.On);
            _settings.BloomIntensity = _bloomIntensityRow.Value.ToString(CultureInfo.InvariantCulture);
            _settings.ColorGrade = ((ColorGradeProfile)Math.Clamp(_colorGradeRow.Index, 0, 3))
                .ToString().ToLowerInvariant();
            _settings.Gamma = _gammaRow.Value.ToString(CultureInfo.InvariantCulture);
            _settings.Contrast = _contrastRow.Value.ToString(CultureInfo.InvariantCulture);
            _settings.Saturation = _saturationRow.Value.ToString(CultureInfo.InvariantCulture);
            _settings.EnhancedLighting = RenderOptions.OnOff(_enhancedLightingRow.On);
            _settings.AmbientOcclusion = ((AmbientOcclusionQuality)Math.Clamp(_ambientOcclusionRow.Index, 0, 3))
                .ToString().ToLowerInvariant();
            _settings.ContactShadows = RenderOptions.OnOff(_contactShadowsRow.On);
            _settings.EnhancedFog = RenderOptions.OnOff(_enhancedFogRow.On);
            _settings.VolumetricFog = RenderOptions.OnOff(_volumetricFogRow.On);
            _settings.InternalHdr = RenderOptions.OnOff(_internalHdrRow.On);
            _settings.Reflections = RenderOptions.OnOff(_reflectionsRow.On);
            _settings.DynamicGlow = RenderOptions.OnOff(_dynamicGlowRow.On);
            _settings.TextureReplacements = RenderOptions.OnOff(_textureReplacementsRow.On);
            RenderOptions.BrightSkins = _brightSkinsRow.Index != 0;
            if (RenderOptions.BrightSkins)
            {
                RenderOptions.BrightSkinStyle = _brightSkinsRow.Index switch
                {
                    1 => PlayerSkinStyle.Textured,
                    2 => PlayerSkinStyle.HighContrastTextured,
                    _ => PlayerSkinStyle.Solid
                };
            }
            RenderOptions.PlayerOutline = (PlayerOutlineStyle)_playerOutlineRow.Index;
            RenderOptions.PlayerOutlineWidth = _playerOutlineWidthRow.Value;
            Features.ProHud = _proHud.On;
            Features.KillFeedEnabled = _killFeed.On;
            Crosshair.Size = (CrosshairSize)_crosshairSizeRow.Index;
            Crosshair.Style = (CrosshairStyle)_crosshairStyleRow.Index;
            Features.ProHudFixedWeapon = _weaponStyleRow.Index == 0;
            Radar.Enabled = _radarRow.On;
            Radar.ShowBackground = _radarBackgroundRow.On;
            Radar.ShowOutlines = _radarOutlinesRow.On;
            // Audio
            _settings.SfxVolume = (_sfxVolume.Value / 100f).ToString(CultureInfo.InvariantCulture);
            _settings.MusicVolume = (_musicVolume.Value / 100f).ToString(CultureInfo.InvariantCulture);
            _settings.Language = _languageRow.Value;
            LauncherPrefs.CombatNotificationsVisible = _combatNotificationsVisible.On;
            LauncherPrefs.CombatFeedbackVolume = Math.Clamp(
                _combatFeedbackVolume.Value / 100f, 0, 1);
            foreach ((Mods.Sound.CombatFeedbackCue cue, ChoiceRow row) in _combatFeedbackRows)
            {
                Mods.Sound.CombatFeedbackOption[] options = _combatFeedbackOptions[cue];
                Mods.Sound.CombatFeedbackAudio.SetSelection(cue,
                    SelectedCombatFeedback(options, row, cue));
            }
            // Controls
            InputSettings.MouseSensitivity = SliderToSensitivity(_sensitivity.Value);
            InputSettings.ImperialistZoomSensitivity = SliderToSensitivity(_imperialistZoomSensitivity.Value);
            InputSettings.ImperialistZoomAmount = _imperialistZoomAmount.Value / 100f;
            InputSettings.AltSwipeSensitivity = _altSwipeSensitivity.Value / 100f;
            InputSettings.InvertMouseY = _invertY.On;
            InputSettings.InvertMouseX = _invertX.On;
            if (_mouseMovementBoost != null)
                InputSettings.MouseMovementBoost = _mouseMovementBoost.On;
            if (_mouseAltFormMovement != null)
                InputSettings.MouseAltFormMovement = _mouseAltFormMovement.On;
            if (_stylusMovementBoost != null)
                InputSettings.StylusMovementBoost = _stylusMovementBoost.On;
            Mods.Input.PointerInput.StylusMode = _penTablet.On;
            if (_repositionFilter != null)
                Mods.Input.PointerInput.GuardJumps = _repositionFilter.On;
            if (_stylusZone != null)
            {
                Mods.Input.StylusZone.Enabled = _stylusZone.On;
            }
            if (_stylusNativeUi != null)
                Mods.Input.StylusZone.NativeUi = _stylusNativeUi.On;
            if (_stylusNativeUiOpacity != null)
                Mods.Input.StylusZone.NativeUiOpacity = Math.Clamp(_stylusNativeUiOpacity.Value / 100f, 0, 1);
            if (_stylusCursorOpacity != null)
                Mods.Input.StylusZone.CursorOpacity = Math.Clamp(_stylusCursorOpacity.Value / 100f, 0, 1);
            if (_stylusOutlineOpacity != null)
                Mods.Input.StylusZone.OutlineOpacity = Math.Clamp(_stylusOutlineOpacity.Value / 100f, 0, 1);
            if (_stylusButtonOpacity != null)
                Mods.Input.StylusZone.ButtonOpacity = Math.Clamp(_stylusButtonOpacity.Value / 100f, 0, 1);
            InputSettings.ScrollAllWeapons = _scrollAllWeapons.On;
            if (_clipPostRollRow != null)
                Mods.Network.DemoClip.PostRollSeconds = Mods.Network.DemoClip.PostRollLengths[
                    Math.Clamp(_clipPostRollRow.Index, 0, Mods.Network.DemoClip.PostRollLengths.Length - 1)];
            if (_clipSecondsRow != null)
                Mods.Network.DemoClip.Seconds = Mods.Network.DemoClip.Lengths[
                    Math.Clamp(_clipSecondsRow.Index, 0, Mods.Network.DemoClip.Lengths.Length - 1)];
            if (_touchButtonsRow != null)
            {
                Mods.Input.TouchSettings.ButtonsVisible = _touchButtonsRow.On;
                foreach ((Mods.Input.TouchControl control, ToggleRow row) in _touchRows)
                {
                    Mods.Input.TouchSettings.SetEnabled(control, row.On);
                }
            }
            InputSettings.Save();
            // The players in the match already have their own copies of these.
            // In-game settings target the registry supplied by the owning match:
            // launcher previews and other side scenes may exist at the same time.
            if (_inGame && _players != null)
            {
                InputSettings.ApplyToPlayers(_players);
            }
            else
            {
                InputSettings.ApplyToPlayers();
            }
            // Launcher preferences
            if (_playerName.Value.Trim().Length > 0)
            {
                LauncherPrefs.PlayerName = _playerName.Value.Trim();
            }
            LauncherPrefs.LastHunter = Enum.Parse<Hunter>(_hunterRow.Value);
            if (Int32.TryParse(_colorRow.Value, out int suit))
            {
                LauncherPrefs.LastColor = Mods.Network.PlayerColors.Clamp(suit - 1);
            }
            // These two rows are reachable from the pause menu as well as from
            // the front screen, so answering them during a match has to mean
            // something. It means the same as the pause menu's own pair: at
            // the next respawn. Outside a match it is queued and then cleared
            // when the next one is built, which is when the launcher's answer
            // -- the same one -- is applied anyway.
            RespawnChoice.Request(LauncherPrefs.LastHunter, LauncherPrefs.LastColor);
            string host = LauncherPrefs.ServerAddress;
            int port = LauncherPrefs.ServerPort;
            if (ParseEndpoint(_serverRow.Value, ref host, ref port))
            {
                LauncherPrefs.ServerAddress = host;
                LauncherPrefs.ServerPort = port;
            }
            string masterHost = LauncherPrefs.MasterHost;
            int masterPort = LauncherPrefs.MasterPort;
            if (ParseEndpoint(_masterRow.Value, ref masterHost, ref masterPort))
            {
                LauncherPrefs.MasterHost = masterHost;
                LauncherPrefs.MasterPort = masterPort;
            }
            LauncherPrefs.AutoUpdate = _autoUpdate.On;
            LauncherPrefs.ReduceMotion = _reduceMotion.On;
            if (_replayStorageRow != null)
            {
                LauncherPrefs.ReplayStorageLimitGb = _replayStorageStops[
                    Math.Clamp(_replayStorageRow.Index, 0, _replayStorageStops.Length - 1)];
            }
            if (_replayAutoPruneRow != null)
                LauncherPrefs.ReplayAutoPrune = _replayAutoPruneRow.On;
            if (_replayDeleteClipsRow != null)
                LauncherPrefs.ReplayDeleteClips = _replayDeleteClipsRow.On;
            if (_spectatorNameTagsRow != null)
                LauncherPrefs.SpectatorNameTags = _spectatorNameTagsRow.On;
            if (_killCamRow != null)
                LauncherPrefs.KillCamEnabled = _killCamRow.On;
            if (_killCameraRow != null) LauncherPrefs.KillCamCamera = _killCameraRow.Index;
            if (_finalKillCamRow != null)
                LauncherPrefs.FinalKillCamEnabled = _finalKillCamRow.On;
            GameState.CommitSettings(_settings);
            LauncherPrefs.Save();
            Mods.Sound.CombatFeedbackAudio.Reload();
            if (_inGame)
            {
                Mods.Sound.CombatFeedbackAudio.Warm();
            }
            if (LauncherPrefs.ReplayAutoPrune && LauncherPrefs.ReplayStorageLimitGb > 0)
            {
                Mods.Replay.ReplayStorageManager.Apply(new Mods.Replay.ReplayStoragePolicy(
                    MaxBytes: LauncherPrefs.ReplayStorageLimitGb * 1024L * 1024L * 1024L,
                    DeleteFullMatches: true,
                    DeleteMaterializedClips: LauncherPrefs.ReplayDeleteClips,
                    DeleteVirtualClips: false));
            }
            // Written and *applied*: the volumes, the language and the match
            // rules were only ever put in the file, so a music slider moved
            // here would otherwise leave the music exactly where it was --
            // during a match as well as before one, since this same window
            // opens from the pause menu.
            Mods.GameSettings.Apply(_settings);
            Saved = true;
            _draft?.Accept();
            if (!_shell) Close();
        }
    }
}
