using System;
using MphRead.Mods;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;

namespace MphRead
{
    /// <summary>
    /// Modern presentation-only processing for the finished 3D scene.
    ///
    /// The cartridge renderer still produces the authoritative world picture.
    /// This pass reads that offscreen picture (and depth when available), writes
    /// a second scene texture, then the existing composite/HUD path presents it.
    /// Nothing here advances simulation, moves an entity or changes hit logic.
    /// </summary>
    public partial class Scene
    {
        private int _graphicsProgram;
        private int _graphicsOutputTexture;
        private int _graphicsOutputFramebuffer;
        private Vector2i _graphicsOutputSize;
        private bool _graphicsOutputReady;
        private bool _graphicsPipelineRefused;

        private int _gfxSceneSampler, _gfxDepthSampler, _gfxTexel;
        private int _gfxNear, _gfxFar, _gfxDepthAvailable;
        private int _gfxAa, _gfxSharpen, _gfxBloom, _gfxBloomIntensity;
        private int _gfxGrade, _gfxGamma, _gfxContrast, _gfxSaturation;
        private int _gfxLighting, _gfxAo, _gfxContactShadows;
        private int _gfxEnhancedFog, _gfxVolumetricFog, _gfxHdr;
        private int _gfxReflections, _gfxDynamicGlow, _gfxFogColor, _gfxTime;

        private void ApplyGraphicsPostProcess()
        {
            _graphicsOutputReady = false;
            if (!RenderOptions.PostProcessingEnabled || _graphicsPipelineRefused)
            {
                return;
            }
            try
            {
                EnsureGraphicsPipeline();
                if (_graphicsProgram == 0 || _graphicsOutputFramebuffer == 0)
                {
                    return;
                }

                Vector2i target = _targetSize;
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, _graphicsOutputFramebuffer);
                GL.Viewport(0, 0, target.X, target.Y);
                SetScreenPassState();
                GL.Disable(EnableCap.Blend);
                GL.UseProgram(_graphicsProgram);

                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, _screenTexture);
                GL.Uniform1(_gfxSceneSampler, 0);

                bool depthAvailable = _depthTexture != 0 && RenderOptions.NeedsReadableDepth;
                GL.ActiveTexture(TextureUnit.Texture1);
                GL.BindTexture(TextureTarget.Texture2D, depthAvailable ? _depthTexture : 0);
                GL.Uniform1(_gfxDepthSampler, 1);
                GL.Uniform1(_gfxDepthAvailable, depthAvailable ? 1 : 0);
                GL.ActiveTexture(TextureUnit.Texture0);

                GL.Uniform2(_gfxTexel, 1f / Math.Max(1, target.X), 1f / Math.Max(1, target.Y));
                GL.Uniform1(_gfxNear, _nearClip);
                GL.Uniform1(_gfxFar, _useClip ? Math.Max(_farClip, _nearClip + 1f) : 10000f);
                GL.Uniform1(_gfxAa, (int)RenderOptions.AntiAliasing);
                GL.Uniform1(_gfxSharpen, RenderOptions.SharpenStrength / 100f);
                GL.Uniform1(_gfxBloom, RenderOptions.Bloom ? 1 : 0);
                GL.Uniform1(_gfxBloomIntensity, RenderOptions.BloomIntensity / 100f);
                GL.Uniform1(_gfxGrade, (int)RenderOptions.ColorGrade);
                GL.Uniform1(_gfxGamma, RenderOptions.Gamma / 100f);
                GL.Uniform1(_gfxContrast, RenderOptions.Contrast / 100f);
                GL.Uniform1(_gfxSaturation, RenderOptions.Saturation / 100f);
                GL.Uniform1(_gfxLighting, RenderOptions.EnhancedLighting ? 1 : 0);
                GL.Uniform1(_gfxAo, (int)RenderOptions.AmbientOcclusion);
                GL.Uniform1(_gfxContactShadows, RenderOptions.ContactShadows ? 1 : 0);
                GL.Uniform1(_gfxEnhancedFog, RenderOptions.EnhancedFog ? 1 : 0);
                GL.Uniform1(_gfxVolumetricFog, RenderOptions.VolumetricFog ? 1 : 0);
                GL.Uniform1(_gfxHdr, RenderOptions.InternalHdr ? 1 : 0);
                GL.Uniform1(_gfxReflections, RenderOptions.Reflections ? 1 : 0);
                GL.Uniform1(_gfxDynamicGlow, RenderOptions.DynamicGlow ? 1 : 0);
                GL.Uniform4(_gfxFogColor, _fogColor);
                GL.Uniform1(_gfxTime, _globalElapsedTime);

