// Foreground compatibility facade. Scene simulation reads Scene.GameState directly.
using System;
using MphRead.Entities;
using MphRead.Formats;
namespace MphRead
{
    public static class GameState
    {
        internal static SceneGameState Current { get; set; } = new SceneGameState(PlayerEntity.LegacyRegistry);
        public const float MatchEndingSeconds = SceneGameState.MatchEndingSeconds;
        public static GameMode Mode { get => Current.Mode; set => Current.Mode = value; }
        public static bool SinglePlayer { get => Current.SinglePlayer; }
        public static bool Multiplayer { get => Current.Multiplayer; }
        public static bool IsOctolithMode { get => Current.IsOctolithMode; }
        public static bool PausePrevented { get => Current.PausePrevented; set => Current.PausePrevented = value; }
        public static bool MenuPause { get => Current.MenuPause; }
        public static bool DialogPause { get => Current.DialogPause; }
        public static MatchState MatchState { get => Current.MatchState; set => Current.MatchState = value; }
        public static TransitionState TransitionState { get => Current.TransitionState; set => Current.TransitionState = value; }
        public static bool InRoomTransition { get => Current.InRoomTransition; }
        public static EscapeState EscapeState { get => Current.EscapeState; set => Current.EscapeState = value; }
        public static float EscapeTimer { get => Current.EscapeTimer; set => Current.EscapeTimer = value; }
        public static bool EscapePaused { get => Current.EscapePaused; set => Current.EscapePaused = value; }
        public static int[] EncounterState { get => Current.EncounterState; }
        public static bool[] CompletedRandomEncounterRooms { get => Current.CompletedRandomEncounterRooms; }
        public static int TransitionRoomId { get => Current.TransitionRoomId; set => Current.TransitionRoomId = value; }
        public static bool TransitionAltForm { get => Current.TransitionAltForm; set => Current.TransitionAltForm = value; }
        public static int ActivePlayers { get => Current.ActivePlayers; set => Current.ActivePlayers = value; }
        public static string[] Nicknames { get => Current.Nicknames; }
        public static int[] Stars { get => Current.Stars; }
        public static int[] Standings { get => Current.Standings; }
        public static int[] TeamStandings { get => Current.TeamStandings; }
        public static int[] ResultSlots { get => Current.ResultSlots; }
        public static int PrimeHunter { get => Current.PrimeHunter; set => Current.PrimeHunter = value; }
        public static bool Teams { get => Current.Teams; set => Current.Teams = value; }
        public static int TeamCount { get => Current.TeamCount; set => Current.TeamCount = value; }
        public static bool FriendlyFire { get => Current.FriendlyFire; set => Current.FriendlyFire = value; }
        public static int PointGoal { get => Current.PointGoal; set => Current.PointGoal = value; }
        public static float TimeGoal { get => Current.TimeGoal; set => Current.TimeGoal = value; }
        public static int DamageLevel { get => Current.DamageLevel; set => Current.DamageLevel = value; }
        public static bool OctolithReset { get => Current.OctolithReset; set => Current.OctolithReset = value; }
        public static bool RadarPlayers { get => Current.RadarPlayers; set => Current.RadarPlayers = value; }
        public static bool AffinityWeapons { get => Current.AffinityWeapons; set => Current.AffinityWeapons = value; }
        public static bool ShadowFreeze { get => Current.ShadowFreeze; set => Current.ShadowFreeze = value; }
        public static float MatchTime { get => Current.MatchTime; set => Current.MatchTime = value; }
        public static bool ForceEndGame { get => Current.ForceEndGame; set => Current.ForceEndGame = value; }
        public static int[] Points { get => Current.Points; }
        public static int[] TeamPoints { get => Current.TeamPoints; }
        public static int[] Kills { get => Current.Kills; }
        public static int[] TeamKills { get => Current.TeamKills; }
        public static int[] Deaths { get => Current.Deaths; }
        public static int[] TeamDeaths { get => Current.TeamDeaths; }
        public static float[] Time { get => Current.Time; }
        public static float[] TeamTime { get => Current.TeamTime; }
        public static int[] BeamDamageMax { get => Current.BeamDamageMax; }
        public static int[] BeamDamageDealt { get => Current.BeamDamageDealt; }
        public static int[] DamageCount { get => Current.DamageCount; }
        public static int[] AltDamageCount { get => Current.AltDamageCount; }
        public static int[] KillStreak { get => Current.KillStreak; }
        public static int[] Suicides { get => Current.Suicides; }
        public static int[] FriendlyKills { get => Current.FriendlyKills; }
        public static int[] HeadshotKills { get => Current.HeadshotKills; }
        public static int[,] BeamKills { get => Current.BeamKills; }
        public static int[] OctolithScores { get => Current.OctolithScores; }
        public static int[] OctolithDrops { get => Current.OctolithDrops; }
        public static int[] OctolithStops { get => Current.OctolithStops; }
        public static int[] NodesCaptured { get => Current.NodesCaptured; }
        public static int[] NodesLost { get => Current.NodesLost; }
        public static int[] KillsAsPrime { get => Current.KillsAsPrime; }
        public static int[] PrimesKilled { get => Current.PrimesKilled; }
        public static Action<Scene> ModeState { get => Current.ModeState; }
        public static void PauseMenu() => Current.PauseMenu();
        public static void UnpauseMenu() => Current.UnpauseMenu();
        public static void PauseDialog() => Current.PauseDialog();
        public static void UnpauseDialog() => Current.UnpauseDialog();
        public static void ApplyPause() => Current.ApplyPause();
        public static bool IsTeamMode(GameMode mode) => Current.IsTeamMode(mode);
        public static void Setup(Scene scene) => Current.Setup(scene);
        public static void ResetMatchProgress() => Current.ResetMatchProgress();
        public static void UpdateTime(Scene scene) => Current.UpdateTime(scene);
        public static void ProcessFrame(Scene scene) => Current.ProcessFrame(scene);
        public static AreaState GetAreaState(int areaId, StorySave? save = null) => Current.GetAreaState(areaId, save);
        public static bool QueuedOublietteUnlockMessage { get => Current.QueuedOublietteUnlockMessage; set => Current.QueuedOublietteUnlockMessage = value; }
        public static void ModeStateAdventure(Scene scene) => Current.ModeStateAdventure(scene);
        public static void ModeStateBattle(Scene scene) => Current.ModeStateBattle(scene);
        public static void ModeStateSurvival(Scene scene) => Current.ModeStateSurvival(scene);
        internal static void UpdateSurvival(float frameTime) => Current.UpdateSurvival(frameTime);
        public static void ModeStateCapture(Scene scene) => Current.ModeStateCapture(scene);
        public static void ModeStateBounty(Scene scene) => Current.ModeStateBounty(scene);
        public static void ModeStateDefender(Scene scene) => Current.ModeStateDefender(scene);
        public static void ModeStateNodes(Scene scene) => Current.ModeStateNodes(scene);
        public static void ModeStatePrimeHunter(Scene scene) => Current.ModeStatePrimeHunter(scene);
        public static int QueuedOctolithMessageId { get => Current.QueuedOctolithMessageId; set => Current.QueuedOctolithMessageId = value; }
        public static void UpdateFrame(Scene scene) => Current.UpdateFrame(scene);
        public static void UpdateBossFlags(int areaId) => Current.UpdateBossFlags(areaId);
        public static void ResetEscapeState(bool updateSounds) => Current.ResetEscapeState(updateSounds);
        public static void UpdateState() => Current.UpdateState();
        public static void CompleteRandomEncounter(int roomId) => Current.CompleteRandomEncounter(roomId);
        public static StorySave StorySave { get => Current.StorySave; }
        public static void UpdateCleanSave(bool force) => Current.UpdateCleanSave(force);
        public static void RestoreCleanSave() => Current.RestoreCleanSave();
        public static void LoadSave() => Current.LoadSave();
        public static void StartNewSave() => Current.StartNewSave();
        public static StorySave ReadSave() => Current.ReadSave();
        public static bool SaveExists(byte slot) => Current.SaveExists(slot);
        public static StorySave? PeekSave(byte slot) => Current.PeekSave(slot);
        public static void CommitSave() => Current.CommitSave();
        public static MenuSettings LoadSettings() => Current.LoadSettings();
        public static void CommitSettings(MenuSettings menuSettings) => Current.CommitSettings(menuSettings);
        public static void Reset() => Current.Reset();
        public static bool IsResultTie { get => Current.IsResultTie; }
        internal static void UpdateStandings() => Current.UpdateStandings();
    }
}
