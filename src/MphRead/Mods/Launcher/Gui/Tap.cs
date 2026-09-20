using System;
using Avalonia;
using Avalonia.Input;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Whether a press and the release after it were a tap, or the beginning
    /// of a scroll that happened to start on this row.
    ///
    /// A mouse asks that question by itself: the pointer goes down on the row
    /// you meant and the wheel scrolls without pressing anything. A finger
    /// cannot. Every scroll on a phone begins as a press on whatever is under
    /// it, so a row that acts on <c>OnPointerPressed</c> -- which is what
    /// <see cref="ChoiceRow"/>, <see cref="ToggleRow"/>, <see cref="ServerRow"/>,
    /// <see cref="SliderRow"/>, <see cref="KeyRow"/> and <see cref="PadRow"/>
    /// all did -- answers the settings page every time somebody drags it. That
    /// is the bug this exists for: scrolling the settings toggled whatever the
    /// scroll started on, and cycled whatever it started on, and moved
    /// whatever slider it started on.
    ///
    /// The rule is the one every phone uses:
    ///
    /// - the press decides nothing; it only remembers where it landed
    /// - a finger that travels more than <see cref="Slop"/> from there has
    ///   stopped meaning the row, and nothing it does afterwards counts
    /// - the release acts, and only if it lands inside the control
    ///
    /// The distance test is for a **finger or a stylus only**. A mouse keeps
    /// what it had -- press here, release here, that is a click -- so nothing
    /// on the desktop changes feel, and a hand that wobbles two points while
    /// clicking still clicks.
    ///
    /// Acting on the release rather than the press is the other half, and it
    /// is what makes the gesture cancellable at all: there is no taking a
    /// toggle back once a press has flipped it.
    ///
    /// The methods come in pairs -- one taking Avalonia's event, one taking
    /// plain values. The plain ones are the rule itself, and are what
    /// <c>-tapcheck</c> drives without a display, a toolkit or a finger.
    /// </summary>
    internal sealed class Tap
    {
        /// <summary>
        /// How far a finger may wander and still mean the row it started on,
        /// in layout points.
        ///
        /// Eight, which is about thirteen Android dp, sits between the two
        /// numbers that matter: above the jitter of a finger held still on
        /// glass, and well under the thirty points Avalonia's
        /// <c>ScrollGestureRecognizer</c> wants before it calls a drag a
        /// scroll. Anything in between cancels the tap without scrolling,
        /// which is the right answer for a gesture that was neither.
        /// </summary>
        public const double Slop = 8;

        private object? _pointer;
        private Point _origin;
        private bool _drags;

        /// <summary>A press is still live: it has not been cancelled or let go.</summary>
        public bool Down => _pointer != null;

        /// <summary>
        /// True for the pointers that scroll by dragging, which are the ones
        /// the distance rule is for.
        /// </summary>
        public static bool Drags(PointerEventArgs e)
        {
            return e.Pointer.Type == PointerType.Touch || e.Pointer.Type == PointerType.Pen;
        }

        public void Press(PointerPressedEventArgs e, Visual over)
        {
            Press(e.Pointer, e.GetPosition(over), Drags(e));
        }

        /// <summary>True when this move is what cancelled the tap.</summary>
        public bool Moved(PointerEventArgs e, Visual over)
        {
            return Moved(e.Pointer, e.GetPosition(over));
        }

        /// <summary>True when the press and this release were a tap.</summary>
        public bool Release(PointerReleasedEventArgs e, Visual over)
        {
            return Release(e.Pointer, e.GetPosition(over), over.Bounds.Size);
        }

        /// <summary>
        /// Where the pointer has got to since the press, or nothing if there
        /// is no press. For <see cref="SliderRow"/>, which has to tell a drag
        /// along its own track from a drag down the page.
        /// </summary>
        public Vector Travel(Point p)
        {
            return _pointer == null ? default : p - _origin;
        }

        // ------------------------------------------------------- the rule

        public void Press(object pointer, Point origin, bool drags)
        {
            _pointer = pointer;
            _origin = origin;
            _drags = drags;
        }

        public bool Moved(object pointer, Point p)
        {
            if (_pointer == null || !ReferenceEquals(pointer, _pointer) || !_drags)
            {
                return false;
            }
            Vector travel = p - _origin;
            if (Math.Abs(travel.X) <= Slop && Math.Abs(travel.Y) <= Slop)
            {
                return false;
            }
            _pointer = null;
            return true;
        }

        public bool Release(object pointer, Point p, Size size)
        {
            if (_pointer == null || !ReferenceEquals(pointer, _pointer))
            {
                // Somebody else's pointer, or one that was cancelled on the
                // way: let go of nothing and act on nothing.
                return false;
            }
            _pointer = null;
            // Where the release landed, not IsPointerOver: a finger hovers
            // nothing, so on a touchscreen the pointer has already left the
            // row by the time it lets go. See UiWord.OnPointerReleased.
            return p.X >= 0 && p.Y >= 0 && p.X <= size.Width && p.Y <= size.Height;
        }

        /// <summary>
        /// Whether a travel is along a row rather than down the page, which is
        /// what tells a slider being dragged from the page being scrolled past
        /// one. <see cref="SliderRow"/> is the only control with a gesture of
        /// its own to defend; it is here so it can be checked with the rest.
        ///
        /// Ties go to the row: a track lives across the page and the scroller
        /// runs down it, so the ambiguous diagonal is the one already under a
        /// finger.
        /// </summary>
        public static bool Sideways(Vector travel)
        {
            return Math.Abs(travel.X) > Slop && Math.Abs(travel.X) >= Math.Abs(travel.Y);
        }

        /// <summary>
        /// Give the gesture up: the pointer left, or something above took it
        /// -- which on a scrolling page is the scroll gesture itself, and
        /// arrives as <c>OnPointerCaptureLost</c>. True if there was one.
        /// </summary>
        public bool Cancel()
        {
            bool was = _pointer != null;
            _pointer = null;
            return was;
        }
    }
}
