using System;
using MphRead.Entities;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    // Flick boosts are generated inside the movement step, after the normal
    // button capture. Repeat the last event for the same window as press
    // history, but consume its frame only once even while an intent is held.
    internal static class NetMovementInput
    {
        private static uint _boostFrame;
        private static Vector2 _boostDirection;
        private static readonly uint[] _consumedBoost = new uint[PlayerEntity.SlotCapacity];

        internal static void RecordBoost(PlayerEntity player, Vector2 direction)
        {
            if (!NetSession.Active || player.SlotIndex != NetSession.LocalSlot) return;
            _boostFrame = NetSession.NetFrame;
            _boostDirection = Normalize(direction);
        }

        internal static void CaptureBoost(ref IntentPacket intent)
        {
            if (_boostFrame != 0 && unchecked(NetSession.NetFrame - _boostFrame) < IntentPacket.PressHistory)
            {
                intent.BoostFrame = _boostFrame;
                intent.BoostDirection = _boostDirection;
            }
        }

        internal static bool ConsumeBoost(int slot, in IntentPacket intent)
        {
            uint frame = intent.BoostFrame;
            if ((uint)slot >= _consumedBoost.Length || frame == 0
                || !intent.HasValidMovement || !intent.Buttons.HasFlag(IntentButtons.InPlayState)
                || unchecked(intent.Frame - frame) >= IntentPacket.PressHistory
                || (_consumedBoost[slot] != 0 && !NetLifecycleTracker.Newer(frame, _consumedBoost[slot])))
                return false;
            _consumedBoost[slot] = frame;
            return true;
        }

        internal static Vector2 Normalize(Vector2 direction)
            => Single.IsFinite(direction.X) && Single.IsFinite(direction.Y)
                && direction.LengthSquared > 0.000001f && direction.LengthSquared < 4
                ? direction.Normalized() : Vector2.Zero;

        internal static void ResetSlot(int slot)
        {
            if ((uint)slot < _consumedBoost.Length) _consumedBoost[slot] = 0;
            if (slot == NetSession.LocalSlot) { _boostFrame = 0; _boostDirection = default; }
        }

        internal static void Reset()
        {
            Array.Clear(_consumedBoost);
            _boostFrame = 0;
            _boostDirection = default;
        }
    }
}

namespace MphRead.Entities
{
    public partial class PlayerEntity
    {
        // The engine chooses the roll basis from its camera before reading
        // roll controls. Remote players use the basis sampled by that input;
        // a server camera can differ because its collision history differs.
        private void ModNetworkRollInput()
        {
            if (!Mods.Network.NetSession.Active) return;
            int slot = SlotIndex;
            if (slot == Mods.Network.NetSession.LocalSlot)
            {
                Mods.Network.NetHooks.RecordRollBasis(new Vector2(_altRollFbX, _altRollFbZ));
                return;
            }
            if ((uint)slot >= Mods.Network.NetSession.RemoteIntents.Length
                || !Mods.Network.NetSession.RemoteIntentValid[slot]
                || Mods.Network.NetSession.RemoteIntentAge(slot) > 30) return;
            var intent = Mods.Network.NetSession.RemoteIntents[slot];
            if (!Mods.Network.NetPlayerLifecycle.Matches(slot, intent.SlotGeneration, intent.LifeId)
                || !intent.HasValidMovement || !intent.Buttons.HasFlag(Mods.Network.IntentButtons.InPlayState)) return;
            Vector2 forward = Mods.Network.NetMovementInput.Normalize(intent.RollForward);
            if (forward != Vector2.Zero)
            {
                _altRollFbX = forward.X; _altRollFbZ = forward.Y;
                _altRollLrX = forward.Y; _altRollLrZ = -forward.X;
            }
            if (_abilities.TestFlag(AbilityFlags.Boost) && !IsMorphing && AttachedEnemy == null
                && Mods.Network.NetMovementInput.ConsumeBoost(slot, intent))
            {
                Vector2 direction = Mods.Network.NetMovementInput.Normalize(intent.BoostDirection);
                SwipeBoostRequested = true;
                SwipeBoostX = -Vector2.Dot(direction, new Vector2(_altRollLrX, _altRollLrZ));
                SwipeBoostY = -Vector2.Dot(direction, new Vector2(_altRollFbX, _altRollFbZ));
            }
        }
    }
}
