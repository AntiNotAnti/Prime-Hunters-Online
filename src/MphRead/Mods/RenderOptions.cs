using System;
using System.Globalization;

namespace MphRead.Mods
{
    /// <summary>
    /// The knobs that trade picture for frame rate.
    ///
    /// The engine already had all of these; they were debug keys in the model
    /// viewer this grew out of (L for lighting, G for fog, F for filtering) and
    /// so were reachable only by someone who had read the help text, and never
    /// at all on a phone, which has no keyboard. They are the things worth
    /// turning off on a device that cannot keep 60, so they belong in the
    /// settings on every platform.
    ///
    /// <see cref="ResolutionScale"/> is the new one and the one that matters
    /// most. The scene is already drawn into an offscreen target and then put
    /// on the screen as one textured quad, so rendering that target smaller and
    /// letting the quad stretch it costs nothing to arrange and saves fill rate
    /// in proportion. The HUD, the helmet and the fade are drawn afterwards,
    /// straight to the window, so they stay sharp at any scale.
    /// </summary>
    public static class RenderOptions
    {
        /// <summary>
        /// Percent of the window the 3D scene is rendered at, 25 to 100.
        /// Halving it quarters the pixels.
        /// </summary>
        public static int ResolutionScale
        {
            get => _resolutionScale;
            set => _resolutionScale = Math.Clamp(value, MinScale, 100);
        }

        private static int _resolutionScale = 100;

        public const int MinScale = 25;

        /// <summary>
        /// How wide the view is, in degrees, measured the way the game
        /// measures it.
        ///
        /// The DS game is a 78: <c>PlayerValues.NormalFov</c> is 39 and every
        /// camera doubles it. That is a narrow picture by the standards of
        /// anything played with a mouse, and it is the single setting most
        /// often asked for in a shooter. The selected view scales the camera's
        /// projection rather than replacing its authored FOV. Zooming with a
        /// weapon, the Judicator's scope and every scripted camera all move
        /// <c>CameraInfo.Fov</c> themselves, and each keeps the same projection
        /// ratio to the hip view that it had on the cartridge.
        ///
        /// Clamped rather than free. Below about 60 the gun fills the screen;
        /// above 120 the projection distorts badly enough at the edges that
        /// aiming gets worse, not better.
        /// </summary>
        public static int FieldOfView
        {
            get => _fieldOfView;
            set => _fieldOfView = Math.Clamp(value, MinFov, MaxFov);
        }

        private static int _fieldOfView = DefaultFov;

        /// <summary>What the DS game plays at: NormalFov 39, doubled.</summary>
        public const int DefaultFov = 78;

        public const int MinFov = 60;
        public const int MaxFov = 120;

        /// <summary>
        /// Scale a camera-authored FOV into the player's selected FOV while
        /// preserving the camera's original zoom ratio.
        ///
        /// Perspective zoom is proportional to 1 / tan(FOV / 2), not to the
        /// angle in degrees. Scaling degrees directly made scopes progressively
        /// lose their intended magnification as the world FOV increased.
        /// </summary>
        public static float ScaleCameraFov(float cameraFov)
        {
            cameraFov = Math.Clamp(cameraFov, 1f, 175f);
            float cameraHalfTan = HalfTan(cameraFov);
            float defaultHalfTan = HalfTan(DefaultFov);
            float selectedHalfTan = HalfTan(_fieldOfView);
            float scaledHalfTan = cameraHalfTan * selectedHalfTan / defaultHalfTan;
            float radians = 2f * MathF.Atan(scaledHalfTan);
            return Math.Clamp(radians * 180f / MathF.PI, 1f, 175f);
        }

        private static float HalfTan(float degrees)
        {
            return MathF.Tan(degrees * MathF.PI / 360f);
        }

        public static int ParseFov(string? value, int fallback)
        {
            return Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int parsed) ? Math.Clamp(parsed, MinFov, MaxFov) : fallback;
        }

        /// <summary>Per-vertex lighting. Off is flatter and cheaper.</summary>
        public static bool Lighting { get; set; } = true;

        /// <summary>
        /// Cel shading: every surface goes to flat colour and the shapes in
        /// the room are drawn around in ink.
        ///
        /// Two halves, and the first one is the one that was missing. The
        /// texture is not banded, it is *replaced*: each one is averaged to a
        /// single colour when it is uploaded and the fragment shader paints
        /// with that, keeping only the texel's alpha so cut-outs are still cut
        /// out. Banding a photograph of rubble only ever gives banded rubble.
        /// What is left -- the vertex colours and the lighting -- is then
        /// banded into <see cref="CelBands"/> steps of brightness, so a wall
        /// keeps its hue and it is the shading across it that goes to steps.
        ///
        /// The second half is <see cref="CelEdge"/>, a pass over the depth the
        /// scene left behind, which is what makes the picture read as drawn
        /// rather than merely posterised.
        /// </summary>
        public static bool CelShading { get; set; }

        /// <summary>
        /// Draw the frame rate over the game.
        ///
        /// Read every frame rather than copied when a scene is built, for the
        /// reason <see cref="Fog"/> and <see cref="Lighting"/> are: the
        /// settings window opens from the pause menu during a match, and a
        /// counter you cannot turn on while you are looking at the stutter is
        /// the wrong tool.
        /// </summary>
        public static bool ShowFps { get; set; }

        /// <summary>How many steps the shading is banded into, 2 to 8.</summary>
        public static int CelBands
        {
            get => _celBands;
            set => _celBands = Math.Clamp(value, 2, 8);
        }

        private static int _celBands = 8;

        /// <summary>
        /// How dark the ink line goes, 0 to 1. Zero is no outline at all, and
        /// the renderer then leaves the depth in the cheaper buffer that
        /// cannot be read back.
        ///
        /// Locked at 0.5 (50%) -- steps and outline strength are no longer
        /// player-configurable, only the on/off switch above is.
        /// </summary>
        public static float CelEdge
        {
            get => _celEdge;
            set => _celEdge = Math.Clamp(value, 0, 1);
        }

        private static float _celEdge = 0.5f;

        /// <summary>Distance fog, where the room asks for it.</summary>
        public static bool Fog { get; set; } = true;

        /// <summary>
        /// Linear texture filtering. The DS had none, so off is both faster and
        /// what the game looked like.
        /// </summary>
        public static bool TextureFiltering { get; set; }

        /// <summary>Apply a scale to one dimension, never below one pixel.</summary>
        public static int Scaled(int pixels)
        {
            if (_resolutionScale >= 100)
            {
                return Math.Max(1, pixels);
            }
            return Math.Max(1, pixels * _resolutionScale / 100);
        }

        public static bool ParseOnOff(string? value, bool fallback)
        {
            if (value == null)
            {
                return fallback;
            }
            string text = value.Trim().ToLowerInvariant();
            if (text == "on" || text == "true" || text == "yes")
            {
                return true;
            }
            if (text == "off" || text == "false" || text == "no")
            {
                return false;
            }
            return fallback;
        }

        public static string OnOff(bool value) => value ? "on" : "off";

        public static int ParseScale(string? value, int fallback)
        {
            if (value != null && Int32.TryParse(value.Trim().TrimEnd('%'),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent))
            {
                return Math.Clamp(percent, MinScale, 100);
            }
            return fallback;
        }

        /// <summary>Unclamped; the properties do their own clamping.</summary>
        public static int ParseInt(string? value, int fallback)
        {
            if (value != null && Int32.TryParse(value.Trim().TrimEnd('%'),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            {
                return number;
            }
            return fallback;
        }
    }
}
