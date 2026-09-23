// Foreground compatibility facade; entity code uses its owning scene's bridge.
using MphRead.Entities;
using OpenTK.Mathematics;
namespace MphRead.Mods.Network
{
    public static class NetPlayerBridge
    {
        private static readonly PlayerReplicationBridge Fallback = new(LivePlayerReplicationHost.Instance);
        internal static PlayerReplicationBridge Current => GameState.Current.Owner?.PlayerReplication ?? Fallback;
        public static int PlacementsRefused { get => Current.PlacementsRefused; set => Current.PlacementsRefused = value; }
        public static int SpawnFacingsTurned { get => Current.SpawnFacingsTurned; set => Current.SpawnFacingsTurned = value; }
        public static float WorstSpawnFacing { get => Current.WorstSpawnFacing; set => Current.WorstSpawnFacing = value; }
        public static int StaleDeathsIgnored { get => Current.StaleDeathsIgnored; set => Current.StaleDeathsIgnored = value; }
        public static string FormSaidByAuthority() => Current.FormSaidByAuthority();
        public static long RejectedUpdates { get => Current.RejectedUpdates; }
        public static long Snaps { get => Current.Snaps; }
        public static float WorstSnap { get => Current.WorstSnap; }
        public static long NodeLookupsUnresolved { get => Current.NodeLookupsUnresolved; set => Current.NodeLookupsUnresolved = value; }
        public static Vector3 InFormFor(PlayerEntity player, Vector3 position, bool measuredInAlt) => Current.InFormFor(player, position, measuredInAlt);
        public static void RecordPresses(PlayerEntity player) => Current.RecordPresses(player);
        public static IntentPacket CaptureIntent(PlayerEntity player) => Current.CaptureIntent(player);
        public static bool RespawnRequested(int slot) => Current.RespawnRequested(slot);
        public static int[] ShootPressAge { get => Current.ShootPressAge; }
        public static void ApplyIntent(PlayerEntity player, in IntentPacket intent) => Current.ApplyIntent(player, in intent);
        public static void NoteSpawn(int slot) => Current.NoteSpawn(slot);
        public static uint[] SpawnFrame { get => Current.SpawnFrame; }
        public static bool AimTrusted(int slot) => Current.AimTrusted(slot);
        public static void ApplyState(PlayerEntity player, in PlayerState state, bool isLocal) => Current.ApplyState(player, in state, isLocal);
        internal static FormCorrection ReconcileForm(int slot, uint frame, bool desiredAlt,
            bool actualAlt, bool morphing, bool unmorphing, int ping) => Current.ReconcileForm(slot, frame, desiredAlt, actualAlt, morphing, unmorphing, ping);
        public static void NoteRoomChanged() => Current.NoteRoomChanged();
        public static void Reset() => Current.Reset();
        public static void ForgetSlot(int slot) => Current.ForgetSlot(slot);
        public static void ApplyReportedPosition(PlayerEntity player, in IntentPacket intent) => Current.ApplyReportedPosition(player, in intent);
        public static void RestoreSnapshotPresentationPosition(PlayerEntity player,
            in PlayerState state) => Current.RestoreSnapshotPresentationPosition(player, in state);
        public static void RestoreSnapshotPosition(PlayerEntity player, in PlayerState state) => Current.RestoreSnapshotPosition(player, in state);
        public static void RestoreReportedPosition(PlayerEntity player, in IntentPacket intent) => Current.RestoreReportedPosition(player, in intent);
    }
}
