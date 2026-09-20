using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Embedding;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MphRead.Mods.Render;
// GLFW's key enum, named rather than imported: this file talks about both
// toolkits at once and three of the names (Key/Keys, MouseButton,
// KeyModifiers) exist in each of them.
using Keys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// The launcher's screens, rendered into the game window.
    ///
    /// One Avalonia top-level exists for the life of the process and is never
    /// shown to the window manager: the toolkit runs on its headless backend,
    /// which measures, arranges and draws with Skia exactly as the X11 or Win32
    /// one does and hands the result back as a buffer of pixels instead of
    /// putting it on a screen. <see cref="UiOverlay"/> then draws that buffer
    /// over the frame, and this class feeds the events the toolkit would have
    /// got from the windowing system back in from GLFW.
    ///
    /// That is the whole of the "one window" change. Before it there were two
    /// programs on the desktop -- an Avalonia launcher that closed, and a GL
    /// window that opened -- plus a third arrangement in-game where the pause
    /// menu was a borderless window chasing the game's rectangle on every
    /// drag. The screens themselves are unchanged: they are the same controls
    /// the Android head has always drawn over its own GL surface, which is why
    /// this could be done without rewriting a single one of them.
    ///
    /// Two things it deliberately does not do. It draws no cursor -- the
    /// window's own is released while the UI is up, so the pointer is the
    /// system's. And it renders at the window's real pixel size with the
    /// content scaled up by <see cref="Scale"/> rather than rendering small
    /// and stretching, so text stays as sharp as the display allows.
    /// </summary>
    internal sealed class UiSurface
    {
        private static UiSurface? _current;

        /// <summary>
        /// The surface, once the toolkit is up. Null when there is none --
        /// a machine with no display, where the text launcher runs instead.
        /// </summary>
        public static UiSurface? Current => _current;

        /// <summary>Stand it up, or return the one already standing.</summary>
        public static UiSurface? Ensure()
        {
            if (_current != null)
            {
                return _current;
            }
            if (!GuiLauncher.EnsureSetup())
            {
                return null;
            }
            try
            {
                _current = new UiSurface();
            }
            catch (Exception ex)
            {
                // The surface is the launcher: without one there are no
                // screens to show, and the text launcher is the fallback --
                // the same one a machine with no display gets.
                Console.WriteLine($"[launcher] the screens could not be built: {ex.Message}");
                Mods.DebugLog.Exception("ui", ex);
                return null;
            }
            return _current;
        }

        private readonly UiTopLevelImpl _impl;
        private readonly EmbeddableControlRoot _window;
        private readonly LayoutTransformControl _host;
        private int _pixelWidth = 1280;
        private int _pixelHeight = 768;
        private double _factor = 1;

        /// <summary>
        /// Surface pixels per window pixel: one, unless the window is bigger
        /// than <see cref="RasterCapWidth"/> by <see cref="RasterCapHeight"/>.
        /// The pointer arrives in the window's pixels and the toolkit is
        /// holding the surface's, so every coordinate crossing that line is
        /// multiplied by this and nothing else is.
        /// </summary>
        private double _raster = 1;
        private Control? _view;
        private readonly GamepadNavigation _gamepad = new();
        private Point _pointer;
        private RawInputModifiers _modifiers;

        private UiSurface()
        {
            _gamepad.Changed += () => Invalidate();
            // Stretched, and the screen inside it is given no size of its own.
            // LayoutTransformControl measures its child through the inverse of
            // its own transform, so a host that fills the window measures the
            // screen at exactly (window / scale) and arranges it to fill --
            // one source of truth, the window's size, instead of a width and a
            // height assigned to the view and able to go stale. They did go
            // stale: a window made bigger during a match told the surface
            // nothing until a screen was shown again, and the pause menu then
            // laid itself out against the size the window used to be.
            _host = new LayoutTransformControl
            {
                LayoutTransform = new ScaleTransform(1, 1),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
            };
            // Our own top level rather than a Window on the headless backend.
            // See UiTopLevel.cs: the backend it replaces allocates and frees a
            // full-window bitmap per redraw, copies every finished frame a
            // second time, and forces two extra rasterisations per input
            // event. Everything else about the arrangement is unchanged --
            // same controls, same Skia, same layout, same pixels out.
            _impl = new UiTopLevelImpl(new Avalonia.Rendering.Composition.Compositor(null));
            _impl.SetClientSize(new Size(_pixelWidth, _pixelHeight));
            _window = new EmbeddableControlRoot(_impl)
            {
                // Transparent, because the pause menu is a scrim over a match
                // that is still being played: what this renders is composited
                // onto the frame, so anything the screens do not paint has to
                // come out of the buffer as nothing at all rather than as
                // black.
                Background = Brushes.Transparent,
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
                RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
                Content = _host
            };
            // Prepare is the initial layout pass a window gets from being
            // shown, and StartRendering is what attaches the tree to the
            // compositor: without the second one the render timer ticks and
            // nothing is ever drawn.
            _window.Prepare();
            _window.StartRendering();
        }

        /// <summary>
        /// What the layout actually came out as, for `-shellshot` to print
        /// beside each picture.
        ///
        /// The scale asked for and the scale on the host: they disagreed for
        /// three builds (see <see cref="ApplyScale"/>) and no picture on its
        /// own says which of the two is wrong.
        /// </summary>
        public string Describe()
        {
            double scale = _host.LayoutTransform is ScaleTransform t ? t.ScaleY : -1;
            return $"scale={_factor:0.###} drawn={(scale < 0 ? "none" : scale.ToString("0.###"))}"
                + $" raster={_pixelWidth}x{_pixelHeight}"
                + (_raster < 1 ? $" ({_raster:0.###} of the window)" : "")
                + $" screen={(_view == null ? "none" : $"{_view.Bounds.Width:0}x{_view.Bounds.Height:0}")}";
        }

        /// <summary>What is on screen, or null when the UI is not up.</summary>
        public Control? View => _view;

        /// <summary>Is there anything to draw and to give input to?</summary>
        public bool Visible => _view != null;

        /// <summary>
        /// How much bigger than its own layout the UI is drawn.
        ///
        /// The screens are laid out for something the size of the launcher
        /// window that used to open -- a bit under a thousand points across --
        /// and a game window is anything from that to a 4K display. Scaling
        /// the content rather than the pixels means the layout sees a constant
        /// size and Skia still draws every glyph and every line at the
        /// window's own resolution.
        /// </summary>
        public double Scale => _factor;

        /// <summary>
        /// The surface's own pixels, which are stretched over the whole
        /// window by the GL blit.
        ///
        /// A rectangle expressed as a fraction of these is therefore the same
        /// fraction of the window, whatever the raster cap did -- which is
        /// what anything drawing *under* the screens needs, and why this is
        /// published rather than the window's own size.
        /// </summary>
        public int WindowWidth => _pixelWidth;

        public int WindowHeight => _pixelHeight;

        /// <summary>The top level a control measures its position against.</summary>
        public Visual Root => _window;

        /// <summary>Put a screen up, or replace the one that is up.</summary>
        public void Show(Control view)
        {
            Invalidate();
            _view = view;
            Mods.Input.GamepadContexts.MenuVisible = true;
            // Whatever the screen thinks its size is, the window's is the one
            // that counts: the host stretches and measures it through the
            // scale, so a stale Width or Height on the view would be the one
            // thing able to contradict the window.
            view.ClearValue(Layoutable.WidthProperty);
            view.ClearValue(Layoutable.HeightProperty);
            view.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            view.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            _host.Child = view;
            // After the child, not before: assigning it is what the toolkit
            // cleared the transform *by*. See ApplyScale.
            ApplyScale();
            // Focus is what makes the keyboard reach the screen at all: a
            // top level with no windowing system above it is never activated
            // by anything, so nothing would otherwise give the content focus
            // and every key would land on nobody.
            Dispatcher.UIThread.Post(() => view.Focus(), DispatcherPriority.Input);
            Tick();
        }

        /// <summary>
        /// Put the scale back on the host if it is not there.
        ///
        /// It goes away on its own: setting <c>Child = null</c> on a
        /// <see cref="LayoutTransformControl"/> clears its
        /// <c>LayoutTransform</c> in Avalonia 11.3 -- proven on its own, away
        /// from this program. So every Hide wiped the scale, and the next
        /// screen was laid out 1:1 until something happened to *change* the
        /// factor and assign a new transform. That is the pause menu drawn
        /// small over a fullscreen match and the front screen that "goes small
        /// again" when a match ends: in both, the window had not changed size,
        /// so nothing re-assigned anything.
        ///
        /// Asserted rather than assumed, wherever the surface is touched. The
        /// check is a property read; the assignment only happens when the
        /// toolkit has dropped it.
        /// </summary>
        private void ApplyScale()
        {
            // Before the early return: this is what a layout point lands on,
            // and the one thing in the tree that cuts its own bitmap rather
            // than drawing into the frame has to be told even on the frame
            // where the transform itself has not moved.
            UiLayout.BakeScale = _factor;
            if (_host.LayoutTransform is ScaleTransform current
                && Math.Abs(current.ScaleY - _factor) < 0.0001
                && Math.Abs(current.ScaleX - _factor) < 0.0001)
            {
                return;
            }
            _host.LayoutTransform = new ScaleTransform(_factor, _factor);
        }

        /// <summary>Take it down. The frame behind it is the game's again.</summary>
        public void Hide()
        {
            _view = null;
            Mods.Input.GamepadContexts.MenuVisible = false;
            _host.Child = null;
            UiOverlay.Visible = false;
            // Anything the screen posted -- a closing animation, a preview
            // still loading -- runs now rather than on the first frame of the
            // next screen.
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>
        /// Follow the game window. Called every frame; does the work only when
        /// the rectangle actually moved, since assigning a size is a layout
        /// pass whether or not the number changed.
        /// </summary>
        public void Resize(int width, int height)
        {
            width = Math.Max(width, 1);
            height = Math.Max(height, 1);
            double raster = Raster(width, height);
            int surfaceWidth = Math.Max((int)Math.Round(width * raster), 1);
            int surfaceHeight = Math.Max((int)Math.Round(height * raster), 1);
            // The curve is asked about the *window*, then scaled down with
            // everything else: the layout box a screen is given has to be the
            // same box whether or not the raster was capped, or capping it
            // would relayout every screen as well as rasterising it smaller.
            double factor = Factor(width, height) * raster;
            if (surfaceWidth == _pixelWidth && surfaceHeight == _pixelHeight
                && factor == _factor)
            {
                return;
            }
            // After the early return, not before it. This is called every
            // frame whether or not the window moved, so an invalidation at the
            // top marks the surface dirty for ever and every frame is a
            // redraw -- which is the thing this is all here to stop.
            Invalidate();
            _pixelWidth = surfaceWidth;
            _pixelHeight = surfaceHeight;
            _raster = raster;
            if (factor != _factor)
            {
                // One line a change, into the debug log: how big the screens
                // are drawn is the first thing to ask about when somebody says
                // the text is too small, and it is decided from a number the
                // player cannot see. The raster is on the same line for the
                // same reason -- "the menus went soft" is the other half.
                Mods.DebugLog.Line("ui", $"screens at {factor:0.###}x "
                    + $"({width}x{height} pixels"
                    + (raster < 1 ? $", rasterised at {surfaceWidth}x{surfaceHeight}" : "")
                    + ")");
                _factor = factor;
            }
            // A *new* transform, never the old one with new numbers:
            // LayoutTransformControl watches the LayoutTransform property, and
            // mutating the object it already holds changes nothing it can see.
            ApplyScale();
            _impl.SetClientSize(new Size(surfaceWidth, surfaceHeight));
            // The layout the new size implies, now rather than on the frame
            // after: a resize that is one frame late is a screen drawn at the
            // old size over a window that is already the new one.
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>
        /// How much bigger than its own layout a window this tall draws the
        /// screens. The rule is <see cref="UiLayout.Factor"/> and lives there
        /// rather than here, because the Android head has no surface at all --
        /// it hands the screens straight to the toolkit as its one view -- and
        /// a phone has to get the same answer out of the same arithmetic. Two
        /// heads with two curves is a bug this file's own history already
        /// records, one screen at a time.
        /// </summary>
        private static double Factor(int width, int height)
        {
            return UiLayout.Factor(width, height);
        }

        // ------------------------------------------------- how many pixels
        //
        // The screens are rasterised by Skia on the CPU, into a framebuffer
        // the headless backend hands over fresh every frame -- so when
        // anything on screen moves, the *whole window* is rasterised again,
        // and there is nothing in the previous frame to reuse. The cost is
        // therefore the window's area, and it is paid inside the game's own
        // frame. Measured on an i5-10600K, one settings page being scrolled,
        // milliseconds a redraw after the backdrop was baked
        // (see BakedBackdrop, which is the other half of this):
        //
        //   1280x720    2.9      1920x1080  13.0
        //   1600x900    8.7      2560x1440  21.5      3840x2160  60.6
        //
        // A 4K window therefore cannot draw a moving menu faster than about
        // seventeen frames a second however fast the machine is, and that is
        // what "the menus scroll at five frames a second" was.
        //
        // So the raster is capped and the result is stretched over the window
        // by the GL blit, which is linear and free. Nothing below the cap is
        // touched -- a 1080p window is rasterised exactly as it always was --
        // and above it the screens are drawn at 1080p and magnified, which is
        // what a 1080p picture on a larger screen looks like: softer type,
        // and a menu that keeps up with the window. `-uinativeres` turns it
        // off for anyone who would rather have the pixels than the frames.

        /// <summary>The most pixels a screen is rasterised into, before magnifying.</summary>
        private const double RasterCapWidth = 1920;
        private const double RasterCapHeight = 1080;

        /// <summary>Rasterise at the window's own resolution however big it is.</summary>
        public static bool NativeRaster { get; set; }

        /// <summary>
        /// How much smaller than the window the screens are rasterised: one
        /// at or below the cap, less above it.
        ///
        /// Both axes, by the smaller of the two ratios, so the shape of the
        /// surface is the shape of the window and nothing in the layout has
        /// to know this happened. Rounded to a sixteenth so that dragging an
        /// edge does not hand the toolkit a new fractional size on every
        /// pixel.
        /// </summary>
        private static double Raster(int width, int height)
        {
            if (NativeRaster)
            {
                return 1;
            }
            double fits = Math.Min(RasterCapWidth / Math.Max(width, 1),
                RasterCapHeight / Math.Max(height, 1));
            if (fits >= 1)
            {
                return 1;
            }
            return Math.Max(Math.Floor(fits * 16) / 16, 0.25);
        }

        /// <summary>
        /// Draw the frame and hand it to GL.
        ///
        /// The dispatcher first, because everything the screens do between
        /// frames is posted to it -- a click's handler, a preview that has
        /// finished loading, the update check answering -- and a render before
        /// those have run is a picture of the screen as it was.
        /// </summary>
        /// <summary>
        /// Run something once, on the UI thread, before the next frame is
        /// drawn -- <see cref="TopLevel.RequestAnimationFrame"/>'s job, for
        /// the one backend where that does not work.
        ///
        /// Nothing pumps the compositor's animation frames here: the launcher
        /// is rendered by Avalonia's headless backend, one forced tick per
        /// game frame, and a callback registered with the top level is simply
        /// never called. Nor can the dispatcher stand in for it --
        /// <c>RunJobs</c> drains jobs posted while it is draining, so anything
        /// that asks for "the next frame" by reposting itself runs to
        /// completion inside this one. <see cref="Tick"/> is the frame, so
        /// this is where the hook belongs.
        ///
        /// One-shot, like the thing it replaces: whatever still has somewhere
        /// to go asks again.
        /// </summary>
        /// <param name="idling">
        /// True when the only thing moving is something that never stops --
        /// see <see cref="IdleAnimGap"/>. A spring, a slide or a hop must not
        /// pass this: they are short, and they are what the player is looking
        /// at while they run.
        /// </param>
        public static void RequestFrame(Action step, bool idling = false)
        {
            UiSurface? surface = _current;
            if (surface == null)
            {
                // No surface to be drawn into -- -uishot renders one frame and
                // stops, and the harness screens are built before Ensure. The
                // dispatcher is the only clock there is then, and a step left
                // on the list below would never run at all: whatever asked for
                // the frame would wait for it forever.
                Dispatcher.UIThread.Post(step, DispatcherPriority.Render);
                return;
            }
            lock (_pending)
            {
                _pending.Add(step);
            }
            // Whatever it is, it is moving something: the glide would
            // otherwise step the scroller and then wait up to IdleGap to be
            // drawn, which is a 20 fps animation.
            surface.Invalidate(animation: true, idling: idling);
        }

        private static readonly List<Action> _pending = new();
        private static readonly List<Action> _running = new();

        /// <summary>Everything waiting on a frame, before this one is drawn.</summary>
        private static void RunPending()
        {
            lock (_pending)
            {
                if (_pending.Count == 0)
                {
                    return;
                }
                // Taken off the list first, so a step that asks for another
                // frame is waiting for the *next* one rather than being run
                // again inside this one.
                _running.AddRange(_pending);
                _pending.Clear();
            }
            foreach (Action step in _running)
            {
                step();
            }
            _running.Clear();
        }

        // --------------------------------------------------- when to redraw
        //
        // The launcher used to be rendered from scratch on every frame the
        // game drew. Measured: 3.5 ms of a 120 Hz frame -- about 3 ms of Skia
        // re-rasterising the whole screen and half a millisecond uploading a
        // full framebuffer to GL -- to produce, almost always, exactly the
        // pixels already on screen. A menu that is not being touched does not
        // change, and 43% of the frame went on proving that over and over.
        //
        // So a redraw happens when something has actually happened: a pointer
        // moved, a key arrived, the window resized, a screen was shown, or
        // something asked for an animation frame. Everything else reuses the
        // texture already uploaded, which costs nothing at all.
        //
        // With one backstop. Not every change announces itself -- a preview
        // that finishes loading, a server that answers, a caret that blinks
        // all reach the screen through a dispatcher job, and the dispatcher
        // will not say whether it ran one. So a clean surface is still
        // redrawn every <see cref="IdleGap"/>, which puts a ceiling on how
        // stale the picture can be and turns "a missed invalidation is a
        // frozen screen" into "a missed invalidation is up to 50 ms late".
        // That is the difference between a bug and a rounding error.

        /// <summary>
        /// Milliseconds between backstop redraws just after something
        /// happened, and once the screen has been still for a while.
        ///
        /// Two numbers because the backstop is paying for two different
        /// things. Shortly after a screen is opened or touched, work is
        /// usually in flight -- a preview loading, a directory answering, a
        /// server being polled -- and 50 ms keeps any of it from looking late.
        /// A launcher nobody has touched for three seconds has nothing in
        /// flight and is being *looked at*, not used; four redraws a second is
        /// plenty to catch whatever the dispatcher did quietly, and it is the
        /// difference between 120 ms and 25 ms of CPU a second spent on a
        /// picture that is not changing.
        /// </summary>
        private const double IdleGap = 50;
        private const double RestingGap = 250;

        /// <summary>How long after the last change the short gap still applies.</summary>
        private const double SettleMs = 3000;

        /// <summary>
        /// The fastest the screens are redrawn when something *is* happening:
        /// sixty a second.
        ///
        /// This was uncapped -- as fast as the window draws -- on the
        /// reasoning that a glide capped at 60 on a 144 Hz screen covers three
        /// rows in six steps of fifteen pixels, and six steps is something you
        /// can count. That reasoning was about a *glide*, an animation this
        /// surface drives itself, and it is now capped separately by
        /// <see cref="AnimGap"/>. What was left uncapped by it was every
        /// redraw of every kind, and a redraw here is the whole window
        /// rasterised by Skia on the CPU: on a 144 Hz monitor that is 144 of
        /// them a second for a menu that cannot look different at 60.
        ///
        /// Sixty, and no lower, because this is also the path a wheel notch
        /// and a keystroke take and those must not feel delayed. On a 60 Hz
        /// window nothing here changes at all.
        ///
        /// The springs keep sixty too, and that is deliberate: they run for a
        /// fifth of a second at a time and they are what the player is looking
        /// at while they run. Dropping them to thirty to save frames was tried
        /// and put back -- it buys almost nothing (a spring is not on for long
        /// enough to matter to the average) and it is exactly the sluggishness
        /// that was reported in the first place. What costs is the animation
        /// that never stops; see <see cref="IdleAnimGap"/>.
        /// </summary>
        private const double BusyGap = 16;

        /// <summary>
        /// The fastest an animation that drives *itself* is redrawn: sixty a
        /// second.
        ///
        /// Not <see cref="BusyGap"/>, and the difference is the whole reason
        /// there are two numbers. A wheel notch or a keystroke is a redraw
        /// because something outside asked for one, and there are only as many
        /// of those as the player makes; a spring or a bob asks again every
        /// frame it is still moving, for as long as it moves -- and the front
        /// screen's one idle button never stops. Uncapped on a 144 Hz monitor
        /// that is 144 full-surface rasterisations a second for a two-and-a-
        /// half point bob, which is most of a core spent on a menu nobody is
        /// touching. Sixty is past the point where a spring looks any smoother
        /// and is less than half the work.
        /// </summary>
        private const double AnimGap = 16;

        /// <summary>
        /// The gap for an animation that is only *idling*: the front screen's
        /// bob, and nothing else so far.
        ///
        /// It moves one button two and a half points over three and a half
        /// seconds. At fifteen frames a second that is a sixth of a point
        /// between frames, which nobody can see -- and the difference matters
        /// because this is the one animation that never stops: the front
        /// screen would otherwise pay a full-window rasterisation thirty times
        /// a second for ever, which measured at about a third of a core doing
        /// nothing.
        /// </summary>
        private const double IdleAnimGap = 66;

        private bool _dirty = true;
        private double _drawnAt = -1000;
        private double _touchedAt;
        private int _redraws;
        private int _ticks;
        private double _jobs, _drawMs, _uploadMs;
        private double _reportedAt;

        /// <summary>
        /// What the screens are costing, once a second, into the debug log.
        ///
        /// Only while the log is on, which is the point: "the menus feel slow"
        /// is a report nobody can act on and nothing else in the program can
        /// answer, because the launcher's cost is invisible -- it is spent
        /// inside the game's own frame, so it shows up as the *game* being
        /// slow and not as anything the player can point at. Four numbers
        /// settle it: how often the window came round, how often the screens
        /// were actually redrawn, and how the redraw split between laying the
        /// screen out, rasterising it and handing it to GL.
        ///
        /// Redraws far below ticks means the dirty flag is doing its job and
        /// the cost is elsewhere; redraws equal to ticks with a large draw
        /// figure means it is not, and this is where to look.
        /// </summary>
        private void Report(double now)
        {
            if (_ticks == 0 || now - _reportedAt < 1000)
            {
                return;
            }
            Mods.DebugLog.Line("ui", $"{_ticks} frames, {_redraws} redraws, "
                + $"jobs {_jobs:0.0} ms, draw {_drawMs:0.0} ms, upload {_uploadMs:0.0} ms "
                + "(per second)");
            _ticks = 0;
            _redraws = 0;
            _jobs = 0;
            _drawMs = 0;
            _uploadMs = 0;
            _reportedAt = now;
        }
        private static readonly System.Diagnostics.Stopwatch _frameClock =
            System.Diagnostics.Stopwatch.StartNew();

        /// <summary>
        /// Something happened; draw on the next tick. Every input entry point
        /// calls this, and so does anything waiting on a frame.
        /// </summary>
        public void Invalidate(bool animation = false, bool idling = false)
        {
            _dirty = true;
            if (animation && _animOnly && !idling)
            {
                // Anything that is actually moving outranks the bob: one
                // button idling must not hold the whole surface down to
                // fifteen frames while a spring is running beside it.
                _animIdle = false;
            }
            if (!animation)
            {
                // An animation is not a touch. Counting it as one would hold
                // the short backstop open for ever on the front screen, whose
                // idle button asks for a frame three times a second even when
                // the spring is asleep.
                _touchedAt = _frameClock.Elapsed.TotalMilliseconds;
                _animOnly = false;
                return;
            }
            if (!_animOnly)
            {
                _animOnly = true;
                _animIdle = idling;
            }
            else if (!idling)
            {
                _animIdle = false;
            }
        }

        /// <summary>True when every animation asking for this frame is an idle one.</summary>
        private bool _animIdle;

        /// <summary>
        /// True when the only thing that has asked for this frame is something
        /// animating itself. Cleared by anything that arrives from outside.
        /// </summary>
        private bool _animOnly;

        public void Tick()
        {
            if (_view == null)
            {
                UiOverlay.Visible = false;
                return;
            }
            Mods.Input.GamepadDesktop.Poll();
            _gamepad.Update(_view);
            RunPending();
            ApplyScale();
            // Always running, not only while the debug log is: the calibration
            // below reads it, and a launcher that only kept up for the people
            // who had switched logging on would be the strangest bug in here.
            // It is one Stopwatch a frame against a redraw measured in
            // milliseconds.
            bool measuring = Mods.DebugLog.Active;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // Always, and cheap: this is what makes everything posted between
            // frames actually happen -- a click's handler, a preview that has
            // finished loading, the update check answering. Skipping it would
            // not save a redraw, it would stop the screen changing at all.
            Dispatcher.UIThread.RunJobs();
            if (measuring)
            {
                _jobs += clock.Elapsed.TotalMilliseconds;
                _ticks++;
            }
            double now = _frameClock.Elapsed.TotalMilliseconds;
            double since = now - _drawnAt;
            double gap = _dirty ? (_animOnly ? (_animIdle ? IdleAnimGap : AnimGap) : BusyGap)
                : now - _touchedAt < SettleMs ? IdleGap : RestingGap;
            if (since < gap)
            {
                // The texture from the last redraw is still on the GPU and
                // still correct. Leave it there.
                UiOverlay.Visible = true;
                return;
            }
            _dirty = false;
            _animOnly = false;
            _animIdle = false;
            _drawnAt = now;
            _redraws++;
            Report(now);
            clock.Restart();
            int drawn = _impl.Drawn;
            UiRenderTimer.Pump();
            _drawMs += clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            // Only when the compositor actually put something in the buffer.
            // It draws nothing when nothing is dirty -- which is most of the
            // backstop redraws -- and the texture already on the card is then
            // still the right one. This is the last full-surface copy in the
            // path and it is skipped on the frames that do not need it.
            if (_impl.Drawn != drawn && _impl.Pixels != IntPtr.Zero)
            {
                UiOverlay.Upload(_impl.Pixels, _impl.PixelWidth, _impl.PixelHeight);
                _uploadMs += clock.Elapsed.TotalMilliseconds;
            }
            UiOverlay.Visible = true;
        }

        /// <summary>
        /// Where the pointer is, in the game window's pixels.
        ///
        /// Not divided by <see cref="Scale"/>, and that was a real bug: the
        /// scale lives on the <see cref="LayoutTransformControl"/> *inside*
        /// this top-level, so the top-level's own coordinate space is the
        /// window's pixels and the toolkit applies the transform itself on the
        /// way down to the control. Dividing here as well meant that at any
        /// scale but 1 -- which is to say after the window had been made
        /// bigger -- every click landed at a fraction of where the player had
        /// pressed, and nothing on the screen could be pressed at all.
        ///
        /// It went unnoticed because the only automated click, ClickOn below,
        /// multiplied by the same factor on the way in: the two errors
        /// cancelled, so the check passed at every window size.
        /// </summary>
        public void PointerMoved(double x, double y)
        {
            Deck.DrivingByPointer();
            Invalidate();
            if (_view == null)
            {
                return;
            }
            // The one place the window's pixels become the surface's. Above
            // the raster cap the surface is smaller than the window, and a
            // pointer handed straight through would land low and right of
            // where the player pressed -- by a quarter of the screen at 4K.
            _pointer = new Point(x * _raster, y * _raster);
            _impl.MouseMove(_pointer, _modifiers);
        }

        public void PointerButton(MouseButton button, bool down)
        {
            Deck.DrivingByPointer();
            Invalidate();
            if (_view == null)
            {
                return;
            }
            RawInputModifiers flag = button switch
            {
                MouseButton.Right => RawInputModifiers.RightMouseButton,
                MouseButton.Middle => RawInputModifiers.MiddleMouseButton,
                _ => RawInputModifiers.LeftMouseButton
            };
            if (down)
            {
                _modifiers |= flag;
                _impl.MouseDown(_pointer, button, _modifiers);
            }
            else
            {
                _modifiers &= ~flag;
                _impl.MouseUp(_pointer, button, _modifiers);
            }
        }

        /// <summary>
        /// Click the middle of the first control the test matches, in the
        /// window's own pixels.
        ///
        /// For <c>-shellshot</c>, and the reason it takes a predicate rather
        /// than a coordinate: a check written against "x 90, y 620" is a check
        /// on where the menu happened to be laid out that day. What has to be
        /// proven is that a click on the *word* reaches the word -- which is
        /// the whole of the pointer arithmetic between GLFW and a screen that
        /// is no longer a window.
        /// </summary>
        public bool ClickOn(Func<Control, bool> match)
        {
            if (_view == null)
            {
                return false;
            }
            foreach (Visual visual in _view.GetVisualDescendants())
            {
                if (visual is not Control control || !match(control))
                {
                    continue;
                }
                Point? centre = control.TranslatePoint(
                    new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), _view);
                if (centre == null)
                {
                    continue;
                }
                // Is it actually the thing at that point? A control can be in
                // the tree and under something else -- the setup panel covers
                // the front screen's whole menu on a fresh install -- and a
                // check that reported a press it did not make would be worse
                // than no check. Asked of the toolkit, in the same
                // coordinates the toolkit lays out in.
                if (!Covers(control, centre.Value))
                {
                    continue;
                }
                // Points to surface pixels, then back out to the window's,
                // because PointerMoved takes the window's and is the thing
                // being proven. Undoing the conversion here rather than
                // skipping it is what keeps the check on the real path.
                double x = centre.Value.X * _factor / _raster;
                double y = centre.Value.Y * _factor / _raster;
                PointerMoved(x, y);
                PointerButton(MouseButton.Left, down: true);
                PointerButton(MouseButton.Left, down: false);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Put the pointer on a control and leave it there, pressing nothing.
        ///
        /// <see cref="ClickOn"/>'s first two lines, and it exists for the one
        /// thing a click cannot photograph: a word lights up over about 140 ms
        /// and a click leaves it focused, which lights it the same way for a
        /// different reason. Hovering proves the pointer alone does it.
        /// </summary>
        public bool HoverOn(Func<Control, bool> match)
        {
            if (_view == null)
            {
                return false;
            }
            foreach (Visual visual in _view.GetVisualDescendants())
            {
                if (visual is not Control control || !match(control))
                {
                    continue;
                }
                Point? centre = control.TranslatePoint(
                    new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), _view);
                if (centre == null)
                {
                    continue;
                }
                if (!Covers(control, centre.Value))
                {
                    continue;
                }
                PointerMoved(centre.Value.X * _factor / _raster,
                    centre.Value.Y * _factor / _raster);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Whether a press at that point reaches that control: the topmost
        /// input element there is it, or something inside it (a button's own
        /// label is what a press on a button actually lands on).
        /// </summary>
        private bool Covers(Control control, Point point)
        {
            if (_view == null)
            {
                return false;
            }
            IInputElement? hit = _view.InputHitTest(point);
            for (Visual? visual = hit as Visual; visual != null;
                visual = visual.GetVisualParent())
            {
                if (ReferenceEquals(visual, control))
                {
                    return true;
                }
            }
            return false;
        }

        public void PointerWheel(double deltaX, double deltaY)
        {
            Deck.DrivingByPointer();
            Invalidate();
            if (_view == null)
            {
                return;
            }
            _impl.MouseWheel(_pointer, new Vector(deltaX, deltaY), _modifiers);
        }

        public void KeyDown(Keys key, RawInputModifiers modifiers)
        {
            Deck.DrivingByKeyboard();
            Invalidate();
            if (_view == null)
            {
                return;
            }
            _modifiers = modifiers | (_modifiers & RawInputModifiers.LeftMouseButton);
            Key mapped = Translate(key);
            if (mapped != Key.None)
            {
                // The five-argument overload: the two-argument one is
                // deprecated, and what it cannot express is the physical key.
                // None is honest here -- GLFW's key *is* a physical one, but
                // the character it produced arrives separately as text input
                // (see TextInput), which is the only form a text box can use.
                _impl.KeyPress(mapped, _modifiers, PhysicalKey.None, "");
            }
        }

        public void KeyUp(Keys key, RawInputModifiers modifiers)
        {
            Invalidate();
            if (_view == null)
            {
                return;
            }
            _modifiers = modifiers | (_modifiers & RawInputModifiers.LeftMouseButton);
            Key mapped = Translate(key);
            if (mapped != Key.None)
            {
                _impl.KeyRelease(mapped, _modifiers, PhysicalKey.None, "");
            }
        }

        /// <summary>
        /// A typed character, which is a different event from a pressed key
        /// and the only one a text box can use: GLFW gives the codepoint the
        /// player's own layout and dead keys produced, and nothing about a
        /// scan code can be turned back into that.
        /// </summary>
        public void TextInput(string text)
        {
            Invalidate();
            if (_view == null || text.Length == 0)
            {
                return;
            }
            _impl.TextInput(text);
        }

        /// <summary>
        /// GLFW's keys to the toolkit's, for the ones a screen acts on.
        ///
        /// Not every key: the letters and digits reach the text boxes as text
        /// input, which is the event that carries what the player's layout
        /// actually produced. What is here is everything that means something
        /// without producing a character -- moving, choosing, editing and
        /// leaving -- plus the letters, which some rows use as shortcuts.
        /// </summary>
        private static Key Translate(Keys key)
        {
            if (key >= Keys.A && key <= Keys.Z)
            {
                return Key.A + (key - Keys.A);
            }
            if (key >= Keys.D0 && key <= Keys.D9)
            {
                return Key.D0 + (key - Keys.D0);
            }
            if (key >= Keys.F1 && key <= Keys.F12)
            {
                return Key.F1 + (key - Keys.F1);
            }
            if (key >= Keys.KeyPad0 && key <= Keys.KeyPad9)
            {
                return Key.NumPad0 + (key - Keys.KeyPad0);
            }
            return key switch
            {
                Keys.Escape => Key.Escape,
                Keys.Enter => Key.Return,
                Keys.KeyPadEnter => Key.Return,
                Keys.Tab => Key.Tab,
                Keys.Backspace => Key.Back,
                Keys.Delete => Key.Delete,
                Keys.Insert => Key.Insert,
                Keys.Home => Key.Home,
                Keys.End => Key.End,
                Keys.PageUp => Key.PageUp,
                Keys.PageDown => Key.PageDown,
                Keys.Left => Key.Left,
                Keys.Right => Key.Right,
                Keys.Up => Key.Up,
                Keys.Down => Key.Down,
                Keys.Space => Key.Space,
                Keys.Minus => Key.OemMinus,
                Keys.Equal => Key.OemPlus,
                Keys.Comma => Key.OemComma,
                Keys.Period => Key.OemPeriod,
                Keys.Slash => Key.Oem2,
                Keys.Backslash => Key.Oem5,
                Keys.Semicolon => Key.Oem1,
                Keys.Apostrophe => Key.OemQuotes,
                Keys.GraveAccent => Key.OemTilde,
                Keys.LeftBracket => Key.Oem4,
                Keys.RightBracket => Key.Oem6,
                Keys.LeftShift => Key.LeftShift,
                Keys.RightShift => Key.RightShift,
                Keys.LeftControl => Key.LeftCtrl,
                Keys.RightControl => Key.RightCtrl,
                Keys.LeftAlt => Key.LeftAlt,
                Keys.RightAlt => Key.RightAlt,
                _ => Key.None
            };
        }
    }
}
