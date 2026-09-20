#if MPHREAD_AVALONIA
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// The panel every screen is dealt onto: the reference's <c>.panel</c>.
    ///
    /// <para>
    /// The screens were pages -- a heading centred at the top of the frame, a
    /// strip under it, content down the middle, marks along the foot, all of
    /// it floating on the photograph. That reads as a program with a
    /// background picture. What the deck theme puts there instead is one
    /// object with everything the screen has inside it, which is what makes
    /// the photograph a table the cards are on rather than wallpaper behind
    /// text.
    /// </para>
    ///
    /// <para>
    /// Every number here is an em off <see cref="Deck"/> rather than a
    /// constant, because the panel is <c>min(44em, 100%)</c> and the em is the
    /// frame's: that one expression is the whole of why a phone needs no
    /// second layout. It is 44 ems wherever there is room and the frame's
    /// width where there is not.
    /// </para>
    ///
    /// <para>
    /// Height is <i>content</i>, capped at the frame, not stretched. A panel
    /// that always filled its box put the two rows of the Story face in the
    /// middle of a card the height of the screen; the reference lets it
    /// shrink to what it holds and centres what is left, and the browser --
    /// which does fill -- looks identical either way.
    /// </para>
    /// </summary>
    internal sealed class DeckCard : Decorator
    {
        /// <summary>
        /// The solid edge under the face. Ten points flat, not an em: the
        /// reference writes <c>0 10px 0</c> and a lip that scaled with the
        /// frame would be a hairline on a phone, where it is the one cue that
        /// says the panel is an object.
        /// </summary>
        private const double Lip = 10;

        /// <summary>
        /// The reference's <c>width: min(44em, 100%)</c>. A screen sets 44
        /// and a dialog a good deal less; it is a maximum either way, so a
        /// frame narrower than the cap simply gets the frame.
        /// </summary>
        public double MaxWidthEms { get; set; } = 44;

        /// <summary>
        /// Take the height offered rather than the height the content wants.
        ///
        /// Off, because a panel that always filled its box put the two rows of
        /// the Story face in the middle of a card the height of the screen.
        /// On for the results panel, which is pinned top and bottom in the
        /// reference (`top: .9em; bottom: .9em`) and whose ballot is a list
        /// that has to have somewhere to scroll.
        /// </summary>
        public bool Fill { get; set; }

        /// <summary><c>padding: 1em</c>, inside the face.</summary>
        public const double PadEms = 1;

        private double Em => Deck.GetEm(this);

        public DeckCard()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Center;
            // Fill sets Stretch instead; see the property.
            // Antialiased, alone among these controls: the panel's corner is
            // eight points of radius and a stepped shadow around it, and both
            // read as a staircase drawn aliased.
            Avalonia.Media.RenderOptions.SetEdgeMode(this, EdgeMode.Antialias);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double em = Em;
            double pad = em * PadEms;
            double limit = em * MaxWidthEms;
            // As a real MaxWidth, not merely as the size returned. A stretched
            // control is arranged at its whole slot whatever it measured, so
            // returning the cap from here caps nothing: the panel came out the
            // width of the frame and the `min(44em, 100%)` was a comment.
            //
            // The em width alone, never the box this method is handed -- that
            // box is the one MaxWidth has just clamped, so the property fed
            // itself and any narrow pass latched the panel there with no way
            // back. Avalonia measures the Android view at 1x1 before the
            // surface has a size, which is one such pass on every launch.
            // Stretch is what does the `100%` half.
            if (Math.Abs(MaxWidth - limit) > 0.01)
            {
                MaxWidth = limit;
            }
            double cap = Math.Min(availableSize.Width, limit);
            var inner = new Size(
                Math.Max(0, cap - pad * 2),
                Math.Max(0, availableSize.Height - pad * 2));
            Size child = base.MeasureOverride(inner);
            // The cap, not the child's own width: the panel is a fixed 44 ems
            // whatever it holds, or a list of four servers would draw a card
            // narrower than a list of thirteen.
            // The edge is a shadow and hangs *below* the face, into the
            // sheet's own bottom padding -- so it is not part of the height
            // asked for. Counting it took ten points off every panel's inside
            // and put the line under the foot ten points high.
            if (Fill && !Double.IsInfinity(availableSize.Height))
            {
                return new Size(cap, availableSize.Height);
            }
            return new Size(cap, Math.Min(availableSize.Height,
                child.Height + pad * 2));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double pad = Em * PadEms;
            Child?.Arrange(new Rect(pad, pad,
                Math.Max(0, finalSize.Width - pad * 2),
                Math.Max(0, finalSize.Height - pad * 2)));
            return finalSize;
        }

        public override void Render(DrawingContext context)
        {
            double w = Bounds.Width, h = Bounds.Height;
            if (w <= 0 || h <= 0)
            {
                return;
            }
            double radius = Em * 0.7;
            var face = new RoundedRect(new Rect(0, 0, w, h), radius);

            // The reference's three shadows, in its own order and its own
            // numbers. The ring is the first of them -- a two-point spread,
            // not a stroke: a stroke straddles the edge and takes a point off
            // the inside, which is how a card ends up a point narrower than
            // the layout says.
            var shadows = new BoxShadows(
                Deck.Shadow(0, 0, 0, 2, Deck.Fade(0xe6eaf2, 0.18)),
                new[]
                {
                    Deck.Shadow(0, Lip, 0, 0, GuiTheme.PanelDeep),
                    Deck.Shadow(0, 26, 50, 0, Deck.Fade(0, 0.75))
                });
            context.DrawRectangle(new SolidColorBrush(GuiTheme.Panel), null, face, shadows);
        }
    }
}
#endif
