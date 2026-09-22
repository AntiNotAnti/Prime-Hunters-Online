using System;
using MphRead.Entities;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead
{
    public partial class Scene
    {
        /// <summary>One fixed replica step. The caller pumps only this scene's
        /// session; no local input, socket, match-ending or foreground HUD pass runs.</summary>
        internal void StepReplica()
        {
            if (Services is not ReplaySceneServices replay)
                throw new InvalidOperationException("Only a replay-owned scene can take a replica step.");
            _frameTime = 1f / 60;
            _effectFrame++;
            _globalElapsedTime += _frameTime;
            _elapsedTime += _frameTime;
            replay.BeforeSimulation(this);
            foreach (EntityBase entity in Entities)
            {
                if (entity.Initialized && !entity.Process())
                {
                    SendMessage(Message.Destroyed, entity, null, 0, 0, delay: 1);
                    entity.Destroy();
                    RemoveEntity(entity);
                }
            }
            replay.AfterSimulation(this);
            ProcessMessageQueue();
            foreach (EntityBase entity in Entities)
                if (entity.Initialized) entity.ModCaptureDrawState();
            foreach (PlayerEntity player in Players.Items)
            {
                player.CameraInfo.ModCaptureDrawState();
                player.ModCaptureFirstPersonDrawState();
            }
            ProcessEffects(_effectFrame);
            _pendingEffectSteps = _pendingFadeSteps = 0;
            _frameCount++;
            _liveFrames++;
        }

        internal void SetReplicaCamera(Vector3 position, Vector3 target, float fov)
        {
            if (!Services.IsReplica) throw new InvalidOperationException("A replica camera requires a replica scene.");
            Vector3 direction = target - position;
            if (!float.IsFinite(direction.LengthSquared) || direction.LengthSquared < 0.00001f) return;
            _cameraMode = CameraMode.Roam;
            _cameraPosition = position;
            _cameraFacing = direction.Normalized();
            Vector3 up = MathF.Abs(Vector3.Dot(_cameraFacing, Vector3.UnitY)) > .999f ? Vector3.UnitZ : Vector3.UnitY;
            _cameraUp = up;
            _cameraRight = Vector3.Cross(_cameraFacing, up).Normalized();
            _viewMatrix = Matrix4.LookAt(position, target, up);
            _viewInvRotMatrix = Matrix4.Transpose(_viewMatrix.ClearTranslation());
            _viewInvRotYMatrix = Matrix4.Identity;
            Vector3 right = _viewInvRotMatrix.Row0.Xyz.WithY(0);
            Vector3 back = _viewInvRotMatrix.Row2.Xyz.WithY(0);
            if (right.LengthSquared > .00001f && back.LengthSquared > .00001f)
            {
                _viewInvRotYMatrix.Row0.Xyz = right.Normalized();
                _viewInvRotYMatrix.Row2.Xyz = back.Normalized();
            }
            _cameraFov = MathHelper.DegreesToRadians(Math.Clamp(fov, 10, 150));
        }
    }
}
