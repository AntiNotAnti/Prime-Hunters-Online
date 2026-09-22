using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    /// <summary>Connection/lifecycle policy used by one scene's player decoder.</summary>
    public interface IPlayerReplicationHost
    {
        bool IsReplica { get; }
        bool Active { get; }
        bool IsAuthority { get; }
        bool IsHost { get; }
        int LocalSlot { get; }
        uint Frame { get; }
        bool Settling { get; }
        bool GameplayReady { get; }
        bool CanSpawn { get; }
        int Ping(int slot);
        bool Matches(int slot, ushort generation, ushort life);
        bool TryGetIntent(int slot, out IntentPacket intent);
        bool TryGetState(int slot, out PlayerState state);
        uint IntentAge(int slot);
        void OnSpawn(PlayerEntity player);
        void BeginLife(PlayerEntity player, in PlayerState state);
        void Spawn(PlayerEntity player, in PlayerState state);
        void ReplayDamage(PlayerEntity player, in PlayerState state);
        void ReplayDeath(PlayerEntity player);
        void NoteDeath(int slot);
        int HealthFor(PlayerEntity player, int health, bool local);
        bool SamplePosition(int slot, bool presentation, out Vector3 position, out bool alt);
        void StampAcknowledgement(ref IntentPacket intent);
    }

    internal sealed class LivePlayerReplicationHost : IPlayerReplicationHost
    {
        internal static LivePlayerReplicationHost Instance { get; } = new();
        public bool IsReplica => false;
        public bool Active => NetSession.Active;
        public bool IsAuthority => NetSession.IsAuthority;
        public bool IsHost => NetSession.IsHost;
        public int LocalSlot => NetHooks.LocalSlot;
        public uint Frame => NetSession.NetFrame;
        public bool Settling => NetRoomChange.Settling;
        public bool GameplayReady => NetRoomChange.GameplayReady;
        public bool CanSpawn => NetPlayerLifecycle.CanSpawn;
        public int Ping(int slot) => (uint)slot < (uint)NetSession.SlotPing.Length ? NetSession.SlotPing[slot] : 0;
        public bool Matches(int slot, ushort generation, ushort life) => NetPlayerLifecycle.Matches(slot, generation, life);
        public bool TryGetIntent(int slot, out IntentPacket intent)
        { intent = NetSession.RemoteIntents[slot]; return NetSession.RemoteIntentValid[slot]; }
        public bool TryGetState(int slot, out PlayerState state)
        { state = NetSession.RemoteStates[slot]; return NetSession.RemoteStateValid[slot]; }
        public uint IntentAge(int slot) => NetSession.RemoteIntentAge(slot);
        public void OnSpawn(PlayerEntity player)
        {
            NetPlayerLifecycle.OnSpawn(player);
            NetSession.ContinuousPhase.ResetSlot(player.SlotIndex);
        }
        public void BeginLife(PlayerEntity player, in PlayerState state)
        {
            int slot = player.SlotIndex;
            NetHitPrediction.NoteRespawn(slot);
            NetDamage.BeginLife(slot, state);
            NetHitClaims.ForgetSlot(slot);
            NetUnlagged.ResetSlot(slot);
        }
        public void Spawn(PlayerEntity player, in PlayerState state)
        {
            bool previous = NetPlayerLifecycle.ApplyingSpawn;
            NetPlayerLifecycle.ApplyingSpawn = true;
            try { player.ModNetSpawn(state.Position, state.Facing); }
            finally { NetPlayerLifecycle.ApplyingSpawn = previous; }
        }
        public void ReplayDamage(PlayerEntity player, in PlayerState state) => NetDamage.Replay(player, state);
        public void ReplayDeath(PlayerEntity player) => NetDamage.ReplayDeath(player);
        public void NoteDeath(int slot) => NetHitPrediction.NoteDeath(slot);
        public int HealthFor(PlayerEntity player, int health, bool local) => local
            ? NetHitPrediction.LocalHealthFor(player, health) : NetHitPrediction.HealthFor(player.SlotIndex, health);
        public bool SamplePosition(int slot, bool presentation, out Vector3 position, out bool alt) => presentation
            ? NetSmoothing.SamplePresentation(slot, out position, out alt) : NetSmoothing.Sample(slot, out position, out alt);
        public void StampAcknowledgement(ref IntentPacket intent)
        {
            intent.AckFrame = NetHooks.SnapshotOwnsPuppets && NetSession.AppliedSnapshotFrame != 0
                ? NetSession.AppliedSnapshotFrame : NetSession.LastSnapshotFrame;
            if (NetSmoothing.AckPoint(out uint frame, out byte sub))
            { intent.AckFrame = frame; intent.AckSubFrame = sub; }
        }
    }
}
