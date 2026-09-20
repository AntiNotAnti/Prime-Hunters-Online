using System;

namespace MphRead.Mods.Launcher
{
    /// <summary>
    /// Stable destinations exposed by the product shell.
    ///
    /// UI frameworks may render these however they like; StartScreen is the
    /// adapter that turns a destination into today's concrete screen.
    /// </summary>
    public enum HubDestination
    {
        Play,
        Servers,
        Custom,
        Clips,
        Settings,
        Support,
        Quit
    }

    /// <summary>
    /// Read-only launcher state the home hub needs to present.
    ///
    /// No Avalonia/OpenTK types live here, so another renderer can consume the
    /// same snapshot without learning where preferences or setup state live.
    /// </summary>
    public readonly record struct HubSnapshot(
        string PlayerName,
        Hunter PreferredHunter,
        Hunter DisplayHunter,
        int Suit,
        bool GameFilesReady,
        string Platform);

    public static class HubState
    {
        public static HubSnapshot Capture()
        {
            string player = LauncherPrefs.PlayerName.Trim();
            Hunter preferred = LauncherPrefs.LastHunter;
            Hunter display = Hunters.Resolve(preferred);
            return new HubSnapshot(
                PlayerName: player.Length == 0 ? "Player" : player,
                PreferredHunter: preferred,
                DisplayHunter: display,
                Suit: Math.Clamp(LauncherPrefs.LastColor, 0, 3),
                GameFilesReady: GameFiles.Ready,
                Platform: OperatingSystem.IsAndroid() ? "Android" : "Desktop");
        }
    }
}
