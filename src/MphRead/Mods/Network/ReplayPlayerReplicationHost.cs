using System;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    /// <summary>A decoder policy with no connection, authority or live prediction state.</summary>
    internal sealed class ReplayPlayerReplicationHost : IPlayerReplicationHost
    {
        private readonly ReplayReplicaState _state;
        private bool _applyingSpawn;
        public ReplayPlayerReplicationHost(ReplayReplicaState state) => _state = state;
        public bool IsReplica => true;
        public bool Active => true;
        public bool IsAuthority => false;
        public bool IsHost => false;
        public int LocalSlot => -1;
        public uint Frame => _state.RecordingFrame;
        public bool Settling => false;
        public bool GameplayReady => true;
        public bool CanSpawn => _applyingSpawn;
        public int Ping(int slot) => 0;
        public bool Matches(int slot, ushort generation, ushort life) => _state.MatchesLife(slot, generation, life);
        public bool TryGetIntent(int slot, out IntentPacket intent) => _state.TryGetIntent(slot, out intent);
        public bool TryGetState(int slot, out PlayerState state) => _state.TryGetPlayer(slot, out state);
        public uint IntentAge(int slot) => _state.IntentAge(slot);
        public void OnSpawn(PlayerEntity player) => player.OwningScene.ContinuousPhase.ResetSlot(player.SlotIndex);
        public void BeginLife(PlayerEntity player, in PlayerState state) { }
        public void Spawn(PlayerEntity player, in PlayerState state)
        {
            bool previous = _applyingSpawn;
            _applyingSpawn = true;
            try { player.ModNetSpawn(state.Position, state.Facing); }
            finally { _applyingSpawn = previous; }
        }
        // Accepted state controls health; reconstruction never runs damage resolution.
        // Transient damage presentation is supplied separately by the replay event stream.
        public void ReplayDamage(PlayerEntity player, in PlayerState state) { }
        public void ReplayDeath(PlayerEntity player) => player.ModAcceptReplicaDeath();
        public void NoteDeath(int slot) { }
        public int HealthFor(PlayerEntity player, int health, bool local) => health;
        public bool SamplePosition(int slot, bool presentation, out Vector3 position, out bool alt)
        {
            // Recorded frames never use a live packet-arrival playout clock.
            position = default; alt = false; return false;
        }
        public void StampAcknowledgement(ref IntentPacket intent)
            => throw new InvalidOperationException("A replay replica cannot acknowledge live gameplay.");
    }
}
