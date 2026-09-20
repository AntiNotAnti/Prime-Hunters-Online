using System;
using System.Threading;
using System.Threading.Tasks;
using MphRead.Mods;
using MphRead.Mods.Render;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;

namespace MphRead.Droid
{
    /// <summary>
    /// The real hunter model, for a launcher with no game window under it.
    ///
    /// <para>
    /// The desktop draws it straight into the back buffer beneath its screens
    /// (<c>Mods/Render/LauncherHunter.cs</c>, which is excluded from this
    /// project because it wants a <c>RenderWindow</c>). Here the screens are a
    /// real Avalonia view with nothing behind them, so the model is rendered
    /// into a pbuffer on a thread of its own and handed over as pixels.
    /// </para>
    ///
    /// <para>
    /// <b>Once per hunter and suit, not once a frame.</b> The model the
    /// desktop draws stands still facing the camera, so a picture is the whole
    /// of it -- and a render here is a room's worth of setup cost on a phone.
    /// The stand asks again only when the picker moves.
    /// </para>
    ///
    /// <para>
    /// <b>One thread, one context, one scene, kept.</b> Standing a scene up
    /// means compiling the shaders and cutting a framebuffer; the picker is
    /// turned a dozen times in a sitting. The scene has nothing in it -- no
    /// room, no players -- which is what <c>Scene.SideScene</c> is for, and it
    /// is never up at the same time as a match: a match owns the world in this
    /// process, so a request while one is running is refused rather than
    /// queued, and the stand keeps its boxes.
    /// </para>
    /// </summary>
    internal sealed class AndroidHunterShot : IHunterShot
    {
        /// <summary>
        /// The one this process installed, for the activity to put down when a
        /// match is about to take the world.
        /// </summary>
        internal static AndroidHunterShot? Current { get; private set; }

        private readonly object _gate = new();
        private Thread? _thread;
        private readonly SemaphoreSlim _work = new(0);
        private Job? _next;
        private bool _failed;
        private volatile bool _retire;

        public AndroidHunterShot()
        {
            Current = this;
        }

        /// <summary>
        /// Let the scene and the context go, and wait for the thread to have
        /// done it.
        ///
        /// One process holds one world, and more than that: the hunter's
        /// textures and display lists are cut in *this* context and their ids
        /// are written onto the <c>Model</c> the reader caches, which the
        /// match then draws from in a context of its own. So they have to be
        /// given back before a match starts rather than merely left alone.
        /// </summary>
        internal void Retire()
        {
            Thread? thread;
            lock (_gate)
            {
                thread = _thread;
                if (thread == null)
                {
                    return;
                }
                _next?.Done.TrySetResult(null);
                _next = null;
                _retire = true;
            }
            _work.Release();
            // Short: what it is waiting for is one DoCleanup. A match that
            // starts anyway is the same risk this is here to remove, so the
            // wait is not optional -- only bounded.
            thread.Join(TimeSpan.FromSeconds(4));
        }

        private sealed class Job
        {
            public Hunter Hunter;
            public int Suit;
            public int Width;
            public int Height;
            public readonly TaskCompletionSource<byte[]?> Done =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task<byte[]?> RenderAsync(Hunter hunter, int suit, int width, int height)
        {
            if (_failed || width <= 0 || height <= 0 || MainActivity.Instance?.InMatch == true)
            {
                return Task.FromResult<byte[]?>(null);
            }
            var job = new Job
            {
                Hunter = hunter,
                Suit = Math.Clamp(suit, 0, 3),
                Width = width,
                Height = height
            };
            Job? dropped;
            lock (_gate)
            {
                // Only the newest is worth rendering: the picker is turned
                // faster than a render takes, and every intermediate hunter is
                // one nobody was still looking at by the time it was ready.
                dropped = _next;
                _next = job;
                _retire = false;
                if (_thread == null)
                {
                    _thread = new Thread(Loop)
                    {
                        IsBackground = true,
                        Name = "hunter preview"
                    };
                    _thread.Start();
                }
            }
            dropped?.Done.TrySetResult(null);
            _work.Release();
            return job.Done.Task;
        }

