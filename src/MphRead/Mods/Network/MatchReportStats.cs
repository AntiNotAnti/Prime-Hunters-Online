using System;
using System.Collections.Generic;
using MphRead.Entities;
using MphRead.Mods.Multiplayer;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// Authoritative per-match combat totals used by the post-match report.
    ///
    /// Online, only the machine simulating the match records these values and
    /// the dedicated server publishes the finished local row to each client.
    /// Offline bot matches record locally. Prediction/replay paths are excluded
    /// so a corrected network hit can never be counted twice.
    /// </summary>
    internal static class MatchReportStats
    {
        private const int Slots = PlayerEntity.SlotCapacity;
        private static readonly HashSet<ShotKey>[] _hitShots = CreateHitSets();

        private static HashSet<ShotKey>[] CreateHitSets()
        {
            var result = new HashSet<ShotKey>[Slots];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = new HashSet<ShotKey>();
            }
            return result;
        }

        public static void ResetTracking()
        {
            for (int i = 0; i < _hitShots.Length; i++)
            {
                _hitShots[i].Clear();
            }
        }

        public static void ForgetSlot(int slot)
        {
            if ((uint)slot >= Slots)
            {
                return;
            }
            _hitShots[slot].Clear();
        }

        private static bool CanRecord(PlayerEntity player)
        {
            return player.OwningScene.GameState.Multiplayer
                && !player.SceneServices.IsReplica
                && (!NetSession.Active || NetSession.IsAuthority);
        }

        private static void Increment(int[] values, int slot)
        {
            if ((uint)slot < values.Length && values[slot] < Int32.MaxValue)
            {
                values[slot]++;
            }
        }

        private static void Add(int[] values, int slot, uint value)
        {
            if ((uint)slot >= values.Length || value == 0)
            {
                return;
            }
            values[slot] = (int)Math.Min(Int32.MaxValue, (long)values[slot] + value);
        }

        public static void NoteShotFired(PlayerEntity shooter)
        {
            if (!CanRecord(shooter))
            {
                return;
            }
            Increment(shooter.OwningScene.GameState.ShotsFired, shooter.SlotIndex);
        }

        public static void NoteDamage(PlayerEntity victim, PlayerEntity? attacker,
            BeamProjectileEntity? beam, uint damage)
        {
            if (!CanRecord(victim) || NetDamage.Replaying || NetHitPrediction.Predicting)
            {
                return;
            }

            int victimSlot = victim.SlotIndex;
            int attackerSlot = attacker?.SlotIndex ?? -1;
            SceneGameState state = victim.OwningScene.GameState;
            bool opposing = attacker != null && attacker != victim
                && (uint)victimSlot < Slots && (uint)attackerSlot < Slots
                && (!state.Teams
                    || !TeamRules.AreAllies(attacker.TeamIndex, victim.TeamIndex));
            if (!opposing)
            {
                return;
            }

            // TakeDamage has not changed health yet. Clamp here so an Imperialist
            // hit on a 1-HP hunter is one point of report damage, not its raw
            // weapon value.
            uint applied = (uint)Math.Min((long)damage, Math.Max(0L, (long)victim.Health));
            if (applied == 0)
            {
                return;
            }

            Add(state.MatchDamageDealt, attackerSlot, applied);
            Add(state.MatchDamageTaken, victimSlot, applied);

            if (beam == null)
            {
                return;
            }

            // Network projectiles already carry a stable launch identity. Use it
            // to count a damaging shot once even when splash, ricochet, or another
            // multi-contact path touches more than one hunter. Offline shots do
            // not always have that identity, so fall back to a damaging-contact
            // count there.
            ShotKey key = beam.ModLaunchKey;
            if (key.LaunchFrame == 0 || _hitShots[attackerSlot].Add(key))
            {
                Increment(state.ShotsHit, attackerSlot);
            }
        }

        public static void NoteKill(PlayerEntity attacker)
        {
            if (!CanRecord(attacker))
            {
                return;
            }
            SceneGameState state = attacker.OwningScene.GameState;
            int slot = attacker.SlotIndex;
            if ((uint)slot >= Slots)
            {
                return;
            }
            state.LongestKillStreak[slot] = Math.Max(
                state.LongestKillStreak[slot], state.KillStreak[slot]);
        }

        public static int AccuracyPercent(SceneGameState state, int slot)
        {
            if ((uint)slot >= Slots || state.ShotsFired[slot] <= 0)
            {
                return 0;
            }
            int hits = Math.Min(state.ShotsHit[slot], state.ShotsFired[slot]);
            return (int)Math.Round(hits * 100.0 / state.ShotsFired[slot]);
        }
    }
}
