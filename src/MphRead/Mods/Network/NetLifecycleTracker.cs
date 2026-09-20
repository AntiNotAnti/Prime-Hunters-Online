using System;

namespace MphRead.Mods.Network
{
    public enum NetworkPlayerState : byte
    {
        Empty, WaitingToSpawn, Alive, Dead, Spectating
    }

    public enum LifecycleRejection : byte
    {
        None, WrongGeneration, OldLife, InvalidResurrection, InvalidState
    }

    /// <summary>Protocol invariants, independent of rendering and the game clock.</summary>
    public sealed class NetLifecycleTracker
    {
        public ushort Generation { get; private set; }
        public ushort LifeId { get; private set; }
        public NetworkPlayerState State { get; private set; }
        private bool _dead;

        // Serial-number arithmetic: zero is reserved for an unassigned identity.
        public static ushort Next(ushort value) => value == ushort.MaxValue ? (ushort)1 : (ushort)(value + 1);
        public static bool Newer(ushort value, ushort previous) => unchecked((short)(value - previous)) > 0;
        public static bool Newer(ulong value, ulong previous) => value > previous;
        public static bool Newer(uint value, uint previous) => unchecked((int)(value - previous)) > 0;

        public void SetOccupant(ushort generation)
        {
            Generation = generation;
            ResetLife();
        }

        public void ResetLife()
        {
            LifeId = 0;
            State = Generation == 0 ? NetworkPlayerState.Empty : NetworkPlayerState.WaitingToSpawn;
            _dead = false;
        }

        public ushort BeginLife()
        {
            LifeId = Next(LifeId);
            State = NetworkPlayerState.Alive;
            _dead = false;
            return LifeId;
        }

        public LifecycleRejection Accept(ushort generation, ushort life, NetworkPlayerState state,
            out bool newLife)
        {
            newLife = false;
            if (generation == 0 || generation != Generation)
            {
                return LifecycleRejection.WrongGeneration;
            }
            if (life != LifeId && (life == 0 || (LifeId != 0 && !Newer(life, LifeId))))
            {
                return LifecycleRejection.OldLife;
            }
            if (life == 0 && state != NetworkPlayerState.WaitingToSpawn && state != NetworkPlayerState.Spectating)
            {
                return LifecycleRejection.InvalidState;
            }
            if (life == LifeId && _dead && state == NetworkPlayerState.Alive)
            {
                return LifecycleRejection.InvalidResurrection;
            }
            newLife = life != LifeId;
            if (newLife)
            {
                LifeId = life;
                _dead = false;
            }
            // Waiting/spectator packets cannot erase the death tombstone.
            _dead |= state == NetworkPlayerState.Dead;
            State = state;
            return LifecycleRejection.None;
        }
    }
}
