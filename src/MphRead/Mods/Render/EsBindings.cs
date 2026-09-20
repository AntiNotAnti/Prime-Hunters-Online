#if ANDROID
using System;
using System.Runtime.InteropServices;
using OpenTK;

namespace MphRead.Mods.Render
{
    /// <summary>
    /// Where OpenTK's ES bindings get their function pointers on Android.
    ///
    /// On the desktop that job belongs to GLFW, which is not here. The driver
    /// is <c>libGLESv2.so</c> and every entry point ES 3.0 defines is an
    /// ordinary exported symbol in it, so the first place to look is the
    /// library itself; <c>eglGetProcAddress</c> is the fallback, and the only
    /// way to reach anything that is an extension rather than core.
    /// </summary>
    internal sealed class EsBindings : IBindingsContext
    {
        private readonly IntPtr _gles;
        private readonly IntPtr _egl;

        private EsBindings(IntPtr gles, IntPtr egl)
        {
            _gles = gles;
            _egl = egl;
        }

        [DllImport("libEGL.so", EntryPoint = "eglGetProcAddress")]
        private static extern IntPtr EglGetProcAddress(string procName);

        public IntPtr GetProcAddress(string procName)
        {
            if (_gles != IntPtr.Zero
                && NativeLibrary.TryGetExport(_gles, procName, out IntPtr address))
            {
                return address;
            }
            try
            {
                return EglGetProcAddress(procName);
            }
            catch (DllNotFoundException)
            {
                return IntPtr.Zero;
            }
            catch (EntryPointNotFoundException)
            {
                return IntPtr.Zero;
            }
        }

        private static bool _loaded;

        /// <summary>
        /// Point OpenTK's ES 3.0 bindings at this process's driver. Must run on
        /// the thread that owns the GL context, and only needs to run once --
        /// the pointers are per-process, not per-context.
        /// </summary>
        public static void Load()
        {
            if (_loaded)
            {
                return;
            }
            IntPtr gles = TryLoad("libGLESv2.so");
            IntPtr egl = TryLoad("libEGL.so");
            if (gles == IntPtr.Zero && egl == IntPtr.Zero)
            {
                throw new ProgramException("Neither libGLESv2.so nor libEGL.so could be loaded.");
            }
            OpenTK.Graphics.ES30.GL.LoadBindings(new EsBindings(gles, egl));
            _loaded = true;
        }

        private static IntPtr TryLoad(string name)
        {
            return NativeLibrary.TryLoad(name, out IntPtr handle) ? handle : IntPtr.Zero;
        }
    }
}
#endif
