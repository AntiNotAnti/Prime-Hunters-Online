using System;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Mods.Input;
using MphRead.Mods.Input.AimAssist;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    public partial class PlayerEntity
    {
        private readonly AimAssistState _controllerAssist = new();
        private long _assistDeviceRevision = -1, _assistContextRevision = -1;
        private long _aimSourceRevision = -1;
        private object? _assistRoom;

        // Position is the locally presented entity, including snapshot playout on clients.
        // Do not substitute authority history, packet positions or projectile convergence here.
        private System.Numerics.Vector2 AssistAngles(Vector3 point)
        {
            Vector3 direction = point - CameraInfo.Position;
            float desiredYaw = MathF.Atan2(direction.X, direction.Z);
            float currentYaw = MathF.Atan2(_gunVec1.X, _gunVec1.Z);
            float yaw = MathF.IEEERemainder(desiredYaw - currentYaw, MathF.PI * 2);
            float pitch = MathF.Atan2(direction.Y, MathF.Sqrt(direction.X * direction.X + direction.Z * direction.Z))
                - MathF.Atan2(_gunVec1.Y, MathF.Sqrt(_gunVec1.X * _gunVec1.X + _gunVec1.Z * _gunVec1.Z));
            return new(MathHelper.RadiansToDegrees(yaw), MathHelper.RadiansToDegrees(pitch));
        }
        private bool AssistVisible(Vector3 point)
        {
            CollisionResult result = default;
            var candidates = CollisionDetection.GetCandidatesForLimits(CameraInfo.Position, point, 0,
                null, Vector3.Zero, includeEntities: true, _scene);
            return !CollisionDetection.CheckBetweenPoints(candidates, CameraInfo.Position, point, TestFlags.Beams, _scene, ref result);
        }
        private AimAssistResult ApplyControllerAssist(float x, float y)
        {
            var snapshot = GamepadInput.FrameSnapshot;
            long context = GamepadContexts.Revision;
            if (_assistDeviceRevision != snapshot.Revision || _assistContextRevision != context                || _aimSourceRevision != AimInputSourceTracker.Revision || !ReferenceEquals(_assistRoom, _scene.Room))
            { _controllerAssist.Reset(); _assistDeviceRevision = snapshot.Revision; _assistContextRevision = context; }
            _aimSourceRevision = AimInputSourceTracker.Revision; _assistRoom = _scene.Room;
            AimInputSourceTracker.Pointer(Input.MouseDeltaX, Input.MouseDeltaY,
                PointerDevice.Active && PointerDevice.Current.Device != PointerDeviceType.Mouse, Environment.TickCount64);
            var aim = GamepadInput.AimStick;
            AimInputSourceTracker.Stick(aim.X, aim.Y, Environment.TickCount64);
            bool eligible = snapshot.State.Connected && GamepadContexts.Focused && !GamepadContexts.MenuVisible
                && GamepadContexts.Current == GamepadContext.Gameplay && !GamepadInput.WheelHeld
                && AimInputSourceTracker.Current == AimInputSource.Gamepad && Health > 0
                && LoadFlags.TestFlag(LoadFlags.Spawned) && !IsAltForm && !Mods.SpectatorMode.IsSpectating;
            var weapon = CurrentWeapon switch {
                BeamType.ShockCoil => AimAssistWeaponClass.Tracking,
                BeamType.Imperialist => AimAssistWeaponClass.Precision,
                BeamType.Missile or BeamType.Magmaul or BeamType.OmegaCannon => AimAssistWeaponClass.Splash,
                BeamType.Judicator or BeamType.Battlehammer => AimAssistWeaponClass.Projectile,
                _ => AimAssistWeaponClass.Standard };
            var profile = AimAssistWeaponProfile.For(weapon, EquipInfo.Zoomed);
            Span<AimAssistTarget> candidates = stackalloc AimAssistTarget[SlotCapacity];
            int count = 0;
            bool observe = AimAssistTelemetry.Enabled && GamepadContexts.Focused && !GamepadContexts.MenuVisible
                && GamepadContexts.Current == GamepadContext.Gameplay && Health > 0 && !Mods.SpectatorMode.IsSpectating;
            if (eligible || observe) for (int index = 0; index < Players.Count; index++)
            {
                var target = Players[index];
                if (target == null || target == this || !target.ModInPlay || !target.LoadFlags.TestFlag(LoadFlags.Active)
                    || !target.LoadFlags.TestFlag(LoadFlags.Spawned) || target.CurAlpha < .95f
                    || (GameState.Teams && TeamIndex == target.TeamIndex)) continue;
                var volume = PlayerVolumes[(int)target.Hunter, target.IsAltForm ? 2 : 0];
                Vector3 center = target.Position + volume.SpherePosition;
                float height = Fixed.ToFloat(target.Values.MaxPickupHeight);
                Vector3 chest = target.IsAltForm ? center : Vector3.Lerp(center, target.Position + new Vector3(0, height - .3f, 0), .65f);
                Vector3 head = target.Position + new Vector3(0, height - .15f, 0);
                float distance = (chest - CameraInfo.Position).Length;
                var bodyError = AssistAngles(chest);
                if (!AimAssistMath.Finite(bodyError) || !float.IsFinite(distance) || distance > 60
                    || bodyError.Length() > profile.ReleaseCone) continue;
                bool visible = AssistVisible(chest);
                if (!visible) continue;
                var headError = AssistAngles(head);
                bool headVisible = !target.IsAltForm && profile.Head && headError.Length() < 1.5f && AssistVisible(head);
                long targetLife = 0;
                candidates[count++] = new(target.SlotIndex, targetLife, bodyError, headError, distance, visible, headVisible,
                    BodyPointType: target.IsAltForm ? AimAssistPointType.CenterMass : AimAssistPointType.UpperChest);
                if (count == candidates.Length) break;
            }
            var pad = snapshot.State;
            var movement = GamepadOptions.Southpaw
                ? GamepadAnalog.ApplyRadialDeadZone(pad.RightX, pad.RightY, GamepadOptions.RightInner, GamepadOptions.RightOuter)
                : GamepadAnalog.ApplyRadialDeadZone(pad.LeftX, pad.LeftY, GamepadOptions.LeftInner, GamepadOptions.LeftOuter);
            float move = MathF.Sqrt(movement.X * movement.X + movement.Y * movement.Y);
            // The engine's input/simulation step is fixed at 60 Hz; render rate does not change this interval.
            var result = AimAssist.Apply(_controllerAssist, candidates[..count], new(x, y), MathF.Sqrt(aim.X * aim.X + aim.Y * aim.Y),
                move, 1f / 60, eligible, profile);
            AimAssistTarget chosen = default;
            foreach (ref readonly var candidate in candidates[..count]) if (candidate.Slot == result.TargetSlot) chosen = candidate;
            if (AimAssistDebug.UnassistedArm)
            {
                result = result with { X = x, Y = y, Friction = 1, RotationStrength = 0, HeadBlend = 0, PointType = chosen.BodyPointType };
                _controllerAssist.PreviousOutput = new(x, y);
            }
            AimAssistDebug.Result = result; AimAssistDebug.Target = chosen;
            AimAssistDebug.Raw = new(x, y); AimAssistDebug.Velocity = _controllerAssist.AngularVelocity;
            var observation = result;
            if (!eligible && observe)
            {
                float nearest = profile.Cone;
                foreach (ref readonly var candidate in candidates[..count])
                    if (candidate.BodyError.Length() < nearest) { chosen = candidate; nearest = candidate.BodyError.Length(); observation = result with { TargetSlot = candidate.Slot }; }
            }
            AimAssistTelemetry.Record(CurrentWeapon, chosen, observation, MathF.Sqrt((result.X-x)*(result.X-x)+(result.Y-y)*(result.Y-y)), _controllerAssist.AngularVelocity.Length());
            return result;
        }
    }
}
