using System;
using Android.Opengl;

namespace MphRead.Droid
{
    /// <summary>
    /// A GL ES context on an ordinary thread, drawing to nothing.
    ///
    /// <c>GLSurfaceView</c> is the only context this head had, and it comes
    /// with a view attached: previews rendered into a surface the player was
    /// looking at, on top of the launcher, with the screen forced to landscape
    /// for the duration. None of that is wanted -- a preview is a file being
    /// written, not something to watch.
    ///
    /// EGL will hand out a context bound to a pbuffer instead, which is a
    /// surface with no window behind it, on any thread that asks. The scene
    /// never draws to it anyway: <see cref="Scene.ReadSceneTarget"/> reads the
    /// framebuffer object the renderer owns, so the pbuffer exists only
    /// because EGL will not make a context current without one.
    /// </summary>
    internal sealed class OffscreenGl : IDisposable
    {
        // EGL_OPENGL_ES3_BIT_KHR. EGL14 exposes the ES2 bit and stops there,
        // and an ES2 config will happily give an ES3 context on most drivers --
        // but "most" is how a phone gets a context that fails every call in
        // GlEs with no message.
        private const int OpenGlEs3Bit = 0x40;

        private EGLDisplay? _display;
        private EGLSurface? _surface;
        private EGLContext? _context;

        public static OffscreenGl Create(int width, int height)
        {
            var gl = new OffscreenGl();
            try
            {
                gl.Init(width, height);
            }
            catch
            {
                gl.Dispose();
                throw;
            }
            return gl;
        }

        private void Init(int width, int height)
        {
            _display = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
            if (_display == null || _display.Equals(EGL14.EglNoDisplay))
            {
                throw new InvalidOperationException("no EGL display");
            }
            int[] version = new int[2];
            if (!EGL14.EglInitialize(_display, version, 0, version, 1))
            {
                throw new InvalidOperationException($"eglInitialize failed (0x{EGL14.EglGetError():X})");
            }
            // The same 8/8/8 colour, 24-bit depth and 8-bit stencil GameView
            // asks for: the renderer's translucency passes mark faces in the
            // stencil buffer, and a config without one draws them wrong rather
            // than failing.
            int[] attributes =
            {
                EGL14.EglRenderableType, OpenGlEs3Bit,
                EGL14.EglSurfaceType, EGL14.EglPbufferBit,
                EGL14.EglRedSize, 8,
                EGL14.EglGreenSize, 8,
                EGL14.EglBlueSize, 8,
                EGL14.EglAlphaSize, 0,
                EGL14.EglDepthSize, 24,
                EGL14.EglStencilSize, 8,
                EGL14.EglNone
            };
            var configs = new EGLConfig[1];
            int[] found = new int[1];
            if (!EGL14.EglChooseConfig(_display, attributes, 0, configs, 0, 1, found, 0)
                || found[0] < 1 || configs[0] == null)
            {
                throw new InvalidOperationException("no EGL config with a pbuffer, depth and stencil");
            }
            _surface = EGL14.EglCreatePbufferSurface(_display, configs[0],
                new[] { EGL14.EglWidth, width, EGL14.EglHeight, height, EGL14.EglNone }, 0);
            if (_surface == null || _surface.Equals(EGL14.EglNoSurface))
            {
                throw new InvalidOperationException($"eglCreatePbufferSurface failed (0x{EGL14.EglGetError():X})");
            }
            _context = EGL14.EglCreateContext(_display, configs[0], EGL14.EglNoContext,
                new[] { EGL14.EglContextClientVersion, 3, EGL14.EglNone }, 0);
            if (_context == null || _context.Equals(EGL14.EglNoContext))
            {
                throw new InvalidOperationException($"eglCreateContext failed (0x{EGL14.EglGetError():X})");
            }
            MakeCurrent();
        }

        public void MakeCurrent()
        {
            if (!EGL14.EglMakeCurrent(_display, _surface, _surface, _context))
            {
                throw new InvalidOperationException($"eglMakeCurrent failed (0x{EGL14.EglGetError():X})");
            }
        }

        public void Dispose()
        {
            if (_display == null)
            {
                return;
            }
            try
            {
                EGL14.EglMakeCurrent(_display, EGL14.EglNoSurface, EGL14.EglNoSurface,
                    EGL14.EglNoContext);
                if (_context != null)
                {
                    EGL14.EglDestroyContext(_display, _context);
                }
                if (_surface != null)
                {
                    EGL14.EglDestroySurface(_display, _surface);
                }
                EGL14.EglTerminate(_display);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[preview] tearing the offscreen context down failed: {ex.Message}");
            }
            _context = null;
            _surface = null;
            _display = null;
        }
    }
}
