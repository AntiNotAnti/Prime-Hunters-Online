using System;
using System.Globalization;

namespace MphRead.Mods
{
    public enum PlayerSkinStyle
    {
        Solid,
        Textured,
        HighContrastTextured
    }

    public enum PlayerOutlineStyle
    {
        Off,
        Team,
        Red
    }

    public enum GraphicsPreset
    {
        Original,
        Performance,
        Enhanced,
        Ultra,
        Extreme,
        Custom
    }

    public enum AntiAliasingMode
    {
        Off,
        Fxaa,
        FxaaHigh
    }

    public enum AmbientOcclusionQuality
    {
        Off,
        Low,
        Medium,
        High
    }

    public enum ColorGradeProfile
    {
        Original,
        Enhanced,
        Vibrant,
        Cinematic
    }

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
        /// Percent of the window the 3D scene is rendered at, 25 to 300.
        /// Halving it quarters the pixels; values above 100 supersample the
        /// world before it is downsampled to the display.
        /// </summary>
        public static int ResolutionScale
        {
            get => _resolutionScale;
            set => _resolutionScale = Math.Clamp(value, MinScale, MaxScale);
        }

        private static int _resolutionScale = 100;

        public const int MinScale = 25;
        /// <summary>300% is 3x per axis / 9x the shaded pixels. This is intentionally an extreme ceiling.</summary>
        public const int MaxScale = 300;

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

        /// <summary>High-contrast multiplayer body colors, local to this client.</summary>
        public static bool BrightSkins { get; set; }

        /// <summary>Solid retains the original bright-skins preference behavior.</summary>
        public static PlayerSkinStyle BrightSkinStyle { get; set; } = PlayerSkinStyle.Solid;

        /// <summary>Independent of skin highlighting; off by default.</summary>
        public static PlayerOutlineStyle PlayerOutline { get; set; } = PlayerOutlineStyle.Off;

        /// <summary>Player outline thickness in screen pixels.</summary>
        public static int PlayerOutlineWidth
        {
            get => _playerOutlineWidth;
            set => _playerOutlineWidth = Math.Clamp(value, 1, 8);
        }

        private static int _playerOutlineWidth = 4;

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

        /// <summary>
        /// Linearly sample native DS HUD sprites when they are scaled to a
        /// modern framebuffer. Text and the supersampled Pro HUD keep their own
        /// sampling paths; this mainly softens the stock reticle, meters and
        /// weapon-menu art without changing their authored geometry.
        /// </summary>
        public static bool SmoothNativeHud { get; set; } = true;

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
        /// Exposed on the Graphics page so the outline can range from disabled
        /// to a heavy ink pass without changing the underlying cel algorithm.
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

        /// <summary>
        /// Build and use mip chains for world textures while filtering is on.
        /// This is independent from bilinear filtering so players can choose
        /// the cheaper single-level path or full trilinear minification.
        /// </summary>
        public static bool TextureMipmaps { get; set; }

        /// <summary>
        /// Requested anisotropic filtering level for world textures. The
        /// renderer clamps this again to what the active GPU reports.
        /// </summary>
        public static int TextureAnisotropy
        {
            get => _textureAnisotropy;
            set => _textureAnisotropy = Math.Clamp(value, 1, 16);
        }

        private static int _textureAnisotropy = 1;

        /// <summary>
        /// Named bundles for the modern presentation path. Individual values remain
        /// authoritative so a saved preset can be edited into a custom setup.
        /// </summary>
        public static GraphicsPreset GraphicsPreset { get; set; } = GraphicsPreset.Original;

        public static AntiAliasingMode AntiAliasing { get; set; } = AntiAliasingMode.Off;

        /// <summary>Contrast-adaptive scene sharpening, 0..100 percent.</summary>
        public static int SharpenStrength
        {
            get => _sharpenStrength;
            set => _sharpenStrength = Math.Clamp(value, 0, 100);
        }
        private static int _sharpenStrength;

        public static bool Bloom { get; set; }
        public static int BloomIntensity
        {
            get => _bloomIntensity;
            set => _bloomIntensity = Math.Clamp(value, 0, 150);
        }
        private static int _bloomIntensity = 60;

        public static ColorGradeProfile ColorGrade { get; set; } = ColorGradeProfile.Original;
        public static int Gamma
        {
            get => _gamma;
            set => _gamma = Math.Clamp(value, 50, 150);
        }
        private static int _gamma = 100;
        public static int Contrast
        {
            get => _contrast;
            set => _contrast = Math.Clamp(value, 50, 150);
        }
        private static int _contrast = 100;
        public static int Saturation
        {
            get => _saturation;
            set => _saturation = Math.Clamp(value, 0, 200);
        }
        private static int _saturation = 100;

        /// <summary>
        /// Depth-derived per-pixel relief lighting. It complements the cartridge's
        /// authored vertex/material lighting without changing simulation or maps.
        /// </summary>
        public static bool EnhancedLighting { get; set; }
        public static AmbientOcclusionQuality AmbientOcclusion { get; set; } = AmbientOcclusionQuality.Off;
        public static bool ContactShadows { get; set; }
        public static bool EnhancedFog { get; set; }
        public static bool VolumetricFog { get; set; }
        public static bool InternalHdr { get; set; }
        public static bool Reflections { get; set; }
        public static bool DynamicGlow { get; set; }

