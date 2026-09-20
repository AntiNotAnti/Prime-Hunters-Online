#if MPHREAD_SHELL
using System;
using MphRead.Mods.Launcher.Gui;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;

namespace MphRead.Mods.Diagnostics
{
    /// <summary>Exercise the real desktop launcher without cartridge data.</summary>
    internal static class LauncherWindowCheck
    {
        private static bool _active;
        private static bool _passed;
        private static int _frames;

        public static int Run()
        {
            _active = true;
            _passed = false;
            _frames = 0;
            bool geometry = WindowGeometry.Enabled;
            WindowGeometry.Enabled = false;
            try
            {
                bool ran = GuiLauncher.TryRun();
                Console.WriteLine(ran && _passed ? "Launcher window check passed." : "Launcher window check failed.");
                return ran && _passed ? 0 : 1;
            }
            finally
            {
                _active = false;
                WindowGeometry.Enabled = geometry;
            }
        }

        // Called before buffer swap, after the same UI path used on first launch.
        internal static void AfterDraw(RenderWindow window)
        {
            if (!_active || (++_frames != 20 && _frames != 40)) return;
            try
            {
                if (_frames == 20)
                {
                    Console.WriteLine($"[windowcheck] {GL.GetString(StringName.Renderer)}; GL {GL.GetString(StringName.Version)}");
                    Link(Shaders.VertexShader, Shaders.FragmentShader);
                    Link(Shaders.RttVertexShader, Shaders.RttFragmentShader);
                    Link(Shaders.RttVertexShader, Shaders.CelFragmentShader);
                    Link(Shaders.RttVertexShader, Shaders.ShiftFragmentShader);
                }
                int width = window.FramebufferSize.X, height = window.FramebufferSize.Y;
                if (!Render.UiOverlay.HasFrame || width <= 0 || height <= 0)
                    throw new InvalidOperationException("Launcher did not upload a frame.");
                byte[] pixels = new byte[checked(width * height * 4)];
                GL.ReadBuffer(ReadBufferMode.Back);
                GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
                int lit = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                    if (pixels[i] > 16 || pixels[i + 1] > 16 || pixels[i + 2] > 16) lit++;
                if (lit < width * height / 100)
                    throw new InvalidOperationException("Launcher frame is black.");
                var error = GL.GetError();
                if (error != ErrorCode.NoError) throw new InvalidOperationException($"OpenGL error: {error}");
                Console.WriteLine($"[windowcheck] rendered {width}x{height}, {lit} lit pixels");
                if (_frames == 20) window.ClientSize = new Vector2i(1100, 740);
                else { _passed = true; window.Close(); }
            }
            catch (Exception ex)
            {
                // Never throw through GLFW's native callbacks.
                Console.Error.WriteLine($"[windowcheck] {ex}");
                _active = false;
                window.Close();
            }
        }

        private static void Link(string vertex, string fragment)
        {
            int program = GL.CreateProgram();
            try
            {
                foreach (var (type, source) in new[] {
                    (ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, fragment) })
                {
                    int shader = GL.CreateShader(type);
                    try
                    {
                        GL.ShaderSource(shader, source);
                        GL.CompileShader(shader);
                        GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
                        if (compiled == 0) throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
                        GL.AttachShader(program, shader);
                    }
                    finally { GL.DeleteShader(shader); }
                }
                GL.LinkProgram(program);
                GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
                if (linked == 0) throw new InvalidOperationException(GL.GetProgramInfoLog(program));
            }
            finally { GL.DeleteProgram(program); }
        }
    }
}
#endif
