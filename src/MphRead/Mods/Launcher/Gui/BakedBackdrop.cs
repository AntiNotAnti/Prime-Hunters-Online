using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// The photograph and the washes over it, drawn once into one bitmap and
    /// then blitted.
    ///
    /// The backdrop was four full-window layers -- a JPEG stretched
    /// UniformToFill, a linear gradient, a radial vignette and the wash --
    /// and the toolkit re-rasterised all four **every time anything on the
    /// screen changed**, because the headless backend hands the renderer a
    /// fresh framebuffer each frame and nothing in the previous one can be
    /// reused. That is free while a menu sits still and ruinous the moment
    /// something moves: a list gliding under the wheel is a redraw a frame,
    /// and a redraw is the whole window.
    ///
    /// Measured on an i5-10600K, one scrolling settings page, milliseconds a
    /// redraw (and what that does to the frame rate of the window the screens
    /// are drawn into):
    ///
    /// <code>
    ///                 four layers        this
    ///   1280x720       4.6  (217 fps)    2.9  (344 fps)
    ///   1600x900      14.7   (68 fps)    8.7  (116 fps)
    ///   1920x1080     24.2   (41 fps)   13.0   (77 fps)
    ///   2560x1440     40.9   (24 fps)   21.5   (46 fps)
    ///   3840x2160     91.7   (11 fps)   60.6   (17 fps)
    /// </code>
    ///
    /// That is the whole of "the menus scroll at five frames a second": the
    /// wheel was never the problem, the redraw behind it was. A notch now
    /// moves the scroller and nothing animates, so a redraw is one a notch
    /// rather than one a frame for a tenth of a second -- but a redraw is
    /// still the whole window, and everything that opens, hovers, types or
    /// resizes pays it.
    ///
    /// Nothing about the picture changes. The bake is the same visual tree
    /// rendered by the same rasteriser at the same device resolution, and at
    /// 1:1 it comes out **byte for byte identical** to the four layers it
    /// replaces -- checked, not assumed.
    ///
    /// The bitmap is cut at the device resolution (points times
    /// <see cref="UiLayout.BakeScale"/>) so the photograph is no softer than
    /// it was, rounded up to a multiple of <see cref="Grain"/> so that
    /// dragging the window's edge re-bakes every thirty-two pixels rather
    /// than every one.
    /// </summary>
    internal sealed class BakedBackdrop : Control
    {
        /// <summary>Device pixels the bake is rounded up to.</summary>
        private const int Grain = 32;

        /// <summary>
        /// How many cuts are kept: the screen up, and the one before it.
        ///
        /// Six rather than three, because a head that draws its own moving
        /// layer bakes the photograph and the washes separately (see
        /// UiLayout.BackdropPart) and therefore wants two entries per size.
        /// </summary>
        private const int Kept = 6;

        private readonly UiLayout.BackdropWash _wash;
        private readonly UiLayout.BackdropPart _part;
        private Bitmap? _image;

        private void SceneChanged()
        {
            _image = null;
            InvalidateArrange();
            InvalidateVisual();
        }

        public BakedBackdrop(UiLayout.BackdropWash wash,
            UiLayout.BackdropPart part = UiLayout.BackdropPart.All)
        {
            _wash = wash;
            _part = part;
            // It is the ground, not a control: a click on the backdrop is a
            // click on whatever the screen put over it, or on nothing.
            IsHitTestVisible = false;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            LauncherBackdrop.Changed += SceneChanged;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            LauncherBackdrop.Changed -= SceneChanged;
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>
        /// Pick (or cut) the bitmap for the size just arrived at.
        ///
        /// In the arrange pass rather than in <see cref="Render"/>: baking
        /// means rendering a visual tree of its own, and doing that from
        /// inside the render pass is how Avalonia is made to throw *Visual was
        /// invalidated during the render pass*.
        /// </summary>
        protected override Size ArrangeOverride(Size finalSize)
        {
            _image = Fetch(finalSize, _wash, _part);
            return base.ArrangeOverride(finalSize);
        }

        public override void Render(DrawingContext context)
        {
            Bitmap? image = _image;
            if (image == null || Bounds.Width <= 0 || Bounds.Height <= 0)
            {
                return;
            }
            context.DrawImage(image, new Rect(0, 0, Bounds.Width, Bounds.Height));
        }

        private readonly record struct Cut(int Width, int Height,
            UiLayout.BackdropWash Wash, UiLayout.BackdropPart Part, string SceneKey);

        private static readonly List<KeyValuePair<Cut, RenderTargetBitmap>> _cache = new();

        private static Bitmap? Fetch(Size size, UiLayout.BackdropWash wash,
            UiLayout.BackdropPart part)
        {
            double scale = UiLayout.BakeScale;
            int width = Round(size.Width * scale);
            int height = Round(size.Height * scale);
            if (width <= 0 || height <= 0)
            {
                return null;
            }
            var cut = new Cut(width, height, wash, part, LauncherBackdrop.CacheKey);
            for (int i = 0; i < _cache.Count; i++)
            {
                if (_cache[i].Key == cut)
                {
                    // Most recently wanted goes last, so the eviction below
                    // takes the one nothing has asked for in longest.
                    KeyValuePair<Cut, RenderTargetBitmap> hit = _cache[i];
                    _cache.RemoveAt(i);
                    _cache.Add(hit);
                    return hit.Value;
                }
            }
            RenderTargetBitmap? baked = Bake(width, height, wash, part);
            if (baked == null)
            {
                return null;
            }
            while (_cache.Count >= Kept)
            {
                _cache[0].Value.Dispose();
                _cache.RemoveAt(0);
            }
            _cache.Add(new KeyValuePair<Cut, RenderTargetBitmap>(cut, baked));
            return baked;
        }

        private static int Round(double value)
        {
            int pixels = (int)Math.Ceiling(value);
            if (pixels <= 0)
            {
                return 0;
            }
            return Math.Min((pixels + Grain - 1) / Grain * Grain, 8192);
        }

        private static RenderTargetBitmap? Bake(int width, int height,
            UiLayout.BackdropWash wash, UiLayout.BackdropPart part)
        {
            try
            {
                Panel layers = UiLayout.BackdropLayers(wash, part);
                // Measured and arranged by hand: it is in no visual tree, and
                // a visual that was never arranged renders as nothing.
                layers.Width = width;
                layers.Height = height;
                layers.Measure(new Size(width, height));
                layers.Arrange(new Rect(0, 0, width, height));
                var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
                target.Render(layers);
                return target;
            }
            catch (Exception ex)
            {
                // A screen with no photograph behind it is a screen; a
                // launcher that will not open is not.
                Mods.DebugLog.Line("ui", $"backdrop bake failed at {width}x{height}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Drop everything. For a window that has just changed size for the
        /// last time, and for the tests.
        /// </summary>
        public static void Forget()
        {
            foreach (KeyValuePair<Cut, RenderTargetBitmap> entry in _cache)
            {
                entry.Value.Dispose();
            }
            _cache.Clear();
        }
    }
}
