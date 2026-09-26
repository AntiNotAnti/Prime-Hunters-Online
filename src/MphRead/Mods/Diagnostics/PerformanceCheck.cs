using System;
using System.IO;
using MphRead.Mods.Network;
using MphRead.Mods.Render;
using OpenTK.Mathematics;

namespace MphRead.Mods.Diagnostics
{
    /// <summary>
    /// Deterministic rendered client workload for before/after comparisons.
    /// It uses MapAudit's real Scene, bots and GL renderer, but disables its
    /// glReadPixels sampling so the benchmark measures gameplay rather than
    /// the diagnostic itself.
    /// </summary>
    public static class PerformanceCheck
    {
        public static int Run(string room, int players = 8, double seconds = 20,
            int presentationHz = 60, string? output = null)
        {
            int oldScale = RenderOptions.ResolutionScale;
            bool oldLighting = RenderOptions.Lighting;
            bool oldFog = RenderOptions.Fog;
            bool oldFiltering = RenderOptions.TextureFiltering;
            bool oldMipmaps = RenderOptions.TextureMipmaps;
            int oldAniso = RenderOptions.TextureAnisotropy;
            bool oldCel = RenderOptions.CelShading;
            bool oldFps = RenderOptions.ShowFps;
            int oldDrawRate = MapAudit.DrawRate;
            int oldPerformanceHz = MapAudit.PerformanceHz;
            Vector2i? oldSize = MapAudit.WindowSize;
            bool oldPerf = MapAudit.PerformanceMode;
            string? oldOutput = MapAudit.PerformanceOutput;
            try
            {
                // Fixed benchmark quality. User preferences are deliberately
                // not part of a regression comparison.
                RenderOptions.ResolutionScale = 100;
                RenderOptions.Lighting = true;
                RenderOptions.Fog = true;
                RenderOptions.TextureFiltering = false;
                RenderOptions.TextureMipmaps = false;
                RenderOptions.TextureAnisotropy = 1;
                RenderOptions.CelShading = false;
                RenderOptions.ShowFps = false;

                MapAudit.DrawRate = 1;
                MapAudit.PerformanceHz = Math.Clamp(presentationHz, 60, 240);
                MapAudit.WindowSize = new Vector2i(1920, 1080);
                MapAudit.PerformanceMode = true;
                MapAudit.PerformanceOutput = output ?? DefaultOutput(MapAudit.PerformanceHz);
                return MapAudit.Run(room, Math.Clamp(players, 1, MphRead.Entities.PlayerEntity.SlotCapacity),
                    Math.Clamp(seconds, 5, 120), GameMode.Battle, bots: true);
            }
            finally
            {
                RenderOptions.ResolutionScale = oldScale;
                RenderOptions.Lighting = oldLighting;
                RenderOptions.Fog = oldFog;
                RenderOptions.TextureFiltering = oldFiltering;
                RenderOptions.TextureMipmaps = oldMipmaps;
                RenderOptions.TextureAnisotropy = oldAniso;
                RenderOptions.CelShading = oldCel;
                RenderOptions.ShowFps = oldFps;
                MapAudit.DrawRate = oldDrawRate;
                MapAudit.PerformanceHz = oldPerformanceHz;
                MapAudit.WindowSize = oldSize;
                MapAudit.PerformanceMode = oldPerf;
                MapAudit.PerformanceOutput = oldOutput;
            }
        }

        private static string DefaultOutput(int presentationHz)
        {
            string root = Path.Combine(Launcher.LauncherPrefs.Directory, "perf");
            Directory.CreateDirectory(root);
            string name = $"perf-{Update.BuildVersion.Display.TrimStart('v')}-"
                + $"{DateTime.Now:yyyyMMdd-HHmmss}-{presentationHz}hz.json";
            return Path.Combine(root, name);
        }
    }
}
