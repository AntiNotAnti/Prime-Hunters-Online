using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Android.App;
using Android.Content;
using Android.OS;
using MphRead.Mods;
using MphRead.Mods.Launcher;

namespace MphRead.Droid
{
    /// <summary>
    /// One preview worker, in a process of its own.
    ///
    /// The desktop renders previews ten at a time by starting ten copies of
    /// itself, because a scene is not a local thing -- the entity lists, the
    /// player roster and the game state are static, so two scenes in one
    /// process would be one world with two cameras. That is just as true here,
    /// and an app *does* have a second process to start after all: a service
    /// declared with <c>android:process</c> gets its own, with its own runtime
    /// and its own statics.
    ///
    /// So the arrangement is the desktop's, with services where the desktop
    /// has child processes. Each is handed a share of the room list, renders
    /// it into the shared cache directory through
    /// <see cref="OffscreenGl"/> -- no window, no surface, nothing on screen --
    /// and drops a marker file when it is finished. The launcher watches the
    /// directory rather than talking to them, which needs no IPC and survives a
    /// worker being killed for memory.
    /// </summary>
    public abstract class PreviewService : Service
    {
        internal const string RoomsExtra = "rooms";
        internal const string MarkerExtra = "marker";
        internal const string WidthExtra = "width";
        internal const string HeightExtra = "height";

        public override IBinder? OnBind(Intent? intent) => null;

        public override StartCommandResult OnStartCommand(Intent? intent,
            StartCommandFlags flags, int startId)
        {
            string[]? rooms = intent?.GetStringArrayExtra(RoomsExtra);
            string? marker = intent?.GetStringExtra(MarkerExtra);
            int width = intent?.GetIntExtra(WidthExtra, PreviewRun.Width) ?? PreviewRun.Width;
            int height = intent?.GetIntExtra(HeightExtra, PreviewRun.Height) ?? PreviewRun.Height;
            if (rooms == null || rooms.Length == 0)
            {
                Finish(marker);
                StopSelf(startId);
                return StartCommandResult.NotSticky;
            }
            var thread = new Thread(() =>
            {
                try
                {
                    Run(rooms, width, height);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[preview worker] {GetType().Name} failed: {ex}");
                }
                finally
                {
                    Finish(marker);
                    StopSelf(startId);
                }
            });
            // A room's worth of models is parsed on this thread before anything
            // is drawn, and the default managed stack is not generous.
            thread.IsBackground = true;
            thread.Start();
            return StartCommandResult.NotSticky;
        }

        private void Run(string[] rooms, int width, int height)
        {
            // This process did not go through MainActivity, so nothing has told
            // it where the game files are. Same answer, same reasons: the
            // package's own directory is read-only, and upstream's Paths reads
            // paths.txt relative to the working directory.
            string root = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir?.AbsolutePath ?? "";
            if (root.Length > 0)
            {
                LauncherPrefs.Directory = root;
                GameFiles.Root = root;
                Directory.SetCurrentDirectory(root);
            }
            if (!GameFiles.Ready)
            {
                Console.WriteLine("[preview worker] no game files in this process; nothing to render");
                return;
            }
            GameFiles.ApplyPaths();
            ThumbnailGenerator.EnsureCacheDirectory();
            // STB ships no native for Android; the framework's encoder does.
            ScreenCapture.PngWriter = AndroidPng.Write;
            using var gl = OffscreenGl.Create(width, height);
            PreviewRun.Render(rooms, width, height,
                line => Console.WriteLine(line));
        }

        private static void Finish(string? marker)
        {
            if (string.IsNullOrEmpty(marker))
            {
                return;
            }
            try
            {
                File.WriteAllText(marker, "done");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[preview worker] could not write {marker}: {ex.Message}");
            }
        }
    }

    // One class per process: android:process is an attribute of the declaration,
    // not of the intent, so the number of workers is the number of declarations.
    // Ten, the same as the desktop batch; how many actually start is chosen at
    // run time from the device's cores and heap, since each one is a runtime, a
    // GL context and a room's textures.
    [Service(Name = "fr.livetek.fruityprime.PreviewWorker0", Process = ":preview0", Exported = false)]
    public sealed class PreviewWorker0 : PreviewService { }

    [Service(Name = "fr.livetek.fruityprime.PreviewWorker1", Process = ":preview1", Exported = false)]
    public sealed class PreviewWorker1 : PreviewService { }

    [Service(Name = "fr.livetek.fruityprime.PreviewWorker2", Process = ":preview2", Exported = false)]
    public sealed class PreviewWorker2 : PreviewService { }

    [Service(Name = "fr.livetek.fruityprime.PreviewWorker3", Process = ":preview3", Exported = false)]
    public sealed class PreviewWorker3 : PreviewService { }

    [Service(Name = "fr.livetek.fruityprime.PreviewWorker4", Process = ":preview4", Exported = false)]
    public sealed class PreviewWorker4 : PreviewService { }

    [Service(Name = "fr.livetek.fruityprime.PreviewWorker5", Process = ":preview5", Exported = false)]
    public sealed class PreviewWorker5 : PreviewService { }

    [Service(Name = "fr.livetek.fruityprime.PreviewWorker6", Process = ":preview6", Exported = false)]
    public sealed class PreviewWorker6 : PreviewService { }

    [Service(Name = "fr.livetek.fruityprime.PreviewWorker7", Process = ":preview7", Exported = false)]
    public sealed class PreviewWorker7 : PreviewService { }

    [Service(Name = "fr.livetek.fruityprime.PreviewWorker8", Process = ":preview8", Exported = false)]
    public sealed class PreviewWorker8 : PreviewService { }

    [Service(Name = "fr.livetek.fruityprime.PreviewWorker9", Process = ":preview9", Exported = false)]
    public sealed class PreviewWorker9 : PreviewService { }

    internal static class PreviewWorkerTypes
    {
        public static readonly IReadOnlyList<Type> All = new[]
        {
            typeof(PreviewWorker0), typeof(PreviewWorker1), typeof(PreviewWorker2),
            typeof(PreviewWorker3), typeof(PreviewWorker4), typeof(PreviewWorker5),
            typeof(PreviewWorker6), typeof(PreviewWorker7), typeof(PreviewWorker8),
            typeof(PreviewWorker9)
        };
    }
}