        private void Loop()
        {
            OffscreenGl? gl = null;
            Scene? scene = null;
            int width = 0, height = 0;
            try
            {
                while (true)
                {
                    _work.Wait();
                    if (_retire)
                    {
                        break;
                    }
                    Job? job;
                    lock (_gate)
                    {
                        job = _next;
                        _next = null;
                    }
                    if (job == null)
                    {
                        continue;
                    }
                    if (MainActivity.Instance?.InMatch == true)
                    {
                        job.Done.TrySetResult(null);
                        continue;
                    }
                    if (gl == null)
                    {
                        gl = OffscreenGl.Create(job.Width, job.Height);
                        EsBindings.Load();
                        GlEs.Reset();
                    }
                    if (scene == null || width != job.Width || height != job.Height)
                    {
                        // A pbuffer cannot be resized, and the scene's own
                        // target follows the size it was told. Both are cheap
                        // enough to cut again on the rare size change -- the
                        // stand rounds its request, so this is the drawer
                        // opening and the screen turning, not every frame.
                        scene?.DoCleanup();
                        scene = null;
                        if (width != 0)
                        {
                            gl.Dispose();
                            gl = OffscreenGl.Create(job.Width, job.Height);
                            EsBindings.Load();
                            GlEs.Reset();
                        }
                        width = job.Width;
                        height = job.Height;
                        var input = new AndroidInput();
                        scene = new Scene(new Vector2i(width, height),
                            input.Keyboard, input.Mouse, _ => { }, () => { })
                        {
                            SideScene = true
                        };
                        scene.OnLoad();
                        GL.Viewport(0, 0, width, height);
                        scene.OnResize();
                    }
                    job.Done.TrySetResult(Draw(scene, job, width, height));
                }
                // Asked to go: everything cut in this context is handed back
                // before the match's context is asked to draw the same models.
                lock (_gate)
                {
                    _thread = null;
                    _next?.Done.TrySetResult(null);
                    _next = null;
                }
                try
                {
                    scene?.DoCleanup();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[hunter] cleanup failed: {ex.Message}");
                }
                gl?.Dispose();
            }
            catch (Exception ex)
            {
                // Every failure here is the launcher's boxes, which is what
                // the stand draws when nothing hands it a picture.
                _failed = true;
                Console.WriteLine($"[hunter] the preview thread stopped: {ex}");
                DebugLog.Line("ui", $"the hunter preview is off: {ex.Message}");
                lock (_gate)
                {
                    _next?.Done.TrySetResult(null);
                    _next = null;
                    _thread = null;
                }
                try
                {
                    scene?.DoCleanup();
                }
                catch (Exception)
                {
                    // Going away either way.
                }
                gl?.Dispose();
            }
        }

        /// <summary>
        /// The pass the results screen and the desktop launcher both use, into
        /// the whole of the pbuffer, read back as BGRA the way a
        /// <c>WriteableBitmap</c> wants it.
        /// </summary>
        private static byte[]? Draw(Scene scene, Job job, int width, int height)
        {
            Scene.LauncherPreview = true;
            Scene.LauncherHunter = job.Hunter;
            Scene.LauncherSuit = job.Suit;
            Scene.PreviewWanted = true;
            Scene.PreviewLeft = 0;
            Scene.PreviewTop = 0;
            Scene.PreviewRight = 1;
            Scene.PreviewBottom = 1;
            bool drawn = false;
            // The model's textures and display lists are made on the first
            // step and its items on the one after, so the first call can
            // honestly have nothing to draw. Three is the whole of it.
            for (int i = 0; i < 3 && !drawn; i++)
            {
                Scene.LauncherPreview = true;
                drawn = scene.ModDrawPreviewAlone(new Vector2i(width, height));
            }
            Scene.LauncherPreview = false;
            Scene.PreviewWanted = false;
            if (!drawn)
            {
                return null;
            }
            byte[] rgb = new byte[width * height * 3];
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.ReadPixels(0, 0, width, height, PixelFormat.Rgb, PixelType.UnsignedByte, rgb);
            byte[] bgra = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                // GL measures up from the bottom and a bitmap measures down
                // from the top.
                int from = (height - 1 - y) * width * 3;
                int to = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    bgra[to + 0] = rgb[from + 2];
                    bgra[to + 1] = rgb[from + 1];
                    bgra[to + 2] = rgb[from + 0];
                    bgra[to + 3] = 255;
                    from += 3;
                    to += 4;
                }
            }
            return bgra;
        }
    }
}
