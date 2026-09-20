#if MPHREAD_AVALONIA
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Palette for the FPS hub.
    ///
    /// Kept separate from <see cref="GuiTheme"/> on purpose: the existing
    /// deck controls still serve the dense game/settings screens while the
    /// shell moves to a flatter tactical language. Once the child screens
    /// migrate, this can become the single product palette.
    /// </summary>
    internal static class HubTheme
    {
        public static readonly Color Ink = Color.FromRgb(0x07, 0x0d, 0x14);
        public static readonly Color Panel = Color.FromRgb(0x0d, 0x17, 0x22);
        public static readonly Color PanelHot = Color.FromRgb(0x12, 0x22, 0x31);
        public static readonly Color Edge = Color.FromRgb(0x2d, 0x43, 0x57);
        public static readonly Color Accent = Color.FromRgb(0x62, 0xd9, 0xff);
        public static readonly Color AccentSoft = Color.FromRgb(0x9b, 0xe8, 0xff);
        public static readonly Color Text = Color.FromRgb(0xed, 0xf7, 0xff);
        public static readonly Color TextDim = Color.FromRgb(0x8a, 0xa0, 0xb3);
        public static readonly Color Good = Color.FromRgb(0x75, 0xd6, 0x9d);
        public static readonly Color Warm = Color.FromRgb(0xf0, 0xae, 0x55);
        public static readonly Color Danger = Color.FromRgb(0xdb, 0x6b, 0x74);

        public static readonly IBrush InkBrush =
            new SolidColorBrush(Color.FromArgb(0xe8, Ink.R, Ink.G, Ink.B));
        public static readonly IBrush PanelBrush =
            new SolidColorBrush(Color.FromArgb(0xd8, Panel.R, Panel.G, Panel.B));
        public static readonly IBrush PanelStrongBrush =
            new SolidColorBrush(Color.FromArgb(0xee, Panel.R, Panel.G, Panel.B));
        public static readonly IBrush PanelHotBrush =
            new SolidColorBrush(Color.FromArgb(0xee, PanelHot.R, PanelHot.G, PanelHot.B));
        public static readonly IBrush EdgeBrush = new SolidColorBrush(Edge);
        public static readonly IBrush AccentBrush = new SolidColorBrush(Accent);
        public static readonly IBrush AccentSoftBrush = new SolidColorBrush(AccentSoft);
        public static readonly IBrush TextBrush = new SolidColorBrush(Text);
        public static readonly IBrush TextDimBrush = new SolidColorBrush(TextDim);
        public static readonly IBrush GoodBrush = new SolidColorBrush(Good);
        public static readonly IBrush WarmBrush = new SolidColorBrush(Warm);
        public static readonly IBrush DangerBrush = new SolidColorBrush(Danger);
    }
}
#endif
