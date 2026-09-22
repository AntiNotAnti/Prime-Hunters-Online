using System;
using System.Buffers.Binary;
using MphRead.Entities;

namespace MphRead.Mods.Network
{
    internal sealed class ReplaySceneServices : ISceneServices
    {
        public ReplayPlaybackSession Session { get; }
        public ReplayReplicaState State { get; }
        public bool IsReplica => true;
        public bool MayEndOnScore => false;
        public bool ShouldLeaveAfterMatch => false;
        public bool SuppressDamage => true;
        public bool AllowsPresentationSideEffects => false;
        public IPlayerReplicationHost PlayerReplication { get; }
        public bool DisablePowerups => State.Configuration?.Match.DisablePowerups == true;
        public Multiplayer.MatchWorldProfile? NetworkWorldProfile => State.Configuration?.WorldProfile
            ?? Multiplayer.MatchWorldProfile.Resolve(State.Match?.PlayerCount ?? 2);
        public bool ReplicatesHealthSpawns => true;
        public bool TryGetHealthSpawn(short id, out HealthSpawnState state) => State.TryGetHealthSpawn(id, out state);
        private readonly ushort[] _generations = new ushort[PlayerEntity.SlotCapacity];
        private uint? _rngTick;
        public ReplaySceneServices(ReplayPlaybackSession session, ReplayReplicaState state)
        { Session = session; State = state; PlayerReplication = new ReplayPlayerReplicationHost(state); }

        public void BeforeSimulation(Scene scene)
        {
            State.Advance(Session.CurrentFrame);
            if (State.Match is not MatchStatePacket match) return;
            if (!string.Equals(scene.Room?.Meta.Name, match.RoomKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The replica scene belongs to a different recorded room.");
            scene.GameState.Mode = (GameMode)match.Mode;
            scene.GameState.PointGoal = match.PointGoal;
            scene.GameState.FriendlyFire = (match.Flags & MatchStatePacket.FlagFriendlyFire) != 0;
            scene.GameState.ShadowFreeze = (match.Flags & MatchStatePacket.FlagNoShadowFreeze) == 0;
            scene.GameState.Teams = scene.GameState.IsTeamMode((GameMode)match.Mode);
            scene.GameState.MatchTime = match.TimeRemaining;
            scene.GameState.MatchState = (match.Flags & MatchStatePacket.FlagEnding) != 0 ? MatchState.Ending : MatchState.InProgress;
            if (_rngTick != State.ServerTick)
            {
                _rngTick = State.ServerTick;
                scene.Random.SetRng1(State.Rng1);
                scene.Random.SetRng2(State.Rng2);
            }
            int active = 0;
            for (int slot = 0; slot < PlayerEntity.SlotCapacity; slot++)
            {
                PlayerEntity player = scene.Players.Items[slot];
                ReplayOccupant occupant = State.Occupant(slot);
                bool changed = _generations[slot] != occupant.Generation;
                if (changed) { _generations[slot] = occupant.Generation; scene.PlayerReplication.ForgetSlot(slot); }
                if (occupant.Generation == 0)
                {
                    player.LoadFlags &= ~(LoadFlags.Active | LoadFlags.Spawned);
                    player.Controls.ClearAll();
                    continue;
                }
                active++;
                scene.GameState.Nicknames[slot] = occupant.Name;
                if (changed || player.Hunter != occupant.Hunter)
                {
                    player.ModPrepareHunterResources(occupant.Hunter);
                    player.ModSetHunter(occupant.Hunter);
                    player.Initialize();
                }
                player.IsBot = false;
                player.TeamIndex = scene.GameState.Teams ? occupant.Team : slot;
                player.Recolor = occupant.Color;
                if (scene.GameState.Teams) Multiplayer.TeamVisuals.Apply(player);
                player.LoadFlags |= LoadFlags.Active | LoadFlags.SlotActive;
                if (State.TryGetPlayer(slot, out var recorded)) scene.PlayerReplication.ApplyState(player, recorded, isLocal: false);
                NetHooks.TryApplyRemoteInput(player, slot);
            }
            scene.Players.PlayerCount = scene.GameState.ActivePlayers = active;
        }

        public void AfterSimulation(Scene scene)
        {
            for (int slot = 0; slot < PlayerEntity.SlotCapacity; slot++)
                if (State.TryGetPlayer(slot, out var recorded))
                    scene.PlayerReplication.ApplyState(scene.Players.Items[slot], recorded, isLocal: false);
            ReadOnlySpan<byte> tail = State.WorldTail;
            if (tail.Length >= NetMatchTimeSync.Size)
                for (int i = 0; i < PlayerEntity.SlotCapacity; i++)
                {
                    scene.GameState.Time[i] = BinaryPrimitives.ReadSingleLittleEndian(tail[(i * 8)..]);
                    scene.GameState.TeamTime[i] = BinaryPrimitives.ReadSingleLittleEndian(tail[(i * 8 + 4)..]);
                }
        }
    }
}
