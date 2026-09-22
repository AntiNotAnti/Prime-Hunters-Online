using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Avalonia.Android;
using MphRead.Mods;
using MphRead.Mods.Launcher;
using MphRead.Mods.Network;
using MovingBackdrop = MphRead.Mods.Launcher.Gui.MovingBackdrop;

namespace MphRead.Droid
{
    /// <summary>
    /// The Android entry point. There is no <c>Main</c> here: Android starts an
    /// activity, and Avalonia's own base class stands the toolkit up on the UI
    /// thread it is given.
    ///
    /// That is the whole difference from the desktop heads, where
    /// <c>GuiLauncher</c> sets Avalonia up on the game's thread and drives a
    /// loop of launcher-then-match. Here the two halves are two views in one
    /// activity: the launcher is Avalonia, the match is a
    /// <see cref="GameView"/> with its own GL thread, and starting one hides
    /// the other. Two activities would have meant handing a
    /// <see cref="LaunchPlan"/> across a process boundary for no gain.
    /// </summary>
    [Activity(
        Label = "Project Prime",
        // Must be an AppCompat descendant: Avalonia's activity is an AndroidX
        // AppCompatActivity and throws out of onCreate under anything else.
        // See Resources/values/styles.xml.
        Theme = "@style/ProjectPrime",
        MainLauncher = true,
        // Landscape, from the icon onwards, and not only for a match.
        //
        // The match already asked for it -- a first-person game on a phone is
        // landscape -- and the launcher was left to the sensor, which meant
        // the program started portrait, turned sideways to play and turned
        // back when the match ended. That is a rotation the player did not
        // ask for at each end of every match, and on this head a rotation is
        // a real cost: the activity handles the configuration change itself,
        // so every one of them re-measures the whole Avalonia tree and
        // re-bakes the backdrop at the new shape.
        //
        // It is also the shape the screens want. They are authored for a box
        // wider than it is tall (UiLayout.MinBoxWidth by MinBoxHeight), and
        // portrait gives UiScaleHost the least of both: 390 points across a
        // phone held upright is a 0.4 factor before the clamp, so the screens
        // come out at the floor with the box no bigger for it. Sensor rather
        // than plain Landscape so the phone can still be held either way up.
        ScreenOrientation = ScreenOrientation.SensorLandscape,
        LaunchMode = LaunchMode.SingleTop,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize
            | ConfigChanges.UiMode | ConfigChanges.Density | ConfigChanges.KeyboardHidden)]
    public class MainActivity : AvaloniaMainActivity,
        Android.Hardware.Display.DisplayManager.IDisplayListener
    {
        internal static MainActivity? Instance { get; private set; }

        private ViewGroup? _content;
        private View? _launcherView;
        private GameView? _gameView;
        private TouchOverlayView? _overlay;
        private TextView? _notice;
        private volatile bool _renderingPreviews;
        private volatile bool _renderingHere;

        /// <summary>
        /// Asked of the in-process preview run between rooms: put it down, a
        /// player wants to play.
        ///
        /// See <see cref="StartMatch"/>. It is set from the UI thread and read
        /// from the render thread, and it is cleared by whoever set it down --
        /// <see cref="RenderHere"/>'s finally -- so that a later run is not
        /// born already cancelled.
        /// </summary>
        private volatile bool _stopPreviews;
        private readonly TouchControls _controls = new TouchControls();
        // What the launcher is in, which is what a match puts back when it
        // ends. The activity itself asks for landscape now, so this is what
        // RequestedOrientation reads back as; it is stated rather than left
        // Unspecified so that a match ending before one ever started cannot
        // hand the front screen an orientation the manifest never asked for.
        private ScreenOrientation _orientationBefore = ScreenOrientation.SensorLandscape;

        internal bool InMatch => _gameView != null;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            Instance = this;
            base.OnCreate(savedInstanceState);
            GamepadBridge.Start(this);
            MphRead.Mods.Input.GamepadContexts.MenuVisible = true;
            // The desktop builds missing map binaries from ModEntry.TryHandle;
            // this head has no Main for that to live in. Off the UI thread:
            // it reads the extracted game files and writes three binaries per
            // map, and only the first launch after a map changes does any work.
            System.Threading.Tasks.Task.Run(AndroidMaps.EnsureBuilt);
            _content = FindViewById(Android.Resource.Id.Content) as ViewGroup;
            _launcherView = _content?.GetChildAt(0);
        }

