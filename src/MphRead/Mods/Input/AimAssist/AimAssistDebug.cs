using System;
using System.Numerics;
using MphRead.Hud;
using MphRead.Mods.Chat;

namespace MphRead.Mods.Input.AimAssist
{
    internal static class AimAssistDebug
    {
        public static bool Enabled;
        public static bool UnassistedArm;
        public static AimAssistResult Result;
        public static AimAssistTarget Target;
        public static Vector2 Raw, Velocity;
        private static Scene? _scene;
        private static HudObjectInstance? _font;
        private static long _textAt;
        private static string[] _lines = Array.Empty<string>();
        public static void Draw(Scene scene)
        {
            if (!Enabled) return;
            if (_scene != scene)
            {
                _scene = scene; _font = new HudObjectInstance(width: ChatFont.Cell, height: ChatFont.Cell) { Enabled = true };
                _font.SetPaletteData(new[] { new ColorRgba(), new ColorRgba(255, 255, 255, 255) }, scene);
                _font.SetCharacterData(ChatFont.Pixels, scene);
            }
            if (Environment.TickCount64 - _textAt > 150)
            {
                _textAt = Environment.TickCount64;
                _lines = new[] {
                    $"Aim: {AimInputSourceTracker.Current} pad: {GamepadManager.Snapshot.DeviceId}",
                    $"Target {Result.TargetSlot} score {Result.Score:0.00} distance {Target.Distance:0.0} body {Target.BodyError.Length():0.00} head {Target.HeadError.Length():0.00}",
                    $"LOS body {Target.BodyVisible} head {Target.HeadVisible} point {Result.PointType} blend {Result.HeadBlend:0.00}",
                    $"Friction {Result.Friction:0.00} rotation {Result.RotationStrength:0.00} velocity {Velocity.X:0.0},{Velocity.Y:0.0}",
                    $"Raw {Raw.X:0.00},{Raw.Y:0.00} final {Result.X:0.00},{Result.Y:0.00} correction {Result.X-Raw.X:0.00},{Result.Y-Raw.Y:0.00}" };
            }
            scene.DrawHudFlatBox(2, 2, 254, 37, new OpenTK.Mathematics.Vector4(0, 0, 0, .8f));
            float y = 4;
            foreach (string line in _lines)
            {
                float x = 4;
                foreach (char ch in line)
                {
                    int index = ChatFont.Index(ch); if (index < 0) continue;
                    _font!.PositionX = x / 256; _font.PositionY = y / 192; _font.Alpha = 1;
                    _font.SetData(index, new ColorRgba(220, 255, 220, 255), scene);
                    scene.DrawHudObject(_font, mode: 1, scale: .45f);
                    x += ChatFont.Widths[index] * .45f; if (x > 250) break;
                }
                y += 6;
            }
        }
    }
}
