#if MPHREAD_AVALONIA
using Avalonia.Media;
namespace MphRead.Mods.Launcher.Gui
{
    // One palette for the shell, workspaces and existing gameplay controls.
    internal static class PrimeTheme
    {
        public static readonly Color Background = Color.Parse("#071018");
        public static readonly IBrush BackgroundBrush = new SolidColorBrush(Background);
        public static readonly Color BackgroundDeep = Color.Parse("#040B12");
        public static readonly IBrush BackgroundDeepBrush = new SolidColorBrush(BackgroundDeep);
        public static readonly Color Panel = Color.Parse("#101A23");
        public static readonly IBrush PanelBrush = new SolidColorBrush(Panel);
        public static readonly Color PanelRaised = Color.Parse("#16212A");
        public static readonly IBrush PanelRaisedBrush = new SolidColorBrush(PanelRaised);
        public static readonly Color PanelHighlight = Color.Parse("#1B2933");
        public static readonly IBrush PanelHighlightBrush = new SolidColorBrush(PanelHighlight);
        public static readonly Color Border = Color.Parse("#233C49");
        public static readonly IBrush BorderBrush = new SolidColorBrush(Border);
        public static readonly Color BorderBright = Color.Parse("#35616E");
        public static readonly IBrush BorderBrightBrush = new SolidColorBrush(BorderBright);
        public static readonly Color Cyan = Color.Parse("#59E7EF");
        public static readonly IBrush CyanBrush = new SolidColorBrush(Cyan);
        public static readonly Color CyanStrong = Color.Parse("#19E9F3");
        public static readonly IBrush CyanStrongBrush = new SolidColorBrush(CyanStrong);
        public static readonly Color CyanDim = Color.Parse("#2B7D88");
        public static readonly IBrush CyanDimBrush = new SolidColorBrush(CyanDim);
        public static readonly Color Green = Color.Parse("#55F3A1");
        public static readonly IBrush GreenBrush = new SolidColorBrush(Green);
        public static readonly Color GreenDim = Color.Parse("#1E805D");
        public static readonly IBrush GreenDimBrush = new SolidColorBrush(GreenDim);
        public static readonly Color Text = Color.Parse("#D3E6E8");
        public static readonly IBrush TextBrush = new SolidColorBrush(Text);
        public static readonly Color TextSecondary = Color.Parse("#9CB0B6");
        public static readonly IBrush TextSecondaryBrush = new SolidColorBrush(TextSecondary);
        public static readonly Color TextMuted = Color.Parse("#60737B");
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