                DrawGraphicsFullscreenQuad();
                _graphicsOutputReady = true;
                RenderPostProcessCount++;
                CheckGlError("GraphicsPostProcess");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                _graphicsPipelineRefused = true;
                _graphicsOutputReady = false;
                Console.WriteLine($"[render] enhanced graphics unavailable: {ex.Message}");
            }
            finally
            {
                GL.ActiveTexture(TextureUnit.Texture1);
                GL.BindTexture(TextureTarget.Texture2D, 0);
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, 0);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, _frameBuffer);
                GL.Viewport(0, 0, _targetSize.X, _targetSize.Y);
            }
        }

        private int GraphicsCompositeTexture()
            => _graphicsOutputReady ? _graphicsOutputTexture : _screenTexture;

        private int GraphicsReadFramebuffer()
            => _graphicsOutputReady ? _graphicsOutputFramebuffer : _frameBuffer;

        private void EnsureGraphicsPipeline()
        {
            if (_graphicsProgram == 0)
            {
                int vertex = CompileGraphicsShader(ShaderType.VertexShader,
                    Mods.Render.GraphicsPipelineShader.VertexSource);
                int fragment = 0;
                try
                {
                    fragment = CompileGraphicsShader(ShaderType.FragmentShader,
                        Mods.Render.GraphicsPipelineShader.FragmentSource);
                    _graphicsProgram = GL.CreateProgram();
                    GL.AttachShader(_graphicsProgram, vertex);
                    GL.AttachShader(_graphicsProgram, fragment);
                    GL.LinkProgram(_graphicsProgram);
#if !ANDROID
                    GL.GetProgram(_graphicsProgram, GetProgramParameterName.LinkStatus, out int linked);
                    if (linked == 0)
                    {
                        throw new ProgramException(GL.GetProgramInfoLog(_graphicsProgram));
                    }
#endif
                    _gfxSceneSampler = GL.GetUniformLocation(_graphicsProgram, "tex");
                    _gfxDepthSampler = GL.GetUniformLocation(_graphicsProgram, "depth_tex");
                    _gfxTexel = GL.GetUniformLocation(_graphicsProgram, "texel");
                    _gfxNear = GL.GetUniformLocation(_graphicsProgram, "near_plane");
                    _gfxFar = GL.GetUniformLocation(_graphicsProgram, "far_plane");
                    _gfxDepthAvailable = GL.GetUniformLocation(_graphicsProgram, "depth_available");
                    _gfxAa = GL.GetUniformLocation(_graphicsProgram, "aa_mode");
                    _gfxSharpen = GL.GetUniformLocation(_graphicsProgram, "sharpen_strength");
                    _gfxBloom = GL.GetUniformLocation(_graphicsProgram, "bloom_enable");
                    _gfxBloomIntensity = GL.GetUniformLocation(_graphicsProgram, "bloom_intensity");
                    _gfxGrade = GL.GetUniformLocation(_graphicsProgram, "grade_mode");
                    _gfxGamma = GL.GetUniformLocation(_graphicsProgram, "gamma_value");
                    _gfxContrast = GL.GetUniformLocation(_graphicsProgram, "contrast_value");
                    _gfxSaturation = GL.GetUniformLocation(_graphicsProgram, "saturation_value");
                    _gfxLighting = GL.GetUniformLocation(_graphicsProgram, "enhanced_lighting");
                    _gfxAo = GL.GetUniformLocation(_graphicsProgram, "ao_quality");
                    _gfxContactShadows = GL.GetUniformLocation(_graphicsProgram, "contact_shadows");
                    _gfxEnhancedFog = GL.GetUniformLocation(_graphicsProgram, "enhanced_fog");
                    _gfxVolumetricFog = GL.GetUniformLocation(_graphicsProgram, "volumetric_fog");
                    _gfxHdr = GL.GetUniformLocation(_graphicsProgram, "hdr_mode");
                    _gfxReflections = GL.GetUniformLocation(_graphicsProgram, "reflections");
                    _gfxDynamicGlow = GL.GetUniformLocation(_graphicsProgram, "dynamic_glow");
                    _gfxFogColor = GL.GetUniformLocation(_graphicsProgram, "fog_color");
                    _gfxTime = GL.GetUniformLocation(_graphicsProgram, "time_value");
                }
                finally
                {
                    GL.DeleteShader(vertex);
                    if (fragment != 0) GL.DeleteShader(fragment);
                }
            }

            if (_graphicsOutputFramebuffer == 0)
            {
                _graphicsOutputFramebuffer = GL.GenFramebuffer();
                _graphicsOutputTexture = GL.GenTexture();
            }

            if (_graphicsOutputSize != _targetSize)
            {
                GL.BindTexture(TextureTarget.Texture2D, _graphicsOutputTexture);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                    _targetSize.X, _targetSize.Y, 0, PixelFormat.Rgba,
                    PixelType.UnsignedByte, IntPtr.Zero);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                    (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                    (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                    (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                    (int)TextureWrapMode.ClampToEdge);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, _graphicsOutputFramebuffer);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                    TextureTarget.Texture2D, _graphicsOutputTexture, 0);
                ValidateFramebuffer("Enhanced graphics");
                GL.BindTexture(TextureTarget.Texture2D, 0);
                _graphicsOutputSize = _targetSize;
            }
        }

        private static int CompileGraphicsShader(ShaderType type, string source)
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

        private static void DrawGraphicsFullscreenQuad()
        {
            GL.Begin(PrimitiveType.TriangleStrip);
            GL.TexCoord3(1f, 1f, 0f); GL.Vertex3(1f, 1f, 0f);
            GL.TexCoord3(0f, 1f, 0f); GL.Vertex3(-1f, 1f, 0f);
            GL.TexCoord3(1f, 0f, 0f); GL.Vertex3(1f, -1f, 0f);
            GL.TexCoord3(0f, 0f, 0f); GL.Vertex3(-1f, -1f, 0f);
            GL.End();
        }
    }
}

