#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// A flag, drawn on the same pixel grid as everything else.
    ///
    /// <para>
    /// Not emoji. A regional-indicator pair renders as a flag on a Mac, as the
    /// two letters on Windows, and as a box wherever the font has neither --
    /// which makes the one glyph on this screen that is supposed to be
    /// recognised at a glance the one glyph that changes shape per platform.
    /// So they are drawn, in whole pixels, out of the shapes flags are
    /// actually made of: bands, crosses, discs. Thirty-five of them, described
    /// rather than hand-drawn, and everything else falls back to its two-letter
    /// code -- which is honest, and is what the emoji would have shown anyway.
    /// </para>
    /// </summary>
    internal static class Flags
    {
        private enum Style
        {
            V, V2, H, H2, H3, Cross, Swiss, Disc,
            Union, Union2, Stars, Maple, Spain, Greece, Czech, Chile
        }

        private readonly record struct Flag(Style Style, uint[] Colours);

        private static readonly Dictionary<string, Flag> _flags = new()
        {
            ["AR"] = new Flag(Style.H, new[] { 0x6f9ec9u, 0xe2e0d8u, 0x6f9ec9u }),
            ["AT"] = new Flag(Style.H, new[] { 0x9e2b2bu, 0xe2e0d8u, 0x9e2b2bu }),
            ["AU"] = new Flag(Style.Union2, Array.Empty<uint>()),
            ["BE"] = new Flag(Style.V, new[] { 0x16181du, 0xc9a227u, 0x9e2b2bu }),
            ["BR"] = new Flag(Style.Disc, new[] { 0x2c7a4eu, 0xc9a227u }),
            ["CA"] = new Flag(Style.Maple, Array.Empty<uint>()),
            ["CH"] = new Flag(Style.Swiss, new[] { 0x9e2b2bu, 0xe2e0d8u }),
            ["CL"] = new Flag(Style.Chile, Array.Empty<uint>()),
            ["CO"] = new Flag(Style.H3, new[] { 0xc9a227u, 0x22457au, 0x9e2b2bu }),
            ["CZ"] = new Flag(Style.Czech, Array.Empty<uint>()),
            ["DE"] = new Flag(Style.H, new[] { 0x16181du, 0x9e2b2bu, 0xc9a227u }),
            ["DK"] = new Flag(Style.Cross, new[] { 0x9e2b2bu, 0xe2e0d8u }),
            ["ES"] = new Flag(Style.Spain, Array.Empty<uint>()),
            ["FI"] = new Flag(Style.Cross, new[] { 0xe2e0d8u, 0x22457au }),
            ["FR"] = new Flag(Style.V, new[] { 0x22457au, 0xe2e0d8u, 0x9e2b2bu }),
            ["GB"] = new Flag(Style.Union, Array.Empty<uint>()),
            ["GR"] = new Flag(Style.Greece, Array.Empty<uint>()),
            ["HU"] = new Flag(Style.H, new[] { 0x9e2b2bu, 0xe2e0d8u, 0x2c7a4eu }),
            ["ID"] = new Flag(Style.H2, new[] { 0x9e2b2bu, 0xe2e0d8u }),
            ["IE"] = new Flag(Style.V, new[] { 0x2c7a4eu, 0xe2e0d8u, 0xc47a33u }),
            ["IS"] = new Flag(Style.Cross, new[] { 0x22457au, 0xe2e0d8u }),
            ["IT"] = new Flag(Style.V, new[] { 0x2c7a4eu, 0xe2e0d8u, 0x9e2b2bu }),
            ["JP"] = new Flag(Style.Disc, new[] { 0xe2e0d8u, 0x9e2b2bu }),
            ["MX"] = new Flag(Style.V, new[] { 0x1f6b45u, 0xe2e0d8u, 0x9e2b2bu }),
            ["NL"] = new Flag(Style.H, new[] { 0x9e2b2bu, 0xe2e0d8u, 0x22457au }),
            ["NO"] = new Flag(Style.Cross, new[] { 0x9e2b2bu, 0x22457au }),
            ["NZ"] = new Flag(Style.Union2, Array.Empty<uint>()),
            ["PL"] = new Flag(Style.H2, new[] { 0xe2e0d8u, 0x9e2b2bu }),
            ["PT"] = new Flag(Style.V2, new[] { 0x2c7a4eu, 0x9e2b2bu }),
            ["RO"] = new Flag(Style.V, new[] { 0x22457au, 0xc9a227u, 0x9e2b2bu }),
            ["RU"] = new Flag(Style.H, new[] { 0xe2e0d8u, 0x22457au, 0x9e2b2bu }),
            ["SE"] = new Flag(Style.Cross, new[] { 0x22457au, 0xc9a227u }),
            ["TR"] = new Flag(Style.Disc, new[] { 0x9e2b2bu, 0xe2e0d8u }),
            ["UA"] = new Flag(Style.H2, new[] { 0x2b6fa8u, 0xc9a227u }),
            ["US"] = new Flag(Style.Stars, Array.Empty<uint>())
        };

        // `1.5em` by `1.05em` of a row's own em -- see Deck.RowEm. Twenty
        // by fourteen, which is also a clean 16:11 for the bands.
        public const double Width = 20;
        public const double Height = 14;

        public static bool Known(string code) => _flags.ContainsKey(code);

        private static SolidColorBrush B(uint hex) => new(Color.FromRgb(
            (byte)(hex >> 16), (byte)(hex >> 8), (byte)hex));

        /// <summary>Whole pixels: a flag on a half pixel is a smudge.</summary>
        private static void Fill(DrawingContext c, IBrush b, double x, double y, double w, double h)
        {
            c.FillRectangle(b, new Rect(Math.Round(x), Math.Round(y), Math.Round(w), Math.Round(h)));
        }

        public static void Draw(DrawingContext context, string code, double x, double y)
        {
            double w = Width, h = Height;
            x = Math.Round(x);
            y = Math.Round(y);
            if (!_flags.TryGetValue(code, out Flag flag))
            {
                DrawCode(context, code, x, y);
                return;
            }
            uint[] c = flag.Colours;
            switch (flag.Style)
            {
                case Style.V:
                    Fill(context, B(c[0]), x, y, w / 3, h);
                    Fill(context, B(c[1]), x + w / 3, y, w / 3, h);
                    Fill(context, B(c[2]), x + 2 * w / 3, y, w / 3, h);
                    break;
                case Style.V2:
                    Fill(context, B(c[0]), x, y, w * 0.4, h);
                    Fill(context, B(c[1]), x + w * 0.4, y, w * 0.6, h);
                    break;
                case Style.H:
                case Style.H3:
                    Fill(context, B(c[0]), x, y, w, h / 3);
                    Fill(context, B(c[1]), x, y + h / 3, w, h / 3);
                    Fill(context, B(c[2]), x, y + 2 * h / 3, w, h / 3);
                    break;
                case Style.H2:
                    Fill(context, B(c[0]), x, y, w, h / 2);
                    Fill(context, B(c[1]), x, y + h / 2, w, h / 2);
                    break;
                case Style.Cross:
                    Fill(context, B(c[0]), x, y, w, h);
                    Fill(context, B(c[1]), x + 6, y, 4, h);
                    Fill(context, B(c[1]), x, y + 6, w, 4);
                    break;
                case Style.Swiss:
                    Fill(context, B(c[0]), x, y, w, h);
                    Fill(context, B(c[1]), x + 9, y + 3, 4, 9);
                    Fill(context, B(c[1]), x + 6, y + 6, 10, 3);
                    break;
                case Style.Disc:
                    Fill(context, B(c[0]), x, y, w, h);
                    context.DrawEllipse(B(c[1]), null,
                        new Point(x + w / 2, y + h / 2), 4, 4);
                    break;
                case Style.Union:
                    DrawUnion(context, x, y, w, h);
                    break;
                case Style.Union2:
                    Fill(context, B(0x22457au), x, y, w, h);
                    DrawUnion(context, x, y, w / 2, h / 2);
                    Fill(context, B(0xe2e0d8u), x + 15, y + 4, 2, 2);
                    Fill(context, B(0xe2e0d8u), x + 17, y + 9, 2, 2);
                    Fill(context, B(0xe2e0d8u), x + 13, y + 11, 2, 2);
                    break;
                case Style.Stars:
                    Fill(context, B(0xe2e0d8u), x, y, w, h);
                    for (int i = 0; i < 4; i++)
                    {
                        Fill(context, B(0x9e2b2bu), x, y + i * 4, w, 2);
                    }
                    Fill(context, B(0x22457au), x, y, 10, 8);
                    break;
                case Style.Maple:
                    Fill(context, B(0xe2e0d8u), x, y, w, h);
                    Fill(context, B(0x9e2b2bu), x, y, 5, h);
                    Fill(context, B(0x9e2b2bu), x + 17, y, 5, h);
                    Fill(context, B(0x9e2b2bu), x + 10, y + 4, 2, 8);
                    Fill(context, B(0x9e2b2bu), x + 8, y + 6, 6, 3);
                    break;
                case Style.Spain:
                    Fill(context, B(0x9e2b2bu), x, y, w, h);
                    Fill(context, B(0xc9a227u), x, y + 4, w, 7);
                    break;
                case Style.Greece:
                    Fill(context, B(0xe2e0d8u), x, y, w, h);
                    for (int i = 0; i < 3; i++)
                    {
                        Fill(context, B(0x2b6fa8u), x, y + i * 4, w, 2);
                    }
                    Fill(context, B(0x2b6fa8u), x, y, 9, 8);
                    Fill(context, B(0xe2e0d8u), x + 3, y, 3, 8);
                    Fill(context, B(0xe2e0d8u), x, y + 3, 9, 2);
                    break;
                case Style.Czech:
                    Fill(context, B(0xe2e0d8u), x, y, w, h / 2);
                    Fill(context, B(0x9e2b2bu), x, y + h / 2, w, h / 2);
                    Fill(context, B(0x22457au), x, y, 4, h);
                    Fill(context, B(0x22457au), x + 4, y + 3, 3, 9);
                    break;
                case Style.Chile:
                    Fill(context, B(0xe2e0d8u), x, y, w, h / 2);
                    Fill(context, B(0x9e2b2bu), x, y + h / 2, w, h / 2);
                    Fill(context, B(0x22457au), x, y, 8, 8);
                    break;
            }
            context.DrawRectangle(null,
                new Pen(new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), 1),
                new Rect(x + 0.5, y + 0.5, w - 1, h - 1));
        }

        private static void DrawUnion(DrawingContext context, double x, double y, double w, double h)
        {
            Fill(context, B(0x22457au), x, y, w, h);
            var white = B(0xe2e0d8u);
            var red = B(0x9e2b2bu);
            Fill(context, white, x + w / 2 - 2, y, 4, h);
            Fill(context, white, x, y + h / 2 - 2, w, 4);
            Fill(context, red, x + w / 2 - 1, y, 2, h);
            Fill(context, red, x, y + h / 2 - 1, w, 2);
        }

        /// <summary>
        /// No flag drawn for this country, or no country at all: the code, in
        /// the same slab. Which is what the emoji would have shown on Windows
        /// anyway, and is at least deliberate here.
        /// </summary>
        private static void DrawCode(DrawingContext context, string code, double x, double y)
        {
            Fill(context, B(0x1b2736u), x, y, Width, Height);
            if (code.Length >= 2)
            {
                var text = new FormattedText(code.ToUpperInvariant(),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(GuiTheme.PixelSemi, FontStyle.Normal, FontWeight.Normal),
                    12, GuiTheme.TextDimBrush);
                context.DrawText(text, new Point(
                    Math.Round(x + (Width - text.Width) / 2),
                    Math.Round(y + (Height - text.Height) / 2)));
            }
            context.DrawRectangle(null,
                new Pen(new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), 1),
                new Rect(x + 0.5, y + 0.5, Width - 1, Height - 1));
        }
    }
}
#endif
