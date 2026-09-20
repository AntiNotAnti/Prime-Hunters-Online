using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MphRead.Mods
{
    /// <summary>
    /// Whoever can render map previews on this platform.
    ///
    /// The desktop spawns one worker process per room (see
    /// <see cref="ThumbnailBatch"/>), because GLFW wants its windows on the
    /// main thread and <c>Scene.AddRoom</c> takes one room per scene. An
    /// Android app has no second process to start, so the head there installs
    /// its own: the same capture, on the GL thread it already owns.
    ///
    /// The point of the seam is that the launcher does not know which it got.
    /// No preview is ever shipped -- every picture is rendered on the machine
    /// it is shown on, from that machine's own extracted files.
    /// </summary>
    public interface IThumbnailHost
    {
        /// <summary>Render these rooms, reporting a line each. Returns how many were written.</summary>
        Task<int> RenderAsync(IReadOnlyList<string> rooms, Action<string> report);
    }

    public static class ThumbnailHost
    {
        /// <summary>Set by a head that cannot use the worker-process batch.</summary>
        public static IThumbnailHost? Current { get; set; }

        /// <summary>True when previews can be rendered here at all.</summary>
        public static bool CanRender => Current != null || ThumbnailBatch.CanRun;

        /// <summary>Render whatever is missing, whichever way this platform can.</summary>
        public static async Task<int> RenderMissingAsync(Action<string> report)
        {
            IReadOnlyList<string> missing = ThumbnailGenerator.MissingThumbnails();
            if (missing.Count == 0)
            {
                return 0;
            }
            IThumbnailHost? host = Current;
            if (host != null)
            {
                return await host.RenderAsync(missing, report);
            }
            if (!ThumbnailBatch.CanRun)
            {
                return 0;
            }
            return await Task.Run(() => ThumbnailBatch.Run(missing,
                ThumbnailBatch.DefaultParallelism, ThumbnailGenerator.ThumbnailWidth,
                ThumbnailGenerator.ThumbnailHeight, report));
        }
    }
}