namespace MphRead.Mods.Render
{
    internal static class GraphicsPipelineShader
    {
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
        public static string FragmentSource { get; } = "#version 300 es\nprecision highp float;\nprecision highp int;\n"
            + "in vec2 texcoord;\nout vec4 frag_color;\nuniform highp sampler2D depth_tex;\n"
            + "#define SAMPLE texture\n#define OUTPUT frag_color\n" + Body;
#else
        public static string VertexSource { get; } = @"#version 120
varying vec2 texcoord;
void main() {
    gl_Position = vec4(gl_Vertex.xy, 0.0, 1.0);
    texcoord = gl_MultiTexCoord0.xy;
}
";
        public static string FragmentSource { get; } = "#version 120\nvarying vec2 texcoord;\n"
            + "uniform sampler2D depth_tex;\n#define SAMPLE texture2D\n#define OUTPUT gl_FragColor\n" + Body;
#endif

        private const string Body = @"
uniform sampler2D tex;
uniform vec2 texel;
uniform float near_plane;
uniform float far_plane;
uniform int depth_available;
uniform int aa_mode;
uniform float sharpen_strength;
uniform int bloom_enable;
uniform float bloom_intensity;
uniform int grade_mode;
uniform float gamma_value;
uniform float contrast_value;
uniform float saturation_value;
uniform int enhanced_lighting;
uniform int ao_quality;
uniform int contact_shadows;
uniform int enhanced_fog;
uniform int volumetric_fog;
uniform int hdr_mode;
uniform int reflections;
uniform int dynamic_glow;
uniform vec4 fog_color;
uniform float time_value;

vec2 uv_clamp(vec2 uv) {
    return clamp(uv, vec2(0.0), vec2(1.0));
}

vec3 scene(vec2 uv) {
    return SAMPLE(tex, uv_clamp(uv)).rgb;
}

float luma(vec3 c) {
    return dot(c, vec3(0.2126, 0.7152, 0.0722));
}

float raw_depth(vec2 uv) {
    if (depth_available == 0) return 1.0;
    return SAMPLE(depth_tex, uv_clamp(uv)).r;
}

float view_depth(float d) {
    float n = max(near_plane, 0.0001);
    float f = max(far_plane, n + 0.01);
    float z = d * 2.0 - 1.0;
    return (2.0 * n * f) / max(0.0001, f + n - z * (f - n));
}

vec3 fxaa(vec2 uv, vec3 center) {
    if (aa_mode == 0) return center;
    float m = luma(center);
    float n = luma(scene(uv + vec2(0.0, texel.y)));
    float s = luma(scene(uv - vec2(0.0, texel.y)));
    float e = luma(scene(uv + vec2(texel.x, 0.0)));
    float w = luma(scene(uv - vec2(texel.x, 0.0)));
    float lo = min(m, min(min(n, s), min(e, w)));
    float hi = max(m, max(max(n, s), max(e, w)));
    if (hi - lo < max(0.035, hi * 0.08)) return center;

    float edgeX = abs(w - e);
    float edgeY = abs(n - s);
    vec2 dir = edgeX > edgeY ? vec2(0.0, texel.y) : vec2(texel.x, 0.0);
    vec3 a = scene(uv - dir * 0.5);
    vec3 b = scene(uv + dir * 0.5);
    vec3 resolved = (a + b) * 0.5;
    if (aa_mode > 1) {
        vec3 c = scene(uv - dir * 1.5);
        vec3 d = scene(uv + dir * 1.5);
        resolved = resolved * 0.75 + (c + d) * 0.125;
    }
    return resolved;
}

vec3 depth_normal(vec2 uv, float centerDepth) {
    if (depth_available == 0 || centerDepth >= 0.999999) return vec3(0.0, 0.0, 1.0);
    float c = view_depth(centerDepth);
    float l = view_depth(raw_depth(uv - vec2(texel.x, 0.0)));
    float r = view_depth(raw_depth(uv + vec2(texel.x, 0.0)));
    float d = view_depth(raw_depth(uv - vec2(0.0, texel.y)));
    float u = view_depth(raw_depth(uv + vec2(0.0, texel.y)));
    float scale = max(c, 1.0);
    vec2 slope = vec2(r - l, u - d) / scale;
    return normalize(vec3(-slope.x * 3.5, -slope.y * 3.5, 1.0));
}

float ambient_occlusion(vec2 uv, float centerDepth) {
    if (depth_available == 0 || ao_quality == 0 || centerDepth >= 0.999999) return 1.0;
    float c = view_depth(centerDepth);
    float radius = ao_quality == 1 ? 1.5 : ao_quality == 2 ? 2.5 : 4.0;
    float bias = max(0.015, c * 0.0020);
    float range = max(0.12, c * 0.035);
    float occ = 0.0;
    vec2 dx = vec2(texel.x * radius, 0.0);
    vec2 dy = vec2(0.0, texel.y * radius);
    vec2 dg = vec2(texel.x * radius * 0.7071, texel.y * radius * 0.7071);
    float z0 = view_depth(raw_depth(uv + dx));
    float z1 = view_depth(raw_depth(uv - dx));
    float z2 = view_depth(raw_depth(uv + dy));
    float z3 = view_depth(raw_depth(uv - dy));
    float z4 = view_depth(raw_depth(uv + dg));
    float z5 = view_depth(raw_depth(uv - dg));
    float z6 = view_depth(raw_depth(uv + vec2(dg.x, -dg.y)));
    float z7 = view_depth(raw_depth(uv + vec2(-dg.x, dg.y)));
    occ += smoothstep(bias, range, c - z0);
    occ += smoothstep(bias, range, c - z1);
    occ += smoothstep(bias, range, c - z2);
    occ += smoothstep(bias, range, c - z3);
    occ += smoothstep(bias, range, c - z4);
    occ += smoothstep(bias, range, c - z5);
    occ += smoothstep(bias, range, c - z6);
    occ += smoothstep(bias, range, c - z7);
    float strength = ao_quality == 1 ? 0.10 : ao_quality == 2 ? 0.17 : 0.24;
    return clamp(1.0 - occ * (strength / 8.0), 0.68, 1.0);
}

float contact_shadow(vec2 uv, float centerDepth) {
    if (depth_available == 0 || contact_shadows == 0 || centerDepth >= 0.999999) return 1.0;
    float c = view_depth(centerDepth);
    vec2 dir = normalize(vec2(-0.65, 0.75));
    float shadow = 0.0;
    float bias = max(0.01, c * 0.0015);
    for (int i = 1; i <= 4; i++) {
        float fi = float(i);
        float z = view_depth(raw_depth(uv + dir * texel * (fi * 2.0)));
        float delta = c - z;
        shadow += smoothstep(bias, max(bias * 8.0, c * 0.025), delta) * (1.0 / fi);
    }
    return 1.0 - clamp(shadow * 0.055, 0.0, 0.18);
}

vec3 bloom_value(vec2 uv) {
    if (bloom_enable == 0 && dynamic_glow == 0) return vec3(0.0);
    float threshold = dynamic_glow != 0 ? 0.48 : 0.62;
    vec3 sum = vec3(0.0);
    float weight = 0.0;
    vec2 x2 = vec2(texel.x * 2.0, 0.0);
    vec2 y2 = vec2(0.0, texel.y * 2.0);
    vec2 x4 = vec2(texel.x * 4.0, 0.0);
    vec2 y4 = vec2(0.0, texel.y * 4.0);
    vec3 taps[8];
    taps[0] = scene(uv + x2); taps[1] = scene(uv - x2);
    taps[2] = scene(uv + y2); taps[3] = scene(uv - y2);
    taps[4] = scene(uv + x4); taps[5] = scene(uv - x4);
    taps[6] = scene(uv + y4); taps[7] = scene(uv - y4);
    for (int i = 0; i < 8; i++) {
        float b = max(0.0, luma(taps[i]) - threshold);
        b = b / max(0.001, 1.0 - threshold);
        sum += taps[i] * b;
        weight += b;
    }
    if (weight <= 0.0001) return vec3(0.0);
    float intensity = bloom_enable != 0 ? bloom_intensity : 0.25;
    if (dynamic_glow != 0) intensity += 0.18;
    return (sum / max(weight, 1.0)) * clamp(weight / 5.0, 0.0, 1.0) * intensity;
}

vec3 grade(vec3 c) {
    if (grade_mode == 1) {
        c = pow(max(c, vec3(0.0)), vec3(0.96));
        c = c * vec3(1.015, 1.01, 1.02);
    }
    else if (grade_mode == 2) {
        float y = luma(c);
        c = mix(vec3(y), c, 1.16);
        c *= vec3(1.025, 1.01, 1.02);
    }
    else if (grade_mode == 3) {
        float y = luma(c);
        vec3 shadows = c * vec3(0.94, 0.98, 1.06);
        vec3 highlights = c * vec3(1.06, 1.015, 0.96);
        c = mix(shadows, highlights, smoothstep(0.20, 0.82, y));
        c = mix(vec3(luma(c)), c, 1.10);
    }
    return c;
}

vec3 aces(vec3 x) {
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

void main() {
    vec2 uv = texcoord;
    vec3 center = scene(uv);
    vec3 color = fxaa(uv, center);
    float d = raw_depth(uv);

    if (sharpen_strength > 0.0001) {
        vec3 blur = (scene(uv + vec2(texel.x, 0.0))
            + scene(uv - vec2(texel.x, 0.0))
            + scene(uv + vec2(0.0, texel.y))
            + scene(uv - vec2(0.0, texel.y))) * 0.25;
        vec3 detail = color - blur;
        float guard = 1.0 - smoothstep(0.16, 0.45, length(detail));
        color += detail * sharpen_strength * 0.75 * guard;
    }

    if (depth_available != 0 && d < 0.999999) {
        vec3 n = depth_normal(uv, d);
        if (enhanced_lighting != 0) {
            vec3 ld = normalize(vec3(-0.45, 0.58, 0.68));
            float diffuse = dot(n, ld) * 0.5 + 0.5;
            float relief = mix(0.93, 1.09, diffuse);
            float spec = pow(max(0.0, dot(reflect(-ld, n), vec3(0.0, 0.0, 1.0))), 18.0);
            color = color * relief + vec3(spec * 0.035);
        }
        color *= ambient_occlusion(uv, d);
        color *= contact_shadow(uv, d);
        if (reflections != 0) {
            float fresnel = pow(clamp(1.0 - n.z, 0.0, 1.0), 3.0);
            color += vec3(0.035, 0.050, 0.070) * fresnel;
        }

        if (enhanced_fog != 0 || volumetric_fog != 0) {
            float dist = view_depth(d);
            float f = max(far_plane, near_plane + 1.0);
            float normalizedDistance = clamp(dist / f, 0.0, 1.0);
            float fog = 1.0 - exp(-normalizedDistance * 2.35);
            if (volumetric_fog != 0) {
                float wave = sin(uv.x * 37.0 + time_value * 0.27)
                    * sin(uv.y * 29.0 - time_value * 0.19);
                fog *= 0.88 + 0.12 * wave;
                fog += (1.0 - n.z) * 0.025;
            }
            float amount = enhanced_fog != 0 ? 0.18 : 0.08;
            if (volumetric_fog != 0) amount += 0.10;
            color = mix(color, fog_color.rgb, clamp(fog * amount, 0.0, 0.32));
        }
    }

    color += bloom_value(uv);
    color = grade(color);

    float y = luma(color);
    color = mix(vec3(y), color, max(0.0, saturation_value));
    color = (color - vec3(0.5)) * contrast_value + vec3(0.5);
    color = pow(max(color, vec3(0.0)), vec3(1.0 / max(0.25, gamma_value)));

    if (hdr_mode != 0) {
        color = aces(max(color, vec3(0.0)));
    }
    OUTPUT = vec4(clamp(color, 0.0, 1.0), 1.0);
}
";
    }
}
