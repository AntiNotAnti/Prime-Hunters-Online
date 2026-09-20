#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MphRead.Mods.Render;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// The front screen's moving ground, drawn by the toolkit: the same field
    /// of domain-warped noise the desktop lays over the photograph in GL, for
    /// the head that has no GL under its launcher.
    ///
    /// <para>
    /// <b>Why there are two of these.</b> On the desktop the launcher is
    /// composited into the game's own GL window, so the photograph and this
    /// are both GL's and are blended together in one fragment shader -- see
    /// <c>Mods/Render/LauncherNoise.cs</c>, which is not compiled on this head
    /// and is also where the reason for a shader is written down. Android has
    /// no window under the screens at
    /// all: they are a real Avalonia view, drawn by Skia. So the field is
    /// drawn here instead and Skia is asked for the blend, which it has
    /// (<see cref="BitmapBlendingMode.Overlay"/>) and fixed-function GL does
    /// not. The arithmetic is <see cref="NoiseField"/> and is shared, because
    /// two copies of it is two different pictures a release apart.
    /// </para>
    ///
    /// <para>
    /// <b>What it costs, and why that is affordable here.</b> A redraw on the
    /// desktop is the whole launcher re-rasterised on the CPU
    /// (<see cref="BakedBackdrop"/> has the numbers), and an animated layer in
    /// that tree would mean paying it thirty times a second for ever -- which
    /// is exactly why the desktop keeps this on the other side of the fence.
    /// Here the screens are a GPU-composited view: the layers above this one
    /// are cached bitmaps and textures the compositor re-blits, the only work
    /// a frame is this field (57k cells at most, 30 times a second) and one
    /// upload. It is still the single most expensive thing on the front
    /// screen, so it steps on a clock of its own rather than with the frame,
    /// and it stops the moment it leaves the tree.
    /// </para>
    ///
    /// <para>
    /// The layer goes <i>between</i> the photograph and the washes over it, as
    /// it does on the desktop -- which is why the backdrop is baked in two
    /// pieces wherever this is used. Over the top of the washes it would tint
    /// the shade the screens are read against, and the point of the layer is
    /// the lava rather than the menu.
    /// </para>
    /// </summary>
    internal sealed class MovingBackdrop : Control
    {
        /// <summary>`#backdrop { opacity: .62 }`.</summary>
        private const double Strength = 0.62;

        /// <summary>The same number as an alpha, which is where it is applied.</summary>
        private const byte Alpha = (byte)(Strength * 255);

        private readonly NoiseField _field = new();
        private readonly DispatcherTimer _timer;
        private Bitmap? _bitmap;
        private byte[] _rgba = Array.Empty<byte>();

        /// <summary>
        /// Every backdrop in the tree, in the order they arrived.
        ///
        /// Every screen builds its own, and a screen pushed over the front
        /// screen does not take the one underneath out of the tree -- it
        /// covers it with an opaque photograph of its own. So there were two
        /// of these filling a noise field and blending a full-window layer
        /// thirty times a second, one of them behind the other where nobody
        /// could see it. Only the last to arrive steps; the rest keep the
        /// frame they had and pick the loop up again when they are uncovered.
        /// </summary>
        private static readonly List<MovingBackdrop> _live = new();

        private static bool _suspended;

        /// <summary>
        /// Nothing steps while this is set, wherever it is in the tree.
        ///
        /// <see cref="OnDetachedFromVisualTree"/> is the whole cost control
        /// only while the screens are a tree that comes and goes, and on
        /// Android they are not: the front screen is the activity's one view
        /// and a match merely hides the Android view it sits in, which is not
        /// a detach and not anything Avalonia hears about. So the field went
        /// on being filled and blended thirty times a second, on the UI thread
        /// of the process the match is running in, for the whole match -- and
        /// again alongside a preview run, which wants every core the device
        /// has. Set while the launcher is off the glass or the device is busy.
        /// </summary>
        public static bool Suspended
        {
            get => _suspended;
            set
            {
                if (_suspended == value)
                {
                    return;
                }
                _suspended = value;
                Arbitrate();
            }
        }

        public MovingBackdrop()
        {
            // It is the ground, not a control: a press on the backdrop is a
            // press on whatever the screen put over it, or on nothing.
            IsHitTestVisible = false;
            _timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(NoiseField.Gap),
                DispatcherPriority.Render,
                (_, _) => Tick());
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _live.Add(this);
            // Nothing moves for a capture: Deck.Still pins every animation on
            // these screens at its resting pose, and this one's is the first
            // frame of the loop. See Deck.Still for why that is a correctness
            // fix rather than a tidiness one.
            // One frame straight away, so the layer is there for the first
            // picture rather than a thirtieth of a second late -- and so a
            // capture, which never starts the timer, has one at all.
            Dispatcher.UIThread.Post(Tick, DispatcherPriority.Loaded);
            Arbitrate();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            // A screen that is not on the glass does not get a frame of CPU a
            // thirtieth of a second. This is the whole of the cost control.
            _live.Remove(this);
            _timer.Stop();
            Arbitrate();
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>Start the frontmost one and stop everything behind it.</summary>
        private static void Arbitrate()
        {
            for (int i = 0; i < _live.Count; i++)
            {
                MovingBackdrop layer = _live[i];
                if (i == _live.Count - 1 && !Deck.Still && !_suspended)
                {
                    layer._timer.Start();
                }
                else
                {
                    layer._timer.Stop();
                }
            }
        }

        /// <summary>
        /// The first cut, before the first render.
        ///
        /// From the arrange for the same reason <see cref="BakedBackdrop"/>
        /// bakes from its arrange -- a bitmap is not made inside a render pass
        /// -- and for one more that cost an afternoon: <b>a control whose first
        /// render draws nothing is one the compositor does not come back to</b>,
        /// and every <c>InvalidateVisual</c> after it is a no-op. That is the
        /// difference between a layer that steps in the debug log and one that
        /// moves on the glass.
        /// </summary>
        protected override Size ArrangeOverride(Size finalSize)
        {
            Step(finalSize.Width, finalSize.Height);
            return base.ArrangeOverride(finalSize);
        }

        private void Tick() => Step(Bounds.Width, Bounds.Height);

        /// <summary>Step the field and cut the bitmap, outside the render pass.</summary>
        private void Step(double w, double h)
        {
            if (w <= 0 || h <= 0 || !_field.Step(w, h, Deck.Still))
            {
                return;
            }
            Bitmap? cut = Cut();
            if (cut == null)
            {
                return;
            }
            // Last frame's, which the compositor has finished with by now.
            _bitmap?.Dispose();
            _bitmap = cut;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            Bitmap? bitmap = _bitmap;
            double w = Bounds.Width, h = Bounds.Height;
            if (bitmap == null || w <= 0 || h <= 0)
            {
                // A still backdrop is a backdrop. Whatever went wrong with one
                // bitmap must not be a front screen that will not draw.
                return;
            }
            // Fully qualified: the engine has a RenderOptions of its own and
            // it wins the name in this namespace.
            using (context.PushRenderOptions(new Avalonia.Media.RenderOptions
            {
                // The reference magnifies this canvas with
                // `image-rendering: pixelated`: the blocky cells are the look,
                // not an artefact of the field being small. Both heads say so.
                BitmapInterpolationMode = BitmapInterpolationMode.None,
                BitmapBlendingMode = BitmapBlendingMode.Overlay
            }))
            {
                context.DrawImage(bitmap, new Rect(0, 0, w, h));
            }
        }

        /// <summary>The field as a bitmap the toolkit can draw, row by row.</summary>
        private Bitmap? Cut()
        {
            int w = _field.Width, h = _field.Height;
            if (w <= 0 || h <= 0)
            {
                return null;
            }
            try
            {
                byte[] source = _field.Pixels;
                int stride = w * 4;
                if (_rgba.Length != stride * h)
                {
                    _rgba = new byte[stride * h];
                }
                for (int i = 0, p = 0; i < _rgba.Length; i += 4, p += 3)
                {
                    _rgba[i + 0] = (byte)(source[p + 0] * Alpha / 255);
                    _rgba[i + 1] = (byte)(source[p + 1] * Alpha / 255);
                    _rgba[i + 2] = (byte)(source[p + 2] * Alpha / 255);
                    _rgba[i + 3] = Alpha;
                }
                GCHandle pin = GCHandle.Alloc(_rgba, GCHandleType.Pinned);
                try
                {
                    return new Bitmap(PixelFormat.Rgba8888, AlphaFormat.Premul,
                        pin.AddrOfPinnedObject(), new PixelSize(w, h), new Vector(96, 96), stride);
                }
                finally
                {
                    pin.Free();
                }
            }
            catch (Exception ex)
            {
                Mods.DebugLog.Line("ui", $"the moving backdrop could not be drawn: {ex.Message}");
                _timer.Stop();
                return null;
            }
        }

    }
}
#endif
