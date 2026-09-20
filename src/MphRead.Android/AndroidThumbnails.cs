using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using MphRead.Mods;

namespace MphRead.Droid
{
    /// <summary>
    /// Map previews, rendered on the phone, in the background, several at once.
    ///
    /// Three things were wrong with doing it on a <c>GLSurfaceView</c>: the
    /// player watched every room load and unload, the screen was forced to
    /// landscape while it happened, and it was one room at a time. This is the
    /// desktop's arrangement instead -- a batch of worker processes, watched
    /// through the cache directory they write into -- with
    /// <see cref="PreviewService"/> where the desktop starts copies of itself.
    ///
    /// **Nothing is shipped.** Every preview is rendered on the device from the
    /// player's own extracted files, into
    /// <see cref="ThumbnailGenerator.CacheDirectory"/> beside them.
    /// </summary>
    internal sealed class AndroidThumbnailHost : IThumbnailHost
    {
        public Task<int> RenderAsync(IReadOnlyList<string> rooms, Action<string> report)
        {
            // A custom map has no picture until it has binaries to render.
            AndroidMaps.EnsureBuilt();
            // Installed from MainApplication.CustomizeAppBuilder, before any
            // activity exists, so the activity itself has to be looked up
            // when this is actually asked to render rather than captured then.
            return MainActivity.Instance!.RenderPreviews(rooms, report);
        }
    }

    internal static class PreviewWorkers
    {
        /// <summary>
        /// How many to start.
        ///
        /// Ten at most, the same as the desktop batch, unless the device says
        /// it cannot: each worker is a runtime, a GL context and a room's
        /// textures, and a phone that runs out kills them rather than slowing
        /// down. A killed worker costs its share of the rooms, which the
        /// in-process pass afterwards picks up -- one room at a time, which is
        /// the slowest path there is, so a worker killed is worse than a
        /// worker never started.
        ///
        /// One per core, not two. The desktop measured the same batch at 5.2 s
        /// on ten workers against 4.1 s on eight, on an eight-core box
        /// (<see cref="ThumbnailBatch.DefaultParallelism"/>), and a phone has
        /// every reason the desktop had and two of its own: one GPU that
        /// serialises the drawing whatever the core count says, and half those
        /// cores are little ones.
        /// </summary>
        public static int Count(Context context)
        {
            int cores = Math.Max(1, Java.Lang.Runtime.GetRuntime()?.AvailableProcessors() ?? 1);
            int heapMb = 128;
            if (context.GetSystemService(Context.ActivityService) is ActivityManager manager)
            {
                heapMb = Math.Max(32, manager.MemoryClass);
            }
            return Math.Clamp(Math.Min(cores, heapMb / 24), 1, PreviewWorkerTypes.All.Count);
        }

        /// <summary>
        /// Hand the rooms out, start the workers, and watch the directory until
        /// they are done. Returns how many of the asked-for rooms now have a
        /// picture.
        /// </summary>
        public static int Run(Context context, IReadOnlyList<string> rooms, int width, int height,
            Action<string> report)
        {
            int workers = Math.Min(Count(context), rooms.Count);
            var shares = new List<string>[workers];
            for (int i = 0; i < workers; i++)
            {
                shares[i] = new List<string>();
            }
            // Round robin rather than contiguous blocks: the rooms are sorted by
            // name and their sizes are not, so one worker would otherwise get
            // every big one.
            for (int i = 0; i < rooms.Count; i++)
            {
                shares[i % workers].Add(rooms[i]);
            }
            var markers = new List<string>();
            int started = 0;
            for (int i = 0; i < workers; i++)
            {
                string marker = Path.Combine(ThumbnailGenerator.CacheDirectory, $".worker{i}.done");
                TryDelete(marker);
                var intent = new Intent(context, PreviewWorkerTypes.All[i]);
                intent.PutExtra(PreviewService.RoomsExtra, shares[i].ToArray());
                intent.PutExtra(PreviewService.MarkerExtra, marker);
                intent.PutExtra(PreviewService.WidthExtra, width);
                intent.PutExtra(PreviewService.HeightExtra, height);
                try
                {
                    context.StartService(intent);
                }
                catch (Exception ex)
                {
                    // Android refuses a service start from the background, and
                    // an OEM build may refuse one for its own reasons. Either
                    // way the rooms this worker was given are simply still
                    // missing, and the caller renders them itself.
                    Console.WriteLine($"[thumbnails] worker {i} would not start: {ex.Message}");
                    continue;
                }
                markers.Add(marker);
                started++;
            }
            if (started == 0)
            {
                return 0;
            }
            report($"[thumbnails] rendering {rooms.Count} preview(s) in the background, "
                + $"{started} at a time");
            Watch(rooms, markers, report);
            return CountWritten(rooms);
        }

        /// <summary>
        /// How long the directory may stand still before the workers are given
        /// up on.
        ///
        /// The limit used to be measured from the start -- 90 seconds plus 30 a
        /// room -- and that is the wrong clock for the one case it exists for.
        /// A worker the system kills for memory never writes its marker, so a
        /// batch that lost one waited out the *whole* allowance, which for
        /// twenty-seven rooms is a quarter of an hour of watching a directory
        /// nothing is writing to, before the in-process pass could pick its
        /// rooms up. A picture lands every second or two while anything is
        /// alive; a minute of silence is every worker gone.
        /// </summary>
        private const double StallSeconds = 60;

        /// <summary>
        /// Watch the cache directory fill up, reporting as it does. Returns
        /// when every picture is there, when every worker has said it is done,
        /// or when nothing has arrived for <see cref="StallSeconds"/>.
        /// </summary>
        private static void Watch(IReadOnlyList<string> rooms, List<string> markers,
            Action<string> report)
        {
            var stall = Stopwatch.StartNew();
            int last = -1;
            while (true)
            {
                int written = CountWritten(rooms);
                if (written != last)
                {
                    last = written;
                    stall.Restart();
                    report($"[thumbnails] {written}/{rooms.Count}");
                }
                // Every picture asked for is on disk. Whether the workers have
                // got round to saying so is not worth another second.
                if (written >= rooms.Count)
                {
                    return;
                }
                bool allDone = true;
                for (int i = 0; i < markers.Count; i++)
                {
                    if (!File.Exists(markers[i]))
                    {
                        allDone = false;
                        break;
                    }
                }
                if (allDone)
                {
                    return;
                }
                if (stall.Elapsed.TotalSeconds >= StallSeconds)
                {
                    report("[thumbnails] the background workers stopped answering; "
                        + "rendering the rest here");
                    return;
                }
                Thread.Sleep(500);
            }
        }

        private static int CountWritten(IReadOnlyList<string> rooms)
        {
            int written = 0;
            for (int i = 0; i < rooms.Count; i++)
            {
                if (ThumbnailGenerator.Exists(rooms[i]))
                {
                    written++;
                }
            }
            return written;
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // Nothing to delete, or a directory that is about to be created.
            }
        }
    }
}
