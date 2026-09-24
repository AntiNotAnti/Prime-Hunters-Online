#if MPHREAD_AVALONIA
using Avalonia.Media;
namespace MphRead.Mods.Launcher.Gui
{
    // One palette for the shell, workspaces and existing gameplay controls.
    internal static class PrimeTheme
    {
        public static readonly Color Background = Color.Parse("#080D18");
        public static readonly IBrush BackgroundBrush = new SolidColorBrush(Background);
        public static readonly Color BackgroundDeep = Color.Parse("#050913");
        public static readonly IBrush BackgroundDeepBrush = new SolidColorBrush(BackgroundDeep);
        public static readonly Color Panel = Color.Parse("#111B2C");
        public static readonly IBrush PanelBrush = new SolidColorBrush(Panel);
        public static readonly Color PanelRaised = Color.Parse("#17243A");
        public static readonly IBrush PanelRaisedBrush = new SolidColorBrush(PanelRaised);
        public static readonly Color PanelHighlight = Color.Parse("#1C2E49");
        public static readonly IBrush PanelHighlightBrush = new SolidColorBrush(PanelHighlight);
        public static readonly Color Border = Color.Parse("#263C5D");
        public static readonly IBrush BorderBrush = new SolidColorBrush(Border);
        public static readonly Color BorderBright = Color.Parse("#345883");
        public static readonly IBrush BorderBrightBrush = new SolidColorBrush(BorderBright);
        public static readonly Color Glow = Color.Parse("#2B9EF7");
        public static readonly IBrush GlowBrush = new SolidColorBrush(Glow);
        public static readonly Color Highlight = Color.Parse("#61B6F6");
        public static readonly IBrush HighlightBrush = new SolidColorBrush(Highlight);
        public static readonly Color Primary = Color.Parse("#1C72E1");
        public static readonly IBrush PrimaryBrush = new SolidColorBrush(Primary);
        public static readonly Color AccentDeep = Color.Parse("#175AB1");
        public static readonly IBrush AccentDeepBrush = new SolidColorBrush(AccentDeep);
        public static readonly Color Green = Color.Parse("#55F3A1");
        public static readonly IBrush GreenBrush = new SolidColorBrush(Green);
        public static readonly Color GreenDim = Color.Parse("#1E805D");
        public static readonly IBrush GreenDimBrush = new SolidColorBrush(GreenDim);
        public static readonly Color Text = Color.Parse("#E2EBFC");
        public static readonly IBrush TextBrush = new SolidColorBrush(Text);
        public static readonly Color TextSecondary = Color.Parse("#9DAFCF");
        public static readonly IBrush TextSecondaryBrush = new SolidColorBrush(TextSecondary);
        public static readonly Color TextMuted = Color.Parse("#647898");
        public static readonly IBrush TextMutedBrush = new SolidColorBrush(TextMuted);
        public static readonly Color Warning = Color.Parse("#F0C05E");
        public static readonly IBrush WarningBrush = new SolidColorBrush(Warning);
        public static readonly Color Danger = Color.Parse("#E96868");
        public static readonly IBrush DangerBrush = new SolidColorBrush(Danger);
        public static readonly Color DangerPanel = Color.Parse("#481B21");
        public static readonly IBrush DangerPanelBrush = new SolidColorBrush(DangerPanel);
    }
}
#endif
