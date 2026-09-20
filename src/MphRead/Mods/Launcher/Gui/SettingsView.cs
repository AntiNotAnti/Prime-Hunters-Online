using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// Six pages: Display, Audio, Controls, Replays, Profile, Credits. There is no
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

        private readonly Panel _pages = new();
        private readonly List<(string Name, Control Page)> _sections = new();
        private UiTabs _tabs = null!;

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
        private static readonly int[] _replayStorageStops = { 0, 5, 10, 25, 50 };
        private SliderRow _resolutionScale = null!;
        private ToggleRow _lightingRow = null!;
        private ToggleRow _fogRow = null!;
        private ToggleRow _filteringRow = null!;
        private ToggleRow _celRow = null!;
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
        private ChoiceRow _crosshairSizeRow = null!;
        private ChoiceRow _crosshairStyleRow = null!;
        private ChoiceRow _weaponStyleRow = null!;
        private ToggleRow _radarRow = null!;
        private ToggleRow _radarBackgroundRow = null!;
        private ToggleRow _radarOutlinesRow = null!;
        private SliderRow _sfxVolume = null!;
        private SliderRow _musicVolume = null!;
        private ChoiceRow _languageRow = null!;
        private SliderRow _sensitivity = null!;
        private ToggleRow _invertY = null!;
        private ToggleRow _invertX = null!;
        private ToggleRow _penTablet = null!;
        private ToggleRow _scrollAllWeapons = null!;
        private GamepadSettingsPanel _gamepadSettings = null!;
        private ToggleRow? _repositionFilter;
        private StackPanel? _stylusAdvanced;
        private DeckButton? _stylusAdvancedButton;
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

        public SettingsView(MenuSettings settings, bool inGame = false)
        {
            _settings = settings;
            _inGame = inGame;

            Background = Brushes.Transparent;
            Focusable = true;

            BuildPages();

            _tabs = new UiTabs(_sections.ConvertAll(s => s.Name));
            _tabs.Changed += (_, _) => ShowPage(_tabs.Index);

            var cancel = new UiMark(UiMark.Shape.Cancel, "cancel");
            cancel.Click += (_, _) => Close();
            var save = new UiMark(UiMark.Shape.Accept, inGame ? "apply" : "save");
            save.Click += (_, _) => TryCommit();

            // Over a match the backdrop is the scrim alone, so the game shows
            // through; away from one it is the same picture every other screen
            // uses, so Settings never reads as a different program.
            //
            // The well is narrower than the window on purpose -- see
            // UiLayout's note. A settings row is a label on the left and its
            // control on the right, and a row as wide as a monitor is one
            // whose two ends have to be read in two glances.
            Panel root = UiLayout.Page(inGame, UiLayout.WellSettings, "settings",
                _tabs, _pages, cancel, save);
            _saveError = new Note("", GuiTheme.Warm)
            {
                IsVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                // Above the marks rather than behind them, which is where a
                // bottom-anchored line lands now that the marks are down the
                // middle too.
                Margin = new Thickness(0, 0, 0, UiLayout.MarksBottom + 38)
            };
            root.Children.Add(_saveError);
            Content = root;
            ShowPage(0);
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
            Dispatcher.UIThread.Post(() => _tabs.FocusSelected(), DispatcherPriority.Background);
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
                    if (_controlTabs != null
                        && String.Equals(name, "Controls", StringComparison.OrdinalIgnoreCase))
                    {
                        _controlTabs.Index = Math.Clamp(sub, 0, 2);
                    }
                    return;
                }
            }
        }

        private void ShowPage(int index)
        {
            for (int i = 0; i < _sections.Count; i++)
            {
                _sections[i].Page.IsVisible = i == index;
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

        private static DeckButton AddAdvancedToggle(StackPanel page, StackPanel advanced, string navId)
        {
            var button = new DeckButton("Advanced", Deck.Face.Slate,
                sizeEms: .9, padXEms: .8, padYEms: .38, lip: 3)
            {
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
        /// Six pages: display, audio, controls, replays, profile and credits.
        /// </summary>
        private void BuildPages()
        {
            BuildDisplay(AddSection("Display"));
            BuildAudio(AddSection("Audio"));
            BuildControls(AddSection("Controls"));
            BuildReplays(AddSection("Replays"));
            BuildLauncher(AddSection("Profile"));
            BuildCredits(AddSection("Credits"));
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
            Heading(page, "Credits");
            Explain(page, Mods.Credits.Summary);
            page.Children.Add(new Caption(Mods.Credits.Author));
            page.Children.Add(new Note(Mods.Credits.ForkWork));
            // The address is put in a line under the word when there is no
            // browser to hand it to -- a headless session, or a handler that
            // refused -- so pressing it says something either way rather than
            // appearing to do nothing.
            var support = new UiWord("\u2615 Support this project", 15);
            var supportUrl = new Note("") { IsVisible = false };
            support.Click += (_, _) =>
            {
                if (!Mods.Update.Updater.OpenLink(Mods.Credits.SupportUrl))
                {
                    supportUrl.Text = Mods.Credits.SupportUrl;
                    supportUrl.IsVisible = true;
                }
            };
            page.Children.Add(support);
            page.Children.Add(supportUrl);
            Heading(page, "Built on");
            foreach (Mods.Credits.Entry entry in Mods.Credits.Entries)
            {
                page.Children.Add(new Caption(entry.Who));
                string what = entry.What;
                if (entry.Where.Length > 0)
                {
                    what += "\n" + entry.Where;
                }
                page.Children.Add(new Note(what));
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
                    new[] { "Windowed", "Fullscreen (borderless)" },
                    LauncherPrefs.WindowMode == WindowStartMode.BorderlessFullscreen ? 1 : 0));
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

            Heading(page, "Performance");
            _resolutionScale = Add(page, new SliderRow("Render scale",
                RenderOptions.ResolutionScale,
                v => $"{Math.Max(RenderOptions.MinScale, v)}%"));
            // Under the render scale because they are the same question asked
            // from both ends -- how much picture, and how often -- and because
            // the two of them are what somebody who is not getting a smooth
            // game comes to this page to change.
            _fpsLimitRow = Add(page, new SliderRow("FPS limit",
                FpsLimitStopIndex(FrameTiming.FrameRateCap),
                v => _fpsLimitStops[Math.Clamp(v, 0, _fpsLimitStops.Length - 1)].Label,
                min: 0, max: _fpsLimitStops.Length - 1, keyStep: 1));
            _lightingRow = Add(page, new ToggleRow("Lighting", RenderOptions.Lighting));
            _fogRow = Add(page, new ToggleRow("Fog", RenderOptions.Fog));
            _filteringRow = Add(page, new ToggleRow("Texture filtering", RenderOptions.TextureFiltering));
            _fpsRow = Add(page, new ToggleRow("FPS counter", RenderOptions.ShowFps));

            Heading(page, "Cel shading");
            _celRow = Add(page, new ToggleRow("Cel shading", RenderOptions.CelShading));

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
            // The crosshair questions belong to Pro mode and nothing else --
            // the DS HUD draws its own reticle sprite and has no use for
            // them -- so they are only asked while it is on. Shown rather than
            // greyed: a row that cannot be answered is still a row to read
            // past, and this page is long enough.
            _crosshairSizeRow = Add(page, new ChoiceRow("Crosshair size",
                Crosshair.SizeNames, (int)Crosshair.Size));
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
            _reduceMotion = Add(page, new ToggleRow(
                "Reduce menu motion", LauncherPrefs.ReduceMotion));
        }

        private void ShowCrosshairRows()
        {
            _crosshairSizeRow.IsVisible = _proHud.On;
            _crosshairStyleRow.IsVisible = _proHud.On;
            _weaponStyleRow.IsVisible = _proHud.On;
        }

        private void ShowRadarRows()
        {
            _radarBackgroundRow.IsVisible = _radarRow.On;
            _radarOutlinesRow.IsVisible = _radarRow.On;
        }

        // --------------------------------------------------------------- audio

        private void BuildAudio(StackPanel page)
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

        private static int Percent(string stored, int fallback)
        {
            return Single.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture,
                out float parsed)
                ? Math.Clamp((int)Math.Round(parsed * 100), 0, 100)
                : fallback;
        }

        // ------------------------------------------------------------ controls

        /// <summary>
        /// Three devices, three pages.
        ///
        /// A keyboard, a pad and a pen tablet do not share a list: "Sensitivity"
        /// is a different number on each of them, the key list means nothing on
        /// two of the three, and the bottom-screen zone means nothing on two
        /// either. They were one page with three headings, which made the page
        /// long enough that the thing you came to change was usually below the
        /// fold -- so they are three pages behind a strip now, and the strip is
        /// the same <see cref="UiTabs"/> the sections above it use.
        /// </summary>
        private void BuildControls(StackPanel outer)
        {
            var keyboard = new StackPanel { Spacing = 2 };
            var gamepad = new StackPanel { Spacing = 2, IsVisible = false };
            var stylus = new StackPanel { Spacing = 2, IsVisible = false };
            var subs = new UiTabs(new[] { "Keyboard", "Gamepad", "Stylus" });
            _controlTabs = subs;
            subs.Margin = new Thickness(0, 0, 0, 8);
            subs.Changed += (_, _) =>
            {
                keyboard.IsVisible = subs.Index == 0;
                gamepad.IsVisible = subs.Index == 1;
                stylus.IsVisible = subs.Index == 2;
            };
            outer.Children.Add(subs);
            outer.Children.Add(keyboard);
            outer.Children.Add(gamepad);
            outer.Children.Add(stylus);
            BuildKeyboard(keyboard);
            BuildGamepad(gamepad);
            BuildStylus(stylus);
        }

        /// <summary>
        /// The Controls page's own strip, so <see cref="ShowSection"/> can
        /// open one of its three sub-pages. The rows under Gamepad are the
        /// ones that have never been arranged until it is opened, which is
        /// where the crash was.
        /// </summary>
        private UiTabs? _controlTabs;

        private void BuildKeyboard(StackPanel page)
        {
            Heading(page, "Mouse");
            _sensitivity = Add(page, new SliderRow("Sensitivity",
                SensitivityToSlider(InputSettings.MouseSensitivity),
                v => $"{SliderToSensitivity(v).ToString("0.00", CultureInfo.InvariantCulture)}x",
                min: 1, max: 300, keyStep: 1));
            _invertY = Add(page, new ToggleRow("Invert vertical aim", InputSettings.InvertMouseY));
            _invertX = Add(page, new ToggleRow("Invert horizontal aim", InputSettings.InvertMouseX));

            var advanced = new StackPanel { Spacing = 2, IsVisible = false };
            _scrollAllWeapons = Add(advanced, new ToggleRow("Wheel cycles every weapon",
                InputSettings.ScrollAllWeapons));
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

            Heading(page, "Replay library");
            Explain(page, "Full recordings, instant clips and recovered sessions appear under "
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
        }

        /// <summary>Every key row, so Reset can redraw them from whichever page it is on.</summary>
        private List<KeyRow> _keyRows = new();

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
            foreach (Mods.Input.PadAction action in Mods.Input.PadBindings.Actions)
            {
                padRows.Add(Add(page, new PadRow(action)));
            }

            var reset = new DeckButton("Reset to defaults", Deck.Face.Brass,
                sizeEms: .9, padXEms: .8, padYEms: .38, lip: 3)
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 10, 0, 0)
            };
            ControllerNav.Identify(reset, "controller.reset");
            reset.Click += (_, _) =>
            {
                InputSettings.Reset();
                _sensitivity.Value = SensitivityToSlider(InputSettings.MouseSensitivity);
                _invertY.On = InputSettings.InvertMouseY;
                _invertX.On = InputSettings.InvertMouseX;
                _penTablet.On = Mods.Input.PointerInput.StylusMode;
                if (_repositionFilter != null) _repositionFilter.On = Mods.Input.PointerInput.GuardJumps;
                if (_stylusZone != null && _stylusOpacity != null)
                {
                    _stylusZone.On = Mods.Input.StylusZone.Wanted;
                    _stylusOpacity.Value = (int)MathF.Round(Mods.Input.StylusZone.Opacity * 100);
                }
                ShowStylusRows();
                _scrollAllWeapons.On = InputSettings.ScrollAllWeapons;
                _gamepadSettings.Reload();
                foreach (PadRow row in padRows) row.InvalidateVisual();
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
        private SliderRow? _stylusOpacity;
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

            var place = new DeckButton("Configure stylus zone", Deck.Face.Slate,
                sizeEms: .9, padXEms: .8, padYEms: .38, lip: 3)
            {
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
            _stylusOpacity = Add(_stylusAdvanced, new SliderRow("Overlay opacity",
                (int)MathF.Round(Mods.Input.StylusZone.Opacity * 100),
                v => $"{v}%", min: 4, max: 60, keyStep: 2));
            _stylusAdvanced.Children.Add(new Note(
                "Reposition filtering ignores tablet jumps after lift/re-contact. The overlay opacity only affects the DS touch-screen guide."));
            _stylusAdvancedButton = new DeckButton("Advanced", Deck.Face.Slate,
                sizeEms: .9, padXEms: .8, padYEms: .38, lip: 3)
            {
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
            _autoUpdate = Add(page, new ToggleRow("Check for updates on startup",
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
                LauncherPrefs.WindowMode = _windowRow.Index == 1
                    ? WindowStartMode.BorderlessFullscreen
                    : WindowStartMode.Windowed;
                WindowMode.Startup = LauncherPrefs.WindowMode;
                // And now, not at the next launch. There is one window and
                // this setting is about the one you are looking at: picking
                // "Fullscreen" and having nothing happen reads as a setting
                // that did not take. Asked for rather than done -- a window
                // attribute belongs to the thread that owns the window, and
                // this runs inside the toolkit; PauseMenu.Poll does it at the
                // end of the frame, which is the same route Escape's own
                // fullscreen entry takes.
                bool wantFullscreen =
                    LauncherPrefs.WindowMode == WindowStartMode.BorderlessFullscreen;
                if (wantFullscreen != WindowMode.IsFullscreen)
                {
                    PauseMenu.RequestFullscreenToggle();
                }
            }
            if (_clipSecondsRow != null)
            {
                Mods.Network.DemoClip.Seconds = Mods.Network.DemoClip.Lengths[
                    Math.Clamp(_clipSecondsRow.Index, 0, Mods.Network.DemoClip.Lengths.Length - 1)];
            }
            _settings.ResolutionScale = Math.Max(RenderOptions.MinScale, _resolutionScale.Value)
                .ToString(CultureInfo.InvariantCulture);
            RenderOptions.FieldOfView = _fovRow.Value;
            _settings.FieldOfView = _fovRow.Value.ToString(CultureInfo.InvariantCulture);
            _settings.Lighting = RenderOptions.OnOff(_lightingRow.On);
            _settings.Fog = RenderOptions.OnOff(_fogRow.On);
            _settings.TextureFiltering = RenderOptions.OnOff(_filteringRow.On);
            _settings.ShowFps = RenderOptions.OnOff(_fpsRow.On);
            int cap = _fpsLimitStops[Math.Clamp(_fpsLimitRow.Value, 0,
                _fpsLimitStops.Length - 1)].Cap;
            FrameTiming.FrameRateCap = cap;
            _settings.FrameRateCap = FrameTiming.CapString(cap);
            _settings.CelShading = RenderOptions.OnOff(_celRow.On);
            _settings.CelBands = "8";
            _settings.CelEdge = "50";
            Features.ProHud = _proHud.On;
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
            // Controls
            InputSettings.MouseSensitivity = SliderToSensitivity(_sensitivity.Value);
            InputSettings.InvertMouseY = _invertY.On;
            InputSettings.InvertMouseX = _invertX.On;
            Mods.Input.PointerInput.StylusMode = _penTablet.On;
            if (_repositionFilter != null)
                Mods.Input.PointerInput.GuardJumps = _repositionFilter.On;
            if (_stylusZone != null && _stylusOpacity != null)
            {
                Mods.Input.StylusZone.Enabled = _stylusZone.On;
                Mods.Input.StylusZone.Opacity = Math.Clamp(_stylusOpacity.Value / 100f, 0.02f, 1f);
            }
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
            InputSettings.ApplyToPlayers();
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
            GameState.CommitSettings(_settings);
            LauncherPrefs.Save();
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
            Close();
        }
    }
}
