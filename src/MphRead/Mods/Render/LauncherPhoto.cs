using System;
using System.IO;
using System.Reflection;
using OpenTK.Graphics.OpenGL;
using ReFuel.Stb;
using MphRead.Mods.Launcher;

namespace MphRead.Mods.Render
{
    /// <summary>
    /// The launcher's cinematic map scene, drawn by GL at the window's own
    /// resolution instead of being rasterised into the screens' texture with them.
    ///
    /// **Why it moved.** The screens are rasterised by Skia on the CPU and
    /// uploaded as one texture, and that raster is capped at 1920x1080 and
    /// magnified -- it has to be, because a redraw is the whole window and a
    /// 4K one costs about 60 ms, which is a menu that scrolls at seventeen
    /// frames a second (<see cref="Launcher.Gui.UiSurface"/> carries the
    /// measurements). The cap is the right trade for type and rows: they are
    /// redrawn whenever anything moves.
    ///
    /// The cinematic scene is not like that. It changes only when the hub
    /// destination or selected map changes, and otherwise remains the single
    /// largest thing on the screen. It was paying the cap for nothing --
    /// on a 1440p or 4K display the launcher's backdrop was a 1080p picture
    /// stretched over the window, which is exactly the softness that was
    /// reported after the launcher stopped being a window of its own (it used
    /// to be an Avalonia window drawn by the platform at native resolution).
    ///
    /// So it is uploaded once as a texture and drawn as one quad under the
    /// screens. The cost is a textured quad a frame on the GPU -- nothing that
    /// shows on a frame graph -- against the whole backdrop's worth of CPU
    /// raster it takes off the bake, and the picture is now as sharp as the
    /// window is big.
    ///
    /// **The washes stay where they are.** The gradient and the vignette are
    /// still drawn into the screens' own bitmap, above this, and the composite
    /// comes out identical: the overlay is blended premultiplied
    /// (<c>One, OneMinusSrcAlpha</c>), which is the same "over" operator
    /// Avalonia applied when the two were in one bitmap. A gradient also
    /// survives being magnified in a way a photograph does not -- it is smooth
    /// by construction, and the magnification dithers the steps rather than
    /// showing them.
    ///
    /// The player-facing scene comes from locally generated map thumbnails,
    /// so no game-derived backdrop is shipped by this repository. It is
    /// decoded here rather than through Avalonia, for the reason
    /// <see cref="AppIcon"/> gives: this is GL's side of the window, it is
    /// compiled into builds that have no toolkit, and avares:// needs Avalonia
    /// to read. The JPEG therefore travels as a plain embedded resource as
    /// well as an Avalonia one -- the same half megabyte twice, which is the
    /// price of the two heads reading it two ways.
    /// </summary>
    public static class LauncherPhoto
    {
        /// <summary>
        /// Whether the screens are leaving the photograph to this.
        ///
        /// Off by default and turned on by the desktop shell alone. The
        /// standalone screen captures (<c>-uishot</c>, the design studies) render
        /// the screens with no GL window under them, and a backdrop that
        /// expected somebody else to draw the picture would photograph as a
        /// wash over nothing. `-shellshot` is unaffected: it drives the real
        /// window and reads its back buffer, which is where this ends up.
        /// </summary>
        public static bool Enabled { get; set; }

        private static int _texture;
        private static int _width;
        private static int _height;
        private static string _loadedKey = "";
        private static long _lastAttemptAt;

        /// <summary>
        /// The program that lays <see cref="LauncherNoise"/> over the picture.
        /// Zero once it has been tried and could not be had, and the draw then
        /// falls back to the fixed-function quad below it.
        /// </summary>
        private static int _program;
        private static bool _programTried;
        private static int _photoUniform = -1, _noiseUniform = -1, _strengthUniform = -1;

        /// <summary>`#backdrop { opacity: .62 }`.</summary>
        private const float Strength = 0.16f;

