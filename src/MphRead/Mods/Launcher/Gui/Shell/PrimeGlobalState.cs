#if MPHREAD_AVALONIA
using MphRead.Mods.Network;
using MphRead.Mods.Update;
namespace MphRead.Mods.Launcher.Gui
{
    internal readonly record struct PrimeGlobalState(string PlayerName, string Hunter, string BuildVersion,
        bool LobbyActive, int LobbyPlayerCount, bool GameFilesReady, bool UpdateAvailable)
    {
        public static PrimeGlobalState Read() => new(
            string.IsNullOrWhiteSpace(LauncherPrefs.PlayerName) ? "PLAYER" : LauncherPrefs.PlayerName,
            LauncherPrefs.LastHunter.ToString(), BuildVersionText(),
            NetSession.Active && NetSession.PersistentLobby,
            NetSession.Active && NetSession.PersistentLobby ? NetSession.LobbyRoster().Count : 0,
            GameFiles.Ready, Updater.Available != null);
        private static string BuildVersionText() => Update.BuildVersion.Current?.ToString(3) ?? "LOCAL";
    }
}
#endif