        /// <summary>
        /// Render map previews on this device and give the launcher its
        /// progress back.
        ///
        /// Nothing is shown while it runs and nothing is rotated: the work goes
        /// to <see cref="PreviewService"/> workers in processes of their own,
        /// drawing into offscreen pbuffers, and the only thing this process
        /// does is watch the cache directory fill up. Whatever they could not
        /// produce is rendered here afterwards, on a background thread with an
        /// offscreen context of its own, so a device that will not start
        /// services still gets its pictures.
        /// </summary>
        internal Task<int> RenderPreviews(IReadOnlyList<string> rooms, Action<string> report)
        {
            if (rooms.Count == 0 || _renderingPreviews)
            {
                return Task.FromResult(0);
            }
            _renderingPreviews = true;
            void Report(string line) => RunOnUiThread(() => report(line));
            // A preview run is every core the device has, in worker processes
            // with GL contexts of their own; the front screen's moving layer
            // is the one thing this process does a frame while it waits, and
            // it is what made the run take minutes.
            RunOnUiThread(() => MovingBackdrop.Suspended = true);
            // The workers are ordinary services, and a device that goes to
            // sleep throttles them; the run is long enough for that to matter.
            RunOnUiThread(() => Window?.AddFlags(WindowManagerFlags.KeepScreenOn));
            return Task.Run(() =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    ThumbnailGenerator.EnsureCacheDirectory();
                    int written = PreviewWorkers.Run(this, rooms,
                        PreviewRun.Width, PreviewRun.Height, Report);
                    var left = new List<string>();
                    for (int i = 0; i < rooms.Count; i++)
                    {
                        if (!ThumbnailGenerator.Exists(rooms[i]))
                        {
                            left.Add(rooms[i]);
                        }
                    }
                    if (left.Count > 0)
                    {
                        // The workers were killed or would not start. One room
                        // at a time from here, which is why it is worth saying
                        // how many ended up on this path.
                        Report($"[thumbnails] {left.Count} left to render here");
                        written += RenderHere(left, Report);
                    }
                    Report($"[thumbnails] {written}/{rooms.Count} in "
                        + $"{clock.Elapsed.TotalSeconds:0.0}s");
                    return written;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[thumbnails] the run failed: {ex}");
                    Report($"[thumbnails] {ex.Message}");
                    return 0;
                }
                finally
                {
                    _renderingPreviews = false;
                    RunOnUiThread(() =>
                    {
                        if (!InMatch)
                        {
                            MovingBackdrop.Suspended = false;
                            Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
                        }
                    });
                }
            });
        }

        /// <summary>
        /// The fallback: render in this process, on this thread, with an
        /// offscreen context.
        ///
        /// A match cannot run at the same time -- one process holds one world,
        /// and <see cref="Mods.ThumbnailMode"/> is on while this runs -- so
        /// <see cref="StartMatch"/> puts it down rather than queueing behind
        /// it: the run is asked to stop between rooms (<see cref="_stopPreviews"/>)
        /// and the match starts as soon as the room in flight is finished with.
        /// </summary>
        private int RenderHere(IReadOnlyList<string> rooms, Action<string> report)
        {
            if (InMatch || _pending != null)
            {
                // `_pending` as well as the match itself: a start that has been
                // asked for and is waiting for the window is a match about to
                // own the world, and a run begun in that gap would be one this
                // process then has to stop again.
                report("[thumbnails] not while a match is running");
                return 0;
            }
            _renderingHere = true;
            // Nothing has asked this run to stop yet; a request that arrived
            // while there was no run to receive it is not one against this.
            _stopPreviews = false;
            try
            {
                using var gl = OffscreenGl.Create(PreviewRun.Width, PreviewRun.Height);
                return PreviewRun.Render(rooms, PreviewRun.Width, PreviewRun.Height, report,
                    () => _stopPreviews);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[thumbnails] the offscreen context failed: {ex}");
                report($"[thumbnails] {ex.Message}");
                return 0;
            }
            finally
            {
                _renderingHere = false;
                _stopPreviews = false;
            }
        }

        // --------------------------------------------------------- rotation

        /// <summary>
        /// The display rotation the window was last laid out for.
        ///
        /// Turning a phone end for end while playing is a **180 degree**
        /// rotation, and it is the only one a match can see: the orientation
        /// is `SensorLandscape`, so landscape is the only shape on offer. That
        /// turn is invisible to everything an app would normally hear about
        /// it. `Configuration.orientation` does not change -- landscape to
        /// landscape -- so there is no configuration change and no
        /// `OnConfigurationChanged`; the window keeps the same size, so no
        /// view gets an `OnSizeChanged`; and the GL thread owns its own EGL
        /// surface, so the match carries on drawing regardless. The one thing
        /// that *does* change is the display's rotation, and
        /// `DisplayManager` is the only thing that reports it.
        ///
        /// That silence is the bug it is here for: the touch controls
        /// stopped answering after a 180 while the game kept running
        /// perfectly. Two things can do that and this answers both.
        ///
        /// - **The window's input transform is stale.** A rotation that does
        ///   not resize the window is finalised when the window next draws,
        ///   and this window is a `SurfaceView` under a control overlay that
        ///   repaints only when a finger moves -- so it may never draw again
        ///   on its own, and touches keep arriving in the old orientation's
        ///   coordinates. Asking for a layout pass is what settles it.
        /// - **Pointers are stranded.** Rotating a phone means holding it,
        ///   which means thumbs on the glass; if the gesture is interrupted
        ///   without an UP or a CANCEL, `_stickPointer` and `_aimPointer`
        ///   keep ids that will never be released and every later touch falls
        ///   through <c>PointerDown</c> doing nothing.
        /// </summary>
        private int _lastRotation = -1;

        private Android.Hardware.Display.DisplayManager? _displays;

        private int CurrentRotation()
        {
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    return (int?)Display?.Rotation ?? -1;
                }
#pragma warning disable CA1422 // the pre-30 way, for the devices that need it
                return (int?)WindowManager?.DefaultDisplay?.Rotation ?? -1;
