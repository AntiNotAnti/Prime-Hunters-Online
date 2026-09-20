using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MphRead.Mods.Network;
using MphRead.Mods.Update;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// The front screen: a picture, three words, and nothing to read before
    /// you can play.
    ///
    /// The layout is the one every screen in the launcher uses: a column down
    /// the middle of the frame, read against a soft wash, with the wordmark
    /// over it where another screen would put its heading. It was anchored in
    /// the bottom-left corner for several releases -- id-Tech3's shape, and
    /// OpenQuake3/defrag's -- and moved when the screens behind it did, for
    /// the reason those anchors existed in the first place: a front screen
    /// laid out differently from everything it opens is the one screen that
    /// does not look like the program. The anchors themselves live in
    /// <see cref="UiLayout"/> and nowhere else.
    ///
    /// It is a <see cref="UserControl"/> rather than a <see cref="Window"/>
    /// for one reason: nothing shows it in a window. The desktop renders it
    /// into the game window through <c>UiSurface</c>; the Android head
    /// hands this same object to Avalonia as its single view.
    /// There is no second front screen to keep in step, which is the point --
    /// a phone-shaped copy was the previous arrangement and it drifted within
    /// a release.
    ///
    /// Everything it opens is pushed onto one stack over the picture, on every
    /// platform. There used to be three windows on the desktop and overlays on
    /// the phone, which is two arrangements of the same four screens.
    /// </summary>
    internal sealed class StartScreen : UserControl
    {
        private readonly MenuSettings _settings;
        private readonly List<string> _rooms;
        private readonly Panel _overlay;
        private readonly List<Control> _stack = new();
        private readonly TextBlock _version;
        private readonly Border _versionBox;
        private readonly StackPanel _menu;
        private readonly Panel _root;
        private readonly Control[] _ground;
        private readonly Border _dark;
        private string _controllerPrompt = "";

        private bool _finished;
        private bool _updatable;
        private bool _updating;
        private bool _groundShown = true;

        /// <summary>What the screen decided. Kind None means it was closed.</summary>
        public LaunchPlan Plan { get; private set; }

        /// <summary>Raised once, when the screen is done with.</summary>
        public event EventHandler<LaunchPlan>? Done;
        public event EventHandler<LaunchPlan>? MatchRequested;
        private LobbyScreen? _lobby;
        public void ResumeLobby() => _lobby?.Resume();
        public void SuspendLobby() => _lobby?.Suspend();

        public StartScreen(MenuSettings settings, IReadOnlyList<string> rooms)
        {
            _settings = settings;
            _rooms = new List<string>(rooms);
            Focusable = true;

            // The wash is baked into the backdrop rather than laid over it:
            // one bitmap a frame instead of four full-window layers. See
            // BakedBackdrop.
            Panel root = UiLayout.Backdrop(wash: UiLayout.BackdropWash.Light);
            _root = root;
            // The photograph, the moving layer and the washes, kept so they
            // can be taken out of the tree again. See ShowGround.
            _ground = new Control[root.Children.Count];
            root.Children.CopyTo(_ground, 0);
            _dark = new Border { Background = GuiTheme.InkBrush, IsVisible = false };
            root.Children.Insert(0, _dark);

            // Centred, like every screen behind it. The corner layout was this
            // screen's own and the rest of the program has been rebuilt around
            // the well, so a front screen still anchored to the bottom-left is
            // the one screen that does not look like the program it opens.
            //
            // The wordmark comes with it. In the corner it was a watermark on
            // somebody else's photograph; over the menu it is what the screen
            // is of, and it means the front screen needs no heading -- the
            // wordmark is the heading.
            _menu = new StackPanel
            {
                Spacing = 0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            // Set, not the shipped bitmap: the PNG is smooth-edged art and it
            // is the one thing on this screen drawn by a different hand from
            // the pixel-type row under it. The mark itself is untouched --
            // it is still the window icon and still the release art.
            _wordmark = new DeckWordmark()
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 30)
            };
            _menu.Children.Add(_wordmark);
            // `.wordmark .sub`: `.82em` of the frame, not eleven points of
            // nothing in particular. It sat beside a mark that has just
            // stopped being a fixed size, and a fixed caption under a mark
            // that grows is a caption that shrinks.
            _sub = new TextBlock
            {
                Text = "METROID PRIME HUNTERS  \u00b7  REBORN",
                FontFamily = GuiTheme.Display,
                FontSize = 11,
                Foreground = GuiTheme.TextDimBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, -18, 0, 0)
            };
            _menu.Children.Add(_sub);
            // A row of faces rather than a column of words. Clips is a
            // first-class destination now, beside Play rather than buried in
            // the play-mode strip; what changed is
            // that each is now an object you press rather than a word that
            // brightens, and the row reads as one bar across the screen
            // instead of a stack down the middle of the photograph.
            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            };
            // The front screen's four faces are the reference's plain
            // `.btn`: `font-size: 1.55em`, `padding: .85em 1.5em`, a
            // six-point edge -- which is what the constructor defaults to.
            // Play is the one that bobs, and the only one on any screen that
            // does.
            var play = new DeckButton("PLAY", Deck.Face.Blue) { Idle = true };
            play.Click += (_, _) => _ = OpenPlay();
            bar.Children.Add(play);
            var clips = new DeckButton("CLIPS", Deck.Face.Moss);
            clips.Click += (_, _) => _ = OpenClips();
            bar.Children.Add(clips);
            var options = new DeckButton("SETTINGS", Deck.Face.Brass);
            options.Click += (_, _) => _ = OpenSettings();
            bar.Children.Add(options);
            var quit = new DeckButton("QUIT", Deck.Face.Rust);
            quit.Click += (_, _) => AskToQuit();
            bar.Children.Add(quit);
            root.Children.Add(_menu);

            // A bar across the foot, not a stack in the middle: the four
            // faces between the profile on one end and the support mark on
            // the other. The photograph gets its middle back, which is what
            // it is there for.
            _chip = new DeckChip("Profile", PlayerNameOrDefault())
            {
                VerticalAlignment = VerticalAlignment.Bottom
            };
            DeckChip chip = _chip;
            DeckButton heart = SupportMark();
            heart.VerticalAlignment = VerticalAlignment.Bottom;
            _heart = heart;

            // Upright there is no room beside a column of full-width faces, so
            // the mark goes to the corner the way it does on a phone. Its own
            // layer, because the foot is a three-column row and this is not in
            // it any more once the row has turned.
            _heartCorner = SupportMark();
            _heartCorner.HorizontalAlignment = HorizontalAlignment.Left;
            _heartCorner.VerticalAlignment = VerticalAlignment.Top;
            _heartCorner.Margin = new Thickness(14, 14, 0, 0);
            _heartCorner.IsVisible = false;
            root.Children.Add(_heartCorner);

            var foot = new Grid
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(26, 0, 26, 24),
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
            };
            Grid.SetColumn(chip, 0);
            foot.Children.Add(chip);
            bar.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetColumn(bar, 1);
            foot.Children.Add(bar);
            Grid.SetColumn(heart, 2);
            foot.Children.Add(heart);
            root.Children.Add(foot);
            _foot = foot;

            // A phone held upright has no room for three of these across, and
            // a row that overflows is worse than a column: the first and last
            // entry lose their ends off the edges of the screen. There is no
            // media query here, so the row watches its own width and turns.
            _bar = bar;
            root.SizeChanged += (_, e) =>
            {
                LayOutBar(e.NewSize.Width);
                LayOutWordmark(e.NewSize);
            };
            LayOutBar(_windowWidthGuess);

            _version = new TextBlock
            {
                Text = VersionNumber(),
                FontFamily = GuiTheme.Display,
                FontSize = 12,
                Foreground = GuiTheme.TextDimBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            // The whole line is the button, rather than a label with one beside
            // it: the state and the action are the same fact here -- dim is
            // "nothing to do" and amber is "press this" -- and two controls
            // saying that would be one of them redundant in either state.
            _versionBox = new Border
            {
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 18, 24, 0),
                Child = _version
            };
            _versionBox.Focusable = true;
            _versionBox.KeyDown += (_, e) =>
            {
                if ((e.Key == Key.Enter || e.Key == Key.Space) && _updatable)
                { e.Handled = true; UpdateNow(); }
            };
            _versionBox.PointerPressed += (_, e) =>
            {
                if (_updatable)
                {
                    e.Handled = true;
                    UpdateNow();
                }
            };
            root.Children.Add(_versionBox);
            var help = new TextBlock { Foreground = GuiTheme.TextDimBrush, FontSize = 12,
                Margin = new Thickness(20, 0, 0, 3), VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };

            var hints = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
            {
                string prompt = Mods.Input.InputSourceTracker.Current == Mods.Input.InputSource.Gamepad
                    ? $"{Mods.Input.InputPrompt.For(Mods.Input.UiAction.Accept).Glyph} Select   "
                        + $"{Mods.Input.InputPrompt.For(Mods.Input.UiAction.Back).Glyph} Back   "
                        + $"{Mods.Input.InputPrompt.For(Mods.Input.UiAction.PreviousTab).Glyph}/{Mods.Input.InputPrompt.For(Mods.Input.UiAction.NextTab).Glyph} Tabs"
                    : "Enter Select   Esc Back";
                if (prompt != _controllerPrompt) { help.Text = prompt; _controllerPrompt = prompt; }
            });
            AttachedToVisualTree += (_, _) => hints.Start();
            DetachedFromVisualTree += (_, _) => hints.Stop();

            _overlay = new Panel { Background = Brushes.Transparent, IsVisible = false };
            root.Children.Add(_overlay);
            root.Children.Add(help);
            Content = root;
