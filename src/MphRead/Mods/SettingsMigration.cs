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
        public const int CurrentSchema = 1;

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
            settings.SettingsSchemaVersion = CurrentSchema;
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