#pragma warning restore CA1422
            }
            catch (Exception)
            {
                return -1;
            }
        }

        public void OnDisplayAdded(int displayId)
        {
        }

        public void OnDisplayRemoved(int displayId)
        {
        }

        /// <summary>
        /// Fires for a rotation, and for a great many things that are not one
        /// -- a refresh rate change, an HDR mode, a resize. So the rotation is
        /// compared rather than assumed, and nothing is done when it has not
        /// moved.
        /// </summary>
        public void OnDisplayChanged(int displayId)
        {
            int rotation = CurrentRotation();
            if (rotation < 0 || rotation == _lastRotation)
            {
                return;
            }
            int before = _lastRotation;
            _lastRotation = rotation;
            if (before < 0)
            {
                // The first reading is not a turn.
                return;
            }
            Console.WriteLine($"[android] display rotation {before} -> {rotation} at "
                + $"{ContentSize.Width}x{ContentSize.Height}");
            AfterRotation();
            // Again once the window has had a chance to settle. The insets
            // move on a rotation even when the size does not -- a cutout
            // changes sides -- and the second pass is what catches a layout
            // that arrives after this callback rather than before it.
            _content?.PostDelayed(AfterRotation, SettleMs);
        }

        private void AfterRotation()
        {
            // Nothing held survives the turn. See _lastRotation: a thumb that
            // was on the glass through the rotation may never send its
            // release, and a stranded pointer id is a control that answers
            // nothing for the rest of the match.
            _controls.ReleaseEverything();
            // A layout pass on the window, which is what finalises the
            // rotation for input. The overlay is asked separately because its
            // size has not changed, so it would not otherwise re-read it.
            _overlay?.Refresh();
            _content?.RequestLayout();
            _content?.Invalidate();
            Window?.DecorView?.RequestLayout();
            // The system puts the bars back on a rotation.
            GoImmersive(true);
        }

        /// <summary>
        /// Delivered when the *configuration* changes, which a 180 does not
        /// cause -- but a 90 does, and so does a fold, a screen resize and a
        /// theme change. The rotation check inside makes this harmless when
        /// nothing turned.
        /// </summary>
        public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);
            OnDisplayChanged(0);
        }

        protected override void OnPause()
        {
            if (_displays != null)
            {
                _displays.UnregisterDisplayListener(this);
                _displays = null;
            }
            _gameView?.OnPause();
            base.OnPause();
        }

        protected override void OnResume()
        {
            base.OnResume();
            // Immersive whether or not a match is running.
            //
            // It used to be turned on for a match and off again for the front
            // screen, which left the status bar and the gesture pill over the
            // launcher -- and the system puts them back on its own after a
            // rotation or a swipe from the edge, so this is asked for again on
            // every resume rather than once at startup.
            GoImmersive(true);
            _lastRotation = CurrentRotation();
            if (_displays == null
                && GetSystemService(DisplayService) is Android.Hardware.Display.DisplayManager manager)
            {
                _displays = manager;
                // No handler: the callback is wanted on the main looper, which
                // is where a null one puts it, and everything it does is view
                // work that belongs on the UI thread anyway.
                manager.RegisterDisplayListener(this, null);
            }
            _gameView?.OnResume();
        }

        /// <summary>
        /// Every key event the window gets, offered to the pad first.
        ///
        /// Here rather than in <see cref="GameView"/> -- which is where it
        /// used to be, and still is as a fallback -- because a pad has to
        /// reach the *settings screen* too: rebinding a button means pressing
        /// it while a launcher view has the focus and no GameView exists at
        /// all. An activity's dispatch is the one place both are underneath.
        ///
        /// Only a pad's events are taken. <see cref="GamepadBridge.HandleKey"/>
        /// answers false for anything a keyboard could have produced, so chat
        /// and the Back button are untouched.
        /// </summary>
        public override bool DispatchKeyEvent(KeyEvent? e)
        {
            bool down = e?.Action == KeyEventActions.Down;
            if (e != null && (down || e.Action == KeyEventActions.Up)
                && GamepadBridge.HandleKey(e.KeyCode, e, down))
            {
                if (down)
                {
                    _controls.NotePadActivity();
                }
                return true;
            }
            return base.DispatchKeyEvent(e);
        }

        /// <summary>
        /// The sticks, the triggers and the hat, for the same reason.
        ///
        /// The controls only step aside for a pad that is actually being
        /// held: a stick resting off centre is every pad, and taking the touch
        /// controls away for it would be taking them away from a phone with a
        /// pad in a drawer. See <c>GamepadInput.InUse</c>.
        /// </summary>
        public override bool DispatchTouchEvent(MotionEvent? e)
        {
            if (e?.ActionMasked == MotionEventActions.Down)
                MphRead.Mods.Input.InputSourceTracker.Note(MphRead.Mods.Input.InputSource.Touch);
            return base.DispatchTouchEvent(e);
        }

        public override bool DispatchGenericMotionEvent(MotionEvent? e)
        {
            if (GamepadBridge.HandleMotion(e))
            {
                if (MphRead.Mods.Input.GamepadInput.InUse)
                {
                    _controls.NotePadActivity();
                }
                return true;
            }
            return base.DispatchGenericMotionEvent(e);
        }

        /// <summary>
        /// Ask for immersive again whenever the window becomes ours.
        ///
        /// OnResume is too early: the toolkit sets the window up after it and
        /// the bars come back. The system also brings them back by itself
        /// after a swipe from the edge, a rotation, or a notification shade
        /// being pulled down and let go, and each of those ends with focus
        /// returning here. This is the one callback that fires for all of it.
        /// </summary>
        public override void OnWindowFocusChanged(bool hasFocus)
        {
            base.OnWindowFocusChanged(hasFocus);
            MphRead.Mods.Input.GamepadContexts.Focused = hasFocus;
            if (!hasFocus) GamepadBridge.Clear();
            if (hasFocus)
            {
                GoImmersive(true);
            }
        }

        protected override void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
            // Leaving the app is the other way out of a match, and it has the
            // same problem EndMatch had: the render thread is a background
            // thread that outlives this activity, so quitting from the pause
            // menu (or by the system taking the activity down) left the room
            // loaded and its music and SFX playing with nothing on screen.
            //
            // Stopping the loop rather than tearing the sound down from here:
            // Sfx is not thread-safe and the GL thread owns it, so it is asked
            // to shut itself down on its own thread, which is what
            // Scene.DoCleanup does at the end of the loop.
            _gameView?.Stop();
            GamepadBridge.Stop();
            base.OnDestroy();
        }

        // Obsolete on 33+ in favour of OnBackPressedDispatcher, which Avalonia's
        // activity does not route; this still runs on every version we target.
