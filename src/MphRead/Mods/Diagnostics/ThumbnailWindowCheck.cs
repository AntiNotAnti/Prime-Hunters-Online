#if MPHREAD_SHELL
using System;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Desktop;

namespace MphRead.Mods.Diagnostics
{
    internal static class ThumbnailWindowCheck
    {
        public static int Run(bool legacyCheck = false)
        {
            try
            {
                // Use the worker's actual settings in a separate process, before
                // the launcher's context can initialize GLFW or mask a failure.
                var settings = ThumbnailCapture.WindowSettings(64, 64);
                if (legacyCheck)
                {
                    settings.APIVersion = new Version(2, 1);
                    settings.Profile = OpenTK.Windowing.Common.ContextProfile.Any;
                }
                using var window = new GameWindow(new GameWindowSettings(), settings);
                window.MakeCurrent();
                bool debugSkipped = false;
                ScreenCapture.EnableDebugOutput(line =>
                {
                    Console.WriteLine(line);
                    debugSkipped |= line.Contains("GL debug output unavailable", StringComparison.Ordinal);
                });
                Console.WriteLine(ScreenCapture.DescribeContext());
                if (GL.GetError() != ErrorCode.NoError)
                    throw new InvalidOperationException("Thumbnail diagnostics raised an OpenGL error.");
                if (legacyCheck && (!debugSkipped
                    || !(GL.GetString(StringName.Version) ?? "").StartsWith("2.1", StringComparison.Ordinal)))
                    throw new InvalidOperationException("Legacy regression requires GL 2.1 without KHR_debug.");
                int texture = GL.GenTexture();
                int framebuffer = GL.GenFramebuffer();
                try
                {
                    GL.BindTexture(TextureTarget.Texture2D, texture);
                    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                        64, 64, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
                    GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                    GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                        FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, texture, 0);
                    if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer)
                        != FramebufferErrorCode.FramebufferComplete)
                        throw new InvalidOperationException("Thumbnail framebuffer is incomplete.");
                    GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
                    GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
                    GL.Viewport(0, 0, 64, 64);
                    GL.ClearColor(0, 0, 0, 1);
                    GL.Clear(ClearBufferMask.ColorBufferBit);
                    // Immediate mode is required by the real thumbnail renderer.
                    GL.Begin(PrimitiveType.Quads);
                    GL.Color3(1f, 0.25f, 0.5f);
                    GL.Vertex2(-1f, -1f); GL.Vertex2(1f, -1f);
                    GL.Vertex2(1f, 1f); GL.Vertex2(-1f, 1f);
                    GL.End();
                    byte[] pixel = new byte[4];
                    GL.ReadPixels(32, 32, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel);
                    if (GL.GetError() != ErrorCode.NoError || pixel[0] < 240
                        || Math.Abs(pixel[1] - 64) > 4 || Math.Abs(pixel[2] - 128) > 4)
                        throw new InvalidOperationException("Thumbnail legacy rendering/readback failed.");
                    Console.WriteLine("Thumbnail window check passed.");
                    return 0;
                }
                finally
                {
                    GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                    GL.DeleteFramebuffer(framebuffer);
                    GL.DeleteTexture(texture);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[thumbnailwindowcheck] {ex}");
                return 1;
            }
        }
    }
}
#endif
