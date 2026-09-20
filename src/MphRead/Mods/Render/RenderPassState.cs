using System;
using System.Diagnostics;
using MphRead.Entities;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;

namespace MphRead
{
    public partial class Scene
    {
        private int _worldSampler, _rttSampler, _maskSampler, _shiftSampler;
        private int _celSampler, _celDepthSampler;
        private bool _hudMaskActive;
        private int _pendingRenderEvents;
        private long _renderSequence;
        private long _respawnRenderSequence = -1;
        private readonly int[] _renderViewport = new int[4];
        public int RenderGlErrorCount { get; private set; }
        internal long RenderPostProcessCount { get; private set; }
        internal bool ConsoleOutputEnabled { get; set; } = true;

        private void InitRenderPassState()
        {
            _worldSampler = GL.GetUniformLocation(_shaderProgramId, "tex");
            _rttSampler = GL.GetUniformLocation(_rttShaderProgramId, "tex");
            _maskSampler = GL.GetUniformLocation(_rttShaderProgramId, "mask");
            _shiftSampler = GL.GetUniformLocation(_shiftShaderProgramId, "tex");
            _celSampler = GL.GetUniformLocation(_celShaderProgramId, "tex");
            _celDepthSampler = GL.GetUniformLocation(_celShaderProgramId, "depth_tex");
        }

