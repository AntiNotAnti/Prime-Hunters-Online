#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// One line of the theme's type, tracked and trimmed, laid out once and
    /// kept.
    ///
    /// <para>
    /// Two things <c>FormattedText</c> does not do and the reference asks for
    /// on nearly every string. The first is <b>letter-spacing</b>: the
    /// reference sets <c>.04em</c> on every label and up to <c>.34em</c> on a
    /// subtitle, and a point of air per letter across a word is the difference
    /// between type that is set and type that is merely rendered. There is no
    /// property for it, so a tracked run is drawn glyph by glyph -- which also
    /// means the space has to be advanced by hand, since a space measured on
    /// its own comes back at zero width (trailing whitespace is trimmed before
    /// it is measured) and "CREATE SERVER" came out "CREATESERVER".
    /// </para>
    ///
    /// <para>
    /// The second is <b>cost</b>. Shaping a string is the expensive half of
    /// drawing it, and these screens are composited into the game window on
    /// every frame it draws. None of this text depends on time, so a run is
    /// shaped once per (string, face, size, colour) and held.
    /// </para>
    /// </summary>
    internal static class DeckText
    {
        private readonly record struct Key(string Text, double Size, int Weight,
            string Family, uint Colour, double Width);

        private static readonly Dictionary<Key, FormattedText> _cache = new();

        /// <summary>
        /// The reference's default tracking on a label, in ems of the label's
        /// own size.
        /// </summary>
        public const double LabelTracking = 0.04;

        /// <summary>
        /// A space's advance, as a fraction of the size.
        ///
        /// Measured rather than assumed would be better, and cannot be: see
        /// the note above about trailing whitespace. Pixelify's space is a
        /// little under half an em and this is what a row of drawn labels
        /// lines up against.
        /// </summary>
        private const double SpaceEm = 0.42;

        private static FormattedText Lay(string text, Typeface face, double size,
            IBrush brush, bool display, double maxWidth)
        {
            uint colour = brush is ISolidColorBrush solid ? solid.Color.ToUInt32() : 0u;
            // The weight and the family are both part of the key. They were
            // not: a `bool Bold` that only ever saw Bold or Normal handed a
            // 400 run back the 600 one it had cached a moment earlier, so the
            // server rows came out in the buttons' weight however they asked.
            // ToString(), not Name: the three Pixelify files are all called
            // "Pixelify Sans" -- deliberately, so a Typeface picks between
            // them by weight -- and keying on the name alone handed a Regular
            // run back the SemiBold one that had been cached a moment
            // earlier. Every server row came out in the buttons' weight, and
            // asking for 400 changed nothing, which is what made it look like
            // a font problem rather than a cache one.
            var key = new Key(text, size, (int)face.Weight,
                face.FontFamily.ToString(), colour, maxWidth);
            if (_cache.TryGetValue(key, out FormattedText? hit))
            {
                return hit;
            }
            if (_cache.Count > 1024)
            {
                _cache.Clear();
            }
            var laid = new FormattedText(text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, face, size, brush);
            if (maxWidth > 0)
            {
                laid.MaxTextWidth = maxWidth;
                // One line, whatever the trimming decides. Without a height
                // limit Avalonia wraps at the first space rather than
                // ellipsizing, and a wrapped cell in a thirty-point row draws
                // its second line over the row beneath.
                laid.MaxTextHeight = size * 1.9;
                laid.Trimming = TextTrimming.CharacterEllipsis;
            }
            _cache[key] = laid;
            return laid;
        }

        /// <summary>A plain run: no tracking, trimmed to a width if one is given.</summary>
        public static FormattedText Run(string text, Typeface face, double size,
            IBrush brush, double maxWidth = 0, bool display = true)
        {
            return Lay(text, face, size, brush, display, maxWidth);
        }

        /// <summary>How wide a tracked run comes out, trailing air excluded.</summary>
        public static double MeasureTracked(string text, Typeface face, double size,
            double trackingEms)
        {
            if (text.Length == 0)
            {
                return 0;
            }
            double tracking = size * trackingEms;
            double pen = 0;
            foreach (char c in text)
            {
                pen += (c == ' '
                    ? size * SpaceEm
                    : Lay(c.ToString(), face, size, Brushes.White, true, 0).Width) + tracking;
            }
            // The trailing space is kept. CSS adds letter-spacing after every
            // character including the last, and dropping it made every label
            // narrower than the reference's by a character's worth of air --
            // which on a strip of four tabs is six points of drift.
            return pen;
        }

        /// <summary>
        /// Draw a tracked run from its left edge, vertically centred in a box
        /// of <paramref name="height"/>. Whole pixels in both directions: a
        /// pixel face landed on a half pixel is a blurred pixel face.
        /// </summary>
        /// <param name="hop">
        /// Where character <i>n</i> currently is, relative to where it sits at
        /// rest: a lift in points and a lean in degrees, about the character's
        /// own centre. This is the reference's <c>chhop</c> -- Balatro's
        /// DynaText, which is the one animation on these screens that happens
        /// to the *word* rather than to the object under it.
        ///
        /// The index counts drawn characters, spaces excluded, exactly as the
        /// reference's own splitter does: it only advances <c>i</c> for a
        /// character it wraps, so a two-word label staggers as though it were
        /// one. Null means the resting pose and costs nothing.
        /// </param>
        public static double DrawTracked(DrawingContext context, string text, Typeface face,
            double size, IBrush brush, double x, double top, double height, double trackingEms,
            Func<int, (double Lift, double Degrees)>? hop = null)
        {
            if (text.Length == 0)
            {
                return 0;
            }
            double tracking = size * trackingEms;
            double pen = x;
            int index = 0;
            foreach (char c in text)
            {
                if (c == ' ')
                {
                    pen += size * SpaceEm + tracking;
                    continue;
                }
                FormattedText glyph = Lay(c.ToString(), face, size, brush, true, 0);
                double gx = Math.Round(pen);
                double gy = Math.Round(top + (height - glyph.Height) / 2);
                (double lift, double degrees) = hop == null ? (0d, 0d) : hop(index);
                index++;
                if (lift == 0 && degrees == 0)
                {
                    context.DrawText(glyph, new Point(gx, gy));
                    pen += glyph.Width + tracking;
                    continue;
                }
                // Rotated about the character's own middle, which is where a
                // CSS transform's origin is by default -- about the baseline
                // instead and a three-degree lean throws the glyph sideways
                // by more than it lifts it.
                double cx = gx + glyph.Width / 2;
                double cy = gy + glyph.Height / 2;
                using (context.PushTransform(
                    Avalonia.Matrix.CreateTranslation(-cx, -cy)
                    * Avalonia.Matrix.CreateRotation(degrees * Math.PI / 180)
                    * Avalonia.Matrix.CreateTranslation(cx, cy + lift)))
                {
                    context.DrawText(glyph, new Point(gx, gy));
                }
                pen += glyph.Width + tracking;
            }
            return pen - x;
        }

        /// <summary>Drop the lot. Only the palette or the face changing needs this.</summary>
        public static void Forget() => _cache.Clear();
    }
}
#endif
