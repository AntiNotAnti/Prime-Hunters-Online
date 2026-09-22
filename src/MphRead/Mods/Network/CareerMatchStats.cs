using System;
using MphRead.Entities;
using MphRead.Mods.Multiplayer;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// Career-only facts the retail scoreboard does not retain.
    ///
    /// This runs only on the simulation authority. Clients may predict and
    /// replay damage for presentation, but neither path is allowed to author
    /// durable damage/assist totals.
    /// </summary>
    internal static class CareerMatchStats
    {
        private const int Slots = PlayerEntity.SlotCapacity;
        private const uint AssistWindowFrames = 5 * 60;
        private const int AssistMinimumDamage = 20;

        public static readonly long[] Damage = new long[Slots];
        public static readonly int[] Assists = new int[Slots];
        public static readonly int[] LongestKillStreak = new int[Slots];

        private static readonly int[,] _assistDamage = new int[Slots, Slots];
        private static readonly ulong[,] _assistFrame = new ulong[Slots, Slots];

        public static void Reset()
        {
            Array.Clear(Damage);
            Array.Clear(Assists);
            Array.Clear(LongestKillStreak);
            Array.Clear(_assistDamage);
            Array.Clear(_assistFrame);
        }

        /// <summary>
        /// A slot may be handed to another person inside one server process.
        /// Clear both what this slot did and damage waiting to credit it.
        /// </summary>
        public static void ForgetSlot(int slot)
        {
            if ((uint)slot >= Slots) return;
            Damage[slot] = 0;
            Assists[slot] = 0;
            LongestKillStreak[slot] = 0;
            for (int i = 0; i < Slots; i++)
            {
                _assistDamage[slot, i] = 0;
                _assistFrame[slot, i] = 0;
                _assistDamage[i, slot] = 0;
                _assistFrame[i, slot] = 0;
            }
        }

        public static void NoteDamage(PlayerEntity victim, PlayerEntity? attacker,
            uint damage, bool lethal, ulong frame)
        {
            if (!GameState.Multiplayer || !NetSession.IsAuthority
                || NetDamage.Replaying || NetHitPrediction.Predicting)
            {
                return;
            }

            int victimSlot = victim.SlotIndex;
            int attackerSlot = attacker?.SlotIndex ?? -1;
            bool opposing = attacker != null && attacker != victim
                && (uint)attackerSlot < Slots
                && (!GameState.Teams
                    || !TeamRules.AreAllies(attacker.TeamIndex, victim.TeamIndex));

            // TakeDamage has not reduced health yet, so current health is the
            // exact cap on damage actually dealt. Overkill is not career damage.
            uint applied = (uint)Math.Min((long)damage, Math.Max(0L, (long)victim.Health));

            if (opposing && applied > 0)
            {
                Damage[attackerSlot] = Math.Min(Int64.MaxValue,
                    Damage[attackerSlot] + applied);
            }

            if (opposing && applied > 0 && !lethal)
            {
                _assistDamage[victimSlot, attackerSlot] = Math.Min(Int32.MaxValue,
                    _assistDamage[victimSlot, attackerSlot] + (int)Math.Min((long)applied, Int32.MaxValue));
                _assistFrame[victimSlot, attackerSlot] = frame;
            }

            if (!lethal || (uint)victimSlot >= Slots)
            {
                return;
            }

            for (int slot = 0; slot < Slots; slot++)
            {
                if (slot == attackerSlot || slot == victimSlot
                    || _assistDamage[victimSlot, slot] < AssistMinimumDamage)
                {
                    continue;
                }
                ulong last = _assistFrame[victimSlot, slot];
                if (last == 0 || frame < last || frame - last > AssistWindowFrames)
                {
                    continue;
                }
                PlayerEntity helper = PlayerEntity.Players[slot];
                if (GameState.Teams
                    && TeamRules.AreAllies(helper.TeamIndex, victim.TeamIndex))
                {
                    continue;
                }
                if (Assists[slot] < Int32.MaxValue) Assists[slot]++;
            }

            for (int slot = 0; slot < Slots; slot++)
            {
                _assistDamage[victimSlot, slot] = 0;
                _assistFrame[victimSlot, slot] = 0;
            }
        }

        public static void NoteKill(PlayerEntity attacker)
        {
            if (!attacker.OwningScene.GameState.Multiplayer || !NetSession.IsAuthority) return;
            int slot = attacker.SlotIndex;
            if ((uint)slot >= Slots) return;
            LongestKillStreak[slot] = Math.Max(LongestKillStreak[slot],
                attacker.OwningScene.GameState.KillStreak[slot]);
        }
    }
}
