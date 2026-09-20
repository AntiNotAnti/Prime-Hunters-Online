using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Text with extra space between the letters.
    ///
    /// Avalonia has no letter-spacing any more than GDI+ does, and the small
    /// upper-case labels this screen uses are unreadable without it. Drawing
    /// glyph by glyph is fine for the handful of short strings involved; do not
    /// use it for running text.
    ///
    /// Shared rather than private to one control because there are two things
    /// that draw tracked capitals now, and a second copy of
    /// <see cref="SpaceWidth"/> in particular is a second chance to get it
    /// wrong in the way that is hard to see.
    /// </summary>
    internal static class TrackedText
    {
        public static FormattedText Make(string text, double size, bool bold, IBrush brush)
            => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                GuiTheme.Face(bold), size, brush);

        public static void Draw(DrawingContext context, string text, double size,
            IBrush brush, double x, double y, double tracking)
        {
            double space = SpaceWidth(size);
            double pen = x;
            foreach (char c in text)
            {
                if (c == ' ')
                {
                    pen += space + tracking;
                    continue;
                }
                FormattedText glyph = Make(c.ToString(), size, bold: true, brush);
                context.DrawText(glyph, new Point(pen, y));
                pen += glyph.Width + tracking;
            }
        }

        public static double Measure(string text, double size, double tracking)
        {
            double space = SpaceWidth(size);
            double width = 0;
            foreach (char c in text)
            {
                width += (c == ' '
                    ? space
                    : Make(c.ToString(), size, bold: true, GuiTheme.TextBrush).Width) + tracking;
            }
            return width;
        }

        /// <summary>Height of a line at this size, measured off a real glyph.</summary>
        public static double LineHeight(double size)
            => Make("X", size, bold: true, GuiTheme.TextBrush).Height;

        /// <summary>
        /// How wide a space is in this font.
        ///
        /// Measured as the difference between two strings rather than directly,
        /// the same way the WinForms theme does it and for the same reason: a
        /// lone space measures as very nearly nothing, and a title drawn glyph
        /// by glyph off that number comes out as PLAYONLINE.
        /// </summary>
        public static double SpaceWidth(double size)
        {
            double pair = Make("nn", size, bold: true, GuiTheme.TextBrush).Width;
            double spaced = Make("n n", size, bold: true, GuiTheme.TextBrush).Width;
            return Math.Max(spaced - pair, size * 0.22);
        }
    }
}
