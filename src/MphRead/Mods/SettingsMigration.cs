using System;
using System.Collections.Generic;
using System.Globalization;
using MphRead.Mods.Render;

namespace MphRead.Mods
{
    /// <summary>
    /// Versioned normalization for settings.json.
    ///
    /// Settings survive upgrades by design. That is useful until a setting's
    /// range or meaning changes: an old value can then make a new build behave
    /// very differently from a clean install. Every load comes through here so
    /// old files are upgraded deliberately rather than accumulating folklore.
    /// </summary>
    public static class SettingsMigration
    {
        public const int CurrentSchema = 2;

        public static bool Apply(MenuSettings settings, out string summary)
        {
            var changed = new List<string>();
            int from = settings.SettingsSchemaVersion;

            settings.ResolutionScale = Normalize(settings.ResolutionScale,
                RenderOptions.ParseScale(settings.ResolutionScale, 100)
                    .ToString(CultureInfo.InvariantCulture), "render scale", changed);
            settings.FieldOfView = Normalize(settings.FieldOfView,
                RenderOptions.ParseFov(settings.FieldOfView, RenderOptions.DefaultFov)
                    .ToString(CultureInfo.InvariantCulture), "field of view", changed);
            settings.Lighting = Normalize(settings.Lighting,
                RenderOptions.OnOff(RenderOptions.ParseOnOff(settings.Lighting, true)),
                "lighting", changed);
            settings.Fog = Normalize(settings.Fog,
                RenderOptions.OnOff(RenderOptions.ParseOnOff(settings.Fog, true)),
                "fog", changed);
            settings.TextureFiltering = Normalize(settings.TextureFiltering,
                RenderOptions.OnOff(RenderOptions.ParseOnOff(settings.TextureFiltering, false)),
                "texture filtering", changed);
            settings.TextureMipmaps = Normalize(settings.TextureMipmaps,
                RenderOptions.OnOff(RenderOptions.ParseOnOff(settings.TextureMipmaps, false)),
                "mipmaps", changed);

            int aniso = Math.Clamp(RenderOptions.ParseInt(settings.TextureAnisotropy, 1), 1, 16);
            aniso = aniso >= 16 ? 16 : aniso >= 8 ? 8 : aniso >= 4 ? 4 : aniso >= 2 ? 2 : 1;
            settings.TextureAnisotropy = Normalize(settings.TextureAnisotropy, aniso.ToString(CultureInfo.InvariantCulture),
                "anisotropy", changed);

            settings.ShowFps = Normalize(settings.ShowFps,
                RenderOptions.OnOff(RenderOptions.ParseOnOff(settings.ShowFps, false)),
                "fps counter", changed);
            settings.SmoothNativeHud = Normalize(settings.SmoothNativeHud,
                RenderOptions.OnOff(RenderOptions.ParseOnOff(settings.SmoothNativeHud, true)),
                "native HUD sampling", changed);

            int cap = FrameTiming.ParseCap(settings.FrameRateCap, FrameTiming.DisplayRate);
            settings.FrameRateCap = Normalize(settings.FrameRateCap, FrameTiming.CapString(cap), "fps limit", changed);

            settings.CelShading = Normalize(settings.CelShading,
                RenderOptions.OnOff(RenderOptions.ParseOnOff(settings.CelShading, false)),
                "cel shading", changed);
            settings.CelBands = Normalize(settings.CelBands,
                Math.Clamp(RenderOptions.ParseInt(settings.CelBands, 8), 2, 8)
                    .ToString(CultureInfo.InvariantCulture), "cel bands", changed);
            settings.CelEdge = Normalize(settings.CelEdge,
                Math.Clamp(RenderOptions.ParseInt(settings.CelEdge, 50), 0, 100)
                    .ToString(CultureInfo.InvariantCulture), "cel edge", changed);

            settings.GraphicsPreset = NormalizeEnum(settings.GraphicsPreset,
                GraphicsPreset.Original, "graphics preset", changed);
            settings.AntiAliasing = NormalizeEnum(settings.AntiAliasing,
                AntiAliasingMode.Off, "anti-aliasing", changed);
            settings.SharpenStrength = Normalize(settings.SharpenStrength,
                Math.Clamp(RenderOptions.ParseInt(settings.SharpenStrength, 0), 0, 100)
                    .ToString(CultureInfo.InvariantCulture), "sharpening", changed);
            settings.Bloom = NormalizeToggle(settings.Bloom, false, "bloom", changed);
            settings.BloomIntensity = Normalize(settings.BloomIntensity,
                Math.Clamp(RenderOptions.ParseInt(settings.BloomIntensity, 60), 0, 150)
                    .ToString(CultureInfo.InvariantCulture), "bloom intensity", changed);
            settings.ColorGrade = NormalizeEnum(settings.ColorGrade,
                ColorGradeProfile.Original, "color grade", changed);
            settings.Gamma = Normalize(settings.Gamma,
                Math.Clamp(RenderOptions.ParseInt(settings.Gamma, 100), 50, 150)
                    .ToString(CultureInfo.InvariantCulture), "gamma", changed);
            settings.Contrast = Normalize(settings.Contrast,
                Math.Clamp(RenderOptions.ParseInt(settings.Contrast, 100), 50, 150)
                    .ToString(CultureInfo.InvariantCulture), "contrast", changed);
            settings.Saturation = Normalize(settings.Saturation,
                Math.Clamp(RenderOptions.ParseInt(settings.Saturation, 100), 0, 200)
                    .ToString(CultureInfo.InvariantCulture), "saturation", changed);
            settings.EnhancedLighting = NormalizeToggle(settings.EnhancedLighting, false,
                "enhanced lighting", changed);
            settings.AmbientOcclusion = NormalizeEnum(settings.AmbientOcclusion,
                AmbientOcclusionQuality.Off, "ambient occlusion", changed);
            settings.ContactShadows = NormalizeToggle(settings.ContactShadows, false,
                "contact shadows", changed);
            settings.EnhancedFog = NormalizeToggle(settings.EnhancedFog, false,
                "enhanced fog", changed);
            settings.VolumetricFog = NormalizeToggle(settings.VolumetricFog, false,
                "volumetric fog", changed);
            settings.InternalHdr = NormalizeToggle(settings.InternalHdr, false,
                "HDR tone mapping", changed);
            settings.Reflections = NormalizeToggle(settings.Reflections, false,
                "reflections", changed);
            settings.DynamicGlow = NormalizeToggle(settings.DynamicGlow, false,
                "dynamic glow", changed);
            settings.TextureReplacements = NormalizeToggle(settings.TextureReplacements, false,
                "HD texture replacements", changed);

            settings.SfxVolume = NormalizeVolume(settings.SfxVolume, 0.35f, "sfx volume", changed);
            settings.MusicVolume = NormalizeVolume(settings.MusicVolume, 0.50f, "music volume", changed);

            if (settings.SettingsSchemaVersion != CurrentSchema)
            {
                settings.SettingsSchemaVersion = CurrentSchema;
            }

            summary = from == CurrentSchema && changed.Count == 0
                ? ""
                : $"settings schema {from} -> {CurrentSchema}"
                    + (changed.Count == 0 ? "" : $"; normalized {String.Join(", ", changed)}");
            return from != CurrentSchema || changed.Count > 0;
        }

