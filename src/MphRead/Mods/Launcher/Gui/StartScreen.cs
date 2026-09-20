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
    /// The front door to the game: a responsive FPS-style hub with direct
    /// routes to Play, Map Editor, Replay Studio, Settings and Quit.
    ///
    /// The hub is intentionally a presentation layer over the existing launch
    /// stack. Play, lobby, replay, setup and settings still own their existing
    /// behaviour while the shell is migrated around them, so a visual overhaul
    /// cannot quietly become a second implementation of networking or startup.
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
        private readonly Control _menu;
        private readonly HubHomeView _hub;
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

            // The home surface is now a game hub rather than a launcher card.
            // Child screens still use the established stack below, which keeps
            // the UI overhaul independent from launch/network behaviour.
            _hub = new HubHomeView();
            _hub.NavigateRequested += NavigateHub;
            _menu = _hub;
            root.Children.Add(_menu);

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
            _hub.RefreshProfile();
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
            HubMotion.Enter(view);
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
                HubMotion.Enter(top, lift: -6);
                Dispatcher.UIThread.Post(() => top.Focus(), DispatcherPriority.Background);
                return;
            }
            _overlay.IsVisible = false;
            _menu.IsVisible = true;
            _versionBox.IsVisible = true;
            ShowGround(true);
            _hub.RefreshProfile();
            RefreshVersionLine();
            HubMotion.Enter(_hub, lift: -6);
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

        private void NavigateHub(HubDestination destination)
        {
            switch (destination)
            {
                case HubDestination.Play:
                    OpenDeployment();
                    break;
                case HubDestination.MapEditor:
                    OpenMapEditorPlaceholder();
                    break;
                case HubDestination.ReplayStudio:
                    _ = OpenReplayStudio();
                    break;
                case HubDestination.Settings:
                    _ = OpenSettings();
                    break;
                case HubDestination.Quit:
                    AskToQuit();
                    break;
            }
        }

        private void OpenDeployment()
        {
            if (!GameFiles.Ready)
            {
                OpenSetup();
                return;
            }
            var view = new HubPlayView();
            view.Closed += (_, _) => Pop();
            view.Selected += destination =>
            {
                switch (destination)
                {
                    case HubPlayDestination.Multiplayer:
                        OpenMultiplayer();
                        break;
                    case HubPlayDestination.Offline:
                        OpenOffline();
                        break;
                    case HubPlayDestination.Adventure:
                        OpenAdventure();
                        break;
                }
            };
            Push(view);
        }

        private void OpenOffline()
        {
            var view = new HubOfflineView(_settings, _rooms);
            view.Closed += (_, _) => Pop();
            view.Launched += (_, plan) => Finish(plan);
            Push(view);
        }

        private void OpenAdventure()
        {
            var view = new HubAdventureView();
            view.Closed += (_, _) => Pop();
            view.Launched += (_, plan) => Finish(plan);
            Push(view);
        }

        private void OpenMultiplayer()
        {
            var view = new HubMultiplayerView();
            view.Closed += (_, _) => Pop();
            view.CreateLobbyRequested += (_, _) => OpenCreateServer();
            view.Launched += (_, plan) => ConnectedOrFinished(plan);
            Push(view);
        }

        private Task OpenPlay(PlayScreen.Face face = PlayScreen.Face.Online,
            bool singleFace = false)
        {
            if (!GameFiles.Ready)
            {
                OpenSetup();
                return Task.CompletedTask;
            }
            var view = new PlayScreen(_settings, _rooms, face,
                singleFace: singleFace);
            view.Closed += (_, _) => Pop();
            view.Launched += (_, plan) => ConnectedOrFinished(plan);
            view.CreateRequested += (_, _) => OpenCreateServer();
            Push(view);
            return Task.CompletedTask;
        }

        private Task OpenReplayStudio()
        {
            var view = new HubReplayStudioView();
            view.Closed += (_, _) => Pop();
            view.Launched += (_, plan) => Finish(plan);
            Push(view);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Create a custom multiplayer lobby. The screen is pushed over
        /// whichever Multiplayer surface opened it, so Back returns to the
        /// player's previous context without duplicating hosting state.
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
                    if (_stack.Count > 0)
                    {
                        if (_stack[^1] is PlayScreen play) play.SessionEnded(reason);
                        else if (_stack[^1] is HubMultiplayerView multiplayer)
                            multiplayer.SessionEnded(reason);
                    }
                };
                Push(_lobby);
            }
            else Finish(plan);
        }

        private Task OpenSettings()
        {
            var landing = new HubSettingsView();
            landing.Closed += (_, _) => Pop();
            landing.SectionRequested += section => OpenSettingsSection(section);
            Push(landing);
            return Task.CompletedTask;
        }

        private void OpenSettingsSection(string section)
        {
            var view = new SettingsView(_settings);
            view.ShowSection(section);
            view.Closed += (_, _) => Pop();
            view.GameFilesRequested += (_, _) =>
            {
                Pop();
                OpenSetup();
            };
            Push(view);
        }

        private void OpenMapEditorPlaceholder()
        {
            var view = new HubPlaceholderView(
                "MAP EDITOR",
                "WORKSHOP PLACEHOLDER",
                "A visual custom-map editor is planned for this hub. This button is intentionally a placeholder for now.");
            view.Closed += (_, _) => Pop();
            Push(view);
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
            var view = new ConfirmScreen("Quit Prime Hunters Online?");
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
