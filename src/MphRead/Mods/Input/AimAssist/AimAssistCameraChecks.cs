using System;
using System.Reflection;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Input.AimAssist
{
    // Exercise the real camera mutators as well as the allocation-free assist core.
    internal static class AimAssistCameraChecks
    {
        internal static void Run()
        {
            var priorGame = GameState.Current;
            var priorPlayers = PlayerEntity.LegacyRegistry;
            var priorRandom = Rng.Current;
            try
            {
                var scene = new Scene(new Vector2i(256, 192), SyntheticInput.CreateKeyboard(),
                    SyntheticInput.CreateMouse(), _ => { }, () => { }, initializeRuntime: false);
                var player = scene.Players.Main;
                typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.Values))!
                    .SetValue(player, Metadata.PlayerValues[(int)Hunter.Samus]);
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                FieldInfo gun = typeof(PlayerEntity).GetField("_gunVec1", flags)!;
                FieldInfo pitch = typeof(PlayerEntity).GetField("_aimY", flags)!;
                MethodInfo yawInput = typeof(PlayerEntity).GetMethod("UpdateAimX", flags)!;
                MethodInfo pitchInput = typeof(PlayerEntity).GetMethod("UpdateAimY", flags)!;
                void Reset()
                {
                    gun.SetValue(player, Vector3.UnitZ);
                    pitch.SetValue(player, 0f);
                    typeof(PlayerEntity).GetField("_facingVector", flags)!.SetValue(player, Vector3.UnitZ);
                }
                void Near(float actual, float expected, string name)
                    => GamepadChecks.Check(Math.Abs(actual - expected) < .001f, "aim camera: " + name);
                float Yaw()
                {
                    var direction = (Vector3)gun.GetValue(player)!;
                    return MathHelper.RadiansToDegrees(MathF.Atan2(direction.X, direction.Z));
                }

                player.EquipInfo.Zoomed = true;
                player.CameraInfo.Fov = Fixed.ToFloat(player.Values.NormalFov) * 2 * .25f;
                Reset();
                yawInput.Invoke(player, new object[] { 4f, true });
                Near(Yaw(), 1, "ordinary input retains zoom sensitivity");
                Reset();
                yawInput.Invoke(player, new object[] { 4f, false });
                Near(Yaw(), 4, "assisted yaw is not scaled by zoom a second time");
                Reset();
                pitchInput.Invoke(player, new object[] { 4f, false });
                var direction = (Vector3)gun.GetValue(player)!;
                Near(MathHelper.RadiansToDegrees(MathF.Asin(direction.Y)), 4,
                    "assisted pitch is in the same angular units as target errors");
                Reset();
                pitchInput.Invoke(player, new object[] { 90f, false });
                Near((float)pitch.GetValue(player)!, 85, "assistance still obeys the camera pitch limit");
            }
            finally
            {
                GameState.Current = priorGame;
                PlayerEntity.LegacyRegistry = priorPlayers;
                Rng.Current = priorRandom;
            }
        }
    }
}
