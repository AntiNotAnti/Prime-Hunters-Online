#if MPHREAD_AVALONIA
using Avalonia;
using Avalonia.Media;
namespace MphRead.Mods.Launcher.Gui
{
    internal enum PrimeDensity { Compact, Standard, Spacious }
    internal static class PrimeMetrics
    {
        public const double PanelGap = 12, SectionGap = 20, PanelPadding = 16;
        public const double ControlHeight = 44, CompactControlHeight = 36;
        public const double HeaderHeight = 80, FooterHeight = 40, TabHeight = 44;
        public static readonly Thickness PageMargin = new(24, 16);
        public static PrimeDensity Density(Size size) => size.Width >= 1600 && size.Height >= 900
            ? PrimeDensity.Spacious : size.Width >= 1280 && size.Height >= 720
                ? PrimeDensity.Standard : PrimeDensity.Compact;
    }
    internal static class PrimeTypography
    {
        public static readonly FontFamily Ui = new("fonts:Inter#Inter");
        public static readonly FontFamily Display = new("avares://ProjectPrime/Assets/Fonts/Rajdhani-Bold.ttf#Rajdhani");
        public static readonly FontFamily Label = new("avares://ProjectPrime/Assets/Fonts/Rajdhani-SemiBold.ttf#Rajdhani");
        public static readonly FontFamily Data = Deck.Mono;
        public const double DisplayLarge = 34, DisplayMedium = 30;
        public const double HeadingLarge = 26, HeadingMedium = 20, HeadingSmall = 17;
        public const double Body = 14, BodySmall = 12, DataSize = 12, DataSmall = 11, Micro = 10;
    }
    internal static class PrimeMotion
    {
        public static void Enter(Avalonia.Controls.Control view) => HubMotion.Enter(view, 5, 7);
    }
}
#endif
