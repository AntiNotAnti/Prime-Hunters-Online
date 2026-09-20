using System;
using MphRead.Entities;

namespace MphRead.Mods.Network
{
    /// <summary>Owns slot/life identity and the resets required at its boundaries.</summary>
    public static class NetPlayerLifecycle
    {
        private static readonly NetLifecycleTracker[] _slots = CreateSlots();
        public static long StaleLifeStates, WrongGeneration, InvalidResurrections;
        public static long Transitions, Spawns, Deaths, OldLifeIntents, OldLifeClaims, OldLifeDamage;
        public static long CrossMatch, CrossAuthority;
        internal static bool ApplyingSpawn { get; set; }
        public static bool CanSpawn => !NetSession.Active || (NetRoomChange.GameplayReady
            && (NetSession.IsHost || NetSession.IsAuthority || ApplyingSpawn));

        private static NetLifecycleTracker[] CreateSlots()
        {
            var slots = new NetLifecycleTracker[PlayerEntity.SlotCapacity];
            for (int i = 0; i < slots.Length; i++) slots[i] = new NetLifecycleTracker();
            return slots;
        }

        public static ushort Get(int slot) => slot >= 0 && slot < _slots.Length ? _slots[slot].LifeId : (ushort)0;
        public static ushort Generation(int slot) => slot >= 0 && slot < _slots.Length ? _slots[slot].Generation : (ushort)0;
        public static bool Matches(int slot, ushort generation, ushort life) => generation != 0
            && Generation(slot) == generation && Get(slot) == life;

        public static void StampProjectile(BeamProjectileEntity beam, BeamProjectileEntity? parent = null)
        {
            PlayerEntity? owner = beam.Owner as PlayerEntity ?? (beam.Owner as HalfturretEntity)?.Owner;
            if (parent != null && NetSession.Active && CurrentProjectile(parent)
                && owner?.SlotIndex == parent.ModLaunchKey.ShooterSlot)
            {
                // A ricochet is the original shot, even if its shooter respawned.
                beam.ModLaunchKey = parent.ModLaunchKey;
                beam.ModLaunchMatch = parent.ModLaunchMatch;
                beam.ModLaunchAuthority = parent.ModLaunchAuthority;
                beam.ModLaunchGeneration = parent.ModLaunchGeneration;
                beam.ModLaunchLife = parent.ModLaunchLife;
                beam.ModLaunchFrame = parent.ModLaunchFrame;
                return;
            }
            beam.ModLaunchMatch = NetSession.CurrentMatchId;
            beam.ModLaunchAuthority = NetSession.AuthorityEpoch;
            beam.ModLaunchGeneration = owner == null ? (ushort)0 : Generation(owner.SlotIndex);
            beam.ModLaunchLife = owner == null ? (ushort)0 : Get(owner.SlotIndex);
            beam.ModLaunchFrame = owner == null ? 0 : NetUnlagged.LaunchFrameFor(owner);
            beam.ModLaunchKey = new ShotKey(beam.ModLaunchAuthority, beam.ModLaunchMatch,
                owner?.SlotIndex ?? -1, beam.ModLaunchGeneration, beam.ModLaunchLife, beam.ModLaunchFrame);
        }

        public static bool CurrentProjectile(BeamProjectileEntity beam)
        {
            PlayerEntity? owner = beam.Owner as PlayerEntity ?? (beam.Owner as HalfturretEntity)?.Owner;
            return owner == null || (beam.ModLaunchMatch == NetSession.CurrentMatchId
                && beam.ModLaunchAuthority == NetSession.AuthorityEpoch
                && beam.ModLaunchLife != 0
                && Generation(owner.SlotIndex) == beam.ModLaunchGeneration
                && beam.ModLaunchKey == new ShotKey(beam.ModLaunchAuthority, beam.ModLaunchMatch,
                    owner.SlotIndex, beam.ModLaunchGeneration, beam.ModLaunchLife, beam.ModLaunchFrame));
        }

        public static void SetOccupant(int slot, ushort generation)
        {
            if (slot < 0 || slot >= _slots.Length || Generation(slot) == generation) return;
            NetSlotManager.ReleaseSlot(slot);
            OnSlotChanged(slot);
            _slots[slot].SetOccupant(generation);
        }

        public static void OnSlotChanged(int slot)
        {
            NetPlayerBridge.ForgetSlot(slot);
            NetDamage.ForgetSlot(slot);
            NetHitPrediction.ForgetSlot(slot);
            NetHitClaims.ForgetSlot(slot);
            NetSmoothing.ResetSlot(slot);
            NetUnlagged.ResetSlot(slot);
            NetSession.ForgetSlot(slot);
            NetScoreboard.ForgetSlot(slot);
            NetTimingDiagnostics.ForgetSlot(slot);
        }

