using System;

namespace MphRead.Mods.Network
{
    /// <summary>Per-slot firing clocks for continuous player weapons.</summary>
    internal sealed class ContinuousWeaponPhase
    {
        internal const uint MaxIntentAge = 30;

        internal struct Clock
        {
            public ulong Phase;
            public ulong SceneFrame;
            public uint SourceTick;
            public bool Valid;
            public bool Explicit;
        }

        private readonly Clock[] _clocks;

        internal ContinuousWeaponPhase(int slots) => _clocks = new Clock[slots];

        // DS fixed-point cadence: ammo rounds strictly above the boundary,
        // damage includes it. Both use the same logical firing phase.
        internal static int Amount(int amount, ulong phase, bool damage)
        {
            if (phase % 2 != 0) return 0;
            ulong bits = (ulong)(amount & 31);
            ulong fraction = (bits * (phase / 2)) & 31;
            return amount / 32 + (bits != 0 && (damage ? fraction >= 32 - bits : fraction > 32 - bits) ? 1 : 0);
        }

        internal void Reset() => Array.Clear(_clocks);

        internal void ResetSlot(int slot)
        {
            if ((uint)slot < (uint)_clocks.Length)
            {
                _clocks[slot] = default;
            }
        }

        // Called on every player simulation step, including steps without a Spawn.
        // A missing packet does not end a held stream; a release, weapon change,
        // or stale intent does. The next fresh shot then seeds a new phase.
        internal void Observe(int slot, ulong sceneFrame, bool continuousHeld, bool intentFresh)
        {
            if (!continuousHeld || !intentFresh)
            {
                ResetSlot(slot);
                return;
            }
            if ((uint)slot < (uint)_clocks.Length && !_clocks[slot].Explicit)
            {
                Advance(ref _clocks[slot], sceneFrame);
            }
        }

        private static void Advance(ref Clock clock, ulong sceneFrame)
        {
            if (!clock.Valid) return;
            if (sceneFrame < clock.SceneFrame)
            {
                clock = default;
            }
            else if (sceneFrame > clock.SceneFrame)
            {
                clock.Phase++;
                clock.SceneFrame = sceneFrame;
            }
        }

        internal ulong Resolve(int slot, ulong sceneFrame, bool networked, bool localOwner,
            uint netFrame, bool intentValid, uint intentFrame, uint intentAge, uint continuousFireTick,
            out bool shared, out bool freshTick, bool receivedBeforeStep = false)
        {
            freshTick = true;
            if (networked && localOwner && netFrame != 0)
            {
                shared = true;
                return netFrame;
            }
            if (networked && !localOwner && (uint)slot < (uint)_clocks.Length
                && intentValid && intentFrame != 0 && intentAge <= MaxIntentAge)
            {
                ref Clock clock = ref _clocks[slot];
                if (continuousFireTick != 0)
                {
                    bool newer = !clock.Valid || !clock.Explicit
                        || SequenceMath.Newer(continuousFireTick, clock.SourceTick);
                    if (newer)
                    {
                        clock.SourceTick = continuousFireTick;
                        clock.Phase = continuousFireTick;
                        clock.SceneFrame = sceneFrame;
                        clock.Valid = true;
                        clock.Explicit = true;
                    }
                    freshTick = newer;
                    shared = true;
                    return clock.Phase;
                }

                clock.Explicit = false;
                Advance(ref clock, sceneFrame);
                if (!clock.Valid)
                {
                    // Legacy/offline inspection only. Protocol 21 live peers always
                    // provide ContinuousFireTick for Shock Coil.
                    uint elapsed = receivedBeforeStep && intentAge > 0 ? intentAge - 1 : intentAge;
                    clock.Phase = (ulong)intentFrame + elapsed;
                    clock.SceneFrame = sceneFrame;
                    clock.Valid = true;
                }
                shared = true;
                return clock.Phase;
            }
            ResetSlot(slot);
            shared = false;
            return sceneFrame;
        }
    }
}
