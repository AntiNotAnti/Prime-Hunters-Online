using System;
using MphRead.Mods;
using MphRead.Mods.Render;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;

namespace MphRead
{
    public partial class Scene
    {
        private int _texturedPlayerSkinUniform;
        private int _playerOutlineMaskUniform;
        private int _playerOutlineColorUniform;
        private bool _drawingPlayerOutlineMask;
        private int _playerOutlineTexture;
        private int _playerOutlineFramebuffer;
        private int _playerOutlineProgram;
        private int _playerOutlineStepXUniform;
        private int _playerOutlineStepYUniform;
        private int _playerOutlineDepth = -1;
        private Vector2i _playerOutlineSize;
        private bool _playerOutlineRefused;

        private void DrawWorldOutlines()
        {
            // Cel's black silhouette can completely cover a colored inward rim at low
            // render scales. Composite player edges last, before preview/HUD layers.
            DrawCelOutline();
            DrawPlayerOutlines();
            GL.UseProgram(_shaderProgramId);
        }

        private void DrawPlayerOutlines()
        {
            if (RenderOptions.PlayerOutline == PlayerOutlineStyle.Off || _playerOutlineRefused)
            {
                return;
            }
            bool any = false;
            foreach (RenderItem item in _usedRenderItems)
            {
                if (item.PlayerOutlineColor.HasValue)
                {
                    any = true;
                    break;
                }
            }
            if (!any)
            {
                return;
            }
            try
            {
                GL.ActiveTexture(TextureUnit.Texture0);
                EnsurePlayerOutlineTarget();
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, _playerOutlineFramebuffer);
                GL.ColorMask(true, true, true, true);
                GL.ClearColor(0, 0, 0, 0);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                GL.Disable(EnableCap.Blend);
                GL.Disable(EnableCap.StencilTest);
                GL.Disable(EnableCap.AlphaTest);
                GL.Enable(EnableCap.DepthTest);
                GL.DepthFunc(DepthFunction.Lequal);
                GL.DepthMask(false);
                GL.UseProgram(_shaderProgramId);
                GL.Uniform1(_playerOutlineMaskUniform, 1);
                _drawingPlayerOutlineMask = true;
                // Reuse submitted geometry and the world depth attachment. No inflated shell:
                // every mask pixel must pass the same depth/culling/cutout tests as its body.
                foreach (RenderItem item in _usedRenderItems)
                {
                    if (item.PlayerOutlineColor is Vector4 color)
                    {
                        GL.Uniform3(_playerOutlineColorUniform, color.Xyz);
                        RenderItem(item);
                    }
                }
                _drawingPlayerOutlineMask = false;
                GL.Uniform1(_playerOutlineMaskUniform, 0);

                GL.BindFramebuffer(FramebufferTarget.Framebuffer, _frameBuffer);
                GL.UseProgram(_playerOutlineProgram);
                // Width is in window pixels, rounded up to the mask's minimum visible
                // texel: nearest sampling cannot detect a fractional-texel boundary.
                GL.Uniform1(_playerOutlineStepXUniform,
                    Math.Max(1f / _targetSize.X, RenderOptions.PlayerOutlineWidth / (float)Math.Max(1, Size.X)));
                GL.Uniform1(_playerOutlineStepYUniform,
                    Math.Max(1f / _targetSize.Y, RenderOptions.PlayerOutlineWidth / (float)Math.Max(1, Size.Y)));
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, _playerOutlineTexture);
                GL.Disable(EnableCap.DepthTest);
                GL.Disable(EnableCap.CullFace);
                GL.PolygonMode(TriangleFace.FrontAndBack, OpenTK.Graphics.OpenGL.PolygonMode.Fill);
                GL.Enable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                GL.Begin(PrimitiveType.TriangleStrip);
                GL.TexCoord3(1f, 1f, 0f); GL.Vertex3(1f, 1f, 0f);
                GL.TexCoord3(0f, 1f, 0f); GL.Vertex3(-1f, 1f, 0f);
                GL.TexCoord3(1f, 0f, 0f); GL.Vertex3(1f, -1f, 0f);
                GL.TexCoord3(0f, 0f, 0f); GL.Vertex3(-1f, -1f, 0f);
                GL.End();
            }
            catch (ProgramException ex)
            {
                _playerOutlineRefused = true;
                Console.WriteLine($"[render] player outline unavailable: {ex.Message}");
                DisposePlayerOutlines();
            }
            finally
            {
                _drawingPlayerOutlineMask = false;
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, _frameBuffer);
                GL.UseProgram(_shaderProgramId);
                GL.Uniform1(_playerOutlineMaskUniform, 0);
                GL.Uniform1(_texturedPlayerSkinUniform, 0);
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, 0);
                GL.ClearColor(_clearColor);
                GL.Enable(EnableCap.DepthTest);
                GL.DepthMask(true);
                GL.DepthFunc(DepthFunction.Lequal);
                GL.Enable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                GL.Disable(EnableCap.StencilTest);
                GL.Disable(EnableCap.AlphaTest);
                GL.PolygonMode(TriangleFace.FrontAndBack, OpenTK.Graphics.OpenGL.PolygonMode.Fill);
            }
        }

        private void EnsurePlayerOutlineTarget()
        {
            if (_playerOutlineProgram == 0)
            {
                int vertex = CompilePlayerOutlineShader(ShaderType.VertexShader, PlayerOutlineShader.VertexSource);
                int fragment = 0;
                try
                {
                    fragment = CompilePlayerOutlineShader(ShaderType.FragmentShader, PlayerOutlineShader.Source);
                    _playerOutlineProgram = GL.CreateProgram();
                    GL.AttachShader(_playerOutlineProgram, vertex);
                    GL.AttachShader(_playerOutlineProgram, fragment);
                    GL.LinkProgram(_playerOutlineProgram);
#if !ANDROID
                    GL.GetProgram(_playerOutlineProgram, GetProgramParameterName.LinkStatus, out int status);
                    if (status == 0)
                    {
                        throw new ProgramException(GL.GetProgramInfoLog(_playerOutlineProgram));
                    }
#endif
                    GL.UseProgram(_playerOutlineProgram);
                    GL.Uniform1(GL.GetUniformLocation(_playerOutlineProgram, "mask_tex"), 0);
                    _playerOutlineStepXUniform = GL.GetUniformLocation(_playerOutlineProgram, "outline_step_x");
                    _playerOutlineStepYUniform = GL.GetUniformLocation(_playerOutlineProgram, "outline_step_y");
                }
                finally
                {
                    GL.DeleteShader(vertex);
                    if (fragment != 0) GL.DeleteShader(fragment);
                }
            }
            if (_playerOutlineFramebuffer == 0)
            {
                _playerOutlineFramebuffer = GL.GenFramebuffer();
                _playerOutlineTexture = GL.GenTexture();

            }
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _playerOutlineFramebuffer);
            bool targetChanged = _playerOutlineSize != _targetSize || _playerOutlineDepth != _depthTexture;
            if (_playerOutlineSize != _targetSize)
            {
                GL.BindTexture(TextureTarget.Texture2D, _playerOutlineTexture);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                    _targetSize.X, _targetSize.Y, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                    TextureTarget.Texture2D, _playerOutlineTexture, 0);
                _playerOutlineSize = _targetSize;
            }
            if (_playerOutlineDepth != _depthTexture)
            {
                if (_depthTexture != 0)
                {
                    GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
                        TextureTarget.Texture2D, _depthTexture, 0);
                }
                else
                {
                    GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
                        RenderbufferTarget.Renderbuffer, _renderBuffer);
                }
                _playerOutlineDepth = _depthTexture;
            }
            if (targetChanged)
            {
                FramebufferErrorCode result = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
                if (result != FramebufferErrorCode.FramebufferComplete)
                {
                    throw new ProgramException($"incomplete player mask target ({result})");
                }
            }
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        private static int CompilePlayerOutlineShader(ShaderType type, string source)
        {
            int shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int status);
            if (status == 0)
            {
                string log = GL.GetShaderInfoLog(shader);
                GL.DeleteShader(shader);
                throw new ProgramException(log);
            }
            return shader;
        }

        private void DisposePlayerOutlines()
        {
            if (_playerOutlineFramebuffer != 0)
            {
                GL.DeleteFramebuffer(_playerOutlineFramebuffer);
                _playerOutlineFramebuffer = 0;
            }
            if (_playerOutlineTexture != 0)
            {
                DeleteTexture(ref _playerOutlineTexture);
            }
            DeleteProgram(ref _playerOutlineProgram);
            _playerOutlineSize = default;
            _playerOutlineDepth = -1;
        }
    }
}