        public static void OnSpawn(PlayerEntity player)
        {
            if (!NetSession.Active || ApplyingSpawn || (!NetSession.IsHost && !NetSession.IsAuthority)) return;
            int slot = player.SlotIndex;
            if (Generation(slot) == 0) SetOccupant(slot, 1);
            _slots[slot].BeginLife();
            NetPlayerBridge.ForgetSlot(slot);
            NetDamage.ForgetSlot(slot);
            NetHitPrediction.NoteRespawn(slot);
            NetHitClaims.ForgetSlot(slot, preserveFlights: true);
            NetSmoothing.ResetSlot(slot);
            NetUnlagged.ResetSlot(slot);
            NetSession.ForgetSlot(slot);
            player.ModResetNetworkHistory();
            player.Controls?.ClearAll();
            Spawns++;
            Transitions++;
            Log(slot, "SPAWN", 0, player.Health, true);
        }

        public static NetworkPlayerState StateOf(in PlayerState state) =>
            (state.Flags & PlayerState.FlagSpectating) != 0 ? NetworkPlayerState.Spectating
            : state.LifeId == 0 ? NetworkPlayerState.WaitingToSpawn
            : state.Health == 0 ? NetworkPlayerState.Dead : NetworkPlayerState.Alive;

        private static bool Sane(OpenTK.Mathematics.Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
            && Math.Abs(value.X) < 100000 && Math.Abs(value.Y) < 100000 && Math.Abs(value.Z) < 100000;

        public static bool AcceptState(in PlayerState state, uint frame)
        {
            int slot = state.SlotIndex;
            if (slot >= _slots.Length || !Sane(state.Position) || !Sane(state.Speed) || !Sane(state.Facing)) return false;
            NetworkPlayerState next = StateOf(state);
            if (state.Health > 0 && ((state.Flags & PlayerState.FlagSpawned) == 0 || state.LifeId == 0)) return false;
            NetworkPlayerState before = _slots[slot].State;
            LifecycleRejection rejection = _slots[slot].Accept(state.SlotGeneration, state.LifeId, next, out bool fresh);
            if (rejection != LifecycleRejection.None)
            {
                if (rejection == LifecycleRejection.WrongGeneration) WrongGeneration++;
                else if (rejection == LifecycleRejection.InvalidResurrection) InvalidResurrections++;
                else StaleLifeStates++;
                Log(slot, $"DROP {rejection}", frame, state.Health, (state.Flags & PlayerState.FlagSpawned) != 0);
                return false;
            }
            if (fresh) NetSmoothing.ResetSlot(slot);
            if (fresh || before != next)
            {
                Transitions++;
                if (fresh && next == NetworkPlayerState.Alive) Spawns++;
                if (next == NetworkPlayerState.Dead) Deaths++;
                Log(slot, fresh ? "SPAWN" : next.ToString(), frame, state.Health,
                    (state.Flags & PlayerState.FlagSpawned) != 0);
            }
            return true;
        }

        public static bool AcceptIntent(int slot, in IntentPacket intent)
        {
            if (!NetSession.MatchesStream(intent.MatchId, intent.AuthorityEpoch)) return false;
            if (intent.SlotGeneration != Generation(slot)) { WrongGeneration++; return false; }
            if (intent.LifeId == 0 || !Matches(slot, intent.SlotGeneration, intent.LifeId)) { OldLifeIntents++; return false; }
            return true;
        }

        public static void ResetLives()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                OnSlotChanged(i);
                _slots[i].ResetLife();
            }
        }

        public static void Reset()
        {
            foreach (var slot in _slots) slot.SetOccupant(0);
            ApplyingSpawn = false;
            StaleLifeStates = WrongGeneration = InvalidResurrections = Transitions = Spawns = Deaths = 0;
            OldLifeIntents = OldLifeClaims = OldLifeDamage = CrossMatch = CrossAuthority = 0;
        }

        private static void Log(int slot, string action, uint frame, int health, bool spawned)
        {
            if (NetLog.Enabled)
                NetLog.Event($"[life] slot={slot} generation={Generation(slot)} life={Get(slot)} {action} "
                    + $"frame={NetSession.NetFrame} authorityFrame={frame} health={health} spawned={spawned} "
                    + NetHitPrediction.LifecycleDetails(slot));
        }

        public static string Describe() => $"life: transitions={Transitions} spawns={Spawns} deaths={Deaths} "
            + $"invalid resurrection={InvalidResurrections} stale life={StaleLifeStates} wrong generation={WrongGeneration} "
            + $"cross match={CrossMatch} cross authority={CrossAuthority} old intents={OldLifeIntents} "
            + $"old claims={OldLifeClaims} old damage={OldLifeDamage}";
    }
}
