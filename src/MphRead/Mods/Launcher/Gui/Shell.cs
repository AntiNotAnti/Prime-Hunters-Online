using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MphRead.Mods.Network;
using MphRead.Mods.Render;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// One window, for the whole program.
    ///
    /// The loop this replaces was: open an Avalonia window, wait for it to
    /// close, open a GL window, run the match in it, destroy it, open another
    /// Avalonia window. Two toolkits, two windows, and a visible seam at every
    /// boundary -- the launcher vanished from the taskbar when a match
    /// started, the match vanished when it ended, and anything that went wrong
    /// in between left the player looking at an empty desktop.
    ///
    /// Now the GL window is opened once and never closed until the player
    /// leaves. The launcher is drawn inside it (<see cref="UiSurface"/>,
    /// <see cref="UiOverlay"/>), a match is a <see cref="Scene"/> built into
    /// that same window, and leaving a match unloads the scene and puts the
    /// front screen back up. The pause menu and the settings are screens in
    /// the same surface rather than borderless windows chasing the game's
    /// rectangle.
    ///
    /// This is the shape the Android head has always had -- one surface, one
    /// view stack over it, matches loaded and unloaded underneath -- which is
    /// why the screens needed no changes to be drawn here.
    ///
    /// Everything below runs on the game's own thread, between frames. The
    /// toolkit is set up on that thread and the screens post their work to its
    /// dispatcher, which <see cref="UiSurface.Tick"/> drains once a frame; a
    /// decision a screen makes is therefore acted on at the top of the next
    /// frame rather than in the middle of the one that is being drawn.
    /// </summary>
    internal static class Shell
    {
        /// <summary>True while the shell window is the one running.</summary>
        public static bool Active { get; private set; }

        /// <summary>Is a screen up and taking the input?</summary>
        public static bool UiVisible => UiSurface.Current?.Visible == true;

        /// <summary>The one window, for anything that needs to own a dialog.</summary>
        internal static RenderWindow? Window => _window;

        private static RenderWindow? _window;

        /// <summary>
        /// Hand the game window to whatever needs it as a parent.
        ///
        /// One thing does: a native file dialog has to be *owned* by the game
        /// window or Windows is free to put it behind a borderless-fullscreen
        /// game -- a modal dialog nobody can see, which from the player's side
        /// is a button that does nothing and then a program that has stopped
        /// answering. See <see cref="NativeFilePicker"/>.
        /// </summary>
        private static void PublishNativeHandle(RenderWindow window)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }
            try
            {
                unsafe
                {
                    NativeFilePicker.Owner = OpenTK.Windowing.GraphicsLibraryFramework.GLFW
                        .GetWin32Window(window.WindowPtr);
                }
            }
            catch (Exception)
            {
                // An owner is an improvement, not a requirement.
            }
        }
        private static StartScreen? _front;
        private static InGameMenu? _menu;
        private static MenuSettings _settings = new MenuSettings();
        private static IReadOnlyList<string> _rooms = Array.Empty<string>();
        private static LaunchPlan? _pending;
        private static bool _endMatch;
        private static bool _quit;
        internal static bool OpenStudioOnStart { get; set; }
        private static MapGen.MapDefinition? _studioPreview;
        internal static void PrepareStudioPreview(MapGen.MapDefinition definition) => _studioPreview = definition;
        // While a persistent-lobby client has finished its local room build but
        // the server is still waiting for the other participants, keep the
        // lobby/loading surface over the scene. Reveal on the committed
        // countdown edge instead of whichever frame the InMatch packet arrives.
        private static bool _matchLoading;

        /// <summary>
        /// Open the window and run until the player quits.
        ///
        /// False means there is nothing to run in: no display, no GL, or a
        /// toolkit that will not start on this machine. The text launcher
        /// plays the same matches and is what the caller falls back to.
        /// </summary>
        public static bool Run()
        {
            if (UiSurface.Ensure() == null)
            {
                return false;
            }
            LauncherPrefs.Load();
            // The backdrop is GL's from here on: this is the one head with a
            // window under the screens, and the photograph is worth the
            // window's own pixels rather than the screens' capped ones. Said
            // before the first screen is built, because it decides what goes
            // into the bake. See Mods.Render.LauncherPhoto.
            Mods.Render.LauncherPhoto.Enabled = true;
            if (GameFiles.Ready)
            {
                // Upstream's CheckSetup does this before anything runs; the
                // launcher is dispatched before that check, so it does it here
                // -- and tolerates the files being absent, which is the whole
                // reason it goes first.
                GameFiles.ApplyPaths();
                // A map added after the install was set up has no picture and
                // no sweep coming to give it one.
                ThumbnailGenerator.EnsureCustomPreviews();
            }
            // How the window opens: the way this one was left, unless the
            // command line said otherwise for this run.
            if (!Mods.WindowMode.StartupForced)
            {
                Mods.WindowMode.Startup = LauncherPrefs.WindowMode;
            }
            RenderWindow.LogCreatingWindow();
            RenderWindow? window = null;
            try
            {
                window = new RenderWindow(shell: true);
                PublishNativeHandle(window);
                _window = window;
                Active = true;
                OfflineRematch.StartNext = PlayAnother;
                ShowFrontScreen();
                window.Run();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"The window could not be opened: {ex.Message}");
                Mods.DebugLog.Exception("launcher", ex);
                return false;
            }
            finally
            {
                Active = false;
                OfflineRematch.StartNext = null;
                _window = null;
                _pending = null;
                _endMatch = false;
                _quit = false;
                _matchLoading = false;
                _studioPreview = null;
                OpenStudioOnStart = false;
                // Both own a worker thread and a bound socket; leaving the
                // program must not leave either behind.
                NetSession.Stop();
                NetHostSession.Stop();
                if (window != null)
                {
                    window.Context.MakeCurrent();
                    UiSurface.Current?.ReleaseMapRenderer();
                }
                _front?.Dispose();
                _front = null;
                window?.Dispose();
            }
        }

        // --------------------------------------------------------- the frame

        /// <summary>
        /// Act on what the screens decided since the last frame: start a
        /// match, end one, or leave.
        /// </summary>
        internal static void BeforeFrame(RenderWindow window)
        {
            if (!Active)
            {
                return;
            }
            if (_quit)
            {
                _quit = false;
                window.Close();
                return;
            }
            if (window.HasScene && NetSession.PersistentLobby && NetSession.IsInLobby && !_endMatch)
                EndNetworkMatchToLobby(window);
            if (window.HasScene && (NetSession.Refused || NetSession.SessionTimedOut)) _endMatch = true;
            if (_endMatch)
            {
                _endMatch = false;
                EndMatch(window);
            }
            if (_pending is LaunchPlan plan)
            {
                _pending = null;
                StartMatch(window, plan);
            }

            if (_matchLoading && window.HasScene)
            {
                if (!NetSession.FreezeGameplay)
                {
                    _matchLoading = false;
                    UiSurface.Current?.Hide();
                }
                else if (NetSession.Refused || NetSession.SessionTimedOut)
                {
                    _matchLoading = false;
                    _endMatch = true;
                }
            }
        }

        /// <summary>
        /// Draw whatever screen is up into the overlay texture.
        ///
        /// Not only in the shell: the pause menu is the same surface, and a
        /// window opened by one of the harness commands has one too.
        /// </summary>
        internal static void TickUi(RenderWindow window)
        {
            UiSurface? surface = UiSurface.Current;
            if (surface == null)
            {
                UiOverlay.Visible = false;
                return;
            }
            // The window's size, every frame, whether or not a screen is up.
            // It used to be read only while one was, so a window made bigger
            // during a match told the surface nothing: Escape then built the
            // pause menu against the size the window had when the match
            // started.
            surface.Resize(window.FramebufferSize.X, window.FramebufferSize.Y);
            surface.PrepareMapRenderer();
            NotePointerBasis(window);
            if (!surface.Visible)
            {
                UiOverlay.Visible = false;
                return;
            }
            surface.Tick();
        }

        private static int _basisWidth;
        private static int _basisHeight;

        /// <summary>
        /// The two sizes the pointer is converted between, once each time they
        /// change. A vertical bias of a few tens of pixels -- "I have to aim
        /// slightly above the row I want" -- is this ratio not being one, and
        /// it is invisible from inside the toolkit, whose own arithmetic is
        /// consistent either way.
        /// </summary>
        private static void NotePointerBasis(RenderWindow window)
        {
            int fw = window.FramebufferSize.X;
            int fh = window.FramebufferSize.Y;
            if (fw == _basisWidth && fh == _basisHeight)
            {
                return;
            }
            _basisWidth = fw;
            _basisHeight = fh;
            int cw = window.ClientSize.X;
            int ch = window.ClientSize.Y;
            double sx = cw > 0 ? fw / (double)cw : 1;
            double sy = ch > 0 ? fh / (double)ch : 1;
            Mods.DebugLog.Line("ui", $"pointer basis: framebuffer {fw}x{fh}, "
                + $"client {cw}x{ch}, pointer scaled by {sx:0.####}x{sy:0.####}"
                + (Math.Abs(sx - 1) > 0.001 || Math.Abs(sy - 1) > 0.001
                    ? " -- not 1, so clicks land off by that fraction" : ""));
        }

        private static EndPanelView? _endPanel;

        /// <summary>
        /// Whether the deck panel is up over the results, so the HUD's own
        /// picker knows to leave the right-hand side alone.
        ///
        /// Read by <c>ModDrawEndScreen</c>, which draws the ballot and the
        /// hunter as arrows and swatches beside a 32x32 sprite. Only that
        /// panel steps aside -- the scoreboard beside it is the engine's own
        /// screen and stays exactly as it is.
        /// </summary>
        public static bool EndPanelUp => _endPanel != null;

        /// <summary>
        /// Put the results panel up while the results are up, and take it down
        /// after.
        ///
        /// Driven from the frame rather than from whatever ends a match: the
        /// results screen arrives because the server said the match is over,
        /// and there is no one place on this machine that hears it. The state
        /// is already published -- EndScreen.Available is the same question
        /// the HUD asks -- so this watches it.
        ///
        /// Never over the pause menu. Escape during the results is a thing a
        /// player can still do, and two panels over one match is one too many.
        /// </summary>
        internal static void TickEndPanel()
        {
            bool want = _window?.HasScene == true && !_matchLoading
                && Mods.EndScreen.PanelAvailable && _menu == null;
            if (want && !EndPanelUp)
            {
                // Ensure, not Current: a session that went straight into a
                // match -- -connect, or anything else that never opened a
                // screen -- has no surface yet, and reading one that is not
                // there left the results with the HUD's own arrows and
                // swatches. Android's TickEndPanel does the same.
                UiSurface? surface = UiSurface.Ensure();
                if (surface == null)
                {
                    return;
                }
                var panel = new EndPanelView();
                _endPanel = panel;
                Mods.EndScreen.PanelUp = true;
                surface.Show(panel);
                return;
            }
            if (!want && EndPanelUp)
            {
                EndPanelView? closing = _endPanel;
                _endPanel = null;
                Mods.EndScreen.PanelUp = false;
                // The lobby or pause menu may already have replaced the
                // results panel on the shared UiSurface earlier in this frame.
                // Never hide that newer view while cleaning up the stale panel.
                UiSurface? surface = UiSurface.Current;
                if (surface != null && ReferenceEquals(surface.View, closing))
                {
                    surface.Hide();
                }
                return;
            }
            _endPanel?.Refresh();
        }

        // -------------------------------------------------------- the screens

        private static void ShowFrontScreen()
        {
            UiSurface? surface = UiSurface.Current;
            if (surface == null)
            {
                return;
            }
            PauseMenu.Reset();
            // Read again rather than reusing the object from the last time
            // round: the settings page opened from the pause menu loads and
            // commits its own copy, so after a match this one is stale and
            // would write the old values back over it.
            _settings = GameState.LoadSettings();
            // LoadSettings only fills in Features; the rest of the file
            // reaches the engine through Mods.GameSettings.
            Mods.GameSettings.Apply(_settings);
            LauncherPrefs.Load();
            if (_rooms.Count == 0 && GameFiles.Ready)
            {
                // Needs the game files: the room list is read out of them.
                _rooms = ThumbnailGenerator.MultiplayerRooms();
            }
            if (_window != null)
            {
                _window.Title = Mods.Branding.Name;
            }
            if (_front == null)
            {
                // Before the screen that offers "Random" as a hunter: the roll
                // is held for one launch so the joined server and the loaded
                // player agree, and this is where a launch begins.
                Hunters.Reroll();
                _front = new StartScreen(_settings, _rooms);
                _front.Done += (_, plan) => Decided(plan);
                _front.MatchRequested += (_, plan) => Decided(plan);
            }
            else
            {
                // One front screen for the life of the process, the way the
                // Android head has always kept one: Reset is what makes it
                // usable again -- the stack emptied, the hunter rerolled, the
                // room list and the version line read afresh.
                _front.Reset(_settings);
            }
            surface.Show(_front);
            if (OpenStudioOnStart)
            {
                OpenStudioOnStart = false;
                _front.OpenMapStudio();
            }
        }

        private static void Decided(LaunchPlan plan)
        {
            if (plan.Kind == LaunchKind.None)
            {
                RequestQuit();
                return;
            }
            _pending = plan;
        }

        // -------------------------------------------------------- the match

        /// <summary>
        /// What the running match was started from, so another can be started
        /// like it. See <see cref="PlayAnother"/>.
        /// </summary>
        private static LaunchPlan? _played;

        /// <summary>
        /// Whether "play the map the results screen picked" means anything
        /// here: an offline match of one's own, started from this shell.
        ///
        /// Online there is a server with a rotation and this is not its
        /// business; a story match and a demo have no next map to pick.
        /// </summary>
        public static bool CanPlayAnother => Active && _pending == null && !NetSession.Active
            && _played is LaunchPlan plan && plan.Kind == LaunchKind.Offline;

        /// <summary>
        /// Load another map, exactly as this match was loaded, with the room
        /// the results screen chose.
        ///
        /// The same two requests the pause menu's "Leave match" and the front
        /// screen's "Start" already use, sent together: the frame that ends the
        /// match starts the next one, so the launcher is built and hidden
        /// again without ever being drawn. Offline that is the whole of what a
        /// rotation is.
        /// </summary>
        public static bool PlayAnother(string roomKey)
        {
            if (!CanPlayAnother || _played is not LaunchPlan plan
                || !OfflineRematch.TryPlan(plan, roomKey, out var next))
            {
                return false;
            }
            _endMatch = true;
            _pending = next;
            return true;
        }

        private static void StartMatch(RenderWindow window, LaunchPlan plan)
        {
            _played = plan;
            _front?.SuspendLobby();
            _matchLoading = false;
            try
            {
                MapGen.MapDefinition? preview = _studioPreview;
                _studioPreview = null;
                if (preview != null)
                {
                    Metadata.RegisterStudioPreview(preview);
                }
                if (!MatchStart.Begin(window, _settings, plan))
                {
                    NetSession.ReportMatchLoadFailed("The map could not be loaded.");
                    EndMatch(window);
                    return;
                }

                if (NetSession.PersistentLobby && NetSession.IsStarting)
                {
                    // Do not reveal a locally loaded scene while another player
                    // is still loading. The authority already waits for every
                    // expected MatchLoaded ack; keeping the front surface up
                    // until it publishes InMatch removes the black gap and gives
                    // every participant the same visible start boundary.
                    _matchLoading = true;
                }
                else if (plan.Kind == LaunchKind.Demo && _front != null)
                {
                    _front.ShowReplayEditor(RequestEndMatch, () => UiSurface.Current?.Hide());
                    UiSurface.Current?.Show(_front);
                }
                else
                {
                    UiSurface.Current?.Hide();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"The game could not start: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                // The Windows build is a GUI binary with no console behind it,
                // so the two lines above reach nobody: from the player's side
                // the game simply disappears while a map is loading. This is
                // the one place that can still be read afterwards -- and the
                // whole reason the switch in the settings exists.
                Mods.DebugLog.Line("crash", "the match could not start");
                Mods.DebugLog.Exception("crash", ex);
                // Back to the front screen rather than out of the program: a
                // map that will not load is a reason to pick another one.
                NetSession.ReportMatchLoadFailed(ex.Message);
                EndMatch(window);
            }
        }

        private static void EndNetworkMatchToLobby(RenderWindow window)
        {
            _matchLoading = false;
            CloseMenu(); window.EndScene(); MatchStart.AfterMatch();
            NetSession.ResetMatchState(); PauseMenu.Reset();
            if (_front != null) { UiSurface.Current?.Show(_front); _front.ResumeLobby(); }
        }

        private static void EndMatch(RenderWindow window)
        {
            _matchLoading = false;
            CloseMenu();
            window.EndScene();
            // Both own a worker thread and a bound socket; a match that ends
            // any way at all must not leave either behind.
            NetSession.Stop();
            NetHostSession.Stop();
            MatchStart.AfterMatch();
            ShowFrontScreen();
        }

        /// <summary>The match is over; the launcher comes back.</summary>
        public static void RequestEndMatch()
        {
            if (!Active)
            {
                return;
            }
            _endMatch = true;
        }

        /// <summary>Leave the program.</summary>
        public static void RequestQuit()
        {
            _quit = true;
        }

        /// <summary>
        /// What the pause menu's "Leave match" and "Quit" mean to a window.
        ///
        /// In the shell they are the two requests above; in one of the harness
        /// windows, where there is no launcher to come back to, both still
        /// mean what they always meant -- close the window and end the run.
        /// </summary>
        internal static void LeaveMatch(OpenTK.Windowing.Desktop.GameWindow window)
        {
            if (Active)
            {
                RequestEndMatch();
                return;
            }
            window.Close();
        }

        internal static void Quit(OpenTK.Windowing.Desktop.GameWindow window)
        {
            if (Active)
            {
                RequestQuit();
                return;
            }
            window.Close();
        }

        // ---------------------------------------------------- the pause menu

        /// <summary>
        /// Open the in-game menu. False when there is no surface to draw it on
        /// -- Escape then keeps its old meaning rather than doing nothing.
        /// </summary>
        internal static bool OpenPauseMenu()
        {
            UiSurface? surface = UiSurface.Ensure();
            if (surface == null)
            {
                return false;
            }
            if (_menu != null)
            {
                return true;
            }
            // The settings page in the menu commits its own copy of the file,
            // so the one the menu is built with is read now rather than kept
            // from whenever the match started.
            ScenePlayerRegistry? players = _window is { HasScene: true } window
                ? window.Scene.Players : null;
            var menu = new InGameMenu(GameState.LoadSettings(), players);
            menu.Emptied += (_, _) => CloseMenu();
            _menu = menu;
            surface.Show(menu);
            return true;
        }

        /// <summary>Take it down and give the match its input back.</summary>
        internal static void CloseMenu()
        {
            if (_menu == null)
            {
                return;
            }
            _menu = null;
            UiSurface.Current?.Hide();
            PauseMenu.MarkClosed();
        }

        // ------------------------------------------------------------ capture

        private static string? _shotDirectory;
        private static int _shotStep;
        private static int _shotWait;

        /// <summary>
        /// Clicks and hovers the script asked for and the screen did not have.
        ///
        /// Counted rather than printed, because printed is what it was: the
        /// front screen's words became deck buttons, every predicate here went
        /// on matching nothing, and the capture wrote its pictures and exited
        /// zero for weeks while proving none of the pointer arithmetic it
        /// exists to prove. <c>-shellshot</c> now fails when a step could not
        /// press what it named.
        /// </summary>
        public static int ShotMisses { get; private set; }

        /// <summary>
        /// Photograph the shell rather than play in it: `-shellshot DIR`.
        ///
        /// <c>-uishot</c> renders the screens on their own and proves the
        /// layout; this proves what replaced the windows. It walks the whole
        /// loop the change is about -- front screen, a match loaded into the
        /// same window, the pause menu over it, leaving the match, the front
        /// screen again -- and photographs the *window* at each stop, so a
        /// picture that is black or a step that never arrives is a failure
        /// with a name rather than a report that "the launcher looks wrong".
        ///
        /// Escape is pressed on the front screen before anything else: on that
        /// screen it is the quit prompt, so a second picture that differs from
        /// the first is the keyboard reaching a screen that is no longer a
        /// window.
        /// </summary>
        public static void RequestShots(string directory)
        {
            _shotDirectory = directory;
            _shotStep = 0;
            _shotWait = 0;
            ShotMisses = 0;
            // No modal dialog may open while the script is running; see the
            // property, and ClickSettings for what presses one.
            NativeFilePicker.Suppressed = true;
            // The window is the capture's for the duration, not the player's:
            // the script maximizes it half way through, and a screenshot run
            // must not be how somebody's window size changes. See
            // WindowGeometry.Enabled.
            Mods.WindowGeometry.Enabled = false;
        }

        /// <summary>
        /// Between the draw and the buffer swap, which is the only moment the
        /// window's back buffer holds this frame. Called from both halves of
        /// the frame -- with a match and without one.
        ///
        /// A list of steps rather than a switch on a frame number: the script
        /// is a sequence, and every insertion into a numbered one renumbered
        /// the rest.
        /// </summary>
        internal static void AfterDraw(RenderWindow window)
        {
            Diagnostics.LauncherWindowCheck.AfterDraw(window);
            if (_shotDirectory == null)
            {
                return;
            }
            if (_shotWait > 0)
            {
                _shotWait--;
                return;
            }
            Action<RenderWindow>[] script = Script;
            if (_shotStep >= script.Length)
            {
                _shotDirectory = null;
                window.Close();
                return;
            }
            script[_shotStep++](window);
        }

        private static Vector2i _shotWindowedSize, _shotWindowedLocation;
        private static unsafe void CheckFullscreen(RenderWindow window, WindowStartMode mode)
        {
            var monitor = OpenTK.Windowing.Desktop.Monitors.GetMonitorFromWindow(window);
            var video = GLFW.GetVideoMode(monitor.Handle.ToUnsafePtr<Monitor>());
            bool attached = Mods.WindowMode.HasMonitor(window);
            bool native = mode == WindowStartMode.Fullscreen;
            bool fills = video != null
                && window.ClientSize.X == video->Width
                && (native
                    ? window.ClientSize.Y == video->Height
                    : Math.Abs(window.ClientSize.Y - video->Height) <= 1);
            bool shape = native
                ? attached && window.AutoIconify && !Mods.WindowMode.IsTopmost
                : !attached && !window.AutoIconify
                    && window.WindowBorder == OpenTK.Windowing.Common.WindowBorder.Hidden;
            if (!shape || !fills || Mods.WindowMode.Current != mode)
            {
                ShotMisses++;
                Console.WriteLine($"[shellshot] {mode} invalid: monitor={attached}, client={window.ClientSize}, "
                    + $"display={(video == null ? "unknown" : $"{video->Width}x{video->Height}")}, "
                    + $"border={window.WindowBorder}, autoIconify={window.AutoIconify}, topmost={Mods.WindowMode.IsTopmost}");
            }
            else Console.WriteLine($"[shellshot] {mode} covers the monitor with the expected focus policy: {window.ClientSize}");
        }
        private static void CheckWindowed(RenderWindow window)
        {
            if (Mods.WindowMode.HasMonitor(window) || Mods.WindowMode.IsFullscreen
                || window.ClientSize != _shotWindowedSize || window.Location != _shotWindowedLocation)
            {
                ShotMisses++;
                Console.WriteLine($"[shellshot] windowed geometry was not restored: {window.ClientSize} at {window.Location}");
            }
        }

        /// <summary>
        /// What the capture does, in order. Each step ends by saying how many
        /// frames to leave before the next one -- a window is not on the
        /// screen the moment it is created (a buffer read before the window
        /// manager has mapped it comes back black under Mesa), a room loads
        /// into a fade, and a window mode takes a few frames to settle.
        /// </summary>
        private static Action<RenderWindow>[] Script => new Action<RenderWindow>[]
        {
            _ => Wait(20),
            w =>
            {
                Shot(w, "shell-startup");
                var startup = _front?.GetVisualDescendants().OfType<PrimeStartupScreen>().FirstOrDefault();
                if (startup == null) { ShotMisses++; Console.WriteLine("[shellshot] startup gate was missing"); }
                else Key(Keys.Enter);
                Wait(12);
            },
            w => { Shot(w, "shell-startup-transition"); Wait(25); },
            _ =>
            {
                // Asset-free CI opens setup after the startup gate. Dismiss
                // that sheet separately so the next two Escapes exercise
                // opening and closing quit confirmation in either environment.
                if (_front?.Prime.Overlays.GetVisualDescendants().OfType<SetupScreen>().Any() == true)
                    Escape();
                Wait(10);
            },
            w =>
            {
                if (_front?.Prime is not { IsVisible: true, IsEnabled: true })
                { ShotMisses++; Console.WriteLine("[shellshot] startup did not reveal the shell"); }
                if (_front?.Prime.Overlays.IsOpen == true)
                { ShotMisses++; Console.WriteLine("[shellshot] startup overlay still blocks the shell"); }
                Shot(w, "shell-start"); Escape(); Wait(15);
            },
            // Escape on the front screen is the quit prompt, so a second
            // picture that differs from the first is the keyboard reaching a
            // screen that is no longer a window.
            w => { Shot(w, "shell-escape"); Escape(); Wait(10); },
            // The pointer resting on a word, pressing nothing. Every word
            // glides towards the accent and a little to the right over about
            // 140 ms, and this is the one picture that says so -- fifteen
            // frames is a quarter of a second, so the glide is over by the
            // time it is taken. Settings rather than Play, because Play starts
            // amber and was the only word whose reaction was ever visible.
            _ => { HoverFront(); Wait(15); },
            w => { Shot(w, "shell-hover"); Wait(2); },
            // A click on a word rather than a key: the pointer has its own
            // arithmetic between GLFW and a screen drawn into the frame, and a
            // picture of the settings page is that arithmetic being right.
            _ => { ClickSettings(); Wait(15); },
            w => { Shot(w, "shell-click"); Escape(); Wait(10); },
            // Resized, maximised and fullscreen, all on the front screen: a
            // screen that does not follow the window is the fault this
            // sequence exists for.
            w => { w.ClientSize = new Vector2i(1000, 620); Wait(20); },
            w => { Shot(w, "shell-resized"); w.WindowState = OpenTK.Windowing.Common.WindowState.Maximized; Wait(20); },
            w => { Shot(w, "shell-maximized"); w.WindowState = OpenTK.Windowing.Common.WindowState.Normal; Wait(20); },
            w =>
            {
                _shotWindowedSize = w.ClientSize; _shotWindowedLocation = w.Location;
                Mods.WindowMode.Set(w, WindowStartMode.Fullscreen); Wait(25);
            },
            // The click that matters: at 1.5x, where a pointer conversion off
            // by the scale factor puts every press in the corner of the screen
            // and nothing can be pressed at all. The windowed click above
            // cannot catch that -- the scale there is 1.
            w => { CheckFullscreen(w, WindowStartMode.Fullscreen); Shot(w, "shell-fullscreen"); ClickSettings(); Wait(15); },
            w =>
            {
                Shot(w, "shell-fullscreen-click");
                Escape();
                Mods.WindowMode.Toggle(w);
                Wait(25);
            },
            w =>
            {
                CheckWindowed(w); Shot(w, "shell-windowed");
                Mods.WindowMode.Set(w, WindowStartMode.BorderlessFullscreen); Wait(25);
            },
            w =>
            {
                CheckFullscreen(w, WindowStartMode.BorderlessFullscreen); Shot(w, "shell-borderless");
                Mods.WindowMode.Set(w, WindowStartMode.Fullscreen); Wait(20);
            },
            w =>
            {
                CheckFullscreen(w, WindowStartMode.Fullscreen);
                // Xvfb supplies an X server but no window manager. Asking GLFW
                // to iconify a monitor-owned fullscreen window there aborts
                // inside the native X11 path instead of simulating Alt+Tab.
                // The state we own is still testable here: switching from
                // borderless to normal fullscreen must keep the monitor
                // attached, set AutoIconify, and Sync must not mistake that
                // attached fullscreen window for an external fullscreen exit.
                Mods.WindowMode.Sync(w);
                if (!Mods.WindowMode.HasMonitor(w)
                    || Mods.WindowMode.Current != WindowStartMode.Fullscreen
                    || !w.AutoIconify)
                {
                    ShotMisses++;
                    Console.WriteLine("[shellshot] fullscreen focus policy lost its monitor/state");
                }
                Mods.WindowMode.Leave(w); Wait(25);
            },
            w => { CheckWindowed(w); Wait(5); },
            // F11 on the front screen. The shell takes the whole keyboard
            // while a screen is up, so this is the one gesture that has to be
            // handled before it does -- and it was not, which made fullscreen
            // the one thing the launcher could not do.
            w => { WindowKey(w, Keys.F11); Wait(20); },
            w =>
            {
                Shot(w, "shell-launcher-fullscreen");
                if (!Mods.WindowMode.IsFullscreen)
                {
                    ShotMisses++;
                    Console.WriteLine("[shellshot] F11 did not reach the window on the front screen");
                }
                WindowKey(w, Keys.F11);
                Wait(20);
            },
            // Through the screens rather than by handing the shell a plan:
            // picking a map is two presses now -- one to select the row, one
            // on the tick -- and the picture after the first of them has to be
            // the play screen and not a match.
            _ => { ClickIfReady(c => FrontAction(c, "PLAY")); Wait(20); },
            // A server that answered, and the drawer it opens beside the list.
            // The browser is the one face where the picture after the click is
            // not the same screen plus a highlight.
            w =>
            {
                Shot(w, "shell-play");
                // Directory availability is external to the layout smoke test.
                UiSurface.Current?.ClickOn(c => c is ServerRow row && row.IsLive);
                Wait(25);
            },
            w =>
            {
                Shot(w, "shell-server-side");
                ClickIfReady(c => c.GetValue(ControllerNav.NavIdProperty) == "multiplayer.create");
                Wait(15);
            },
            w =>
            {
                Shot(w, "shell-create-lobby");
                // Clean CI machines reach Game Files setup instead of Create Lobby.
                if (GameFiles.Ready && (_front?.Prime.Overlays.IsOpen != true || Mods.Render.LauncherHunter.Drawn))
                { ShotMisses++; Console.WriteLine("[shellshot] create lobby must cover the native hunter preview"); }
                Escape(); Wait(90);
            },
            w => { Shot(w, "shell-create-lobby-closed"); ClickIfReady(c => FrontAction(c, "OFFLINE")); Wait(15); },
            // A card, not a row: the offline face is every map at once now.
            // See DeckTile.
            // Land the pointer in the middle of the grid, then scroll it for
            // two seconds: this is the one step that measures a steady state
            // rather than photographing a moment. See Scroll.
            w =>
            {
                Shot(w, "shell-play-offline");
                UiSurface.Current?.PointerMoved(w.ClientSize.X / 2.0, w.ClientSize.Y / 2.0);
                Scroll(60);
                Scroll(60, 1);
                Wait(10);
            },
            w => { ClickIfReady(c => c.GetValue(ControllerNav.NavIdProperty) == "offline.map"); Wait(25); },
            w =>
            {
                // Still the play screen, with the drawer open beside it: a
                // press on a card picks it and starts nothing. A picture of a
                // room here is the regression.
                Shot(w, "shell-play-selected");
                ClickIfReady(c => c is UiListRow);
                if (GameFiles.Ready) Key(Keys.Enter);
                ClickIfReady(c => c.GetValue(ControllerNav.NavIdProperty) == "offline.start");
                Wait(40);
            },
            w =>
            {
                if (w.HasScene)
                {
                    Wait(0);
                    return;
                }
                // CI intentionally has no extracted game assets. In that
                // environment the shell/UI checks above are the complete test:
                // there is no legal room to start, so falling through to a
                // direct match request only converts "no assets" into a false
                // failure.
                if (!GameFiles.Ready || _rooms.Count == 0)
                {
                    Console.WriteLine("[shellshot] no game files; screen-only checks complete");
                    _shotDirectory = null;
                    w.Close();
                    return;
                }

                // A real installation had rooms available but the UI failed
                // to start one. Fall back to the direct shell plan so the rest
                // of the match/pause/result lifecycle can still be exercised,
                // while the earlier missed press remains visible in ShotMisses.
                Console.WriteLine("[shellshot] the play screen started nothing; asking directly");
                if (!StartShotMatch())
                {
                    ShotMisses++;
                    Console.WriteLine("[shellshot] game files are ready but no playable room was available");
                    _shotDirectory = null;
                    w.Close();
                    return;
                }
                Wait(40);
            },
            // Made bigger *during* the match, with no screen up: the surface
            // hears nothing about the window until a screen is shown again,
            // which is the state Escape then has to size itself against.
            w => { Shot(w, "shell-match"); w.WindowState = OpenTK.Windowing.Common.WindowState.Maximized; Wait(30); },
            w => { PauseMenu.HandleEscape(w); Wait(20); },
            w =>
            {
                Shot(w, "shell-pause-maximized");
                PauseMenu.HandleEscape(w);
                w.WindowState = OpenTK.Windowing.Common.WindowState.Normal;
                Wait(30);
            },
            w => { Mods.WindowMode.Toggle(w); Wait(30); },
            // The pause menu over a fullscreen match, which is where a menu
            // drawn into the wrong viewport shows up worst.
            w => { Shot(w, "shell-match-fullscreen"); PauseMenu.HandleEscape(w); Wait(20); },
            w => { Shot(w, "shell-pause-fullscreen"); Mods.WindowMode.Toggle(w); Wait(30); },
            // The settings, in a match and in the smallest window the
            // sequence uses: the page that has to fit is this one, and the
            // in-game screens are drawn larger than the launcher's.
            w => { Shot(w, "shell-pause"); Click(c => FrontAction(c, "SETTINGS")); Wait(20); },
            w => { Shot(w, "shell-settings-ingame"); Escape(); Wait(15); },
            // The match *ending*, in fullscreen, rather than being left: a
            // different path out (the engine fades and quits the scene itself)
            // and the one the front screen comes back from. "The text is small
            // again in the launcher when the game finishes" was about this
            // frame and not about the one Leave match produces.
            w => { Escape(); Mods.WindowMode.Toggle(w); Wait(30); },
            // The results, held rather than allowed to run their clock: the
            // engine's own scoreboard on the left and the deck panel on the
            // right, which is the one picture that shows the two halves
            // together. The real screen runs for ten seconds and then
            // rotates, and a capture that has to be caught inside ten seconds
            // is a capture that fails on a slow machine.
            _ => { HoldResults(); Wait(60); },
            w =>
            {
                Shot(w, "shell-endgame");
                // Some rule sets do not expose character changes at results.
                UiSurface.Current?.ClickOn(c => c is DeckButton tab && tab.Text == "Change hunter");
                Wait(30);
            },
            w => { Shot(w, "shell-endgame-hunter"); MapPick.Reset(); ReleaseResults(); Wait(90); },
            w =>
            {
                CheckShotRematch(w, _played?.RoomKey);
                Shot(w, "shell-bot-rematch"); HoldResults(); Wait(20);
            },
            _ =>
            {
                _shotNextRoom = MapPick.Order.FirstOrDefault(room => !MapPick.IsReturnToLobby(room));
                if (_shotNextRoom != null) MapPick.Choose(MapPick.IndexOf(_shotNextRoom));
                ReleaseResults(); Wait(90);
            },
            w =>
            {
                CheckShotRematch(w, _shotNextRoom);
                Shot(w, "shell-bot-next-map"); RequestEndMatch(); Wait(90);
            },
            w => { Shot(w, "shell-back-fullscreen"); Mods.WindowMode.Toggle(w); Wait(25); },
            w => { Shot(w, "shell-back"); Wait(5); }
        };

        private static void Wait(int frames)
        {
            _shotWait = frames;
        }

        // Setup and quit confirmation are dismissed before targeting the header.
        private static void ClickSettings() => Click(c => FrontAction(c, "SETTINGS"));
        private static void HoverFront() => Hover(c => FrontAction(c, "SETTINGS"));

        /// <summary>
        /// The steps that need a room to load. Skipped rather than failed
        /// where there are no game files: a browser with no rooms in it has no
        /// row to select and no tick to press, and that is the machine CI is,
        /// not a fault.
        /// </summary>
        private static bool FrontAction(Control control, string label) =>
            control is HubNavButton hub
                && String.Equals(hub.Label, label, StringComparison.OrdinalIgnoreCase)
            || control is DeckButton deck
                && String.Equals(deck.Text, label, StringComparison.OrdinalIgnoreCase);

        private static void ClickIfReady(Func<Control, bool> match)
        {
            if (GameFiles.Ready)
            {
                Click(match);
            }
        }

        private static void Hover(Func<Control, bool> match)
        {
            if (!(UiSurface.Current?.HoverOn(match) ?? false))
            {
                ShotMisses++;
                Console.WriteLine("[shellshot] nothing on screen matched the hover");
            }
        }

        private static void Click(Func<Control, bool> match)
        {
            if (!(UiSurface.Current?.ClickOn(match) ?? false))
            {
                ShotMisses++;
                Console.WriteLine("[shellshot] nothing on screen matched the click");
            }
        }

        private static void Key(Keys key)
        {
            UiSurface? surface = UiSurface.Current;
            surface?.KeyDown(key, Avalonia.Input.RawInputModifiers.None);
            surface?.KeyUp(key, Avalonia.Input.RawInputModifiers.None);
        }

        /// <summary>
        /// Scroll whatever is under the pointer, a notch at a time, for a
        /// number of frames.
        ///
        /// Here because "the grid does not scroll smoothly" is a report about
        /// a *steady state* and every other step in this harness is a single
        /// act. A notch a frame across a screenful of cards is what the
        /// player is doing when they say it is slow, and the debug log's
        /// per-second `ui` line is what answers it -- which is why this is a
        /// step rather than something measured by hand.
        /// </summary>
        private static void Scroll(int frames, double notches = -1)
        {
            UiSurface? surface = UiSurface.Current;
            if (surface == null)
            {
                return;
            }
            for (int i = 0; i < frames; i++)
            {
                surface.PointerWheel(0, notches);
                Wait(1);
            }
        }

        /// <summary>
        /// A key put through the *window's* handler rather than handed to the
        /// surface.
        ///
        /// <see cref="Key"/> is for testing a screen and goes straight to
        /// UiSurface; that skips everything the window claims first, so it can
        /// never catch a key the window was supposed to take and did not.
        /// </summary>
        private static void WindowKey(RenderWindow window, Keys key)
        {
            window.FeedKey(new OpenTK.Windowing.Common.KeyboardKeyEventArgs(
                key, scanCode: 0, 0,
                isRepeat: false));
        }

        private static void Escape()
        {
            UiSurface? surface = UiSurface.Current;
            surface?.KeyDown(OpenTK.Windowing.GraphicsLibraryFramework.Keys.Escape,
                Avalonia.Input.RawInputModifiers.None);
            surface?.KeyUp(OpenTK.Windowing.GraphicsLibraryFramework.Keys.Escape,
                Avalonia.Input.RawInputModifiers.None);
        }

        /// <summary>An offline match on the first room the build knows.</summary>
        private static bool StartShotMatch()
        {
            if (_rooms.Count == 0)
            {
                return false;
            }
            Decided(new LaunchPlan
            {
                Kind = LaunchKind.Offline,
                RoomKey = _rooms[0],
                Mode = GameMode.Battle,
                Hunter = Hunter.Samus,
                Bots = 1,
                BotLevel = 1
            });
            return true;
        }

        /// <summary>
        /// End the match the way running out of time does: the engine plays
        /// its own end sequence, fades and quits the scene. Shortened to a
        /// fifth of a second of results, since the capture is about what comes
        /// after it.
        /// </summary>
        /// <summary>
        /// Put the match at its results and keep it there.
        ///
        /// The continuation check shortens the results to a fifth of a
        /// second because the capture it belongs to is about what comes
        /// *after* them. This one is about the results themselves, so the
        /// clock is given thirty seconds instead -- the screen it photographs
        /// is the engine's scoreboard and the deck panel beside it, and both
        /// have to be settled before the shutter.
        ///
        /// The ballot is begun by hand as well: on a server it arrives in a
        /// packet, and there is no server here.
        /// </summary>
        private static void HoldResults()
        {
            // `Ending`, not `GameOver`. Both satisfy EndScreen.Available, so
            // the deck panel comes up either way -- but the HUD draws the
            // scoreboard on `Ending` and only the words GAME OVER on
            // `GameOver`, and the scoreboard is the half of this picture the
            // theme does not touch. A capture without it is a capture of half
            // the screen.
            GameState.MatchState = MatchState.Ending;
            GameState.MatchTime = 30;
            try
            {
                MapPick.Begin(_settings.RoomKey ?? "", open: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[shellshot] no ballot to show: {ex.Message}");
            }
        }

        /// <summary>Let the clock run again, so the sequence can leave.</summary>
        private static void ReleaseResults()
        {
            GameState.MatchTime = 0.2f;
        }

        private static string? _shotNextRoom;
        private static void CheckShotRematch(RenderWindow window, string? expected)
        {
            int expectedId = expected == null ? -1 : Metadata.GetRoomByName(expected).Item2;
            if (!window.HasScene || GameState.MatchState != MatchState.InProgress
                || expectedId < 0 || window.Scene.RoomId != expectedId)
            {
                ShotMisses++;
                Console.WriteLine($"[shellshot] bot rematch failed: expected {expected}, scene={window.HasScene}, state={GameState.MatchState}");
            }
            else Console.WriteLine($"[shellshot] bot rematch loaded {expected}");
        }

        private static void Shot(RenderWindow window, string name)
        {
            string path = System.IO.Path.Combine(_shotDirectory!, $"{name}.png");
            System.IO.Directory.CreateDirectory(_shotDirectory!);
            Console.WriteLine($"[shellshot] {name}: pixels={window.FramebufferSize} "
                + $"scene={(window.HasScene ? window.Scene.Size.ToString() : "no match")} "
                + $"{UiSurface.Current?.Describe()}");
            bool saved = Mods.ScreenCapture.SaveWindow(
                window.FramebufferSize.X, window.FramebufferSize.Y, path);
            Console.WriteLine(saved
                ? $"[shellshot] {path}"
                : $"[shellshot] {name} could not be read from the window");
        }

        // -------------------------------------------------------------- input

        public static void PointerMoved(double x, double y)
        {
            UiSurface.Current?.PointerMoved(x, y);
        }

        public static void PointerButton(MouseButton button, double x, double y, bool down)
        {
            UiSurface? surface = UiSurface.Current;
            if (surface == null)
            {
                return;
            }
            // Where the click landed, before the click itself: the pointer is
            // the system's while a screen is up, so this is the first the
            // toolkit hears of it if the player moved and clicked between two
            // frames.
            surface.PointerMoved(x, y);
            surface.PointerButton(Translate(button), down);
        }

        public static void PointerWheel(double deltaX, double deltaY)
        {
            UiSurface.Current?.PointerWheel(deltaX, deltaY);
        }

        public static void KeyDown(KeyboardKeyEventArgs e)
        {
            UiSurface.Current?.KeyDown(e.Key, Modifiers(e));
        }

        public static void KeyUp(KeyboardKeyEventArgs e)
        {
            UiSurface.Current?.KeyUp(e.Key, Modifiers(e));
        }

        public static void TextInput(string text)
        {
            UiSurface.Current?.TextInput(text);
        }

        private static Avalonia.Input.MouseButton Translate(MouseButton button)
        {
            return button switch
            {
                MouseButton.Button2 => Avalonia.Input.MouseButton.Right,
                MouseButton.Button3 => Avalonia.Input.MouseButton.Middle,
                MouseButton.Button4 => Avalonia.Input.MouseButton.XButton1,
                MouseButton.Button5 => Avalonia.Input.MouseButton.XButton2,
                _ => Avalonia.Input.MouseButton.Left
            };
        }

        private static Avalonia.Input.RawInputModifiers Modifiers(KeyboardKeyEventArgs e)
        {
            Avalonia.Input.RawInputModifiers modifiers = Avalonia.Input.RawInputModifiers.None;
            if (e.Shift)
            {
                modifiers |= Avalonia.Input.RawInputModifiers.Shift;
            }
            if (e.Control)
            {
                modifiers |= Avalonia.Input.RawInputModifiers.Control;
            }
            if (e.Alt)
            {
                modifiers |= Avalonia.Input.RawInputModifiers.Alt;
            }
            return modifiers;
        }
    }
}