namespace MphRead.Mods.Render
{
    internal static class PlayerOutlineShader
    {
        // A world composite must not inherit HUD scale/placement uniforms. Uninitialized
        // uniforms default to zero, which can collapse a reused HUD quad to a point.
#if ANDROID
        public static string VertexSource { get; } = @"#version 300 es
precision highp float;
layout(location = 0) in vec4 a_position;
layout(location = 3) in vec3 a_texcoord;
out vec2 texcoord;
void main() {
    gl_Position = vec4(a_position.xy, 0.0, 1.0);
    texcoord = a_texcoord.xy;
}
";
#else
        public static string VertexSource { get; } = @"#version 120
varying vec2 texcoord;
void main() {
    gl_Position = vec4(gl_Vertex.xy, 0.0, 1.0);
    texcoord = gl_MultiTexCoord0.xy;
}
";
#endif
        // One body for both targets: no separate ES algorithm to drift out of sync.
#if ANDROID
        public static string Source { get; } = "#version 300 es\nprecision highp float;\n"
            + "in vec2 texcoord;\nout vec4 frag_color;\n#define SAMPLE texture\n#define OUTPUT frag_color\n" + Body;
#else
        public static string Source { get; } = "#version 120\nvarying vec2 texcoord;\n"
            + "#define SAMPLE texture2D\n#define OUTPUT gl_FragColor\n" + Body;
#endif
        private const string Body = @"
uniform sampler2D mask_tex;
uniform float outline_step_x;
uniform float outline_step_y;
float coverage(vec2 offset) { return SAMPLE(mask_tex, texcoord + offset).a; }
void main() {
    vec4 center = SAMPLE(mask_tex, texcoord);
    // Inward only: a wall pixel or a fully occluded body can never acquire an outline.
    if (center.a <= 0.01) discard;
    vec2 d = vec2(outline_step_x, outline_step_y);
    float inside = min(min(coverage(vec2(d.x, 0.0)), coverage(vec2(-d.x, 0.0))),
        min(coverage(vec2(0.0, d.y)), coverage(vec2(0.0, -d.y))));
    d *= 0.70710678;
    inside = min(inside, min(min(coverage(d), coverage(-d)),
        min(coverage(vec2(d.x, -d.y)), coverage(vec2(-d.x, d.y)))));
    if (inside > 0.01) discard;
    OUTPUT = center;
}
";
    }
}