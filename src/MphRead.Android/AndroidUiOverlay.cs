using System;
using Android.Opengl;

namespace MphRead.Droid
{
    /// <summary>
    /// The launcher's picture, drawn inside the game's frame.
    ///
    /// The ES half of what <c>Mods/Render/UiOverlay.cs</c> is on the desktop,
    /// and it exists separately for one reason: that one is fixed function --
    /// <c>glBegin</c>, the matrix stack, <c>glTexEnv</c> -- and while
    /// <c>Mods/Render/GlEs.cs</c> stands in for most of the pipeline it has
    /// none of those three. A screen-filling quad in normalised coordinates
    /// with its own two-line program is less code than teaching the emulator
    /// a matrix stack it would use nowhere else.
    ///
    /// Premultiplied alpha, because that is what Avalonia renders. Blending
    /// with SrcAlpha instead would darken every glyph edge against the match
    /// behind the panel.
    ///
    /// Everything here runs on the GL thread and nowhere else.
    /// </summary>
    internal static class AndroidUiOverlay
    {
        private const string VertexSource = @"#version 300 es
layout(location = 0) in vec2 a_pos;
out vec2 v_uv;
void main()
{
    // Avalonia's first row is the top of the screen and GL's is the bottom.
    v_uv = vec2(a_pos.x * 0.5 + 0.5, 0.5 - a_pos.y * 0.5);
    gl_Position = vec4(a_pos, 0.0, 1.0);
}";

        private const string FragmentSource = @"#version 300 es
precision mediump float;
uniform sampler2D u_screen;
in vec2 v_uv;
out vec4 o_colour;
void main()
{
    o_colour = texture(u_screen, v_uv);
}";

        private static int _program;

        /// <summary>
        /// <c>GL_CULL_FACE</c>. Spelled out because the binding gives the
        /// constant and <c>glCullFace</c> the same name, and the method wins.
        /// </summary>
        private const int CullFace = 0x0B44;
        private static int _vao;
        private static int _buffer;
        private static int _texture;
        private static int _width;
        private static int _height;
        private static bool _hasFrame;
        private static bool _failed;

        /// <summary>Whether the last uploaded frame should be drawn.</summary>
        public static bool Visible { get; set; }

        /// <summary>
        /// Take a rendered frame. Tightly packed RGBA, top row first, which is
        /// what the offscreen top level hands over.
        /// </summary>
        public static void Upload(byte[] pixels, int width, int height)
        {
            if (_failed || width <= 0 || height <= 0 || pixels.Length < width * height * 4)
            {
                return;
            }
            if (!Ensure())
            {
                return;
            }
            GLES30.GlActiveTexture(GLES30.GlTexture0);
            GLES30.GlBindTexture(GLES30.GlTexture2d, _texture);
            GLES30.GlPixelStorei(GLES30.GlUnpackAlignment, 4);
            using var buffer = Java.Nio.ByteBuffer.Wrap(pixels)!;
            if (width != _width || height != _height)
            {
                _width = width;
                _height = height;
                GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, width, height, 0,
                    GLES30.GlRgba, GLES30.GlUnsignedByte, buffer);
            }
            else
            {
                // The size is the window's and changes only when the window
                // does; respecifying the same rectangle is a reallocation the
                // driver cannot batch.
                GLES30.GlTexSubImage2D(GLES30.GlTexture2d, 0, 0, 0, width, height,
                    GLES30.GlRgba, GLES30.GlUnsignedByte, buffer);
            }
            GLES30.GlBindTexture(GLES30.GlTexture2d, 0);
            _hasFrame = true;
        }

        /// <summary>Put it on the screen, over everything drawn so far.</summary>
        public static void Draw(int width, int height)
        {
            if (_failed || !Visible || !_hasFrame || _program == 0)
            {
                return;
            }
            if (width > 0 && height > 0)
            {
                // Its own viewport, never the one the frame was left with: the
                // scene sets one from its own idea of the window's size, and a
                // screen drawn into that is a panel off the edge of the glass.
                GLES30.GlViewport(0, 0, width, height);
            }
            GLES30.GlUseProgram(_program);
            GLES30.GlDisable(GLES30.GlDepthTest);
            GLES30.GlDisable(CullFace);
            GLES30.GlDisable(GLES30.GlStencilTest);
            GLES30.GlDisable(GLES30.GlScissorTest);
            GLES30.GlEnable(GLES30.GlBlend);
            GLES30.GlBlendFunc(GLES30.GlOne, GLES30.GlOneMinusSrcAlpha);
            GLES30.GlActiveTexture(GLES30.GlTexture0);
            GLES30.GlBindTexture(GLES30.GlTexture2d, _texture);
            GLES30.GlBindVertexArray(_vao);
            GLES30.GlDrawArrays(GLES30.GlTriangleStrip, 0, 4);
            GLES30.GlBindVertexArray(0);
            GLES30.GlBindTexture(GLES30.GlTexture2d, 0);
            GLES30.GlUseProgram(0);
            // What the engine expects to find when it comes round again.
            GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
            GLES30.GlEnable(GLES30.GlDepthTest);
        }