        private static void ClearRenderTextures()
        {
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        private void BeginWorldPass()
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _frameBuffer);
            GL.Viewport(0, 0, _targetSize.X, _targetSize.Y);
            GL.UseProgram(_shaderProgramId);
            ClearRenderTextures();
            GL.Enable(EnableCap.DepthTest);
            GL.DepthMask(true);
            GL.DepthFunc(DepthFunction.Less);
            GL.ColorMask(true, true, true, true);
            GL.StencilMask(0xFF);
            GL.ClearStencil(0);
            GL.ClearColor(_clearColor);
            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.StencilTest);
            GL.Disable(EnableCap.AlphaTest);
            GL.Disable(EnableCap.PolygonOffsetFill);
            GL.Disable(EnableCap.Blend);
            GL.BlendEquation(BlendEquationMode.FuncAdd);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            if (_faceCulling)
            {
                GL.Enable(EnableCap.CullFace);
                GL.CullFace(TriangleFace.Back);
            }
            else
            {
                GL.Disable(EnableCap.CullFace);
            }
            GL.PolygonMode(TriangleFace.FrontAndBack, OpenTK.Graphics.OpenGL.PolygonMode.Fill);
            GL.Uniform1(_worldSampler, 0);
            GL.Uniform1(_shaderLocations.UseOverride, 0);
            GL.Uniform1(_shaderLocations.UsePaletteOverride, 0);
            GL.Uniform1(_shaderLocations.UseFlat, 0);
            GL.Uniform1(_shaderLocations.UseTexture, 0);
            GL.Uniform1(_shaderLocations.UseLight, 0);
            GL.Uniform1(_shaderLocations.MaterialAlpha, 1f);
            GL.Uniform1(_shaderLocations.MaterialMode, (int)PolygonMode.Modulate);
            GL.Uniform1(_shaderLocations.TexgenMode, (int)TexgenMode.None);
            Matrix4 identity = Matrix4.Identity;
            GL.UniformMatrix4(_shaderLocations.TextureMatrix, false, ref identity);
            GL.UniformMatrix4(_shaderLocations.ViewMatrix, false, ref _viewMatrix);
            GL.UniformMatrix4(_shaderLocations.ProjectionMatrix, false, ref _perspectiveMatrix);
            // Per-material matrices/lighting are set by RenderItem. Frame uniforms
            // (including fade progression) remain in UpdateUniforms, once per draw.
            CheckGlError("BeginWorldPass");
        }

        private static void SetScreenPassState()
        {
            GL.Disable(EnableCap.DepthTest);
            GL.DepthMask(false);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.StencilTest);
            GL.Disable(EnableCap.AlphaTest);
            GL.Disable(EnableCap.PolygonOffsetFill);
            GL.ColorMask(true, true, true, true);
            GL.PolygonMode(TriangleFace.FrontAndBack, OpenTK.Graphics.OpenGL.PolygonMode.Fill);
            GL.ActiveTexture(TextureUnit.Texture0);
        }

        private void BeginPostProcessPass()
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _frameBuffer);
            GL.Viewport(0, 0, _targetSize.X, _targetSize.Y);
            SetScreenPassState();
            GL.Disable(EnableCap.Blend);
            ClearRenderTextures();
            CheckGlError("BeginPostProcessPass");
        }

        private void UseHudShader()
        {
            GL.UseProgram(_rttShaderProgramId);
            GL.Uniform1(_rttSampler, 0);
            GL.Uniform1(_maskSampler, 1);
            GL.Uniform1(_shaderLocations.UseMask, 0);
            GL.Uniform1(_shaderLocations.LayerAlpha, 1f);
            GL.Uniform4(_shaderLocations.FadeColor, Vector4.Zero);
            GL.Uniform1(_shaderLocations.ViewWidth, (float)Size.X);
            GL.Uniform1(_shaderLocations.ViewHeight, (float)Size.Y);
            // RTT vertices are already in clip space; its vertex shader does
            // not consume the world's projection or fixed-function matrices.
            _hudMaskActive = false;
        }

        private void BeginCompositePass()
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.Viewport(0, 0, Size.X, Size.Y);
            SetScreenPassState();
            GL.Enable(EnableCap.Blend);
            GL.BlendEquation(BlendEquationMode.FuncAdd);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            ClearRenderTextures();
            UseHudShader();
            PlayerEntity player = PlayerEntity.Main;
            // Reset even when inactive: switching effects must not revive the
            // previous life's shift/whiteout values.
            GL.UseProgram(_shiftShaderProgramId);
            GL.Uniform1(_shiftSampler, 0);
            float phase = _elapsedTime / (1 / 30f);
            GL.Uniform1(_shaderLocations.ShiftIndex, (int)phase);
            GL.Uniform1(_shaderLocations.LerpFactor, phase % 1);
            GL.Uniform1(_shaderLocations.ShiftFactor,
                player.HudDisruptedState != 0 ? player.HudDisruptionFactor : 0f);
            GL.Uniform1(_shaderLocations.WhiteoutFactor,
                player.HudWhiteoutState != -1 ? player.HudWhiteoutFactor : 0f);
            if (player.HudWhiteoutState != -1 && player.HudWhiteoutFactor != 0)
            {
                GL.Uniform1(_shaderLocations.WhiteoutTable, 192, PlayerEntity.HudWhiteoutTable);
            }
            if (player.HudDisruptedState == 0 && player.HudWhiteoutState == -1)
            {
                GL.UseProgram(_rttShaderProgramId);
            }
            GL.BindTexture(TextureTarget.Texture2D, _screenTexture);
            CheckGlError("BeginCompositePass");
        }

        private void BeginHudPass()
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.Viewport(0, 0, Size.X, Size.Y);
            SetScreenPassState();
            GL.Enable(EnableCap.Blend);
            GL.BlendEquation(BlendEquationMode.FuncAdd);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            ClearRenderTextures();
            UseHudShader();
            CheckGlError("BeginHudPass");
        }

        private void BeginHudMask(int maskId)
        {
            _hudMaskActive = maskId != -1;
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, _hudMaskActive ? maskId : 0);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.Uniform1(_shaderLocations.UseMask, _hudMaskActive ? 1 : 0);
            GL.Uniform1(_shaderLocations.ViewWidth, (float)Size.X);
            GL.Uniform1(_shaderLocations.ViewHeight, (float)Size.Y);
        }

        private void EndHudMask()
        {
            _hudMaskActive = false;
            GL.UseProgram(_rttShaderProgramId);
            GL.Uniform1(_shaderLocations.UseMask, 0);
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.ActiveTexture(TextureUnit.Texture0);
        }

        private void EndHudPass()
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.Viewport(0, 0, Size.X, Size.Y);
            EndHudMask();
            GL.Uniform4(_shaderLocations.FadeColor, Vector4.Zero);
            GL.Uniform1(_shaderLocations.LayerAlpha, 1f);
            ClearRenderTextures();
            GL.DepthMask(true);
            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.StencilTest);
            GL.Disable(EnableCap.AlphaTest);
            CheckGlError("EndHudPass");
        }

        private static void ValidateFramebuffer(string name)
        {
            FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != FramebufferErrorCode.FramebufferComplete)
            {
                throw new InvalidOperationException($"{name} framebuffer incomplete: {status}");
            }
        }

        [Conditional("DEBUG")]
        private void CheckGlError(string stage)
        {
            ErrorCode error;
            while ((error = GL.GetError()) != ErrorCode.NoError)
            {
                RenderGlErrorCount++;
                Mods.DebugLog.Line("render", $"GL error at {stage}: {error}");
            }
        }

        // Simulation entry points may run without a GL context (server/load).
        // Record CPU state here; inspect GPU state only at the next draw.
        internal void NoteRenderLifecycle(string stage)
        {
            if (Mods.Headless.Active || !Mods.DebugLog.Active)
            {
                return;
            }
            int bit = stage switch
            {
                "death" => 1,
                "respawn requested" => 2,
                "spawn begin" => 4,
                "spawn complete" => 8,
                _ => 0
            };
            _pendingRenderEvents |= bit;
            PlayerEntity player = PlayerEntity.Main;
            Mods.DebugLog.Line("render", $"frame={_frameCount} {stage}: health={player.Health} "
                + $"respawn={player.RespawnTimer} load={player.LoadFlags} flags={player.Flags1} "
                + $"disruption={player.HudDisruptedState}/{player.HudDisruptionFactor} "
                + $"whiteout={player.HudWhiteoutState} fade={_fadeType}/{_fadePercent}");
        }

        private void BeginRenderDiagnostics()
        {
            _renderSequence++;
            if (_pendingRenderEvents == 0)
            {
                return;
            }
            if ((_pendingRenderEvents & 8) != 0)
            {
                _respawnRenderSequence = _renderSequence;
            }
            LogRenderState($"before world, lifecycle bits={_pendingRenderEvents}");
            _pendingRenderEvents = 0;
        }

        private void EndRenderDiagnostics()
        {
            long since = _renderSequence - _respawnRenderSequence;
            if (_respawnRenderSequence >= 0 && (since == 0 || since == 1 || since == 2 || since == 5 || since == 10))
            {
                LogRenderState($"respawn frame +{since}");
            }
            AssertRenderInvariants();
            CheckGlError("EndFrame");
        }

        private void LogRenderState(string stage)
        {
            if (!Mods.DebugLog.Active)
            {
                return;
            }
            int active = GL.GetInteger(GetPName.ActiveTexture);
            GL.ActiveTexture(TextureUnit.Texture0);
            int tex0 = GL.GetInteger(GetPName.TextureBinding2D);
            GL.ActiveTexture(TextureUnit.Texture1);
            int tex1 = GL.GetInteger(GetPName.TextureBinding2D);
            GL.ActiveTexture((TextureUnit)active);
            GL.GetInteger(GetPName.Viewport, _renderViewport);
            GL.GetUniform(_rttShaderProgramId, _shaderLocations.UseMask, out int mask);
            PlayerEntity player = PlayerEntity.Main;
            Mods.DebugLog.Line("render", $"frame={_frameCount} draw={_renderSequence} {stage}: "
                + $"fbo={GL.GetInteger(GetPName.FramebufferBinding)} read={GL.GetInteger(GetPName.ReadFramebufferBinding)} "
                + $"draw={GL.GetInteger(GetPName.DrawFramebufferBinding)} program={GL.GetInteger(GetPName.CurrentProgram)} "
                + $"viewport={String.Join(',', _renderViewport)} active={active} textures={tex0},{tex1} "
                + $"depth={GL.IsEnabled(EnableCap.DepthTest)}/{GL.GetInteger(GetPName.DepthWritemask)} "
                + $"blend={GL.IsEnabled(EnableCap.Blend)}/{GL.GetInteger(GetPName.BlendSrcRgb)}/{GL.GetInteger(GetPName.BlendDstRgb)} "
                + $"cull={GL.IsEnabled(EnableCap.CullFace)} scissor={GL.IsEnabled(EnableCap.ScissorTest)} "
                + $"screen={_screenTexture} sceneFbo={_frameBuffer} target={_targetSize} window={Size} "
                + $"mask={mask} fade={_fadeType}/{_fadePercent} disruption={player.HudDisruptedState}/{player.HudDisruptionFactor} "
                + $"whiteout={player.HudWhiteoutState} respawn={player.RespawnTimer} health={player.Health} load={player.LoadFlags}");
        }

        public bool CheckRenderInvariants()
        {
            int active = GL.GetInteger(GetPName.ActiveTexture);
            GL.ActiveTexture(TextureUnit.Texture1);
            int maskTexture = GL.GetInteger(GetPName.TextureBinding2D);
            GL.ActiveTexture((TextureUnit)active);
            GL.GetUniform(_rttShaderProgramId, _shaderLocations.UseMask, out int mask);
            GL.GetInteger(GetPName.Viewport, _renderViewport);
            return active == (int)TextureUnit.Texture0 && maskTexture == 0 && mask == 0
                && GL.GetInteger(GetPName.DrawFramebufferBinding) == 0
                && GL.GetInteger(GetPName.ReadFramebufferBinding) == 0
                && GL.GetInteger(GetPName.DepthWritemask) != 0
                && _renderViewport[0] == 0 && _renderViewport[1] == 0
                && _renderViewport[2] == Size.X && _renderViewport[3] == Size.Y;
        }

        [Conditional("DEBUG")]
        private void AssertRenderInvariants() => Debug.Assert(CheckRenderInvariants(), "Leaked end-of-frame GL state");
    }
}
