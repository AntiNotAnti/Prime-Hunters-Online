using System;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// The front screen's palette and metrics, in Avalonia terms.
    ///
    /// The colours are <c>LauncherTheme</c>'s, value for value, and are
    /// meant to stay that way: the two screens are the same product on
    /// different toolkits, and a palette copied by eye would drift the first
    /// time either was adjusted. They are duplicated rather than shared because
    /// LauncherTheme is System.Drawing and does not compile off Windows;
    /// the numbers, not the types, are the thing being kept in step.
    ///
    /// No DPI scaling here, unlike the WinForms theme: Avalonia lays out in
    /// device-independent pixels and scales the whole visual tree itself, which
    /// is the one piece of per-monitor work LauncherTheme.S had to do by hand.
    /// </summary>
    internal static class GuiTheme
    {
        public static readonly Color Ink = Color.FromRgb(10, 12, 16);
        public static readonly Color Panel = Color.FromRgb(18, 21, 28);
        public static readonly Color PanelLight = Color.FromRgb(26, 31, 41);
        public static readonly Color Edge = Color.FromRgb(38, 46, 60);
        /// <summary>Under Panel: the well a row or a card sits in.</summary>
        public static readonly Color PanelDeep = Color.FromRgb(14, 17, 24);
        public static readonly Color Text = Color.FromRgb(230, 234, 242);
        public static readonly Color TextDim = Color.FromRgb(138, 147, 166);
        /// <summary>The one accent colour everywhere: the same warm the main menu's Play uses.</summary>
        public static readonly Color Accent = Color.FromRgb(255, 179, 71);
        public static readonly Color Warm = Color.FromRgb(255, 179, 71);
        // The deck palette's, not the old neon pair: #6ee787 and #ff6b6b were
        // chosen against a flat dark panel and buzz on this one, which is two
        // stops down and forty points less saturated. A ping column is where
        // that showed -- three rows of vivid green over a map render.
        public static readonly Color Good = Color.FromRgb(0x5f, 0x9e, 0x72);
        public static readonly Color Warn = Color.FromRgb(0xc0, 0x8a, 0x3e);
        public static readonly Color Bad = Color.FromRgb(0xa8, 0x54, 0x54);

        public static readonly IBrush InkBrush = new SolidColorBrush(Ink);
        public static readonly IBrush PanelBrush = new SolidColorBrush(Panel);
        public static readonly IBrush PanelLightBrush = new SolidColorBrush(PanelLight);
        public static readonly IBrush EdgeBrush = new SolidColorBrush(Edge);
        public static readonly IBrush TextBrush = new SolidColorBrush(Text);
        public static readonly IBrush TextDimBrush = new SolidColorBrush(TextDim);
        public static readonly IBrush AccentBrush = new SolidColorBrush(Accent);
        public static readonly IBrush WarmBrush = new SolidColorBrush(Warm);
        public static readonly IBrush GoodBrush = new SolidColorBrush(Good);
        public static readonly IBrush WarnBrush = new SolidColorBrush(Warn);
        public static readonly IBrush BadBrush = new SolidColorBrush(Bad);

        /// <summary>Panel and PanelLight, thinned so the backdrop still shows through them.</summary>
        public static readonly IBrush GlassBrush =
            new SolidColorBrush(Color.FromArgb(220, Panel.R, Panel.G, Panel.B));
        public static readonly IBrush GlassLightBrush =
            new SolidColorBrush(Color.FromArgb(220, PanelLight.R, PanelLight.G, PanelLight.B));

        /// <summary>
        /// What the pause menu and the in-game settings lay over the match.
        ///
        /// Dark enough to read a menu on and clear enough to watch through,
        /// because the match behind it is still running -- a networked one
        /// cannot be paused, and hiding it would be a lie. A compositor that
        /// refuses window transparency renders this opaque instead, which
        /// costs the view and nothing else.
        /// </summary>
        public static readonly IBrush ScrimBrush =
            new SolidColorBrush(Color.FromArgb(196, Ink.R, Ink.G, Ink.B));

        /// <summary>
        /// The display face: Roboto Bold, embedded rather than looked up on the
        /// system for the same reason the WinForms theme's Bahnschrift lookup
        /// does not apply here -- there is no font every Linux install has.
        /// Every menu uses this one weight, the same way OpenQuake3/defrag's
        /// own UI does (its "default.ttf" is this exact file under another
        /// name); hierarchy there is colour and size; nothing is ever regular.
        /// </summary>
        /// <summary>
        /// The primary interface face. Inter is already registered by both the
        /// desktop and Android Avalonia builders through WithInterFont().
        /// </summary>
        public static readonly FontFamily Interface = new("fonts:Inter#Inter");

        public static FontFamily Display => Interface;

        /// <summary>
        /// Roboto Bold, still here: it is what the few places that are prose
        /// rather than interface use -- a wrapped note, a credits line -- where
        /// a pixel face costs more in legibility than it returns in character.
        /// </summary>
        public static readonly FontFamily Prose =
            new("avares://FruityPrime/Assets/Fonts/Roboto-Bold.ttf#Roboto");

        /// <summary>
        /// The deck theme's face: Pixelify Sans, SIL OFL 1.1, vendored beside
        /// the other two.
        ///
        /// A pixel font is not decoration here. The chunky face, the solid
        /// edge and the pop are three parts of one look and the fourth is the
        /// type: set the same buttons in Roboto and they read as web buttons
        /// with a drop shadow. It is also why <see cref="PixelSize"/> exists
        /// -- a pixel face is drawn on a grid, and a size that is not a whole
        /// multiple of that grid is a blurred pixel font, which is worse than
        /// no pixel font at all.
        /// </summary>
        public static readonly FontFamily Pixel =
            new("avares://FruityPrime/Assets/Fonts/PixelifySans-Regular.ttf#Pixelify Sans");

        /// <summary>
        /// The same face at 600 and 700, as their own files.
        ///
        /// Not a weight on <see cref="Pixel"/>: the shipped face was the
        /// upstream variable file, Avalonia's font manager does not set a
        /// variation axis, and a request for SemiBold therefore got the 400
        /// default with a synthesised bold laid over it. On a pixel face that
        /// is an extra half-pixel on every stem -- which is exactly the
        /// difference anybody comparing this to the reference could see and
        /// nobody could name.
        /// </summary>
        public static readonly FontFamily PixelSemi =
            new("avares://FruityPrime/Assets/Fonts/PixelifySans-SemiBold.ttf#Pixelify Sans");

        public static readonly FontFamily PixelBold =
            new("avares://FruityPrime/Assets/Fonts/PixelifySans-Bold.ttf#Pixelify Sans");

        /// <summary>
        /// The nearest size at or below <paramref name="wanted"/> that lands
        /// on the face's own grid. Pixelify draws on a 4-unit em, so sizes
        /// that are multiples of four are the ones whose stems come out one
        /// pixel wide instead of one and a half.
        /// </summary>
        public static double PixelSize(double wanted)
        {
            // Nearest even point, floor of 9.
            //
            // There was a floor of 16 here, put in when the glyphs were being
            // drawn aliased and anything smaller was a smudge. The glyphs are
            // no longer drawn aliased -- see PixelPerfect -- so the floor was
            // buying nothing and costing everything: every control sized off
            // its text came out half again as tall as the layout this is a
            // port of, and the whole screen shifted down behind it.
            return Math.Max(9, Math.Round(wanted / 2) * 2);
        }

        /// <summary>Hey November, from the same font folder: reserved for Play, and nothing else.</summary>
        public static readonly FontFamily Title =
            new("avares://FruityPrime/Assets/Fonts/heyNovember.ttf#Hey November");

        /// <summary>
        /// The face every self-drawn control lays its text in.
        ///
        /// One place, because the deck theme's type is not a per-control
        /// choice: the pixel face, the chunky edge and the pop are three parts
        /// of one look, and a launcher that is pixel on its buttons and Roboto
        /// in its server browser is a launcher with two themes in it.
        /// </summary>
        public static Typeface Face(bool bold) =>
            new(Interface, FontStyle.Normal,
                bold ? FontWeight.SemiBold : FontWeight.Normal);

        /// <summary>
        /// Lay a string out on the pixel grid: the size snapped to the face's
        /// own step, and nothing else changed. Every <c>FormattedText</c> the
        /// launcher builds goes through here so no single call site can be the
        /// one that comes out blurred.
        /// </summary>
        public static FormattedText Lay(string text, double size, IBrush brush, bool bold)
        {
            return new FormattedText(text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Face(bold), size, brush);
        }

        /// <summary>
        /// Aliased glyphs, aliased edges, applied once at the root of a screen
        /// and inherited by everything under it.
        ///
        /// These are attached properties that flow down the visual tree, so
        /// the alternative is setting them on every control that draws -- and
        /// the one that gets forgotten is the one that shows.
        /// </summary>
        public static void PixelPerfect(Avalonia.Visual visual)
        {
            // Edges aliased, glyphs not.
            //
            // The chunky look is the *shapes*: a slab with a hard corner and a
            // solid lip, which wants no feathering. The type is a different
            // question -- the layout this is a port of is a browser's, where
            // glyphs are anti-aliased, and a pixel face at eleven points with
            // no anti-aliasing loses the difference between an "e" and an "o".
            // Aliasing everything made both worse at once.
            Avalonia.Media.RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);
            // And the glyphs explicitly *not*. EdgeMode is not only about
            // geometry in the Skia backend -- it turns antialiasing off for
            // everything drawn under it, text included -- so the paragraph
            // above described an intention the code was not carrying out, and
            // every label came out hard-edged where the layout this is a port
            // of has grey. A separate attached property says so, and it is set
            // in the same place for the same reason: the control that would
            // have been forgotten is the one that shows.
            Avalonia.Media.TextOptions.SetTextRenderingMode(visual,
                TextRenderingMode.Antialias);
            // Nearest-neighbour for bitmaps too, so a map render scaled into a
            // row is scaled the way the rest of the screen is drawn.
            Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(visual,
                Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        }

        /// <summary>
        /// The window's icon -- the cherry mark alone, not the wordmark: a
        /// title bar, taskbar entry and alt-tab thumbnail are all small and
        /// square, and the wide banner would either be squeezed unreadable or
        /// cropped to nothing. Lazy for the same reason the splash's copy of
        /// the wordmark is: decoded once, and a build missing the asset gets
        /// no icon rather than a crash before the window exists.
        ///
        /// Named AppIcon rather than WindowIcon: this is a
        /// <c>Lazy&lt;Avalonia.Controls.WindowIcon?&gt;</c>, and giving it the
        /// same name as the type it holds is the kind of thing that reads fine
        /// today and confuses whoever edits it next.
        /// </summary>
        public static readonly Lazy<WindowIcon?> AppIcon = new(() =>
        {
            try
            {
                using Stream stream = AssetLoader.Open(
                    new Uri("avares://FruityPrime/Assets/fruity-prime-mark.png"));
                return new WindowIcon(stream);
            }
            catch (Exception)
            {
                return null;
            }
        });

        /// <summary>Blend towards white or black, for hover and pressed states.</summary>
        public static Color Shade(Color color, double amount)
        {
            double t = amount < 0 ? -amount : amount;
            int target = amount >= 0 ? 255 : 0;
            return Color.FromArgb(color.A,
                (byte)(color.R + (target - color.R) * t),
                (byte)(color.G + (target - color.G) * t),
                (byte)(color.B + (target - color.B) * t));
        }
    }
}