        /// <summary>Give it all back. The context has to be current.</summary>
        public static void Release()
        {
            if (_texture != 0)
            {
                GLES30.GlDeleteTextures(1, new[] { _texture }, 0);
            }
            if (_buffer != 0)
            {
                GLES30.GlDeleteBuffers(1, new[] { _buffer }, 0);
            }
            if (_vao != 0)
            {
                GLES30.GlDeleteVertexArrays(1, new[] { _vao }, 0);
            }
            if (_program != 0)
            {
                GLES30.GlDeleteProgram(_program);
            }
            _texture = 0;
            _buffer = 0;
            _vao = 0;
            _program = 0;
            _width = 0;
            _height = 0;
            _hasFrame = false;
            _failed = false;
            Visible = false;
        }

        private static bool Ensure()
        {
            if (_program != 0)
            {
                return true;
            }
            try
            {
                _program = Link();
                float[] quad = { -1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f };
                int[] names = new int[1];
                GLES30.GlGenVertexArrays(1, names, 0);
                _vao = names[0];
                GLES30.GlGenBuffers(1, names, 0);
                _buffer = names[0];
                GLES30.GlBindVertexArray(_vao);
                GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _buffer);
                using (var bytes = Java.Nio.ByteBuffer.AllocateDirect(quad.Length * 4)!)
                {
                    bytes.Order(Java.Nio.ByteOrder.NativeOrder());
                    Java.Nio.FloatBuffer floats = bytes.AsFloatBuffer()!;
                    floats.Put(quad);
                    floats.Position(0);
                    GLES30.GlBufferData(GLES30.GlArrayBuffer, quad.Length * 4, floats,
                        GLES30.GlStaticDraw);
                }
                GLES30.GlEnableVertexAttribArray(0);
                GLES30.GlVertexAttribPointer(0, 2, GLES30.GlFloat, false, 0, 0);
                GLES30.GlBindVertexArray(0);
                GLES30.GlGenTextures(1, names, 0);
                _texture = names[0];
                GLES30.GlBindTexture(GLES30.GlTexture2d, _texture);
                // Linear: the screens are drawn at the window's own resolution
                // so there is nothing to magnify, and it is what keeps the one
                // frame where a resize has not caught up from showing a seam.
                GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter,
                    GLES30.GlLinear);
                GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter,
                    GLES30.GlLinear);
                GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS,
                    GLES30.GlClampToEdge);
                GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT,
                    GLES30.GlClampToEdge);
                GLES30.GlBindTexture(GLES30.GlTexture2d, 0);
                GLES30.GlUseProgram(_program);
                GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_program, "u_screen"), 0);
                GLES30.GlUseProgram(0);
                return true;
            }
            catch (Exception ex)
            {
                // No panel rather than no match: the engine's own picker is
                // still on the results screen underneath.
                _failed = true;
                Console.WriteLine($"[ui] the overlay could not be set up: {ex}");
                return false;
            }
        }

        private static int Link()
        {
            int vertex = Compile(GLES30.GlVertexShader, VertexSource);
            int fragment = Compile(GLES30.GlFragmentShader, FragmentSource);
            int program = GLES30.GlCreateProgram();
            GLES30.GlAttachShader(program, vertex);
            GLES30.GlAttachShader(program, fragment);
            GLES30.GlLinkProgram(program);
            int[] status = new int[1];
            GLES30.GlGetProgramiv(program, GLES30.GlLinkStatus, status, 0);
            if (status[0] == 0)
            {
                throw new InvalidOperationException(
                    "the overlay program would not link: " + GLES30.GlGetProgramInfoLog(program));
            }
            GLES30.GlDeleteShader(vertex);
            GLES30.GlDeleteShader(fragment);
            return program;
        }

        private static int Compile(int type, string source)
        {
            int shader = GLES30.GlCreateShader(type);
            GLES30.GlShaderSource(shader, source);
            GLES30.GlCompileShader(shader);
            int[] status = new int[1];
            GLES30.GlGetShaderiv(shader, GLES30.GlCompileStatus, status, 0);
            if (status[0] == 0)
            {
                throw new InvalidOperationException(
                    "the overlay shader would not compile: " + GLES30.GlGetShaderInfoLog(shader));
            }
            return shader;
        }
    }
}
