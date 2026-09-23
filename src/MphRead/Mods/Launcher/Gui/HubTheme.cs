#if MPHREAD_AVALONIA
using Avalonia;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>Compatibility names for controls sharing the Prime palette.</summary>
    internal static class HubTheme
    {
        public static readonly Color Ink = PrimeTheme.Background;
        public static readonly Color Panel = PrimeTheme.Panel;
        public static readonly Color PanelHot = PrimeTheme.PanelHighlight;
        public static readonly Color Edge = PrimeTheme.Border;
        public static readonly Color Accent = PrimeTheme.Primary;
        public static readonly Color AccentSoft = PrimeTheme.Highlight;
        public static readonly Color Text = PrimeTheme.Text;
        public static readonly Color TextDim = PrimeTheme.TextSecondary;
        public static readonly Color Good = PrimeTheme.Green;
        public static readonly Color Warm = PrimeTheme.Warning;
        public static readonly Color Danger = PrimeTheme.Danger;

        public static readonly IBrush InkBrush =
            new SolidColorBrush(Color.FromArgb(0xe8, Ink.R, Ink.G, Ink.B));

        // A slight directional tint gives the glass panels depth without adding
        // another animated layer to the CPU-rasterised launcher.
        public static readonly IBrush PanelBrush = PanelGradient(
            Color.FromArgb(0xd8, PanelHot.R, PanelHot.G, PanelHot.B),
            Color.FromArgb(0xe2, Panel.R, Panel.G, Panel.B));
        public static readonly IBrush PanelStrongBrush = PanelGradient(
            Color.FromArgb(0xf2, 0x12, 0x22, 0x31),
            Color.FromArgb(0xf2, Panel.R, Panel.G, Panel.B));
        public static readonly IBrush PanelHotBrush = PanelGradient(
            Color.FromArgb(0xf4, 0x18, 0x31, 0x43),
            Color.FromArgb(0xf2, PanelHot.R, PanelHot.G, PanelHot.B));
        public static readonly IBrush EdgeBrush = new SolidColorBrush(Edge);
        public static readonly IBrush AccentBrush = new SolidColorBrush(Accent);
        public static readonly IBrush AccentSoftBrush = new SolidColorBrush(AccentSoft);
        public static readonly IBrush TextBrush = new SolidColorBrush(Text);
        public static readonly IBrush TextDimBrush = new SolidColorBrush(TextDim);
        public static readonly IBrush GoodBrush = new SolidColorBrush(Good);
        public static readonly IBrush WarmBrush = new SolidColorBrush(Warm);
        public static readonly IBrush DangerBrush = new SolidColorBrush(Danger);

        /// <summary>
        /// Inter is already shipped and registered by both app heads through
        /// Avalonia.Fonts.Inter/WithInterFont(). It is the hub's primary face:
        /// neutral enough for dense settings and server rows, but much cleaner
        /// than the old pixel display face at menu sizes.
        /// </summary>
        public static readonly FontFamily Ui =
            new("fonts:Inter#Inter");

        /// <summary>
        /// Technical data keeps a mono voice without turning every label into
        /// terminal text. Ping, build, platform and packet-ish metadata use it.
        /// </summary>
        public static readonly FontFamily Data = Deck.Mono;

        public static IBrush AccentPanel(Color accent, byte alpha = 42)
        {
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(
                        Color.FromArgb(alpha, accent.R, accent.G, accent.B), 0),
                    new GradientStop(
                        Color.FromArgb(0xe8, Panel.R, Panel.G, Panel.B), 0.46),
                    new GradientStop(
                        Color.FromArgb(0xf0, Ink.R, Ink.G, Ink.B), 1)
                }
            };
        }

        private static IBrush PanelGradient(Color from, Color to)
        {
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(from, 0),
                    new GradientStop(to, 1)
                }
            };
        }

        public static readonly FontFamily DataBold = Deck.MonoBold;
    }
}
#endif