        /// <summary>
        /// Look for user-supplied desktop texture replacements under
        /// texture-packs/default/&lt;model&gt;/. Missing files fall back to cartridge pixels.
        /// </summary>
        public static bool TextureReplacements { get; set; }

        public static bool NeedsReadableDepth => AmbientOcclusion != AmbientOcclusionQuality.Off
            || ContactShadows || EnhancedLighting || EnhancedFog || VolumetricFog || Reflections;

        public static bool PostProcessingEnabled => AntiAliasing != AntiAliasingMode.Off
            || SharpenStrength > 0 || Bloom || ColorGrade != ColorGradeProfile.Original
            || Gamma != 100 || Contrast != 100 || Saturation != 100
            || EnhancedLighting || AmbientOcclusion != AmbientOcclusionQuality.Off
            || ContactShadows || EnhancedFog || VolumetricFog || InternalHdr
            || Reflections || DynamicGlow;

        public static void ApplyGraphicsPreset(GraphicsPreset preset)
        {
            GraphicsPreset = preset;
            switch (preset)
            {
            case GraphicsPreset.Original:
                ResolutionScale = 100;
                Lighting = true; Fog = true;
                TextureFiltering = false; TextureMipmaps = false; TextureAnisotropy = 1;
                AntiAliasing = AntiAliasingMode.Off; SharpenStrength = 0;
                Bloom = false; BloomIntensity = 60;
                ColorGrade = ColorGradeProfile.Original; Gamma = Contrast = Saturation = 100;
                EnhancedLighting = false; AmbientOcclusion = AmbientOcclusionQuality.Off;
                ContactShadows = false; EnhancedFog = false; VolumetricFog = false;
                InternalHdr = false; Reflections = false; DynamicGlow = false;
                break;
            case GraphicsPreset.Performance:
                ResolutionScale = 85;
                Lighting = true; Fog = true;
                TextureFiltering = true; TextureMipmaps = true; TextureAnisotropy = 4;
                AntiAliasing = AntiAliasingMode.Fxaa; SharpenStrength = 20;
                Bloom = false; BloomIntensity = 45;
                ColorGrade = ColorGradeProfile.Enhanced; Gamma = 100; Contrast = 104; Saturation = 106;
                EnhancedLighting = false; AmbientOcclusion = AmbientOcclusionQuality.Off;
                ContactShadows = false; EnhancedFog = true; VolumetricFog = false;
                InternalHdr = false; Reflections = false; DynamicGlow = false;
                break;
            case GraphicsPreset.Enhanced:
                ResolutionScale = 100;
                Lighting = true; Fog = true;
                TextureFiltering = true; TextureMipmaps = true; TextureAnisotropy = 16;
                AntiAliasing = AntiAliasingMode.FxaaHigh; SharpenStrength = 25;
                Bloom = true; BloomIntensity = 60;
                ColorGrade = ColorGradeProfile.Enhanced; Gamma = 100; Contrast = 108; Saturation = 112;
                EnhancedLighting = true; AmbientOcclusion = AmbientOcclusionQuality.Medium;
                ContactShadows = true; EnhancedFog = true; VolumetricFog = false;
                InternalHdr = false; Reflections = false; DynamicGlow = true;
                break;
            case GraphicsPreset.Ultra:
                ResolutionScale = 150;
                Lighting = true; Fog = true;
                TextureFiltering = true; TextureMipmaps = true; TextureAnisotropy = 16;
                AntiAliasing = AntiAliasingMode.FxaaHigh; SharpenStrength = 18;
                Bloom = true; BloomIntensity = 80;
                ColorGrade = ColorGradeProfile.Cinematic; Gamma = 100; Contrast = 110; Saturation = 115;
                EnhancedLighting = true; AmbientOcclusion = AmbientOcclusionQuality.High;
                ContactShadows = true; EnhancedFog = true; VolumetricFog = true;
                InternalHdr = true; Reflections = true; DynamicGlow = true;
                break;
            case GraphicsPreset.Extreme:
                ResolutionScale = MaxScale;
                Lighting = true; Fog = true;
                TextureFiltering = true; TextureMipmaps = true; TextureAnisotropy = 16;
                AntiAliasing = AntiAliasingMode.FxaaHigh; SharpenStrength = 12;
                Bloom = true; BloomIntensity = 95;
                ColorGrade = ColorGradeProfile.Cinematic; Gamma = 100; Contrast = 112; Saturation = 118;
                EnhancedLighting = true; AmbientOcclusion = AmbientOcclusionQuality.High;
                ContactShadows = true; EnhancedFog = true; VolumetricFog = true;
                InternalHdr = true; Reflections = true; DynamicGlow = true;
                break;
            case GraphicsPreset.Custom:
                break;
            }
        }

        /// <summary>Apply a scale to one dimension, never below one pixel.</summary>
        public static int Scaled(int pixels)
        {
            if (_resolutionScale == 100)
            {
                return Math.Max(1, pixels);
            }
            return Math.Max(1, (int)Math.Round(pixels * (_resolutionScale / 100d)));
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
                return Math.Clamp(percent, MinScale, MaxScale);
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
