#if MPHREAD_SHELL
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Embedding;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// What a redraw of the screens actually costs, split into its parts.
    ///
    /// `-uibench` stands a surface up, puts a real screen in it and walks the
    /// same steps the game's frame walks -- drain the dispatcher, tick the
    /// render timer, take the frame, hand the pixels over -- timing each one
    /// on its own at every resolution anybody plays at.
    ///
    /// The split is the point. "The menus are slow" is one number and there
    /// are four places it can come from, three of which are not the toolkit
    /// drawing anything: the layout pass, Skia rasterising, the *copy* the
    /// headless backend makes of every finished frame, and the handover to GL.
    /// A fix aimed at the wrong one of those is a fix that measures the same
    /// afterwards.
    ///
    /// Two surfaces, and `-uibenchslow` is the reason there are two: the
    /// headless <see cref="Window"/> is what shipped and what every figure in
    /// <see cref="UiTopLevelImpl"/>'s comment was taken against, and a fix that
    /// cannot be shown beside the thing it fixed is an assertion.
    ///
    /// Medians rather than means, and the collector's own pause time taken out
    /// and shown on its own. The old path allocates two full-window bitmaps
    /// per redraw and disposes neither, so a mean over forty redraws is mostly
    /// a measurement of where the blocking collections happened to land: the
    /// first run of this showed 26 ms inside <c>RunJobs</c>, which does
    /// nothing at all when nothing is posted.
    ///
    /// The scenarios, because the fixed cost and the drawing cost are
    /// different problems with different answers:
    ///
    ///   still    nothing has changed at all. Whatever this costs is what a
    ///            redraw costs before anything is drawn -- the backstop
    ///            redraws pay exactly this, four times a second, for ever.
    ///   paint    the screen's root is marked dirty: a small dirty rectangle,
    ///            and what the compositor does with one.
    ///   repaint  every control on the page marked dirty: one full-viewport
    ///            rasterisation, and no layout.
    ///   scroll   the page scrolled a few points: layout, rasterisation and
    ///            the fixed cost together.
    ///   pointer  a mouse move delivered, timed on its own.
    ///   wheel    a wheel notch delivered, timed on its own. This is the one
    ///            the report was about.
    /// </summary>
    internal static class UiBench
    {
        private static readonly (int Width, int Height)[] _sizes =
        {
            (1280, 720),
            (1600, 900),
            (1920, 1080),
            (2560, 1440),
            (3840, 2160)
        };

        private enum Move { Still, Paint, Repaint, Scroll, Pointer, Wheel }

        /// <summary>
        /// Give the frame back after uploading it, which the shipped headless
        /// path did not. Only means anything with <see cref="Slow"/>.
        /// </summary>
        public static bool FreeFrames { get; set; }

        /// <summary>
        /// Measure the headless <see cref="Window"/> the launcher used to use
        /// instead of the surface it uses now.
        /// </summary>
        public static bool Slow { get; set; }

        /// <summary>
        /// One resolution only, as `WIDTHxHEIGHT`.
        ///
        /// Every size in one process was the first shape of this and the
        /// numbers were nonsense: a top level that has been closed is not
        /// necessarily off the compositor yet, and a render tick
        /// renders every target there is -- so each size measured itself plus
        /// the ghosts of the ones before it, and an idle 1440p frame came out
        /// at 44 ms. The runner starts a process per size.
        /// </summary>
        public static string? OnlySize { get; set; }

        /// <summary>One scenario only, for profiling a single one of them.</summary>
        public static string? OnlyMove { get; set; }

        /// <summary>
        /// Draw at this scale instead of the one the curve asks for.
        ///
        /// A full repaint measured 0.27 ms at 1280x720 and 28.7 ms at
        /// 1600x900 once, and the one other thing that differs between those
        /// rows is the scale on the host. This asks that question directly
        /// rather than by inference; the answer was no.
        /// </summary>
        public static double ScaleOverride { get; set; }

        /// <summary>Write the last frame here as a PNG, to prove it drew the screen.</summary>
        public static string? Shot { get; set; }

        /// <summary>
        /// Lay the screens out the way the Android head does: no GL under
        /// them, so the toolkit draws the whole backdrop itself and the
        /// animated layer between its two halves is in the tree.
        ///
        /// That layer is the reason this switch exists. It steps a field of
        /// noise thirty times a second and it fills the window, so on that
        /// head *something is dirty every frame whether or not anybody is
        /// touching the screen* -- and everything above it is redrawn with it.
        /// The desktop keeps it on the GL side of the fence and pays none of
        /// that, which is why a desktop benchmark says nothing about Android
        /// unless it is asked to.
        /// </summary>
        public static bool AsAndroid { get; set; }

        public static int Run(string? what)
        {
            if (!GuiLauncher.EnsureSetup())
            {
                Console.WriteLine("[uibench] no Avalonia backend on this machine");
                return 1;
            }
            // Springs and bobs jump to their resting place: a benchmark has to
            // measure the same screen every iteration, and the front screen's
            // idle button otherwise moves under it.
            Deck.Still = true;
            // The desktop shell draws the photograph and the moving layer as
            // GL quads under the screens, so the toolkit's backdrop there is
            // one cached bitmap. Without saying so the screens build the
            // standalone arrangement instead -- three layers, one of them
            // animating every frame -- and every figure below would be a
            // measurement of a backdrop the game does not draw.
            Mods.Render.LauncherPhoto.Enabled = !AsAndroid;
            string screen = String.IsNullOrEmpty(what) ? "settings" : what!;
            Dispatcher.UIThread.Invoke(() => Measure(screen));
            Deck.Still = false;
            return 0;
        }

        private static void Measure(string screen)
        {
            var settings = new MenuSettings();
            bool head = String.IsNullOrEmpty(OnlySize);
            if (head)
            {
                Console.WriteLine($"[uibench] screen={screen}  "
                    + $"surface {(Slow ? "headless Window, as shipped" : "UiTopLevelImpl")}"
                    + (AsAndroid ? "  backdrop as Android (animated layer in the tree)" : "")
                    + (DeckTile.CacheChrome ? "" : "  card shadows blurred every frame")
                    + (Slow ? $"  frames {(FreeFrames ? "freed" : "leaked")}" : "")
                    + $"  raster cap {(UiSurface.NativeRaster ? "off" : "on")}  "
                    + $"{Environment.ProcessorCount} cores");
                Console.WriteLine();
                Console.WriteLine("  window      surface     x     what   |"
                    + "   input   render     grab   upload |   total     fps |  gc ms  colls");
                Console.WriteLine("  " + new string('-', 104));
            }
            foreach ((int width, int height) in _sizes)
            {
                if (!head && OnlySize != $"{width}x{height}")
                {
                    continue;
                }
                One(screen, settings, width, height);
            }
            if (head)
            {
                Tail();
            }
        }

        internal static void Tail()
        {
            Console.WriteLine();
            Console.WriteLine("  medians per redraw, milliseconds.");
            Console.WriteLine("  input  = delivering one pointer or wheel event");
            Console.WriteLine("  render = RunJobs() + one render tick: layout and Skia");
            Console.WriteLine("  grab   = getting at the finished pixels "
                + "(a second full-surface allocation and copy on the old path)");
            Console.WriteLine("  upload = reading every byte of them "
                + "(stands in for glTexSubImage2D)");
            Console.WriteLine("  gc ms  = blocking collector pause per redraw; colls = "
                + "gen0/gen1/gen2 collections per redraw");
            Console.WriteLine();
        }

        private static Control Build(string screen, MenuSettings settings)
        {
            var rooms = new List<string>();
            try
            {
                foreach (RoomMetadata meta in Metadata.RoomMetadata.Values)
                {
                    if (meta.Multiplayer)
                    {
                        rooms.Add(meta.Name);
                    }
                }
            }
            catch (Exception)
            {
                // No game files: the screens still lay out, with empty lists.
            }
            rooms.Sort(StringComparer.OrdinalIgnoreCase);
            switch (screen)
            {
                case "start":
                    return new StartScreen(settings, rooms);
                case "play":
                    return new PlayScreen(settings, rooms, PlayScreen.Face.Online);
                case "maps":
                    // The map grid, which is the screen the sluggishness was
                    // reported on -- and reported on the Android head too,
                    // which is GPU-accelerated. A screen that is slow on both
                    // is not slow because of the rasteriser.
                    return new PlayScreen(settings, rooms, PlayScreen.Face.Offline);
                case "pause":
                    return new PauseMenuView(offerWindowMode: true);
                default:
                    var view = new SettingsView(settings);
                    view.ShowSection("Controls");
                    return view;
            }
        }

        // ------------------------------------------------------------- rigs
        //
        // The two surfaces behind one set of verbs, so that one measurement
        // loop covers both and the rows are comparable by construction.

        private abstract class Rig : IDisposable
        {
            public abstract int Width { get; }
            public abstract int Height { get; }
            public abstract void Pump();
            public abstract void MouseMove(Point point);
            public abstract void MouseWheel(Point point, Vector delta);

            /// <summary>
            /// Get at the finished pixels and read them, timing the two
            /// separately. Returns false when nothing was drawn.
            /// </summary>
            public abstract bool Take(byte[] sink, out double grab, out double upload);

            public abstract void Save(string path);
            public abstract void Dispose();
        }

        /// <summary>The headless window the launcher used to draw into.</summary>
        private sealed class SlowRig : Rig
        {
            private readonly Window _window;

            public SlowRig(Window window)
            {
                _window = window;
            }

            public override int Width => (int)_window.Width;
            public override int Height => (int)_window.Height;

            public override void Pump()
            {
                Dispatcher.UIThread.RunJobs();
                UiRenderTimer.Pump();
            }

            public override void MouseMove(Point point) =>
                _window.MouseMove(point, RawInputModifiers.None);

            public override void MouseWheel(Point point, Vector delta) =>
                _window.MouseWheel(point, delta, RawInputModifiers.None);

            public override bool Take(byte[] sink, out double grab, out double upload)
            {
                var clock = Stopwatch.StartNew();
                WriteableBitmap? frame = _window.GetLastRenderedFrame();
                grab = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                if (frame == null)
                {
                    upload = 0;
                    return false;
                }
                using (ILockedFramebuffer buffer = frame.Lock())
                {
                    System.Runtime.InteropServices.Marshal.Copy(buffer.Address, sink, 0,
                        Math.Min(sink.Length, buffer.RowBytes * buffer.Size.Height));
                }
                // Not disposed as shipped: UiSurface dropped the reference and
                // let the finaliser find it. Whether that mattered is one of
                // the things being measured.
                if (FreeFrames)
                {
                    frame.Dispose();
                }
                upload = clock.Elapsed.TotalMilliseconds;
                return true;
            }

            public override void Save(string path)
            {
                using WriteableBitmap? frame = _window.GetLastRenderedFrame();
                frame?.Save(path, PngBitmapEncoderOptions.Default);
            }

            public override void Dispose() => _window.Close();
        }

        /// <summary>The surface the launcher draws into now.</summary>
        private sealed class FastRig : Rig
        {
            private readonly EmbeddableControlRoot _root;
            private readonly UiTopLevelImpl _impl;
            private int _drawn;

            public FastRig(EmbeddableControlRoot root, UiTopLevelImpl impl)
            {
                _root = root;
                _impl = impl;
            }

            public override int Width => _impl.PixelWidth;
            public override int Height => _impl.PixelHeight;

            public override void Pump()
            {
                _drawn = _impl.Drawn;
                Dispatcher.UIThread.RunJobs();
                UiRenderTimer.Pump();
            }

            public override void MouseMove(Point point) =>
                _impl.MouseMove(point, RawInputModifiers.None);

            public override void MouseWheel(Point point, Vector delta) =>
                _impl.MouseWheel(point, delta, RawInputModifiers.None);

            public override bool Take(byte[] sink, out double grab, out double upload)
            {
                grab = 0;
                upload = 0;
                if (_impl.Drawn == _drawn || _impl.Pixels == IntPtr.Zero)
                {
                    // Nothing was drawn, so there is nothing to hand to GL and
                    // the texture on the card is still right. The skipped
                    // upload is part of the measurement, not a hole in it.
                    return false;
                }
                var clock = Stopwatch.StartNew();
                System.Runtime.InteropServices.Marshal.Copy(_impl.Pixels, sink, 0,
                    Math.Min(sink.Length, _impl.PixelWidth * _impl.PixelHeight * 4));
                upload = clock.Elapsed.TotalMilliseconds;
                return true;
            }

            public override void Save(string path)
            {
                if (_impl.Pixels == IntPtr.Zero)
                {
                    return;
                }
                using var bitmap = new WriteableBitmap(PixelFormat.Rgba8888,
                    AlphaFormat.Premul, _impl.Pixels,
                    new PixelSize(_impl.PixelWidth, _impl.PixelHeight),
                    new Vector(96, 96), _impl.PixelWidth * 4);
                bitmap.Save(path, PngBitmapEncoderOptions.Default);
            }

            public override void Dispose() => _root.Dispose();
        }

        /// <summary>
        /// One resolution, from a fresh surface.
        ///
        /// A surface per size rather than one resized, because the old backend
        /// allocated its framebuffer from the client size and a resize is the
        /// one thing in the path that is *not* paid every frame -- leaving it
        /// in the average would flatter every number.
        /// </summary>
        private static void One(string screen, MenuSettings settings, int width, int height)
        {
            double raster = Raster(width, height);
            int surfaceWidth = Math.Max((int)Math.Round(width * raster), 1);
            int surfaceHeight = Math.Max((int)Math.Round(height * raster), 1);
            double factor = UiLayout.Factor(width, height) * raster;
            if (ScaleOverride > 0)
            {
                factor = ScaleOverride;
            }
            UiLayout.BakeScale = factor;
            var host = new LayoutTransformControl
            {
                LayoutTransform = new ScaleTransform(factor, factor),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Control view = Build(screen, settings);
            view.HorizontalAlignment = HorizontalAlignment.Stretch;
            view.VerticalAlignment = VerticalAlignment.Stretch;
            host.Child = view;
            host.LayoutTransform = new ScaleTransform(factor, factor);
            using Rig rig = Stand(host, surfaceWidth, surfaceHeight);
            // The screens post work as they are built. Several passes, since
            // one job queues another.
            for (int i = 0; i < 8; i++)
            {
                rig.Pump();
            }
            view.Focus();
            for (int i = 0; i < 4; i++)
            {
                rig.Pump();
            }
            var centre = new Point(surfaceWidth / 2.0, surfaceHeight / 2.0);
            rig.MouseMove(centre);
            ScrollViewer? scroller = Scroller(view);
            byte[] sink = new byte[surfaceWidth * surfaceHeight * 4];
            bool first = true;
            foreach (Move move in new[] { Move.Still, Move.Paint, Move.Repaint, Move.Scroll,
                Move.Pointer, Move.Wheel })
            {
                if (!String.IsNullOrEmpty(OnlyMove)
                    && !String.Equals(OnlyMove, move.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                Row(rig, view, scroller, centre, sink, move,
                    first ? $"{width}x{height}" : "",
                    first ? $"{surfaceWidth}x{surfaceHeight}" : "", first ? factor : -1);
                first = false;
            }
            if (!String.IsNullOrEmpty(Shot))
            {
                view.InvalidateVisual();
                rig.Pump();
                string path = System.IO.Path.Combine(Shot!,
                    $"{screen}-{surfaceWidth}x{surfaceHeight}-"
                    + $"{(Slow ? "headless" : "fast")}.png");
                System.IO.Directory.CreateDirectory(Shot!);
                rig.Save(path);
                Console.WriteLine($"  [uibench] {path}");
            }
            for (int i = 0; i < 4; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }
        }

        private static Rig Stand(Control content, int width, int height)
        {
            if (!Slow)
            {
                var impl = new UiTopLevelImpl(
                    new Avalonia.Rendering.Composition.Compositor(null));
                impl.SetClientSize(new Size(width, height));
                var root = new EmbeddableControlRoot(impl)
                {
                    Background = Brushes.Transparent,
                    TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
                    RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
                    Content = content
                };
                root.Prepare();
                root.StartRendering();
                return new FastRig(root, impl);
            }
            var window = new Window
            {
                Width = width,
                Height = height,
                WindowDecorations = WindowDecorations.None,
                Background = Brushes.Transparent,
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
                RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
                Content = content
            };
            window.Show();
            window.Activate();
            return new SlowRig(window);
        }

        /// <summary>
        /// The biggest scroll viewer on the screen with something to scroll.
        ///
        /// Driven by its <c>Offset</c> rather than by a wheel event, and the
        /// first version of this did use the wheel: on a page whose content
        /// happens to fit, a notch changes nothing, nothing is marked dirty
        /// and the "scroll" row measures an idle frame. Which is exactly the
        /// trap this whole file exists to avoid.
        /// </summary>
        private static ScrollViewer? Scroller(Control view)
        {
            ScrollViewer? best = null;
            foreach (Visual visual in view.GetVisualDescendants())
            {
                if (visual is ScrollViewer scroll && scroll.IsVisible
                    && scroll.Extent.Height > scroll.Viewport.Height + 1
                    && (best == null || scroll.Bounds.Height > best.Bounds.Height))
                {
                    best = scroll;
                }
            }
            return best;
        }

        private static void Row(Rig rig, Control view, ScrollViewer? scroller, Point centre,
            byte[] sink, Move move, string windowLabel, string surfaceLabel, double factor)
        {
            const int Warm = 10;
            const int Runs = 61;
            var render = new double[Runs];
            var grabs = new double[Runs];
            var uploads = new double[Runs];
            var inputs = new double[Runs];
            var clock = new Stopwatch();
            TimeSpan pause = TimeSpan.Zero;
            int gen0 = 0, gen1 = 0, gen2 = 0;
            double step = 0;
            int draws = 0;
            int layouts = 0;
            // How often the toolkit actually did anything, beside how long it
            // took: a scenario that changes nothing and still lays out and
            // draws on every tick is a different bug from a slow draw, and the
            // two are indistinguishable in a millisecond figure.
            EventHandler counter = (_, _) => layouts++;
            view.LayoutUpdated += counter;
            for (int i = 0; i < Warm + Runs; i++)
            {
                if (i == Warm)
                {
                    pause = GC.GetTotalPauseDuration();
                    gen0 = GC.CollectionCount(0);
                    gen1 = GC.CollectionCount(1);
                    gen2 = GC.CollectionCount(2);
                }
                double input = 0;
                switch (move)
                {
                    case Move.Scroll when scroller != null:
                        // A few points a frame, back and forth inside whatever
                        // room the page has: the same thing the glide does,
                        // and the thing the report is about.
                        double room = Math.Max(scroller.Extent.Height
                            - scroller.Viewport.Height, 1);
                        step = (step + 7) % room;
                        scroller.Offset = new Vector(scroller.Offset.X, step);
                        break;
                    case Move.Paint:
                        view.InvalidateVisual();
                        break;
                    case Move.Repaint when scroller?.Content is Visual content:
                        // The whole page marked dirty without moving it: one
                        // full-viewport rasterisation and no layout at all,
                        // which is the half of a scroll step the toolkit
                        // cannot avoid. Whatever a scroll costs above this is
                        // layout.
                        foreach (Visual child in content.GetVisualDescendants())
                        {
                            (child as Control)?.InvalidateVisual();
                        }
                        break;
                    case Move.Pointer:
                    case Move.Wheel:
                        clock.Restart();
                        if (move == Move.Wheel)
                        {
                            rig.MouseWheel(centre, new Vector(0, i % 2 == 0 ? -1 : 1));
                        }
                        else
                        {
                            rig.MouseMove(new Point(centre.X + i % 7, centre.Y));
                        }
                        input = clock.Elapsed.TotalMilliseconds;
                        break;
                }
                clock.Restart();
                rig.Pump();
                double drew = clock.Elapsed.TotalMilliseconds;
                bool painted = rig.Take(sink, out double grab, out double upload);
                if (i >= Warm)
                {
                    render[i - Warm] = drew;
                    grabs[i - Warm] = grab;
                    uploads[i - Warm] = upload;
                    inputs[i - Warm] = input;
                    if (painted)
                    {
                        draws++;
                    }
                }
            }
            double gcMs = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds / Runs;
            double g0 = (GC.CollectionCount(0) - gen0) / (double)Runs;
            double g1 = (GC.CollectionCount(1) - gen1) / (double)Runs;
            double g2 = (GC.CollectionCount(2) - gen2) / (double)Runs;
            double mRender = Median(render), mGrab = Median(grabs);
            double mUpload = Median(uploads), mInput = Median(inputs);
            double total = mRender + mGrab + mUpload + mInput;
            string label = move switch
            {
                Move.Still => "still",
                Move.Paint => "paint",
                Move.Repaint => "repaint",
                Move.Pointer => "pointer",
                Move.Wheel => "wheel",
                _ => scroller == null ? "scroll!" : "scroll"
            };
            Console.WriteLine($"  {windowLabel,-11} {surfaceLabel,-11}"
                + $" {(factor < 0 ? "" : factor.ToString("0.###")),-5} {label,-7}"
                + $"| {mInput,7:0.00} {mRender,8:0.00} {mGrab,8:0.00} {mUpload,8:0.00}"
                + $" | {total,7:0.00} {1000 / Math.Max(total, 0.001),7:0} |"
                + $" {gcMs,6:0.00} {g0,4:0.0}/{g1,0:0.0}/{g2,0:0.0}"
                + $" | {draws,3}/{Runs} drawn {layouts,4} layouts");
            view.LayoutUpdated -= counter;
            if (DeckTile.ChromeAsks > 0)
            {
                Console.WriteLine($"            card chrome: {DeckTile.ChromeBakes} cut, "
                    + $"{DeckTile.ChromeAsks} asked for");
            }
        }

        private static double Median(double[] values)
        {
            var copy = (double[])values.Clone();
            Array.Sort(copy);
            return copy[copy.Length / 2];
        }

        /// <summary>
        /// <see cref="UiSurface"/>'s own cap, repeated here because it is
        /// private there and the point of this is to measure what the game
        /// actually does.
        /// </summary>
        private static double Raster(int width, int height)
        {
            if (UiSurface.NativeRaster)
            {
                return 1;
            }
            double fits = Math.Min(1920.0 / Math.Max(width, 1), 1080.0 / Math.Max(height, 1));
            if (fits >= 1)
            {
                return 1;
            }
            return Math.Max(Math.Floor(fits * 16) / 16, 0.25);
        }
    }
}
#endif
