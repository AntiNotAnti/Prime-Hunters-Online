using System;
using MphRead.Formats;
using MphRead.Hud;
using MphRead.Mods.Launcher;
using MphRead.Mods.Network;
using MphRead.Mods.Replay;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    public partial class PlayerEntity
    {
        private static readonly ColorRgba SpectatorNameInk = new(235, 242, 250, 255);
        private static readonly ColorRgba SpectatorNameShadow = new(0, 0, 0, 255);

        private void ModDrawSpectatorNameTags()
        {
            if (!LauncherPrefs.SpectatorNameTags || !_scene.IsFreeCam)
            {
                return;
            }
            // Chase/orbit are implemented with the roam camera too, but they are
            // authored follow shots rather than the user-controlled free camera.
            if (DemoPlayback.IsActive
                && ReplayCamera.Mode is ReplayCameraMode.Chase or ReplayCameraMode.Orbit)
            {
                return;
            }

            for (int slot = 0; slot < _scene.Players.Items.Count; slot++)
            {
                PlayerEntity player = _scene.Players.Items[slot];
                if (!player.LoadFlags.TestFlag(LoadFlags.Active)
                    || !player.LoadFlags.TestFlag(LoadFlags.Spawned)
                    || player.Health <= 0
                    || player.Flags2.TestFlag(PlayerFlags2.Spectating))
                {
                    continue;
                }

                Vector3 position;
                if (_scene.ReplayPoses?.Sample(slot, _scene.ReplayRenderAlpha,
                    out Vector3 replayPosition, out _) == true)
                {
                    position = replayPosition;
                }
                else if (!_scene.Services.IsReplica && NetSession.Active
                    && slot != NetHooks.LocalSlot
                    && NetSmoothing.SamplePresentation(slot,
                        out Vector3 presented, out bool presentedAlt))
                {
                    position = NetPlayerBridge.InFormFor(player, presented, presentedAlt);
                }
                else
                {
                    position = _scene.Services.IsReplica
                        ? player.ReplayDrawTransform.Row3.Xyz
                        : player.Position;
                }

                float distance = (position - _scene.CameraPosition).Length;
                float alpha = Math.Clamp((42f - distance) / 22f, 0f, 1f);
                if (alpha <= 0.02f)
                {
                    continue;
                }
                alpha = Math.Min(alpha, 0.95f);

                float height = player.IsAltForm ? 0.9f
                    : player.IsMorphing || player.IsUnmorphing ? 1.35f : 1.85f;
                Vector3 anchor = position + Vector3.UnitY * height;
                Vector3 view = Matrix.Vec3MultMtx4(anchor, _scene.ViewMatrix);
                if (!Single.IsFinite(view.Z) || view.Z >= -0.05f)
                {
                    continue;
                }
                if (Matrix.ProjectPosition(anchor, _scene.ViewMatrix,
                    _scene.PerspectiveMatrix, out Vector2 projected) <= 0
                    || !Single.IsFinite(projected.X) || !Single.IsFinite(projected.Y)
                    || projected.X < 0.02f || projected.X > 0.98f
                    || projected.Y < 0.02f || projected.Y > 0.98f)
                {
                    continue;
                }

                string name = slot < _scene.GameState.Nicknames.Length
                    ? _scene.GameState.Nicknames[slot] : "";
                if (String.IsNullOrWhiteSpace(name))
                {
                    name = $"P{slot + 1}";
                }
                if (name.Length > 20)
                {
                    name = name[..20];
                }

                float x = projected.X * 256f;
                float y = projected.Y * 192f - 5f;
                const float scale = 0.65f;
                DrawText2D(x + 0.65f, y + 0.65f, Align.Center, 0, name,
                    SpectatorNameShadow, alpha: alpha * 0.8f, fontSpacing: 8, scale: scale);
                DrawText2D(x, y, Align.Center, 0, name,
                    SpectatorNameInk, alpha: alpha, fontSpacing: 8, scale: scale);
            }
        }
    }
}