#pragma warning disable CA1422
        public override void OnBackPressed()
        {
            if (InMatch)
            {
                // Back used to end the match outright, which is a gesture a
                // phone makes by accident and an hour of adventure mode thrown
                // away. It is what Escape is on the desktop now: the menu,
                // which offers leaving as one of four things rather than as
                // the only one.
                TogglePauseMenu();
                return;
            }
            if (_pending != null)
            {
                // Between the front screen going away and the match being
                // built there is nothing but the loading line. Back has to
                // mean something there, or a start that takes longer than the
                // player expected is an app with no way out of it.
                CancelPending("the player went back");
                return;
            }
            // The same question Escape asks the desktop launcher: close the
            // overlay, or go back one card. Only when the front screen has
            // nothing left to go back to does this leave the app.
            if (AndroidApp.Home?.GoBack() == true)
            {
                return;
            }
            base.OnBackPressed();
        }
#pragma warning restore CA1422

        /// <summary>Load what the plan asks for and hand the screen to it.</summary>
        internal void StartMatch(LaunchPlan plan)
        {
            AndroidApp.Home?.SuspendLobby();
            if (_content == null || InMatch)
            {
                return;
            }
            if (_pending != null)
            {
                // A start is already on its way. Taking this again would
                // overwrite _orientationBefore with the landscape this asked
                // for, and the front screen would be stuck sideways for the
                // rest of the session -- and pressing START twice is exactly
                // what a player does when the first press seems to do nothing.
                Console.WriteLine("[android] a match is already starting; ignoring");
                return;
            }
            if (_renderingHere)
            {
                // One process holds one world: the preview run owns the entity
                // lists and the game state until it is finished, and
                // ThumbnailMode is on while it is. So it is put down, not
                // queued behind.
                //
                // **This is what "I press START and nothing happens" was.**
                // A first run renders every map's picture without being asked
                // (StartScreen.CatchUpPreviews), which is minutes of work on a
                // phone, and pressing START during it refused the match and
                // called Reset() -- which throws the whole screen stack away
                // and puts the player back on the front screen, having lost
                // the map they picked, with nothing to read but a toast behind
                // an immersive window. The picture of a map nobody has asked
                // for yet does not outrank playing: the run is told to stop
                // between rooms and the start waits for the room in flight,
                // which is a second or two. Whatever it did not get to is
                // rendered by the next launch, or from the Render map
                // previews entry on the setup screen.
                Console.WriteLine("[android] stopping the preview run: a match was asked for");
                _stopPreviews = true;
            }
            // Same reason, and it has to happen whether or not a preview run
            // is going: the launcher's hunter preview holds a scene and a GL
            // context of its own, and a display list cut in that context is
            // written onto the *shared* Model the match is about to draw from.
            AndroidHunterShot.Current?.Retire();
            var input = new AndroidInput();
            _controls.ReleaseEverything();
            // TouchControls is created with the activity, before AndroidApp
            // loads controls.txt. Its buttons therefore start with their
            // constructor defaults even when the saved master switch (or an
            // individual button) is off. Apply the loaded settings before the
            // very first overlay is ever drawn instead of waiting for some
            // later state change or a pause-menu round trip to do it.
            _controls.ReloadSettings();
            // The controls object outlives a match -- it is a field here, not
            // the view's -- so a match left while spectating would hand the
            // next one a screen of NEXT and VIEW buttons.
            _controls.SetSpectator(spectating: false, freeCamera: false);
            _orientationBefore = RequestedOrientation;
            // A first-person game on a phone is landscape. The activity handles
            // its own configuration changes, so this rotates the surface rather
            // than restarting anything.
            RequestedOrientation = ScreenOrientation.SensorLandscape;
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
            GoImmersive(true);
            _pending = (plan, input);
            _waitingSince = SystemClock.UptimeMillis();
            if (_endPanelTick == null)
            {
                _endPanelTick = TickEndPanel;
                _content.PostDelayed(_endPanelTick, 100);
            }
            _lastSize = ContentSize;
            _sizeSettledAt = _waitingSince;
            // Before the wait, not after it. The front screen has just been
            // hidden and the GameView does not exist yet, so anything that
            // delays the start -- a rotation, a window that will not hold
            // still -- is a black screen with nothing on it and nothing to
            // press. That is indistinguishable from the app having failed.
            // An adventure plan carries no room key -- the slot decides the
            // room -- so naming it would leave "Loading ..." on screen for the
            // length of the load.
            ShowNotice(plan.RoomKey.Length > 0
                ? $"Loading {plan.RoomKey}..."
                : "Loading your game...");
            Console.WriteLine($"[android] starting {plan.RoomKey} from "
                + $"{_lastSize.Width}x{_lastSize.Height}");
            WaitForSteadyWindow();
        }

        private (LaunchPlan Plan, AndroidInput Input)? _pending;
        private (int Width, int Height) _lastSize;
        private long _waitingSince;
        private long _sizeSettledAt;

        /// <summary>How long the window has to hold still before a match starts.</summary>
        private const int SettleMs = 250;

        /// <summary>
        /// And how long to wait for it to become landscape at all before
        /// giving up and playing in whatever shape the display is. A device
        /// can refuse to rotate -- a display with one orientation, or a large
        /// screen on the Android versions that ignore an app's request.
        /// </summary>
        private const int RotateMs = 3000;

        /// <summary>
        /// The hard deadline, after which the start is abandoned and the
        /// player is put back on the front screen with a reason.
        ///
        /// Only reached if the content view never reports a usable size, which
        /// is the one case that cannot be started from: the scene would be
        /// built for a zero-pixel window. Everything else starts at RotateMs.
        /// </summary>
        private const int GiveUpMs = 8000;

        private (int Width, int Height) ContentSize =>
            _content == null ? (0, 0) : (_content.Width, _content.Height);

        /// <summary>
        /// Do not create the <see cref="GameView"/> until the window has
        /// stopped changing shape.
        ///
        /// This is the portrait launch bug, and it is worth writing down
        /// because nothing about it looks like a bug in this file.
        /// <c>GLSurfaceView.surfaceChanged</c> hands the new size to the GL
        /// thread and then *blocks the UI thread* until that thread has
        /// finished a frame. Loading a room takes seconds on the GL thread. So
        /// a rotation -- or the system bars going away, which resizes the
        /// window just the same -- landing while the room loads freezes the UI
        /// thread for as long as the load takes, and Android puts its own "is
        /// not responding" dialog over the black loading screen. From the
        /// player's side: a white box and a black box, and nothing to press.
        ///
        /// Waiting for the rotation was the first answer to this and only
        /// covered half of it: the configuration change arrives before the
        /// window has been laid out at its new size, so the surface was still
        /// being created mid-rotation. What has to hold still is the window,
        /// not the configuration, which is what this waits for.
        /// </summary>
        private void WaitForSteadyWindow()
        {
            if (_pending == null || _content == null || InMatch)
            {
                return;
            }
            // The preview run has been asked to stop and is finishing the room
            // it was in the middle of. Nothing may build a scene until it has
            // let go of the one it has -- the entity lists and the game state
            // are static and there is one of each per process. The clocks are
            // held back with it, so the deadlines below measure the window
            // rather than the wait for a picture of a map.
            if (_renderingHere)
            {
                _waitingSince = SystemClock.UptimeMillis();
                _sizeSettledAt = _waitingSince;
                _content.PostDelayed(WaitForSteadyWindow, 50);
                return;
            }
            long now = SystemClock.UptimeMillis();
            (int Width, int Height) size = ContentSize;
            if (size != _lastSize)
            {
                _lastSize = size;
                _sizeSettledAt = now;
            }
            bool haveSize = size.Width > 0 && size.Height > 0;
            bool landscape = size.Width > size.Height;
            bool steady = haveSize && now - _sizeSettledAt >= SettleMs;
            long waited = now - _waitingSince;
            if (steady && landscape)
            {
                StartPending(null);
                return;
            }
            // The deadline is not conditional on the window having settled,
            // which is what it used to be. A window that never holds still --
            // system bars coming and going, a device that reshapes the app's
            // area on its own -- left this loop running for ever with the
            // front screen already hidden, and that is the failure the player
            // sees as "it will not start a map". Waiting is a nicety; starting
            // is the job.
            if (haveSize && waited >= RotateMs)
            {
                StartPending(landscape
                    ? $"the window was still moving after {waited} ms"
                    : $"the display did not turn landscape in {waited} ms");
                return;
            }
            if (waited >= GiveUpMs)
            {
                // No size at all. A scene built for this would be a match in a
                // zero-pixel window, so say so and go back rather than start
                // something the player cannot see or leave.
                CancelPending($"the window never took a size ({size.Width}x{size.Height})");
                return;
            }
            _content.PostDelayed(WaitForSteadyWindow, 50);
        }

        /// <summary>
        /// Give up on a start that cannot be made, and put the player back
        /// where they were rather than on a black screen.
        /// </summary>
        private void CancelPending(string reason)
        {
            if (_pending == null)
            {
                return;
            }
            Console.WriteLine($"[android] the match was not started: {reason}");
            _pending = null;
            HideNotice();
            _controls.ReleaseEverything();
            if (_launcherView != null)
            {
                _launcherView.Visibility = ViewStates.Visible;
                MphRead.Mods.Input.GamepadContexts.MenuVisible = true;
            }
            MphRead.Mods.Launcher.Gui.Deck.Asleep = false;
            AndroidApp.Home?.Reset();
            Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
            GoImmersive(true);
            RequestedOrientation = _orientationBefore;
            Toast.MakeText(this, $"Could not start the match: {reason}",
                ToastLength.Long)?.Show();
        }

        private void StartPending(string? note)
        {
            if (_pending == null || InMatch)
            {
                return;
            }
            (LaunchPlan plan, AndroidInput input) = _pending.Value;
            _pending = null;
            if (_launcherView != null)
            {
                _launcherView.Visibility = ViewStates.Gone;
                MphRead.Mods.Input.GamepadContexts.MenuVisible = false;
            }
            // An Android view going away is not a detach, so the front
            // screen's moving layer would go on filling a noise field and
            // blending a full-window bitmap for the whole match, on the UI
            // thread of the process running it.
            MovingBackdrop.Suspended = true;
            // And neither would any of the other animations on it. Deck.Still
            // is what each of them already checks.
            MphRead.Mods.Launcher.Gui.Deck.Asleep = true;
            if (note != null)
            {
                Console.WriteLine($"[android] starting the match anyway: {note}");
            }
            Console.WriteLine($"[android] building the match at "
                + $"{ContentSize.Width}x{ContentSize.Height}");
            // Nailed down for the length of the load, for the reason above: a
            // sensor orientation lets the phone turn end for end while the
            // room is loading, which is a resize the UI thread would wait out.
            // Released again the moment the match is on screen, in
            // MatchLoaded, so the player can still hold the phone either way.
            RequestedOrientation = ScreenOrientation.Locked;
            BeginMatch(plan, input);
        }

        /// <summary>
        /// Put the soft keyboard up, or take it away.
        ///
        /// Asked for by the game loop when the chat prompt opens and closes;
        /// done here because an IME call belongs to the UI thread and to the
        /// view that says it is a text editor (see
        /// <c>GameView.OnCheckIsTextEditor</c>). The view has to be focused
        /// first -- the touch overlay above it takes every touch but never
        /// focus, so this is normally already true and costs nothing.
        /// </summary>
        private void ShowSoftKeyboard(bool show)
        {
            if (_gameView == null
                || GetSystemService(InputMethodService) is not InputMethodManager ime)
            {
                return;
            }
            if (show)
            {
                _gameView.RequestFocus();
                ime.ShowSoftInput(_gameView, ShowFlags.Implicit);
            }
            else
            {
                ime.HideSoftInputFromWindow(_gameView.WindowToken, HideSoftInputFlags.None);
            }
        }

        private void BeginMatch(LaunchPlan plan, AndroidInput input)
        {
            if (_content == null)
            {
                return;
            }
            _gameView = new GameView(this, _controls, input,
                (i, size) => AndroidMatch.Build(i, size, plan, () => RunOnUiThread(EndMatch)),
                () => RunOnUiThread(EndMatch),
                () => RunOnUiThread(MatchLoaded),
                error => RunOnUiThread(() => FailMatch(error)),
                () => RunOnUiThread(TogglePauseMenu),
                show => RunOnUiThread(() => ShowSoftKeyboard(show)));
            // The launcher is Avalonia, which draws on a surface of its own,
            // and two surfaces in one window have no z-order between them
            // unless one is asked for. Above the other surface and below the
            // window, so the touch controls and the loading notice -- ordinary
            // views -- still draw over the game.
            _gameView.SetZOrderMediaOverlay(true);
            _overlay = new TouchOverlayView(this, _controls);
            _content.AddView(_gameView);
            _content.AddView(_overlay);
            // The notice has been up since StartMatch. The GameView draws
            // below the window, so an ordinary view still covers it, but the
            // touch overlay was added after it -- put it back on top so the
            // player reads the loading line and not the controls behind it.
            ShowNotice($"Loading {plan.RoomKey}...");
            _notice?.BringToFront();
        }

        /// <summary>
        /// Put a line of text over everything, or change the one that is
        /// already there. This is the only thing on screen between the front
        /// screen being hidden and the first frame of the match.
        /// </summary>
        private void ShowNotice(string text)
        {
            if (_content == null)
            {
                return;
            }
            if (_notice != null)
            {
                _notice.Text = text;
                return;
            }
            _notice = new TextView(this)
            {
                Text = text,
                TextAlignment = Android.Views.TextAlignment.Center
            };
            _notice.SetTextColor(Android.Graphics.Color.Argb(230, 230, 234, 242));
            _notice.SetBackgroundColor(Android.Graphics.Color.Argb(255, 10, 12, 16));
            _notice.Gravity = GravityFlags.Center;
            _content.AddView(_notice);
        }

        /// <summary>The room is loaded and the match is drawing.</summary>
        private void MatchLoaded()
        {
            HideNotice();
            // The load is over, so a resize is one frame's wait rather than a
            // freeze; the phone can turn end for end again.
            RequestedOrientation = ScreenOrientation.SensorLandscape;
        }

        private void HideNotice()
        {
            if (_notice != null && _content != null)
            {
                _content.RemoveView(_notice);
                _notice = null;
            }
        }

        private void FailMatch(string message)
        {
            if (_notice != null)
            {
                _notice.Text = message;
            }
            else
            {
                Toast.MakeText(this, message, ToastLength.Long)?.Show();
            }
            // Leave the message up for long enough to read, then go back.
            _content?.PostDelayed(EndMatch, 4000);
        }

        private bool _pauseMenuOpen;

        private MphRead.Mods.Launcher.Gui.EndPanelView? _endPanel;
        private Action? _endPanelTick;
        private bool _endPanelWanted;

        /// <summary>
        /// Put the results panel up while the results are up, and take it down
        /// after -- the same two questions the desktop asks there (where next,
        /// and who you are coming back as) with the program's own controls
        /// rather than with arrows and swatches beside a 32x32 sprite.
        ///
        /// Driven from a poll rather than from whatever ends a match, for the
        /// reason <c>Shell.TickEndPanel</c> gives: the results arrive because
        /// the *server* said the match is over and there is no one place on
        /// this machine that hears it. A tenth of a second, not a frame --
        /// the frame belongs to the GL thread and this is the toolkit's.
        ///
        /// Never over the pause menu: Escape during the results is a thing a
        /// player can still do, and two screens over one match is one too
        /// many.
        /// </summary>
        private void TickEndPanel()
        {
            // `_pending` counts as in a match: the poll is started when one is
            // asked for and the view does not exist until the window has held
            // still, which is a second or two of ticks that must not stop it.
            if (!InMatch && _pending == null)
            {
                HideEndPanel();
                _endPanelTick = null;
                return;
            }
            bool want = MphRead.Mods.EndScreen.PanelAvailable && !_pauseMenuOpen;
            // The panel going up and down mid-results is what "the 3D model
            // appears and disappears" is: the HUD draws its own picker the
            // moment PanelUp clears. Say which of Available's six clauses moved.
            if (want != _endPanelWanted)
            {
                _endPanelWanted = want;
                MphRead.Mods.DebugLog.Line("ui", $"end panel {(want ? "up" : "down")}"
                    + $": state={MphRead.GameState.MatchState}"
                    + $" main={MphRead.Entities.PlayerEntity.Main != null}"
                    + $" mp={MphRead.GameState.Multiplayer}"
                    + $" menupause={MphRead.GameState.MenuPause}"
                    + $" spectating={MphRead.Mods.SpectatorMode.IsSpectating}"
                    + $" freecam={MphRead.Mods.SpectatorMode.FreeCamera}"
                    + $" pausemenu={_pauseMenuOpen}");
            }
            if (want && _endPanel == null)
            {
                AndroidUiSurface? surface = AndroidUiSurface.Ensure();
                if (surface != null && _gameView != null
                    && _gameView.Width > 0 && _gameView.Height > 0)
                {
                    surface.Resize(_gameView.Width, _gameView.Height);
                    _endPanel = new MphRead.Mods.Launcher.Gui.EndPanelView();
                    MphRead.Mods.EndScreen.PanelUp = true;
                    // On the glass, even though the launcher's own view is
                    // not: this panel is composited into the game's frame.
                    MphRead.Mods.Launcher.Gui.Deck.Asleep = false;
                    surface.Show(_endPanel);
                    _controls.ReleaseEverything();
                    _overlay?.Invalidate();
                }
            }
            else if (!want && _endPanel != null)
            {
                HideEndPanel();
            }
            else
            {
                _endPanel?.Refresh();
            }
            if (_endPanelTick != null)
            {
                _content?.PostDelayed(_endPanelTick, 100);
            }
        }

        private void HideEndPanel()
        {
            if (_endPanel == null)
            {
                return;
            }
            _endPanel = null;
            MphRead.Mods.EndScreen.PanelUp = false;
            MphRead.Mods.Launcher.Gui.Deck.Asleep = InMatch;
            AndroidUiSurface.Current?.Hide();
            _controls.ReleaseEverything();
            _overlay?.Invalidate();
        }

        /// <summary>
        /// The app's own menu, over a running match: resume, settings, leave,
        /// quit.
        ///
        /// The match keeps running behind it, which is the desktop's choice
        /// too and the honest one -- a networked match cannot be paused, and
        /// pretending otherwise is worse than showing it.
        ///
        /// The launcher's surface is what draws the menu, so it comes back
        /// into view and the game's surface gives up the z-order it was given
        /// to sit above it. The touch controls go away with it: they are
        /// ordinary views over the game and would otherwise be pressable
        /// through the menu.
        /// </summary>
        internal void TogglePauseMenu()
        {
            if (!InMatch)
            {
                return;
            }
            if (_pauseMenuOpen)
            {
                ClosePauseMenu();
                return;
            }
            _pauseMenuOpen = true;
            _controls.ReleaseEverything();
            // Two screens over one match is one too many, and the pause menu
            // is the one the player just asked for.
            HideEndPanel();
            if (_overlay != null)
            {
                _overlay.Visibility = ViewStates.Gone;
            }
            // Keep the game's SurfaceView attached while the menu is up.
            //
            // Setting a SurfaceView to Gone destroys its native window
            // surface. Resuming then has to race Android's replacement surface
            // through EGL release/create/make-current while the loaded scene is
            // still alive. Some devices lose that race and the render thread
            // dies immediately after Resume. Alpha hides the media-overlay
            // surface without changing its lifetime, so pausing is now just a
            // composition change instead of an EGL teardown/rebuild cycle.
            if (_gameView != null)
            {
                _gameView.Alpha = 0f;
            }
            if (_launcherView != null)
            {
                _launcherView.Visibility = ViewStates.Visible;
                // The transparent SurfaceView is still a live Android child.
                // Put the launcher above it in the view hierarchy as well so
                // touch events land on the pause menu rather than the hidden
                // game view.
                _launcherView.BringToFront();
                MphRead.Mods.Input.GamepadContexts.MenuVisible = true;
            }
            MphRead.Mods.Launcher.Gui.Deck.Asleep = false;
            GoImmersive(true);
            AndroidApp.Home?.ShowPauseMenu(
                ClosePauseMenu, EndMatch, () => Finish(),
                () => { _gameView?.RequestSpectate(); },
                () => { _gameView?.RequestRejoin(); });
        }

        private void ClosePauseMenu()
        {
            if (!_pauseMenuOpen)
            {
                return;
            }
            _pauseMenuOpen = false;
            if (_launcherView != null)
            {
                _launcherView.Visibility = ViewStates.Gone;
                MphRead.Mods.Input.GamepadContexts.MenuVisible = false;
            }
            MphRead.Mods.Launcher.Gui.Deck.Asleep = true;
            if (_gameView != null)
            {
                _gameView.Alpha = 1f;
            }
            if (_overlay != null)
            {
                _overlay.Visibility = ViewStates.Visible;
                // Restore the gameplay touch layer to the top of the Android
                // view hierarchy after the launcher was brought forward for
                // the pause menu.
                _overlay.BringToFront();
            }
            _controls.ReleaseEverything();
            // Settings are reachable from the pause menu, and one of the pages
            // there decides which of these buttons is on the glass.
            _controls.ReloadSettings();
            GoImmersive(true);
        }

        /// <summary>Back to the front screen.</summary>
        internal void EndMatch() => EndMatchCore(false);
        internal void EndMatchToLobby() => EndMatchCore(true);
        private void EndMatchCore(bool keepSession)
        {
            if (_content == null)
            {
                return;
            }
            _pending = null;
            _pauseMenuOpen = false;
            HideNotice();
            HideEndPanel();
            _endPanelTick = null;
            if (_overlay != null)
            {
                _content.RemoveView(_overlay);
                _overlay = null;
            }
            if (_gameView != null)
            {
                // Stop the render loop *before* the view goes, and this is the
                // only thing that ends the match's sound.
                //
                // Removing the view only destroys the surface, and this loop is
                // built to survive that -- it lets go of the surface and waits
                // for another one, keeping its context, its scene and every
                // voice that scene has playing. So the front screen came back
                // over a match that was still running, with the music and the
                // SFX still going, which is exactly how it was reported. The
                // stop is what breaks the loop, and breaking the loop is what
                // reaches Scene.DoCleanup -> Sfx.ShutDown and OutputStop.
                _gameView.Stop();
                _content.RemoveView(_gameView);
                _gameView = null;
            }
            _controls.ReleaseEverything();
            _controls.SetSpectator(spectating: false, freeCamera: false);
            if (_launcherView != null)
            {
                _launcherView.Visibility = ViewStates.Visible;
                MphRead.Mods.Input.GamepadContexts.MenuVisible = true;
            }
            MphRead.Mods.Launcher.Gui.Deck.Asleep = false;
            // The front screen is on the glass again, so its ground may move
            // -- unless the device is still rendering previews.
            MovingBackdrop.Suspended = _renderingPreviews;
            // The desktop builds a fresh front screen each time round its loop;
            // this one is the same object across a match, so it is told the
            // match is over rather than left believing it already answered.
            if (keepSession)
            {
                NetSession.ResetMatchState();
                AndroidApp.Home?.ResumeLobby();
            }
            else
            {
                NetSession.Stop(); NetHostSession.Stop();
                AndroidApp.Home?.Reset();
            }
            // A demo feeds NetSession from a file rather than a socket, so
            // stopping the session is not what closes it.
            DemoPlayback.Stop();
            Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
            GoImmersive(true);
            RequestedOrientation = _orientationBefore;
        }

        private void GoImmersive(bool immersive)
        {
            Window? window = Window;
            if (window == null)
            {
                return;
            }
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                IWindowInsetsController? controller = window.InsetsController;
                if (controller != null)
                {
                    if (immersive)
                    {
                        controller.SystemBarsBehavior =
                            (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                        controller.Hide(WindowInsets.Type.SystemBars());
                    }
                    else
                    {
                        controller.Show(WindowInsets.Type.SystemBars());
                    }
                }
                return;
            }
#pragma warning disable CA1422, CS0618 // the pre-30 way, for the devices that need it
            window.DecorView.SystemUiVisibility = immersive
                ? (StatusBarVisibility)(SystemUiFlags.ImmersiveSticky | SystemUiFlags.HideNavigation
                    | SystemUiFlags.Fullscreen | SystemUiFlags.LayoutStable)
                : StatusBarVisibility.Visible;
#pragma warning restore CA1422, CS0618
        }
    }
}
