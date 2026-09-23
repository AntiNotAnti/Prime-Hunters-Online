using System;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Shared launcher palette and typography.
    ///
    /// The FPS-hub direction uses cold dark panels, blue focus/selection,
    /// restrained semantic status colours and Inter as the player-facing UI
    /// face. JetBrains Mono is reserved for technical data. Older deck controls
    /// still consume these tokens while their layouts are migrated, which is
    /// how the program remains one visual system during the transition.
    /// </summary>
    internal static class GuiTheme
    {
        public static readonly Color Ink = PrimeTheme.Background;
        public static readonly Color Panel = PrimeTheme.Panel;
        public static readonly Color PanelLight = PrimeTheme.PanelRaised;
        public static readonly Color Edge = PrimeTheme.Border;
        /// <summary>Under Panel: the well a row or a card sits in.</summary>
        public static readonly Color PanelDeep = PrimeTheme.BackgroundDeep;
        public static readonly Color Text = PrimeTheme.Text;
        public static readonly Color TextDim = PrimeTheme.TextSecondary;
        /// <summary>The primary focus/selection colour across hub and migrated screens.</summary>
        public static readonly Color Accent = PrimeTheme.Primary;
        public static readonly Color Warm = PrimeTheme.Warning;
        // The deck palette's, not the old neon pair: #6ee787 and #ff6b6b were
        // chosen against a flat dark panel and buzz on this one, which is two
        // stops down and forty points less saturated. A ping column is where
        // that showed -- three rows of vivid green over a map render.
        public static readonly Color Good = PrimeTheme.Green;
        public static readonly Color Warn = PrimeTheme.Warning;
        public static readonly Color Bad = PrimeTheme.Danger;

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
        /// The primary interface face. Inter is already registered by both the
        /// desktop and Android Avalonia builders through WithInterFont().
        /// </summary>
        public static readonly FontFamily Interface = new("fonts:Inter#Inter");

        public static FontFamily Display => PrimeTypography.Display;

        /// <summary>
        /// Roboto Bold remains for a handful of legacy prose/credit call sites.
        /// New hub-facing prose should use <see cref="Interface"/>.
        /// </summary>
        public static readonly FontFamily Prose =
            new("avares://ProjectPrime/Assets/Fonts/Roboto-Bold.ttf#Roboto");

        /// <summary>
        /// Legacy Pixelify grid helper. New Inter text must not use it.
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

        /// <summary>Legacy title asset retained for compatibility; not used by the FPS hub.</summary>
        public static readonly FontFamily Title =
            new("avares://ProjectPrime/Assets/Fonts/heyNovember.ttf#Hey November");

        /// <summary>
        /// The face every self-drawn launcher control uses for player-facing
        /// labels. Centralizing this is what let the Pixelify-to-Inter change
        /// reach server rows, settings controls and transitional deck buttons
        /// without forking those controls.
        /// </summary>
        public static Typeface Face(bool bold) =>
            new(Interface, FontStyle.Normal,
                bold ? FontWeight.SemiBold : FontWeight.Normal);

        /// <summary>
        /// Lay launcher text with the shared Inter face and requested size.
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
                    new Uri("avares://ProjectPrime/Assets/project-prime-mark.png"));
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
