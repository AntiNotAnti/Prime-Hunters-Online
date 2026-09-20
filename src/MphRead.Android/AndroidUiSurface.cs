using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Embedding;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MphRead.Mods.Launcher.Gui;

namespace MphRead.Droid
{
    /// <summary>
    /// The launcher's screens rendered into the game's own frame, the way the
    /// desktop does it.
    ///
    /// <para>
    /// Everything else on this head puts Avalonia on the glass as a real view
    /// and hides the game's surface underneath -- which is fine for the pause
    /// menu, where there is nothing behind worth seeing. The results screen is
    /// the case it cannot answer: the scoreboard beside the panel is drawn by
    /// the engine, so the panel has to be *over* a running match. Two
    /// <c>SurfaceView</c>s in one window have no z-order between them except
    /// the one asked for when they are created, and putting Avalonia's on top
    /// costs the frame rate -- a second full-screen surface composited every
    /// frame behind a panel that covers a fifth of it.
    /// </para>
    ///
    /// <para>
    /// So: the same answer the desktop reached. An offscreen top level
    /// (<see cref="UiTopLevelImpl"/>, shared) renders the screen with Skia
    /// into a buffer this owns, and <see cref="AndroidUiOverlay"/> draws that
    /// buffer as one quad at the end of the game's frame. One surface, one
    /// composite, and the panel is part of the picture rather than a second
    /// window pretending to be.
    /// </para>
    ///
    /// <para>
    /// <b>The thread boundary is the one thing that differs.</b> The desktop
    /// does all of this on one thread; here the toolkit is the UI thread's and
    /// the frame is the GL thread's, so the finished pass is copied as it ends
    /// (<see cref="UiTopLevelImpl.Painted"/>) and the GL thread takes the copy.
    /// Nothing else crosses.
    /// </para>
    /// </summary>
    internal sealed class AndroidUiSurface
    {
        /// <summary>The one this process uses, once something has asked for it.</summary>
        public static AndroidUiSurface? Current { get; private set; }

        private readonly UiTopLevelImpl _impl;
        private readonly EmbeddableControlRoot _window;
        private readonly LayoutTransformControl _host;

        private readonly object _gate = new();
        private byte[] _frame = Array.Empty<byte>();
        private int _frameWidth;
        private int _frameHeight;
        private int _version;

        private int _width = 1280;
        private int _height = 720;
        private Control? _view;

