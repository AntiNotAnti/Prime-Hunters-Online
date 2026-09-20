using System;
namespace MphRead.Mods.Input
{
    public static class GamepadGlyphs
    {
        public static GamepadFamily Detect(string name, string? guid = null, int vendorId = 0)
        {
            string vendor = guid != null && guid.Length >= 12 ? guid.Substring(8, 4).ToLowerInvariant() : "";
            if (vendorId == 0x054c || vendor == "4c05") return GamepadFamily.PlayStation;
            if (vendorId == 0x045e || vendor == "5e04") return GamepadFamily.Xbox;
            if (vendorId == 0x057e || vendor == "7e05") return GamepadFamily.Nintendo;
            string n = name.ToLowerInvariant();
            if (n.Contains("xbox") || n.Contains("x-box") || n.Contains("xinput")) return GamepadFamily.Xbox;
            if (n.Contains("playstation") || n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("sony") || n.Contains("ps4") || n.Contains("ps5")) return GamepadFamily.PlayStation;
            if (n.Contains("nintendo") || n.Contains("switch") || n.Contains("joy-con")) return GamepadFamily.Nintendo;
            return GamepadFamily.Generic;
        }
        public static string Resolve(GamepadButtons button, GamepadFamily? family = null)
        {
            var style = family ?? (GamepadOptions.GlyphStyle == GamepadFamily.Unknown
                ? GamepadManager.ActiveDevice?.Family ?? GamepadFamily.Generic : GamepadOptions.GlyphStyle);
            if (style == GamepadFamily.PlayStation)
            {
                string? label = button switch
                {
                    GamepadButtons.A => "Cross", GamepadButtons.B => "Circle",
                    GamepadButtons.X => "Square", GamepadButtons.Y => "Triangle",
                    GamepadButtons.LeftBumper => "L1", GamepadButtons.RightBumper => "R1",
                    GamepadButtons.LeftTrigger => "L2", GamepadButtons.RightTrigger => "R2",
                    GamepadButtons.Back => "Share", GamepadButtons.Start => "Options", _ => null
                };
                if (label != null) return label;
            }
            if (style == GamepadFamily.Nintendo)
            {
                string? label = button switch
                {
                    GamepadButtons.A => "B", GamepadButtons.B => "A", GamepadButtons.X => "Y", GamepadButtons.Y => "X",
                    GamepadButtons.LeftBumper => "L", GamepadButtons.RightBumper => "R",
                    GamepadButtons.LeftTrigger => "ZL", GamepadButtons.RightTrigger => "ZR",
                    GamepadButtons.Back => "-", GamepadButtons.Start => "+", _ => null
                };
                if (label != null) return label;
            }
            return button switch
            {
                GamepadButtons.LeftBumper => "LB", GamepadButtons.RightBumper => "RB",
                GamepadButtons.LeftTrigger => "LT", GamepadButtons.RightTrigger => "RT",
                GamepadButtons.LeftThumb => "Left stick", GamepadButtons.RightThumb => "Right stick",
                GamepadButtons.DpadUp => "D-pad up", GamepadButtons.DpadDown => "D-pad down",
                GamepadButtons.DpadLeft => "D-pad left", GamepadButtons.DpadRight => "D-pad right",
                GamepadButtons.None => "unbound", _ => button.ToString()
            };
        }
    }
}
