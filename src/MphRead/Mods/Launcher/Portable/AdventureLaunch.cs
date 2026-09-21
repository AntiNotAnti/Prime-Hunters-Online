namespace MphRead.Mods.Launcher
{
    /// <summary>
    /// The one place launcher presentations turn an Adventure choice into a
    /// launch plan. Loading/new-game state still belongs to AdventureSave and
    /// MatchStart; this only persists the player's menu choice.
    /// </summary>
    public static class AdventureLaunch
    {
        public static LaunchPlan Create(byte slot, bool newGame, Hunter hunter)
        {
            LauncherPrefs.LastHunter = hunter;
            LauncherPrefs.LastKind = (int)LaunchKind.Adventure;
            LauncherPrefs.Save();
            return new LaunchPlan
            {
                Kind = LaunchKind.Adventure,
                Hunter = hunter,
                PlayerName = LauncherPrefs.PlayerName,
                RoomKey = "",
                SaveSlot = slot,
                NewGame = newGame
            };
        }
    }
}
