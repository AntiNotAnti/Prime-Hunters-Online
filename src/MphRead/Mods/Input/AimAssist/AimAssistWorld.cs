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
        private BeamType _assistWeapon;
        private bool _assistZoomed;

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
        // Project the full cylinder silhouette. Destination rays are checked against
        // its real vertical band so rectangular projection corners cannot create headshots.
        private AimAssistRegion AssistRegion(PlayerEntity target, float lower, float upper, float radius)
        {
            Vector3 offset = target.Position - CameraInfo.Position;
            float horizontal = MathF.Sqrt(offset.X * offset.X + offset.Z * offset.Z);
            float near = Math.Max(.001f, horizontal - radius), far = horizontal + radius;
            float yaw = AssistAngles(target.Position).X;
            float half = MathHelper.RadiansToDegrees(MathF.Asin(Math.Clamp(radius / Math.Max(radius, horizontal), 0, 1)));
            float cameraPitch = MathF.Atan2(_gunVec1.Y, MathF.Sqrt(_gunVec1.X * _gunVec1.X + _gunVec1.Z * _gunVec1.Z));
            float low = offset.Y + lower, high = offset.Y + upper;
            float min = Math.Min(MathF.Atan2(low, near), MathF.Atan2(low, far));
            float max = Math.Max(MathF.Atan2(high, near), MathF.Atan2(high, far));
            return new(yaw - half, yaw + half, MathHelper.RadiansToDegrees(min - cameraPitch),
                MathHelper.RadiansToDegrees(max - cameraPitch));
        }

        private bool AssistRegionRayVisible(PlayerEntity target, System.Numerics.Vector2 error,
            float lower, float upper)
        {
            float yaw = MathF.Atan2(_gunVec1.X, _gunVec1.Z) + MathHelper.DegreesToRadians(error.X);
            float pitch = MathF.Atan2(_gunVec1.Y, MathF.Sqrt(_gunVec1.X * _gunVec1.X + _gunVec1.Z * _gunVec1.Z))
                + MathHelper.DegreesToRadians(error.Y);
            Vector3 direction = new(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch),
                MathF.Cos(yaw) * MathF.Cos(pitch));
            Vector3 offset = CameraInfo.Position - target.Position;
            float a = direction.X * direction.X + direction.Z * direction.Z;
            float b = offset.X * direction.X + offset.Z * direction.Z;
            float c = offset.X * offset.X + offset.Z * offset.Z
                - target.Volume.SphereRadius * target.Volume.SphereRadius;
            float discriminant = b * b - a * c;
            if (a <= .000001f || discriminant < 0) return false;
            float distance = (-b - MathF.Sqrt(discriminant)) / a;
            Vector3 impact = CameraInfo.Position + direction * distance;
            float impactHeight = impact.Y - target.Position.Y;
            return distance > 0 && impactHeight >= lower && impactHeight <= upper && AssistVisible(impact);
        }

        private bool AssistRegionVisible(PlayerEntity target, AimAssistRegion region, float lower, float upper)
        {
            // Visibility belongs to the hittable region, not its center. A hunter
            // peeking around a pillar or over cover can expose a legitimate slice
            // while the center ray remains blocked. Sample deterministic inset
            // points and accept the region only when one real cylinder impact has LOS.
            AimAssistRegion inset = region.Inset(.12f);
            float pitch = Math.Clamp(0, inset.MinPitch, inset.MaxPitch);
            float yaw = Math.Clamp(0, inset.MinYaw, inset.MaxYaw);
            Span<System.Numerics.Vector2> samples = stackalloc System.Numerics.Vector2[6];
            samples[0] = AimAssistMath.RegionError(inset);
            samples[1] = inset.Center;
            samples[2] = new(inset.MinYaw, pitch);
            samples[3] = new(inset.MaxYaw, pitch);
            samples[4] = new(yaw, inset.MinPitch);
            samples[5] = new(yaw, inset.MaxPitch);
            for (int i = 0; i < samples.Length; i++)
            {
                if (AssistRegionRayVisible(target, samples[i], lower, upper))
                {
                    return true;
                }
            }
            return false;
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
            // Remote and replay actors consume recorded aim. They must never read or
            // update the foreground controller's source, target or telemetry state.
            if (_scene.Services.IsReplica || NetHooks.IsPuppet(this))
                return new AimAssistResult(x, y);
            var snapshot = GamepadInput.FrameSnapshot;
            long context = GamepadContexts.Revision;
            long now = Environment.TickCount64;
            AimInputSourceTracker.Pointer(Input.MouseDeltaX, Input.MouseDeltaY,
                PointerDevice.Active && PointerDevice.Current.Device != PointerDeviceType.Mouse, now);
            var aim = GamepadInput.AimStick;
            AimInputSourceTracker.Stick(aim.X, aim.Y, now);
            if (_assistDeviceRevision != snapshot.Revision || _assistContextRevision != context
                || _aimSourceRevision != AimInputSourceTracker.Revision || !ReferenceEquals(_assistRoom, _scene.Room)
                || _assistWeapon != CurrentWeapon || _assistZoomed != EquipInfo.Zoomed)
                _controllerAssist.Reset();
            _assistDeviceRevision = snapshot.Revision;
            _assistContextRevision = context;
            _assistRoom = _scene.Room;
            _assistWeapon = CurrentWeapon;
            _assistZoomed = EquipInfo.Zoomed;
            _aimSourceRevision = AimInputSourceTracker.Revision;
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
            if (eligible || observe) for (int index = 0; index < _scene.Players.Items.Count; index++)
            {
                var target = _scene.Players.Items[index];
                if (target == null || target == this || !target.ModInPlay || !target.LoadFlags.TestFlag(LoadFlags.Active)
                    || !target.LoadFlags.TestFlag(LoadFlags.Spawned) || target.CurAlpha < .95f
                    || (_scene.GameState.Teams && TeamIndex == target.TeamIndex)) continue;
                var volume = PlayerVolumes[(int)target.Hunter, target.IsAltForm ? 2 : 0];
                Vector3 center = target.Position + volume.SpherePosition;
                float height = Fixed.ToFloat(target.Values.MaxPickupHeight);
                Vector3 chest = target.IsAltForm ? center
                    : Vector3.Lerp(center, target.Position + new Vector3(0, height - .3f, 0), .65f);
                Vector3 head = target.Position + new Vector3(0, height - .15f, 0);
                float distance = (head - CameraInfo.Position).Length;
                float radius = target.Volume.SphereRadius;
                var bodyRegion = AssistRegion(target, target.IsAltForm ? volume.SpherePosition.Y - radius
                    : Fixed.ToFloat(target.Values.MinPickupHeight), target.IsAltForm ? volume.SpherePosition.Y + radius : height, radius);
                var headRegion = AssistRegion(target, height - .3f, height, radius);
                var bodyError = AssistAngles(chest);
                var headError = AssistAngles(head);
                if (!AimAssistMath.Finite(bodyError) || !float.IsFinite(distance) || distance > 60
                    || (AimAssistMath.RegionDistance(bodyRegion) > profile.ReleaseCone
                        && (!profile.Head || target.IsAltForm || !AimAssistMath.Finite(headError)
                            || AimAssistMath.RegionDistance(headRegion) > profile.ReleaseCone))) continue;

                long targetLife = NetSession.Active
                    ? ((long)NetPlayerLifecycle.Generation(target.SlotIndex) << 16)
                        | NetPlayerLifecycle.Get(target.SlotIndex)
                    : 0;
                bool retained = target.SlotIndex == _controllerAssist.TargetSlot
                    && targetLife == _controllerAssist.TargetLife;
                bool visible = AssistRegionVisible(target, bodyRegion,
                    target.IsAltForm ? volume.SpherePosition.Y - radius : Fixed.ToFloat(target.Values.MinPickupHeight),
                    target.IsAltForm ? volume.SpherePosition.Y + radius : height);
                bool headVisible = !target.IsAltForm && profile.Head
                    && AimAssistMath.Finite(headError)
                    && AimAssistMath.RegionDistance(headRegion) <= profile.ReleaseCone
                    && headRegion.MaxPitch > headRegion.MinPitch
                    && AimAssistMath.CanHeadshotAtDistance(CurrentWeapon, distance)
                    && AssistRegionVisible(target, headRegion, height - .3f, height);
                if (!visible && !headVisible && !retained) continue;
                candidates[count++] = new(target.SlotIndex, targetLife, bodyError, headError, distance,
                    visible, headVisible,
                    BodyPointType: target.IsAltForm ? AimAssistPointType.CenterMass : AimAssistPointType.UpperChest,
                    // Half the 0.3-unit headshot band used by BeamProjectileEntity.
                    HeadRadiusDegrees: MathHelper.RadiansToDegrees(MathF.Atan2(.15f, (head - CameraInfo.Position).Length)),
                    BodyRegion: bodyRegion, HeadRegion: headRegion);
                if (count == candidates.Length) break;
            }
            var pad = snapshot.State;
            var movement = GamepadOptions.Southpaw
                ? GamepadAnalog.ApplyRadialDeadZone(pad.RightX, pad.RightY, GamepadOptions.RightInner, GamepadOptions.RightOuter)
                : GamepadAnalog.ApplyRadialDeadZone(pad.LeftX, pad.LeftY, GamepadOptions.LeftInner, GamepadOptions.LeftOuter);
            float move = MathF.Sqrt(movement.X * movement.X + movement.Y * movement.Y);
            // The engine's input/simulation step is fixed at 60 Hz; render rate does not change this interval.
            var result = AimAssist.Apply(_controllerAssist, candidates[..count], new(x, y), new System.Numerics.Vector2(-aim.X * (GamepadOptions.InvertX != Controls.InvertAimX ? -1 : 1),
                    aim.Y * (GamepadOptions.InvertY != Controls.InvertAimY ? -1 : 1)),
                move, 1f / 60, eligible, profile, Controls.Shoot.IsDown);
            AimAssistTarget chosen = default;
            foreach (ref readonly var candidate in candidates[..count]) if (candidate.Slot == result.TargetSlot) chosen = candidate;
            if (AimAssistDebug.UnassistedArm)
            {
                result = result with
                {
                    X = x, Y = y, Friction = 1, RotationStrength = 0, HeadBlend = 0,
                    PointType = chosen.BodyPointType, HeadPrediction = 0, Occluded = false, Saturated = false,
                    PositionCorrection = default, TrackingCorrection = default, StrafeTracking = false,
                    TrackingState = result.TargetSlot < 0 ? AimAssistTrackingState.None : AimAssistTrackingState.TrackingBody
                };
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