        public static void ResetPerformance(MenuSettings settings)
        {
            settings.ResolutionScale = "100";
            settings.FieldOfView = RenderOptions.DefaultFov.ToString(CultureInfo.InvariantCulture);
            settings.Lighting = "on";
            settings.Fog = "on";
            settings.TextureFiltering = "off";
            settings.TextureMipmaps = "off";
            settings.TextureAnisotropy = "1";
            settings.ShowFps = "off";
            settings.SmoothNativeHud = "on";
            settings.FrameRateCap = "display";
            settings.CelShading = "off";
            settings.CelBands = "8";
            settings.CelEdge = "50";
            settings.GraphicsPreset = "original";
            settings.AntiAliasing = "off";
            settings.SharpenStrength = "0";
            settings.Bloom = "off";
            settings.BloomIntensity = "60";
            settings.ColorGrade = "original";
            settings.Gamma = "100";
            settings.Contrast = "100";
            settings.Saturation = "100";
            settings.EnhancedLighting = "off";
            settings.AmbientOcclusion = "off";
            settings.ContactShadows = "off";
            settings.EnhancedFog = "off";
            settings.VolumetricFog = "off";
            settings.InternalHdr = "off";
            settings.Reflections = "off";
            settings.DynamicGlow = "off";
            settings.TextureReplacements = "off";
            settings.SettingsSchemaVersion = CurrentSchema;
        }

        private static string NormalizeToggle(string value, bool fallback, string name,
            List<string> changed)
        {
            return Normalize(value,
                RenderOptions.OnOff(RenderOptions.ParseOnOff(value, fallback)), name, changed);
        }

        private static string NormalizeEnum<T>(string value, T fallback, string name,
            List<string> changed) where T : struct, Enum
        {
            T normalized = Enum.TryParse(value, true, out T parsed)
                && Enum.IsDefined(typeof(T), parsed) ? parsed : fallback;
            return Normalize(value, normalized.ToString().ToLowerInvariant(), name, changed);
        }

        private static string NormalizeVolume(string value, float fallback, string name,
            List<string> changed)
        {
            float normalized = Single.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                out float parsed) ? Math.Clamp(parsed, 0, 1) : fallback;
            return Normalize(value, normalized.ToString("0.###", CultureInfo.InvariantCulture), name, changed);
        }

        private static string Normalize(string value, string normalized, string name,
            List<string> changed)
        {
            if (!String.Equals(value, normalized, StringComparison.OrdinalIgnoreCase))
            {
                value = normalized;
                changed.Add(name);
            }
            return value;
        }
    }
}