        private AndroidUiSurface()
        {
            _host = new LayoutTransformControl
            {
                LayoutTransform = new ScaleTransform(1, 1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            _impl = new UiTopLevelImpl(new Avalonia.Rendering.Composition.Compositor(null));
            _impl.SetClientSize(new Size(_width, _height));
            _impl.Painted = Painted;
            _window = new EmbeddableControlRoot(_impl)
            {
                // Whatever the screen does not paint has to come out of the
                // buffer as nothing at all rather than as black: what is
                // behind it is the match.
                Background = Brushes.Transparent,
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
                RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
                Content = _host
            };
            _window.Prepare();
            _window.StartRendering();
        }

        /// <summary>Stand it up, or hand back the one already standing.</summary>
        public static AndroidUiSurface? Ensure()
        {
            if (Current != null)
            {
                return Current;
            }
            try
            {
                Current = new AndroidUiSurface();
            }
            catch (Exception ex)
            {
                // Without one the results screen is the engine's own picker,
                // which is what shipped and still works.
                Console.WriteLine($"[ui] the in-frame surface could not be built: {ex}");
                MphRead.Mods.DebugLog.Exception("ui", ex);
            }
            return Current;
        }

        /// <summary>Is there a screen on it?</summary>
        public bool Visible => _view != null;

        /// <summary>
        /// The pixels the game window is, which is what the screens are laid
        /// out against and what the quad is stretched over.
        /// </summary>
        public void Resize(int width, int height)
        {
            if (width <= 0 || height <= 0 || (width == _width && height == _height))
            {
                return;
            }
            _width = width;
            _height = height;
            _impl.SetClientSize(new Size(width, height));
            MphRead.Mods.Render.HunterShot.FrameWidth = width;
            MphRead.Mods.Render.HunterShot.FrameHeight = height;
            ApplyScale();
        }

        /// <summary>
        /// The same curve every screen on this head is drawn at, and the same
        /// unit conversion: the screens are authored in desktop points and the
        /// layout here is in device pixels, so the factor carries the display
        /// density as well.
        /// </summary>
        private void ApplyScale()
        {
            double density = MainActivity.Instance?.Resources?.DisplayMetrics?.Density ?? 1;
            density = density <= 0 ? 1 : density;
            // Points the screens would have been handed as an ordinary
            // Avalonia view, which is what UiScaleHost is asked about.
            double points = UiScaleHost.FactorFor(_width / density, _height / density);
            double factor = points * density;
            MphRead.Mods.Render.HunterShot.FrameScale = factor;
            if (_host.LayoutTransform is ScaleTransform current
                && Math.Abs(current.ScaleY - factor) < 0.0001)
            {
                return;
            }
            _host.LayoutTransform = new ScaleTransform(factor, factor);
            MphRead.Mods.DebugLog.Line("ui", $"the in-frame surface is at {factor:0.###}x "
                + $"({_width}x{_height} pixels)");
        }

        /// <summary>Put a screen up, or replace the one that is up.</summary>
        public void Show(Control view)
        {
            _view = view;
            _host.Child = view;
            // What the hunter stand reads to leave a hole rather than draw
            // its own boxes: from here on the screens are part of the frame
            // and the engine can paint into them.
            MphRead.Mods.Render.HunterShot.InFrame = true;
            ApplyScale();
            Dispatcher.UIThread.Post(() => view.Focus(), DispatcherPriority.Background);
        }

        /// <summary>Take it down. The overlay stops drawing on the next frame.</summary>
        public void Hide()
        {
            _view = null;
            _host.Child = null;
            MphRead.Mods.Render.HunterShot.InFrame = false;
            MphRead.Mods.Render.HunterShot.HoleWanted = false;
            lock (_gate)
            {
                _version++;
                _frameWidth = 0;
                _frameHeight = 0;
            }
        }

        // ------------------------------------------------------- the frame

        private void Painted()
        {
            IntPtr address = _impl.Pixels;
            int width = _impl.PixelWidth;
            int height = _impl.PixelHeight;
            if (address == IntPtr.Zero || width <= 0 || height <= 0)
            {
                return;
            }
            int bytes = width * height * 4;
            lock (_gate)
            {
                if (_frame.Length < bytes)
                {
                    _frame = new byte[bytes];
                }
                System.Runtime.InteropServices.Marshal.Copy(address, _frame, 0, bytes);
                _frameWidth = width;
                _frameHeight = height;
                _version++;
            }
        }

        /// <summary>
        /// The newest frame, copied for the GL thread.
        ///
        /// <paramref name="version"/> is what the caller had; it comes back
        /// with what this is. Unchanged means the texture on the card is still
        /// the right one, which is most frames -- the panel is a static screen
        /// between two matches.
        /// </summary>
        public bool TakeFrame(ref byte[] into, ref int version, out int width, out int height)
        {
            lock (_gate)
            {
                width = _frameWidth;
                height = _frameHeight;
                if (_view == null || width <= 0 || height <= 0 || version == _version)
                {
                    return false;
                }
                int bytes = width * height * 4;
                if (into.Length < bytes)
                {
                    into = new byte[bytes];
                }
                Array.Copy(_frame, into, bytes);
                version = _version;
                return true;
            }
        }

        // ------------------------------------------------------- the input
        //
        // Touches, as the pointer the toolkit would have been given by a
        // windowing system. The results screen is the one moment in a match
        // when nobody is aiming, so while a screen is up every touch is the
        // screen's -- which is also what the desktop does, where the window
        // releases the cursor for the length of the panel.

        public void TouchDown(double x, double y)
        {
            _touchId++;
            _impl.TouchBegin(new Point(x, y), _touchId);
        }

        public void TouchMove(double x, double y)
        {
            _impl.TouchUpdate(new Point(x, y), _touchId);
        }

        public void TouchUp(double x, double y)
        {
            _impl.TouchEnd(new Point(x, y), _touchId);
        }

        // A fresh id per contact, never reused. Avalonia keys a pointer --
        // and the capture a control takes on it -- by this number, so a
        // constant meant every tap inherited the capture the tap before it
        // took: the press went to the tile last touched rather than the one
        // under the finger, whatever the position said.
        private long _touchId;
    }
}