        /// <summary>
        /// Build the overlay program, once, and never again if it will not
        /// build.
        ///
        /// Soft, unlike the engine's own shader setup, which throws. A driver
        /// that will not compile this should cost the player a still backdrop,
        /// not a launcher that does not open -- and on Windows the binary is a
        /// GUI one with no console, so "does not open" is all they would get.
        /// </summary>
        private static bool EnsureProgram()
        {
            if (_programTried)
            {
                return _program != 0;
            }
            _programTried = true;
            int vertex = 0, fragment = 0;
            try
            {
                vertex = GL.CreateShader(ShaderType.VertexShader);
                GL.ShaderSource(vertex, Shaders.BackdropVertexShader);
                GL.CompileShader(vertex);
                GL.GetShader(vertex, ShaderParameter.CompileStatus, out int vertexOk);
                fragment = GL.CreateShader(ShaderType.FragmentShader);
                GL.ShaderSource(fragment, Shaders.BackdropFragmentShader);
                GL.CompileShader(fragment);
                GL.GetShader(fragment, ShaderParameter.CompileStatus, out int fragmentOk);
                if (vertexOk == 0 || fragmentOk == 0)
                {
                    Mods.DebugLog.Line("ui", "the moving backdrop's shaders would not compile: "
                        + GL.GetShaderInfoLog(vertex) + " " + GL.GetShaderInfoLog(fragment));
                    return false;
                }
                int program = GL.CreateProgram();
                GL.AttachShader(program, vertex);
                GL.AttachShader(program, fragment);
                GL.LinkProgram(program);
                GL.DetachShader(program, vertex);
                GL.DetachShader(program, fragment);
                GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
                if (linked == 0)
                {
                    Mods.DebugLog.Line("ui", "the moving backdrop would not link: "
                        + GL.GetProgramInfoLog(program));
                    GL.DeleteProgram(program);
                    return false;
                }
                _program = program;
                _photoUniform = GL.GetUniformLocation(program, "photo");
                _noiseUniform = GL.GetUniformLocation(program, "noise");
                _strengthUniform = GL.GetUniformLocation(program, "strength");
                Mods.DebugLog.Line("ui", "the moving backdrop is on");
                return true;
            }
            catch (Exception ex)
            {
                Mods.DebugLog.Line("ui", $"the moving backdrop could not be set up: {ex.Message}");
                _program = 0;
                return false;
            }
            finally
            {
                if (fragment != 0)
                {
                    GL.DeleteShader(fragment);
                }
                if (vertex != 0)
                {
                    GL.DeleteShader(vertex);
                }
            }
        }

