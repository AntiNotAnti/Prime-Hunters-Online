using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// The launcher's screens, scaled to whatever they have been handed.
    ///
    /// This is the other half of <c>UiSurface</c>, for the head that has
    /// no surface. The desktop composites the screens into the game window and
    /// therefore already had somewhere to put the scale; Android hands one
    /// control to the toolkit as its single view and had nowhere, so the
    /// screens were laid out in the view's own points and nothing scaled them
    /// at all.
    ///
    /// What that looked like: a phone reports about 830 by 390 points in
    /// landscape (1080x2280 pixels at 2.75x density), and every screen in this
    /// folder is authored for something near
    /// <see cref="UiLayout.MinBoxWidth"/> by <see cref="UiLayout.MinBoxHeight"/>.
    /// The well alone is 820 points wide (<see cref="UiLayout.WellPlay"/>) --
    /// wider than the whole screen once there is any margin -- so the heading
    /// ran off both edges, the marks at the foot were below the bottom of the
    /// glass, and a list given a column narrower than one of its rows drew its
    /// rows somewhere nobody could see. "The servers are invisible" is that:
    /// the browser was laying out correctly, off the side of the display.
    ///
    /// <c>LayoutTransformControl</c> measures its child through the
    /// inverse of its own transform, so a host that fills the view measures the
    /// screen at exactly (view / factor): the screens get their box back and
    /// Skia still draws every glyph at the panel's real resolution. It is the
    /// same control the desktop surface uses, for the same reason.
    ///
    /// What it must *not* do is squeeze them until they fit, which is what the
    /// first version did -- see the unit note in <see cref="Apply"/>. A phone
    /// gets 1.0 here, the size the screens were drawn at, and the box it
    /// leaves is narrower than the widest well; <see cref="UiLayout.WellGutter"/>
    /// is the other half of that and lets the well give way instead.
    /// </summary>
    internal sealed class UiScaleHost : Decorator
    {
        /// <summary>
        /// Android's layout unit in the unit the screens are authored in:
        /// 1/160 inch against 1/96, so 5/3.
        ///
        /// It is a fixed ratio rather than anything read off the display,
        /// which is the point of dp -- the density is already divided out, so
        /// this is the same number on every phone and every tablet.
        /// </summary>
        private const double DipsPerPoint = 160.0 / 96.0;

        /// <summary>
        /// The factor for a view of this many Android layout points, for
        /// whoever is not this control -- the surface that draws the screens
        /// into the game's own frame asks the same question about the same
        /// curve, and two answers to it is two sizes of type in one program.
        /// </summary>
        public static double FactorFor(double widthDips, double heightDips)
        {
            if (Double.IsInfinity(widthDips) || Double.IsInfinity(heightDips)
                || widthDips <= 0 || heightDips <= 0)
            {
                return 1;
            }
            return UiLayout.Factor(widthDips / DipsPerPoint, heightDips / DipsPerPoint)
                * DipsPerPoint;
        }

        private readonly LayoutTransformControl _host;
        private double _factor = -1;

        public UiScaleHost(Control screen)
        {
            _host = new LayoutTransformControl
            {
                LayoutTransform = new ScaleTransform(1, 1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Child = screen
            };
            Child = _host;
        }

        /// <summary>The screen inside, for whoever needs to swap it.</summary>
        public Control? Screen
        {
            get => _host.Child;
            set => _host.Child = value;
        }

        /// <summary>
        /// In the measure pass, because the size is not announced any other
        /// way here. The desktop is told the window's rectangle by the game
        /// loop and can set the transform outside a layout pass; this head
        /// learns its size by being measured, and a screen laid out one pass
        /// behind its own scale is the phone drawing the previous orientation.
        ///
        /// The assignment is guarded on the value having actually moved, which
        /// is what keeps it from being a layout loop: a transform assigned on
        /// every measure invalidates the child on every measure.
        /// </summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            Apply(availableSize);
            return base.MeasureOverride(availableSize);
        }

        private void Apply(Size size)
        {
            // Measured unbounded while something works out how big it wants to
            // be. There is no factor to pick from an infinity, and picking one
            // from the finite axis alone would set a scale off half the
            // question.
            if (Double.IsInfinity(size.Width) || Double.IsInfinity(size.Height)
                || size.Width <= 0 || size.Height <= 0)
            {
                return;
            }
            // Asked in the screens' own points and then given back in this
            // platform's, which is the whole of what was wrong with the first
            // version of this.
            //
            // A layout point here is Android's dp, defined as 1/160 inch; the
            // point the screens are drawn in is the desktop's, 1/96. So the
            // *same number* is 0.6 of the physical size on a phone that it is
            // on a monitor, and a curve that treats the two as the same unit
            // asks a 830-point-wide view to hold a 960-point layout and scales
            // everything down to 0.6 to make it -- which is 0.36 of the size
            // the text is on a desktop, on the screen held closest to the
            // face. That is "bien trop petit", and it is not a taste question:
            // it is a unit error.
            //
            // So the view is converted into the authored unit, the shared
            // curve is asked about *that*, and the answer is converted back.
            // A phone lands on the curve's own 0.6 floor, which comes back
            // here as exactly 1.0 -- the size the screens were drawn at, and
            // what the last release put on screen. A tablet has more of them
            // and climbs above it, as a bigger window does on the desktop.
            double factor = UiScaleHost.FactorFor(size.Width, size.Height);
            // Device pixels per layout point, for the one control that cuts
            // its own bitmap. Two multiplications here, not one: the view's
            // points are already the display's pixels divided by its density,
            // so a backdrop baked at the factor alone would be cut at 0.6 of
            // the points and blown up by the density on top of that.
            double density = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            UiLayout.BakeScale = factor * density;
            if (Math.Abs(factor - _factor) < 0.0001)
            {
                return;
            }
            _factor = factor;
            // A *new* transform, never the old one with new numbers:
            // LayoutTransformControl watches the property, and mutating the
            // object it already holds changes nothing it can see.
            _host.LayoutTransform = new ScaleTransform(factor, factor);
            Mods.DebugLog.Line("ui", $"screens at {factor:0.###}x "
                + $"({size.Width:0}x{size.Height:0} points)");
        }
    }
}
