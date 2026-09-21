#if MPHREAD_AVALONIA
using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Project Prime's renderer-native deck wordmark.
    ///
    /// The two words are drawn directly in the deck pixel face so the front
    /// door does not depend on a baked legacy logo asset. The same control is
    /// reused by design captures and responsive launcher layouts.
    ///
    /// <para>
    /// Two lines, because the name is two words and stacking them is what
    /// gives a wordmark in a pixel face something to be: a wide single line
    /// of it is a caption. The outline is four hard offsets rather than a
    /// blur, for the same reason the buttons' edge is not a gradient -- a
    /// pixel face with a soft glow behind it is a pixel face that has been
    /// apologised for.
    /// </para>
    /// </summary>
    internal sealed class DeckWordmark : Control
    {
        /// <summary>How far the hard outline is thrown, in whole pixels.</summary>
        private const double Outline = 3;

        /// <summary>The shadow under it, which is the outline again, further down.</summary>
        private const double Drop = 10;

        /// <summary>
        /// The mark's size in the frame's ems -- <c>.wordmark h1</c>'s
        /// <c>font-size: 7.6em</c>, and the two the reference overrides it
        /// with on a phone.
        ///
        /// <b>Not a constant number of points.</b> It was 64, and 64 is a
        /// number that is only right at one window size: the stage's em is
        /// <c>clamp(9px, 1.15cqw, 15px)</c>, so on a 16:9 desktop the mark
        /// should be 7.6 of it -- 82 points in a 940-wide frame and 103 in an
        /// 1180-wide one -- and a fixed 64 is three quarters of that at the
        /// small end and under two thirds at the large. Everything around it
        /// is already in ems, so the mark was the one thing on the front
        /// screen that did not grow with the window.
        ///
        /// It is also most of "the white is not as white": the outline is a
        /// flat three points whatever the type is doing, which on a mark a
        /// third too small is an outline half again too heavy, and six passes
        /// of near-black around a thinner letter is a letter that reads grey.
        /// The face colour itself is and was <c>#f2ede2</c>, byte for byte
        /// the reference's.
        /// </summary>
        public double SizeEms { get; set; } = 7.6;

        private double Size => GuiTheme.PixelSize(Deck.Px(this, SizeEms));

        static DeckWordmark()
        {
            AffectsMeasure<DeckWordmark>(Deck.EmProperty);
            AffectsRender<DeckWordmark>(Deck.EmProperty);
        }

        public DeckWordmark(double sizeEms = 7.6)
        {
            SizeEms = sizeEms;
            IsHitTestVisible = false;
            Avalonia.Media.RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        }

        private FormattedText Line(string text, IBrush brush)
        {
            return new FormattedText(text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(GuiTheme.PixelBold, FontStyle.Normal, FontWeight.Normal),
                Size, brush);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            FormattedText top = Line("PROJECT", GuiTheme.TextBrush);
            FormattedText bottom = Line("PRIME", GuiTheme.AccentBrush);
            return new Size(
                Math.Max(top.Width, bottom.Width) + Outline * 2,
                top.Height + bottom.Height * 0.88 + Outline * 2 + Drop);
        }

        public override void Render(DrawingContext context)
        {
            FormattedText top = Line("PROJECT", new SolidColorBrush(Color.FromRgb(0xf2, 0xed, 0xe2)));
            FormattedText bottom = Line("PRIME", GuiTheme.AccentBrush);
            double w = Bounds.Width;

            // Whole pixels, both lines, or the outline lands half on one row.
            double topX = Math.Round((w - top.Width) / 2);
            double bottomX = Math.Round((w - bottom.Width) / 2);
            double topY = Math.Round(Outline);
            double bottomY = Math.Round(topY + top.Height * 0.88);

            double size = Size;
            Draw(context, "PROJECT", top, topX, topY, size);
            Draw(context, "PRIME", bottom, bottomX, bottomY, size);
        }

        private static void Draw(DrawingContext context, string word,
            FormattedText text, double x, double y, double size)
        {
            var ink = new SolidColorBrush(GuiTheme.Ink);
            // `0 10px 0 rgba(0,0,0,.55)` -- 140 is that alpha in bytes.
            var shadow = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0));

            // The drop first, then the outline, then the face over both.
            context.DrawText(Recolour(word, size, shadow), new Point(x, y + Drop));
            foreach ((double dx, double dy) in new[]
            {
                (-Outline, 0d), (Outline, 0d), (0d, -Outline), (0d, Outline),
                (-Outline, Outline), (Outline, Outline)
            })
            {
                context.DrawText(Recolour(word, size, ink), new Point(x + dx, y + dy));
            }
            context.DrawText(text, new Point(x, y));
        }

        /// <summary>
        /// A FormattedText carries its brush and hands back neither the string
        /// nor the size it was built from, so each outline pass builds its own
        /// rather than mutating one the next pass would inherit.
        /// </summary>
        private static FormattedText Recolour(string word, double size, IBrush brush)
        {
            return new FormattedText(word, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(GuiTheme.PixelBold, FontStyle.Normal, FontWeight.Normal),
                size, brush);
        }
    }
}
#endif
