#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering.Composition;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// The clock the compositor renders on, which is this program's frame and
    /// nothing else.
    ///
    /// Avalonia's headless backend binds a render timer that is a
    /// <c>DispatcherTimer</c> at sixty a second. A dispatcher timer fires when
    /// the dispatcher is pumped, and <see cref="UiSurface.Tick"/> pumps the
    /// dispatcher on <i>every game frame</i> -- it has to, because that is how
    /// everything the screens posted between frames gets run. So the toolkit
    /// rendered the whole surface inside <c>RunJobs</c>, on frames the surface
    /// had already decided were not worth redrawing, and the dirty-flag
    /// throttle that the surface is built around was answering a question
    /// nobody was asking any more.
    ///
    /// It does not show up as rendering, either, which is why it survived:
    /// the time lands inside the <c>jobs</c> figure in the debug log. Measured
    /// on the real launcher, one second at a time: <c>25 frames, 6 redraws,
    /// jobs 555.5 ms, draw 1.7 ms</c> -- half a second of rendering per second
    /// on a screen that had asked for six redraws and reported two
    /// milliseconds of drawing.
    ///
    /// Taking the binding means the compositor ticks when <see cref="Pump"/>
    /// says so, and <see cref="UiSurface.Tick"/> says so only when it has
    /// decided to draw.
    /// </summary>
    internal sealed class UiRenderTimer : Avalonia.Rendering.IRenderTimer
    {
        private readonly System.Diagnostics.Stopwatch _clock =
            System.Diagnostics.Stopwatch.StartNew();

        public Action<TimeSpan>? Tick { get; set; }

        /// <summary>The frame is this thread's, and so is everything it drives.</summary>
        public bool RunsInBackground => false;

        /// <summary>The one in the locator, once <see cref="Install"/> has run.</summary>
        public static UiRenderTimer? Current { get; private set; }

        /// <summary>
        /// Put it in front of the headless backend's, after the toolkit is up
        /// and before anything has asked for a render loop.
        ///
        /// The loop as well as the timer: a compositor takes its
        /// <c>IRenderLoop</c> from the locator and the loop holds the timer it
        /// was built with, so rebinding the timer alone changes nothing and
        /// the first version of this silently rendered nothing at all.
        /// </summary>
        public static void Install()
        {
            if (Current != null)
            {
                return;
            }
            var timer = new UiRenderTimer();
            AvaloniaLocator.CurrentMutable
                .Bind<Avalonia.Rendering.IRenderTimer>().ToConstant(timer)
                .Bind<Avalonia.Rendering.IRenderLoop>()
                .ToConstant(Avalonia.Rendering.RenderLoop.FromTimer(timer));
            Current = timer;
        }

        /// <summary>
        /// Render now, if there is anything to render.
        ///
        /// The loop puts a callback here when work arrives and takes it away
        /// again when there is none, so a pump with nothing to do costs a null
        /// check -- which is most frames.
        /// </summary>
        public static void Pump() => Current?.Tick?.Invoke(Current._clock.Elapsed);
    }

    /// <summary>
    /// What the screens are drawn into, and where their input comes from.
    ///
    /// Avalonia's own headless backend is what this replaces, and it was the
    /// right thing to start from -- it measures, arranges and draws with Skia
    /// exactly as a real backend does and hands the result back as pixels.
    /// What it is not is a backend meant to do that sixty times a second
    /// inside somebody else's frame, and three things it does are the whole of
    /// why the menus were slow. All three are measurable with <c>-uibench</c>,
    /// and all three are gone here.
    ///
    /// <para><b>A new framebuffer every frame.</b>
    /// <c>HeadlessWindowImpl.Lock</c> allocates a fresh
    /// <c>WriteableBitmap</c> for every render -- eight megabytes at 1080p,
    /// off the unmanaged heap, with <c>GC.AddMemoryPressure</c> on it. Two
    /// things follow. The compositor asks the render target whether the
    /// previous frame's contents are still there, is told no, and therefore
    /// renders through an intermediate layer which it <i>clears and blits
    /// whole</i> on every pass -- so the dirty rectangle it carefully tracked
    /// buys nothing. And the memory pressure drove the collector into about
    /// two blocking gen-2 collections per redraw: the finaliser thread was
    /// measured at 47% of the process. The buffer here is allocated once per
    /// size and kept, so <see cref="RetainsFrameContents"/> is true, the layer
    /// and the blit go away, and Skia draws the dirty rectangle straight into
    /// the bytes that are about to be uploaded.</para>
    ///
    /// <para><b>A copy of every finished frame.</b>
    /// <c>GetLastRenderedFrame</c> is for tests: it allocates a second
    /// full-window bitmap and copies the first into it, which measured 3 to 4
    /// milliseconds a redraw at 1080p and was paid even on frames where
    /// nothing had been drawn at all. What the overlay needs is a pointer to
    /// the pixels, which <see cref="Pixels"/> is.</para>
    ///
    /// <para><b>Two extra renders per input event.</b>
    /// <c>MouseMove</c>, <c>MouseWheel</c> and <c>KeyPress</c> on
    /// <c>HeadlessWindowExtensions</c> are test helpers, and they drain the
    /// dispatcher and force a render timer tick <i>before and after</i>
    /// delivering the event, up to ten of each. So every wheel notch was two
    /// full-window rasterisations on top of the one the frame asked for: 68
    /// milliseconds at 1080p, measured, for one notch of scrolling, which is
    /// the sluggish scrolling that was reported. The methods below raise the
    /// same events and render nothing; the frame renders, once, when it comes
    /// round.</para>
    ///
    /// None of that is a criticism of the headless backend, which is a
    /// unit-testing backend and is not slow at what it is for. It is the
    /// difference between rendering a screen once to look at it and rendering
    /// it as fast as a game draws.
    ///
    /// A top level rather than a window, and that is the one thing the toolkit
    /// insists on: <c>IWindowImpl</c>'s own members are not public in the
    /// reference assemblies, so a windowing backend cannot be written outside
    /// Avalonia -- but <c>ITopLevelImpl</c> can, and
    /// <c>EmbeddableControlRoot</c> is the top level that takes one. That is
    /// the supported way to put Avalonia inside something else's frame, which
    /// is exactly what this is.
    /// </summary>
    internal sealed class UiTopLevelImpl : ITopLevelImpl
    {
        private readonly IKeyboardDevice _keyboard;
        private readonly MouseDevice _mouse;
        private readonly TouchDevice _touch = new();
        private readonly Surface _surface;
        private readonly System.Diagnostics.Stopwatch _clock =
            System.Diagnostics.Stopwatch.StartNew();

        public UiTopLevelImpl(Compositor compositor)
        {
            Compositor = compositor;
            // Not GetRequiredService: which devices a platform registers is
            // the platform's business, and the Android backend does not put a
            // keyboard in the locator. Nothing below needs it to be the
            // platform's -- it is the device a raw key event is stamped with.
            _keyboard = AvaloniaLocator.Current.GetService<IKeyboardDevice>()
                ?? new KeyboardDevice();
            _mouse = new MouseDevice(new Pointer(Pointer.GetNextFreeId(),
                PointerType.Mouse, isPrimary: true));
            _surface = new Surface(this);
            Surfaces = [_surface];
            ClientSize = new Size(1280, 768);
        }

        /// <summary>
        /// A pass has just finished into <see cref="Pixels"/>.
        ///
        /// For the head that has to get the frame across a thread: the game
        /// draws on its own GL thread there and this buffer belongs to
        /// whoever the compositor renders on, so the copy has to be taken
        /// while the pass is the thing that just happened. The desktop leaves
        /// it null -- it reads <see cref="Drawn"/> from its own frame, on the
        /// one thread that does everything.
        /// </summary>
        public Action? Painted { get; set; }

        /// <summary>
        /// Stand one up, or say why not.
        ///
        /// The compositor is ours rather than the headless platform's, which
        /// keeps its own to itself and does not publish it. It is the same
        /// kind -- no GPU, the shared render loop, the shared media context --
        /// so one <c>ForceRenderTimerTick</c> still renders what it holds.
        /// </summary>
        public static UiTopLevelImpl? TryCreate()
        {
            try
            {
                return new UiTopLevelImpl(new Compositor(null));
            }
            catch (Exception ex)
            {
                // The launcher still works without this: what it replaces is
                // the headless backend, which is what shipped. Slower, and
                // drawn identically.
                Mods.DebugLog.Exception("ui", ex);
                Console.WriteLine("[launcher] the fast surface could not be built; "
                    + $"falling back to the toolkit's own: {ex.Message}");
                return null;
            }
        }

        // ------------------------------------------------------ the pixels

        /// <summary>
        /// The frame, as the toolkit left it. Tightly packed RGBA, top row
        /// first, alpha premultiplied -- which is what <c>UiOverlay</c>
        /// uploads and what Avalonia renders.
        ///
        /// Zero until something has been drawn. The buffer belongs to this
        /// object and outlives any one frame: it is reallocated only when the
        /// surface changes size, which is what lets the compositor keep the
        /// previous frame and draw only what moved.
        /// </summary>
        public IntPtr Pixels => _surface.Address;

        public int PixelWidth => _surface.Size.Width;

        public int PixelHeight => _surface.Size.Height;

        /// <summary>
        /// Counts finished draws.
        ///
        /// The frame asks the compositor to render and then wants to know
        /// whether there is anything to hand to GL. A number that has not
        /// moved means the compositor found nothing dirty and the texture
        /// already on the card is still the right one -- which is the ordinary
        /// case, and the upload it saves is the last full-surface copy in the
        /// path.
        /// </summary>
        public int Drawn => _surface.Drawn;

        /// <summary>Set the size the screens are laid out and drawn at.</summary>
        public void SetClientSize(Size size)
        {
            if (ClientSize == size)
            {
                return;
            }
            ClientSize = size;
            Resized?.Invoke(size, WindowResizeReason.Application);
        }

        // -------------------------------------------------------- the input
        //
        // The same events the windowing system would have delivered, raised
        // straight into the toolkit. No dispatcher drain and no render: see
        // the class comment for what the helpers these replace were doing.

        public void MouseMove(Point point, RawInputModifiers modifiers)
        {
            Raise(new RawPointerEventArgs(_mouse, Timestamp, InputRoot!,
                RawPointerEventType.Move, point, modifiers));
        }

        public void MouseDown(Point point, MouseButton button, RawInputModifiers modifiers)
        {
            Raise(new RawPointerEventArgs(_mouse, Timestamp, InputRoot!, button switch
            {
                MouseButton.Right => RawPointerEventType.RightButtonDown,
                MouseButton.Middle => RawPointerEventType.MiddleButtonDown,
                MouseButton.XButton1 => RawPointerEventType.XButton1Down,
                MouseButton.XButton2 => RawPointerEventType.XButton2Down,
                _ => RawPointerEventType.LeftButtonDown
            }, point, modifiers));
        }

        public void MouseUp(Point point, MouseButton button, RawInputModifiers modifiers)
        {
            Raise(new RawPointerEventArgs(_mouse, Timestamp, InputRoot!, button switch
            {
                MouseButton.Right => RawPointerEventType.RightButtonUp,
                MouseButton.Middle => RawPointerEventType.MiddleButtonUp,
                MouseButton.XButton1 => RawPointerEventType.XButton1Up,
                MouseButton.XButton2 => RawPointerEventType.XButton2Up,
                _ => RawPointerEventType.LeftButtonUp
            }, point, modifiers));
        }

        public void MouseWheel(Point point, Vector delta, RawInputModifiers modifiers)
        {
            Raise(new RawMouseWheelEventArgs(_mouse, Timestamp, InputRoot!,
                point, delta, modifiers));
        }

        // A finger, not the mouse. Avalonia's ScrollGestureRecognizer only
        // engages for a touch pointer, so a drag delivered as a mouse button
        // scrolls nothing and a list can only be moved by its scrollbar.

        public void TouchBegin(Point point, long id)
        {
            // Settle the layout first. Hit-testing reads the composition tree,
            // which is only brought up to date by a tick -- so a press that
            // arrives between two of them is resolved against the layout as it
            // stood before the last change, and lands on whatever used to be
            // under the finger. One removed row is one row of error.
            UiRenderTimer.Pump();
            Raise(new RawTouchEventArgs(_touch, Timestamp, InputRoot!,
                RawPointerEventType.TouchBegin, point, RawInputModifiers.None, id));
        }

        public void TouchUpdate(Point point, long id)
        {
            Raise(new RawTouchEventArgs(_touch, Timestamp, InputRoot!,
                RawPointerEventType.TouchUpdate, point, RawInputModifiers.None, id));
        }

        public void TouchEnd(Point point, long id)
        {
            Raise(new RawTouchEventArgs(_touch, Timestamp, InputRoot!,
                RawPointerEventType.TouchEnd, point, RawInputModifiers.None, id));
        }

        public void KeyPress(Key key, RawInputModifiers modifiers,
            PhysicalKey physicalKey, string? keySymbol)
        {
            Raise(new RawKeyEventArgs(_keyboard, Timestamp, InputRoot!,
                RawKeyEventType.KeyDown, key, modifiers, physicalKey, keySymbol));
        }

        public void KeyRelease(Key key, RawInputModifiers modifiers,
            PhysicalKey physicalKey, string? keySymbol)
        {
            Raise(new RawKeyEventArgs(_keyboard, Timestamp, InputRoot!,
                RawKeyEventType.KeyUp, key, modifiers, physicalKey, keySymbol));
        }

        public void TextInput(string text)
        {
            Raise(new RawTextInputEventArgs(_keyboard, Timestamp, InputRoot!, text));
        }

        private void Raise(RawInputEventArgs args)
        {
            if (InputRoot == null)
            {
                return;
            }
            Input?.Invoke(args);
        }

        private ulong Timestamp => (ulong)_clock.ElapsedMilliseconds;

        // ------------------------------------------------------ the surface

        /// <summary>
        /// One buffer, kept across frames, handed to Skia as somewhere to draw
        /// and to GL as something to upload.
        ///
        /// <see cref="RetainsFrameContents"/> is the whole point: it is how a
        /// render target says "what you drew last time is still here", and
        /// saying so is what makes the compositor draw directly and
        /// incrementally rather than through a layer it clears and blits whole
        /// on every pass.
        /// </summary>
        private sealed class Surface : IFramebufferPlatformSurface, IFramebufferRenderTarget
        {
            private readonly UiTopLevelImpl _owner;
            private IntPtr _address;
            private bool _fresh = true;

            public Surface(UiTopLevelImpl owner)
            {
                _owner = owner;
            }

            public IntPtr Address => _address;

            public PixelSize Size { get; private set; }

            public int Drawn { get; private set; }

            public bool RetainsFrameContents => true;

            public IFramebufferRenderTarget CreateFramebufferRenderTarget() => this;

            public ILockedFramebuffer Lock(IRenderTarget.RenderTargetSceneInfo sceneInfo,
                out FramebufferLockProperties properties)
            {
                // The compositor's own idea of how big the scene is, which is
                // the size it is about to draw: taking it from here rather
                // than from ClientSize is what keeps the buffer and the pass
                // in step on the frame a resize lands.
                PixelSize size = sceneInfo.Size;
                if (size.Width <= 0 || size.Height <= 0)
                {
                    size = PixelSize.FromSize(_owner.ClientSize, _owner.RenderScaling);
                }
                size = new PixelSize(Math.Max(size.Width, 1), Math.Max(size.Height, 1));
                if (size != Size || _address == IntPtr.Zero)
                {
                    Resize(size);
                }
                // False for one pass after the buffer is allocated and true
                // ever after. A fresh buffer holds nothing, so the first pass
                // into it has to be the whole surface -- saying so is what
                // stops the frame after a resize coming out as a strip of new
                // content over stale pixels.
                properties = new FramebufferLockProperties(!_fresh);
                _fresh = false;
                return new Pass(this);
            }

            private void Resize(PixelSize size)
            {
                Free();
                int bytes = size.Width * size.Height * 4;
                // A new address, which is also what makes Skia rebuild its
                // surface: FramebufferRenderTarget caches the SKSurface
                // against the pointer it was handed, so a buffer that keeps
                // its address keeps its surface. That cache is the other thing
                // a per-frame bitmap defeated.
                _address = Marshal.AllocHGlobal(bytes);
                Size = size;
                _fresh = true;
                unsafe
                {
                    new Span<byte>((void*)_address, bytes).Clear();
                }
            }

            private void Free()
            {
                if (_address != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_address);
                    _address = IntPtr.Zero;
                    Size = default;
                    _fresh = true;
                }
            }

            /// <summary>
            /// The render target is given back when the top level goes away,
            /// and the buffer goes with it -- not when a pass ends, which is
            /// the whole difference this class is here to make.
            /// </summary>
            public void Dispose() => Free();

            /// <summary>One pass over the buffer. Disposing it is the frame ending.</summary>
            private sealed class Pass : ILockedFramebuffer
            {
                private readonly Surface _surface;

                public Pass(Surface surface)
                {
                    _surface = surface;
                }

                public IntPtr Address => _surface._address;
                public PixelSize Size => _surface.Size;
                public int RowBytes => _surface.Size.Width * 4;
                public Vector Dpi { get; } = new(96, 96);
                public PixelFormat Format => PixelFormat.Rgba8888;
                public AlphaFormat AlphaFormat => AlphaFormat.Premul;

                public void Dispose()
                {
                    _surface.Drawn++;
                    _surface._owner.Painted?.Invoke();
                }
            }
        }

        // --------------------------------------------------- the rest of it
        //
        // A top level that is not on a desktop: nothing to move, no cursor of
        // its own (the game window's is released while the UI is up), nothing
        // in front of it. What is left is the size, which the game window
        // gives it, and the callbacks the toolkit needs answering.

        public Size ClientSize { get; private set; }
        public double RenderScaling => 1;
        public double DesktopScaling => 1;
        public IPlatformHandle? Handle => null;
        public IPlatformRenderSurface[] Surfaces { get; }
        public Compositor Compositor { get; }
        public IInputRoot? InputRoot { get; private set; }
        public Action<RawInputEventArgs>? Input { get; set; }
        public Action<Rect>? Paint { get; set; }
        public Action<Size, WindowResizeReason>? Resized { get; set; }
        public Action<double>? ScalingChanged { get; set; }
        public Action<WindowTransparencyLevel>? TransparencyLevelChanged { get; set; }
        public Action? Closed { get; set; }
        public Action? LostFocus { get; set; }
        public WindowTransparencyLevel TransparencyLevel { get; private set; }
            = WindowTransparencyLevel.Transparent;
        public AcrylicPlatformCompensationLevels AcrylicCompensationLevels => new(1, 1, 1);

        public void SetInputRoot(IInputRoot inputRoot) => InputRoot = inputRoot;

        public Point PointToClient(PixelPoint point) => point.ToPoint(1);

        public PixelPoint PointToScreen(Point point) => PixelPoint.FromPoint(point, 1);

        public IPopupImpl? CreatePopup() => null;

        public object? TryGetFeature(Type featureType)
        {
            if (featureType == typeof(IClipboard))
            {
                return AvaloniaLocator.Current.GetService<IClipboard>();
            }
            return null;
        }

        public void SetTransparencyLevelHint(IReadOnlyList<WindowTransparencyLevel> levels)
        {
            foreach (WindowTransparencyLevel level in levels)
            {
                if (level == WindowTransparencyLevel.Transparent)
                {
                    TransparencyLevel = level;
                    return;
                }
            }
            TransparencyLevel = WindowTransparencyLevel.None;
        }

        public void SetCursor(ICursorImpl? cursor) { }

        public void SetFrameThemeVariant(PlatformThemeVariant? themeVariant) { }

        public void Dispose()
        {
            _surface.Dispose();
            Closed?.Invoke();
        }
    }
}
#endif