        /// <summary>
        /// Put it on the screen, cropped to fill the window the way
        /// <c>Stretch.UniformToFill</c> filled it -- centred, and losing
        /// whichever axis has the spare picture on it, so the framing is what
        /// it always was.
        ///
        /// Draws nothing at all when it is not this object's job, which is
        /// every build but the desktop shell's and every frame with a match in
        /// it.
        /// </summary>
        public static void Draw(int width, int height)
        {
            if (!Enabled || width <= 0 || height <= 0 || !Ensure())
            {
                return;
            }
            // How much of the picture the window can see. The window is
            // filled; whatever does not fit on the other axis is trimmed off
            // both ends.
            double window = width / (double)height;
            double picture = _width / (double)_height;
            float u = 1;
            float v = 1;
            if (window > picture)
            {
                // A window wider than the picture: all of the width, a band
                // out of the middle of the height.
                v = (float)(picture / window);
            }
            else
            {
                u = (float)(window / picture);
            }
            // Keep a little image outside the viewport so the scene can
            // breathe underneath the UI without ever exposing an edge.
            float zoom = Math.Clamp(LauncherBackdrop.Zoom, 0.84f, 1f);
            u *= zoom;
            v *= zoom;
            float centreU = 0.5f;
            float centreV = 0.5f;
            if (!LauncherPrefs.ReduceMotion)
            {
                double seconds = Environment.TickCount64 / 1000.0;
                centreU += (float)Math.Sin(seconds * 0.075) * (1 - u) * 0.20f;
                centreV += (float)Math.Cos(seconds * 0.052) * (1 - v) * 0.14f;
            }
            float u0 = centreU - u / 2;
            float u1 = centreU + u / 2;
            float v0 = centreV - v / 2;
            float v1 = centreV + v / 2;
            // The moving field over the photograph. Both have to be there:
            // no program, or no field yet, and this is the still picture it
            // has always been.
            bool moving = LauncherNoise.Step(width, height)
                && LauncherNoise.Texture != 0 && EnsureProgram();
            GL.UseProgram(moving ? _program : 0);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.AlphaTest);
            GL.Disable(EnableCap.StencilTest);
            // Opaque: this is the ground, and the frame under it has just been
            // cleared. Blending it would cost a read per pixel for nothing.
            GL.Disable(EnableCap.Blend);
            // Unit 1 off, unit 0 ours -- the same care UiOverlay takes, and
            // for the same reason: the scene leaves the active unit wherever
            // its last shader wanted it.
            GL.ActiveTexture(TextureUnit.Texture1);
            if (moving)
            {
                GL.Enable(EnableCap.Texture2D);
                GL.BindTexture(TextureTarget.Texture2D, LauncherNoise.Texture);
            }
            else
            {
                GL.BindTexture(TextureTarget.Texture2D, 0);
                GL.Disable(EnableCap.Texture2D);
            }
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.Enable(EnableCap.Texture2D);
            GL.BindTexture(TextureTarget.Texture2D, _texture);
            GL.TexEnv(TextureEnvTarget.TextureEnv, TextureEnvParameter.TextureEnvMode,
                (int)TextureEnvMode.Replace);
            GL.Color4(1f, 1f, 1f, 1f);
            if (moving)
            {
                GL.Uniform1(_photoUniform, 0);
                GL.Uniform1(_noiseUniform, 1);
                GL.Uniform1(_strengthUniform, Strength);
            }
            GL.MatrixMode(MatrixMode.Projection);
            GL.PushMatrix();
            GL.LoadIdentity();
            GL.MatrixMode(MatrixMode.Modelview);
            GL.PushMatrix();
            GL.LoadIdentity();
            // Flipped in T, like the overlay: the decoder's first row is the
            // top of the picture and GL's is the bottom.
            // Unit 1 carries the field's own framing: edge to edge, like the
            // canvas this is a port of, which is `inset: 0` over the whole
            // screen rather than cropped with the picture. Flipped in T for
            // the same reason unit 0 is -- the first row of both buffers is
            // the top of the image and GL's is the bottom.
            GL.Begin(PrimitiveType.TriangleStrip);
            GL.MultiTexCoord2(TextureUnit.Texture0, u1, v0);
            GL.MultiTexCoord2(TextureUnit.Texture1, 1f, 0f);
            GL.Vertex3(1f, 1f, 0f);
            GL.MultiTexCoord2(TextureUnit.Texture0, u0, v0);
            GL.MultiTexCoord2(TextureUnit.Texture1, 0f, 0f);
            GL.Vertex3(-1f, 1f, 0f);
            GL.MultiTexCoord2(TextureUnit.Texture0, u1, v1);
            GL.MultiTexCoord2(TextureUnit.Texture1, 1f, 1f);
            GL.Vertex3(1f, -1f, 0f);
            GL.MultiTexCoord2(TextureUnit.Texture0, u0, v1);
            GL.MultiTexCoord2(TextureUnit.Texture1, 0f, 1f);
            GL.Vertex3(-1f, -1f, 0f);
            GL.End();
            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Projection);
            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Modelview);
            GL.TexEnv(TextureEnvTarget.TextureEnv, TextureEnvParameter.TextureEnvMode,
                (int)TextureEnvMode.Modulate);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            if (moving)
            {
                // Put the units back the way everything after this expects
                // them: the overlay and the scene both assume unit 1 is off
                // and unit 0 is the active one.
                GL.ActiveTexture(TextureUnit.Texture1);
                GL.BindTexture(TextureTarget.Texture2D, 0);
                GL.Disable(EnableCap.Texture2D);
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.UseProgram(0);
            }
            GL.Enable(EnableCap.Blend);
            GL.Enable(EnableCap.DepthTest);
        }

        /// <summary>
        /// Decode and upload, once. False when there is no picture to draw --
        /// a build without the resource, or a decode that failed -- and the
        /// launcher then looks the way it does over a match: the washes on
        /// black, which is a screen rather than a crash.
        /// </summary>
        private static bool Ensure()
        {
            string key = LauncherBackdrop.CacheKey;
            bool same = String.Equals(_loadedKey, key, StringComparison.Ordinal);
            if (same && _texture != 0)
            {
                return true;
            }

            long now = Environment.TickCount64;
            if (same && now - _lastAttemptAt < 1000)
            {
                return false;
            }

            _loadedKey = key;
            _lastAttemptAt = now;
            if (_texture != 0) GL.DeleteTexture(_texture);
            _texture = 0;
            _width = _height = 0;

            string room = LauncherBackdrop.RoomKey;
            if (room.Length == 0)
            {
                Mods.DebugLog.Line("ui",
                    $"cinematic backdrop {LauncherBackdrop.Scene}: graded field");
                return false;
            }

            try
            {
                string path = MphRead.Mods.ThumbnailGenerator.PathFor(room);
                if (!File.Exists(path))
                {
                    Mods.DebugLog.Line("ui",
                        $"cinematic backdrop has no thumbnail for {room}");
                    return false;
                }

                using Stream stream = File.OpenRead(path);
                using StbImage image = StbImage.Load(stream, StbiImageFormat.Rgba);
                _width = image.Width;
                _height = image.Height;
                if (_width <= 0 || _height <= 0 || image.ImagePointer == IntPtr.Zero)
                {
                    return false;
                }

                GL.ActiveTexture(TextureUnit.Texture0);
                _texture = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, _texture);
                GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
                GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
                GL.PixelStore(PixelStoreParameter.UnpackSkipPixels, 0);
                GL.PixelStore(PixelStoreParameter.UnpackSkipRows, 0);
                GL.PixelStore(PixelStoreParameter.UnpackImageHeight, 0);
                GL.PixelStore(PixelStoreParameter.UnpackSkipImages, 0);
                GL.PixelStore(PixelStoreParameter.UnpackSwapBytes, 0);
                GL.PixelStore(PixelStoreParameter.UnpackLsbFirst, 0);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                    _width, _height, 0, PixelFormat.Rgba, PixelType.UnsignedByte,
                    image.ImagePointer);

                ErrorCode uploaded = GL.GetError();
                if (uploaded != ErrorCode.NoError)
                {
                    Mods.DebugLog.Line("ui",
                        $"cinematic backdrop upload said {uploaded}");
                }

                GL.TexParameter(TextureTarget.Texture2D,
                    TextureParameterName.TextureBaseLevel, 0);
                GL.TexParameter(TextureTarget.Texture2D,
                    TextureParameterName.TextureMaxLevel, 0);
                GL.TexParameter(TextureTarget.Texture2D,
                    TextureParameterName.TextureMinFilter,
                    (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D,
                    TextureParameterName.TextureMagFilter,
                    (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D,
                    TextureParameterName.TextureWrapS,
                    (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D,
                    TextureParameterName.TextureWrapT,
                    (int)TextureWrapMode.ClampToEdge);
                GL.BindTexture(TextureTarget.Texture2D, 0);

                Mods.DebugLog.Line("ui",
                    $"cinematic backdrop {LauncherBackdrop.Scene}: {room} "
                    + $"{_width}x{_height}");
                return true;
            }
            catch (Exception ex)
            {
                if (_texture != 0) GL.DeleteTexture(_texture);
                _texture = 0;
                Mods.DebugLog.Line("ui",
                    $"cinematic backdrop could not load {room}: {ex.Message}");
                return false;
            }
        }
    }
}