#if ANDROID
            var navigation = new GamepadNavigation();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input,
                (_, _) => { if (Mods.Input.GamepadContexts.MenuVisible) navigation.Update(this); });
            AttachedToVisualTree += (_, _) => timer.Start();
            DetachedFromVisualTree += (_, _) => timer.Stop();
#endif

            if (LauncherPrefs.AutoUpdate)
            {
                // In the background, and never blocking the window: a launcher
                // that will not draw until GitHub answers looks broken on a bad
                // connection.
                Updater.CheckInBackground(
                    _ => Dispatcher.UIThread.Post(RefreshVersionLine),
                    () => Dispatcher.UIThread.Post(RefreshVersionLine));
            }
            RefreshVersionLine();
            _ = CatchUpPreviews();
        }

        /// <summary>The width below which the four faces will not fit across.</summary>
        private const double BarTurnsWidth = 620;
        private const double _windowWidthGuess = 940;
        private StackPanel? _bar;
        private DeckWordmark? _wordmark;
        private TextBlock? _sub;
        private Grid? _foot;
        private DeckChip? _chip;
        private DeckButton? _heart;
        private DeckButton? _heartCorner;

        /// <summary>
        /// The support mark: a button like every other, with a heart where the
        /// word goes.
        ///
        /// `class="btn f-rust heart"` in the reference -- the same object as
        /// QUIT beside it, with `--lip: 5px`, `padding: .7em .9em` and a
        /// `1.9em` by `1.27em` picture. It used to be a control of its own and
        /// had none of what makes these buttons what they are: no bevel, no
        /// spring, no lean towards the pointer, no press that travels by the
        /// lip it loses.
        /// </summary>
        private static DeckButton SupportMark()
        {
            var mark = new DeckButton("", Deck.Face.Rust,
                sizeEms: 1.55, padXEms: 0.9, padYEms: 0.7, lip: 5)
            {
                Glyph = DeckHeart.DrawHeart,
                GlyphEms = new Size(1.9, 1.27),
                GlyphColour = Color.FromRgb(0xe8, 0xa0, 0xa0),
                Tip = "Support this project <3"
            };
            mark.Click += (_, _) => Updater.OpenLink(Mods.Credits.SupportUrl);
            return mark;
        }

        private static string PlayerNameOrDefault()
        {
            string name = LauncherPrefs.PlayerName.Trim();
            return name.Length > 0 ? name : "Player";
        }

        private void LayOutBar(double width)
        {
            StackPanel? bar = _bar;
            if (bar == null)
            {
                return;
            }
            bool column = width < BarTurnsWidth;
            bar.Orientation = column ? Orientation.Vertical : Orientation.Horizontal;
            bar.Spacing = column ? 9 : 12;
            bar.Width = column ? Math.Max(160, Math.Min(320, width - 48)) : double.NaN;
            if (_foot != null)
            {
                // Turned, the foot is one column: the profile above the faces
                // and the support mark up in the corner, which is the only
                // place left for it.
                _foot.ColumnDefinitions = new ColumnDefinitions(column ? "*" : "Auto,*,Auto");
                _foot.RowDefinitions = new RowDefinitions(column ? "Auto,Auto" : "*");
                if (_chip != null)
                {
                    Grid.SetColumn(_chip, 0);
                    Grid.SetRow(_chip, 0);
                    _chip.HorizontalAlignment = column
                        ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
                    _chip.Margin = new Thickness(0, 0, 0, column ? 9 : 0);
                }
                Grid.SetColumn(bar, column ? 0 : 1);
                Grid.SetRow(bar, column ? 1 : 0);
                if (_heart != null)
                {
                    Grid.SetColumn(_heart, column ? 0 : 2);
                    _heart.IsVisible = !column;
                }
                if (_heartCorner != null)
                {
                    _heartCorner.IsVisible = column;
                }
                _foot.Margin = new Thickness(column ? 14 : 26, 0, column ? 14 : 26, column ? 18 : 24);
            }
            foreach (Control child in bar.Children)
            {
                child.HorizontalAlignment = column
                    ? HorizontalAlignment.Stretch
                    : HorizontalAlignment.Center;
            }
        }

        /// <summary>
        /// The mark's size, and the caption under it, for the box the screen
        /// has actually been handed.
        ///
        /// Three numbers, and they are the reference's three:
        /// <c>7.6em</c> on a 16:9 desktop, <c>5.2em</c> on a phone held
        /// upright and <c>4.4em</c> on the same phone turned -- a phone in
        /// landscape has the height of a letterbox and a mark sized for a
        /// monitor would leave no room under it for the row it introduces.
        ///
        /// <see cref="DeckStage"/> is the root here, so the size it reports is
        /// the one it measured its em from and <see cref="Deck.EmFor"/> hands
        /// back that same number rather than a guess at it.
        /// </summary>
        private void LayOutWordmark(Size frame)
        {
            if (frame.Width <= 0 || frame.Height <= 0)
            {
                return;
            }
            bool landscape = frame.Width > frame.Height;
            double ems = !Deck.Phone ? 7.6 : landscape ? 4.4 : 5.2;
            if (_wordmark != null)
            {
                _wordmark.SizeEms = ems;
                // The gap under the mark is the caption's `margin-top: 1.4em`
                // of its own size, less the drop shadow the mark already
                // carries inside its own box.
                _wordmark.Margin = new Thickness(0, 0, 0, 0);
            }
            double em = Deck.EmFor(frame.Width, frame.Height);
            if (_sub != null)
            {
                _sub.FontSize = Math.Max(8, Math.Round(em * 0.82));
                _sub.Margin = new Thickness(0, Math.Round(em * 0.82 * 1.4) - 10, 0, 0);
            }
        }

        private static UiWord Word(string text, FontFamily font, double size,
            Color colour, Action go)
        {
            var word = new UiWord(text, size, font, colour);
            word.Click += (_, _) => go();
            return word;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            // A fresh install has nothing to play, so the one thing it needs is
            // the whole screen rather than one refused entry among three.
            //
            // **After the first frame, not during the attachment.** Two things
            // go wrong when a screen is pushed from inside this method, and
            // the second is worse than the first.
            //
            // A control added to the tree while an ancestor is still being
            // attached never inherits <see cref="Deck.EmProperty"/> from the
            // <see cref="DeckStage"/> above it: it is laid out on the
            // property's own default -- 10.81, which happens to be the
            // capture's em and is why nothing on the desktop showed it -- and
            // the panel comes out a column of text a dozen characters wide
            // with no card behind it.
            //
            // And on Android the tree is swapped before the toolkit has put
            // anything on the glass at all, which leaves a window that never
            // draws: the compositor does not come back to a control whose
            // first render drew nothing, so the app sits on the activity's
            // background colour until some input forces a pass. That is what
            // "black screen on the first launch, and back gets past it" was,
            // and a dispatcher turn is not enough for it -- <see
            // cref="Deck.NextFrame"/> is, because it is the one hook here that
            // waits for a *frame* rather than for the queue to drain.
            if (!GameFiles.Ready && _stack.Count == 0)
            {
                Deck.NextFrame(this, () =>
                {
                    if (!GameFiles.Ready && _stack.Count == 0)
                    {
                        OpenSetup();
                    }
                });
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _stack.Count == 0)
            {
                AskToQuit();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        /// <summary>
        /// Come back from a match and be usable again. Android keeps this view
        /// alive across a match, where the desktop builds a new one each time
        /// round <see cref="GuiLauncher"/>'s loop.
        /// </summary>
        public void Reset()
        {
            _lobby?.Suspend();
            _lobby = null;
            _finished = false;
            Plan = default;
            while (_stack.Count > 0)
            {
                Pop();
            }
            ShowGround(true);
            // Android keeps one of these for the life of the app, so this is
            // where a launch begins there -- the roll behind "Random" is held
            // for exactly one launch.
            Hunters.Reroll();
            LauncherPrefs.Load();
            RefreshRooms();
            RefreshVersionLine();
            if (!GameFiles.Ready)
            {
                OpenSetup();
            }
        }

        /// <summary>
        /// Answer a back gesture: Escape and the phone's back button are the
        /// same question. True when this screen dealt with it.
        /// </summary>
        public bool GoBack()
        {
            if (_stack.Count == 0)
            {
                return false;
            }
            if (_stack[^1] is LobbyScreen lobby) lobby.Leave("");
            else Pop();
            return true;
        }

        // -------------------------------------------------------------- stack

        /// <summary>
        /// Take the front screen's own backdrop out of the tree, or put it
        /// back.
        ///
        /// Every screen pushed onto the stack draws an opaque backdrop of its
        /// own, so what is underneath has never mattered -- except for the one
        /// screen that deliberately draws none. The pause menu is a scrim and
        /// nothing else, so on the head that shows it here it was read against
        /// the front screen's photograph and its moving layer: the main menu's
        /// background, over a match, with the layer still costing a field and
        /// a full-window blend thirty times a second behind a menu. The
        /// desktop never showed it because there the same menu goes into the
        /// game window through <see cref="InGameMenu"/>, where there is no
        /// front screen to show through to.
        ///
        /// Removed rather than hidden: <see cref="MovingBackdrop"/> stops on
        /// a detach and IsVisible is not one.
        /// </summary>
        private void ShowGround(bool show)
        {
            if (_groundShown == show)
            {
                return;
            }
            _groundShown = show;
            _dark.IsVisible = !show;
            // The foot goes with it. Push hides the wordmark and the version
            // line and nothing else, because every other screen covers what is
            // left; the profile, the three faces and the support mark were
            // still on the glass behind a menu that covers nothing.
            if (_foot != null)
            {
                _foot.IsVisible = show;
            }
            if (_heartCorner != null)
            {
                _heartCorner.IsVisible = show && _bar?.Orientation == Orientation.Vertical;
            }
            if (show)
            {
                _root.Children.InsertRange(1, _ground);
                return;
            }
            for (int i = 0; i < _ground.Length; i++)
            {
                _root.Children.Remove(_ground[i]);
            }
        }

        private void Push(Control view)
        {
            _stack.Add(view);
            _overlay.Children.Clear();
            _overlay.Children.Add(view);
            _overlay.IsVisible = true;
            _menu.IsVisible = false;
            _versionBox.IsVisible = false;
            Dispatcher.UIThread.Post(() => view.Focus(), DispatcherPriority.Background);
        }

        private void Pop()
        {
            if (_stack.Count > 0)
            {
                _stack.RemoveAt(_stack.Count - 1);
            }
            _overlay.Children.Clear();
            if (_stack.Count > 0)
            {
                Control top = _stack[^1];
                _overlay.Children.Add(top);
                Dispatcher.UIThread.Post(() => top.Focus(), DispatcherPriority.Background);
                return;
            }
            _overlay.IsVisible = false;
            _menu.IsVisible = true;
            _versionBox.IsVisible = true;
            ShowGround(true);
            RefreshVersionLine();
            Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Background);
        }

        /// <summary>Hand the answer back, once.</summary>
        private void Finish(LaunchPlan plan)
        {
            if (_finished)
            {
                return;
            }
            _finished = true;
            Plan = plan;
            Done?.Invoke(this, plan);
        }

        // ------------------------------------------------------------- screens

        private Task OpenPlay()
        {
            if (!GameFiles.Ready)
            {
                OpenSetup();
                return Task.CompletedTask;
            }
            var view = new PlayScreen(_settings, _rooms);
            view.Closed += (_, _) => Pop();
            view.Launched += (_, plan) => ConnectedOrFinished(plan);
            view.CreateRequested += (_, _) => OpenCreateServer();
            Push(view);
            return Task.CompletedTask;
        }

        private Task OpenClips()
        {
            var view = new PlayScreen(_settings, _rooms, PlayScreen.Face.Clips);
            view.Closed += (_, _) => Pop();
            view.Launched += (_, plan) => Finish(plan);
            Push(view);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Run a server rather than join one -- pushed over the browser rather
        /// than replacing it, so backing out lands on the list of servers,
        /// which is where somebody who changed their mind was going anyway.
        /// </summary>
        private void OpenCreateServer()
        {
            var view = new CreateServerScreen(_rooms, _settings.RoomKey);
            view.Closed += (_, _) => Pop();
            view.Launched += (_, plan) => { Pop(); ConnectedOrFinished(plan); };
            Push(view);
        }

        private void ConnectedOrFinished(LaunchPlan plan)
        {
            if (NetSession.Active && NetSession.PersistentLobby)
            {
                _lobby = new LobbyScreen(_rooms, plan.Lobby);
                _lobby.MatchRequested += (_, match) => MatchRequested?.Invoke(this, match);
                _lobby.Closed += (_, reason) =>
                {
                    _lobby = null; Pop();
                    if (_stack.Count > 0 && _stack[^1] is PlayScreen play) play.SessionEnded(reason);
                };
                Push(_lobby);
            }
            else Finish(plan);
        }

        private Task OpenSettings()
        {
            var view = new SettingsView(_settings);
            view.Closed += (_, _) => Pop();
            view.GameFilesRequested += (_, _) =>
            {
                Pop();
                OpenSetup();
            };
            Push(view);
            return Task.CompletedTask;
        }

        private void OpenSetup()
        {
            var view = new SetupScreen();
            view.Closed += (_, _) =>
            {
                Pop();
                RefreshRooms();
            };
            Push(view);
        }

        private void AskToQuit()
        {
            var view = new ConfirmScreen($"Quit {Mods.Branding.Name}?");
            view.Answered += (_, yes) =>
            {
                Pop();
                if (yes)
                {
                    Finish(default);
                }
            };
            Push(view);
        }

        /// <summary>
        /// The pause menu, over a running match.
        ///
        /// Android takes this path, where the front screen is the view the
        /// activity keeps; the desktop pushes the same menu onto
        /// <see cref="InGameMenu"/> instead, over the frame. Both show the
        /// same <see cref="PauseMenuView"/>, so the menu cannot drift into
        /// being two menus.
        /// </summary>
        public void ShowPauseMenu(Action onResume, Action onLeave, Action onQuit)
        {
            // Everything on this stack is read over the match: the menu, the
            // settings it opens and the map vote all ask for the scrim alone.
            ShowGround(false);
            var view = new PauseMenuView(offerWindowMode: false);
            view.Resumed += (_, _) => { Pop(); onResume(); };
            view.LeaveRequested += (_, _) => { Pop(); onLeave(); };
            view.QuitRequested += (_, _) => { Pop(); onQuit(); };
            view.SpectateRequested += (_, _) =>
            {
                Pop();
                SpectatorMode.Start();
                onResume();
            };
            view.RejoinRequested += (_, _) =>
            {
                Pop();
                SpectatorMode.Rejoin();
                onResume();
            };
            view.RecordToggleRequested += (_, _) =>
            {
                if (DemoRecorder.IsRecording)
                {
                    Console.WriteLine($"[demo] recording saved to {DemoRecorder.CurrentPath}");
                    DemoRecorder.Stop();
                }
                else
                {
                    DemoRecorder.Start();
                }
                Pop();
                onResume();
            };
            view.VoteMapRequested += (_, _) => OpenVote();
            view.ReplayControlsRequested += (_, _) =>
            {
                var controls = new ReplayControlsView();
                controls.Closed += (_, _) => Pop();
                controls.ResumeRequested += (_, _) =>
                {
                    Pop();
                    Pop();
                    onResume();
                };
                Push(controls);
            };
            view.SettingsRequested += (_, _) =>
            {
                var settings = new SettingsView(_settings, inGame: true);
                settings.Closed += (_, _) => Pop();
                Push(settings);
            };
            Push(view);
            view.FocusResume();
        }

        /// <summary>
        /// Pick a map and put it to the room -- the same screen a match is
        /// chosen from, with the strip of sources taken away.
        ///
        /// The list is read again here rather than taken from the one this
        /// screen was built with. On the head that shows the pause menu on
        /// this stack the front screen is built once, before the game files
        /// have necessarily been found, and it is still that same object
        /// during every match afterwards -- so a launch that started with no
        /// rooms opened a ballot with nothing on it, which is what "there is
        /// no map vote on Android" was. <see cref="InGameMenu.OpenVote"/> does
        /// the same and this is the other half of it.
        /// </summary>
        private void OpenVote()
        {
            string why = MapVote.WhyNotProposing();
            if (why.Length > 0)
            {
                // Said in the game's own chat rather than in a box here: it is
                // one sentence, the player is about to go back to the match,
                // and a dialog for it is a second thing to dismiss.
                Chat.ChatBox.System(why);
                Pop();
                return;
            }
            IReadOnlyList<string> rooms = _rooms;
            if (rooms.Count == 0)
            {
                try
                {
                    rooms = ThumbnailGenerator.MultiplayerRooms();
                }
                catch (Exception ex)
                {
                    Mods.DebugLog.Exception("pause", ex);
                    rooms = Array.Empty<string>();
                }
            }
            if (rooms.Count == 0)
            {
                Chat.ChatBox.System("no maps to vote for");
                Pop();
                return;
            }
            var view = new PlayScreen(_settings, rooms, PlayScreen.Face.Vote,
                overGame: true);
            view.Closed += (_, _) => Pop();
            view.Voted += (_, room) =>
            {
                MapVote.Propose(room);
                // Both this and the pause menu under it: the answer arrives as
                // a prompt over the match, which is not a thing to read through
                // a menu.
                Pop();
                Pop();
            };
            Push(view);
        }

        // -------------------------------------------------------------- version

        /// <summary>
        /// Render the pictures of any map that does not have one yet, without
        /// being asked. Nothing happens in the ordinary case, which is every
        /// map already having one.
        /// </summary>
        private async Task CatchUpPreviews()
        {
            if (!GameFiles.Ready || !ThumbnailHost.CanRender
                || ThumbnailGenerator.MissingThumbnails().Count == 0)
            {
                return;
            }
            await ThumbnailHost.RenderMissingAsync(_ => { });
        }

        private void RefreshRooms()
        {
            if (!GameFiles.Ready)
            {
                return;
            }
            _rooms.Clear();
            foreach (string room in ThumbnailGenerator.MultiplayerRooms())
            {
                _rooms.Add(room);
            }
        }

        /// <summary>
        /// "1.2.3", or what to say instead when this build is not a release.
        /// Not <c>BuildVersion.Display</c>, which puts a v in front: this is a
        /// corner of a picture rather than a sentence.
        /// </summary>
        private static string VersionNumber()
        {
            Version? current = BuildVersion.Current;
            return current == null ? "a local build" : current.ToString(3);
        }

        private void Say(string text, Color colour, bool pressable = false)
        {
            _version.Text = text;
            _version.Foreground = new SolidColorBrush(colour);
            _updatable = pressable;
            _versionBox.Cursor = new Cursor(
                pressable ? StandardCursorType.Hand : StandardCursorType.Arrow);
        }

        /// <summary>
        /// Three states, not two. Amber is "there is a newer build, press
        /// this"; green is "this is the published one"; dim is everything else
        /// -- a local build, or a check that has not answered. Painting "no
        /// answer" green would be the one wrong thing this can do: a server
        /// refuses a client on a different build at Hello, so being told you
        /// are current when nobody has checked is worse than being told
        /// nothing.
        /// </summary>
        private void RefreshVersionLine()
        {
            if (_updating)
            {
                return;
            }
            string number = VersionNumber();
            if (Updater.Available != null)
            {
                Say($"{number} -- update available, click here", GuiTheme.Warm,
                    pressable: true);
                return;
            }
            Say(number, BuildVersion.IsRelease && Updater.Checked
                ? GuiTheme.Good : GuiTheme.TextDim);
        }

        /// <summary>
        /// Take the update.
        ///
        /// On the desktop that still means opening the release page: a release
        /// is an archive somebody unpacks over their own copy, and a program
        /// that rewrote its own files while running would have to solve
        /// restarting itself on three operating systems to save one unzip.
        /// Where the platform can install for itself -- a phone, today -- it
        /// fetches the file and hands it to the system installer instead.
        /// </summary>
        private void UpdateNow()
        {
            UpdateInfo? found = Updater.Available;
            if (found == null)
            {
                return;
            }
            UpdateInfo update = found.Value;
            if (UpdateInstall.CanInstall(update))
            {
                _ = FetchAndInstall(update, UpdateInstall.Current!);
                return;
            }
            if (!Updater.OpenPage(update))
            {
                // No browser to open, or it refused. Putting the address on the
                // line beats a button that appears to do nothing.
                Say(update.PageUrl, GuiTheme.Warm);
            }
        }

        private async Task FetchAndInstall(UpdateInfo update, IUpdateInstaller installer)
        {
            if (_updating)
            {
                return;
            }
            string number = VersionNumber();
            if (!installer.Allowed)
            {
                // This stage has to leave the line pressable: on a phone,
                // allowing this app as an install source is a Settings screen,
                // nothing here can wait for it, and the player comes back and
                // presses again.
                Say($"{number} -- allow installs from this app, then press again",
                    GuiTheme.Warm, pressable: true);
                installer.RequestPermission();
                return;
            }
            _updating = true;
            installer.Finished = (ok, message) => Dispatcher.UIThread.Post(() =>
            {
                _updating = false;
                Say(ok ? number : $"{number} -- {message}",
                    ok ? GuiTheme.TextDim : GuiTheme.Warm, pressable: !ok);
            });
            string label = update.AssetName.Length > 0 ? update.AssetName : update.Tag;
            Say($"{number} -- downloading {label}...", GuiTheme.Warm);
            var reported = new object();
            int shown = -1;
            void Progress(float fraction)
            {
                // Whole percents only, and only when one changes: this is
                // called for every 64 KB and each post crosses to the UI thread.
                int percent = fraction < 0 ? -1 : (int)(fraction * 100);
                lock (reported)
                {
                    if (percent == shown)
                    {
                        return;
                    }
                    shown = percent;
                }
                Dispatcher.UIThread.Post(() => Say(percent < 0
                    ? $"{number} -- downloading {label}..."
                    : $"{number} -- downloading {label}... {percent}%", GuiTheme.Warm));
            }
            string error = "";
            bool ready = await Task.Run(() => installer.Prepare(update, Progress, out error));
            if (!ready)
            {
                _updating = false;
                Say($"{number} -- {(error.Length > 0 ? error : "the download failed")}",
                    GuiTheme.Warm, pressable: true);
                return;
            }
            Say(installer.ExitAfterInstall
                ? $"{number} -- restarting to finish..."
                : $"{number} -- waiting for the system installer...", GuiTheme.Warm);
            if (!installer.Install(out error))
            {
                _updating = false;
                Say($"{number} -- {(error.Length > 0 ? error : "the install could not be started")}",
                    GuiTheme.Warm, pressable: true);
                return;
            }
            if (installer.ExitAfterInstall)
            {
                // The copying process is already running and waiting for this
                // one to be gone before it touches a single file. Staying open
                // would leave it waiting until its own deadline.
                Finish(default);
            }
        }
    }
}
