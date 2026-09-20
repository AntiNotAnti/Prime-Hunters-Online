using System;
using System.Collections.Generic;
using MphRead.Entities;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;

namespace MphRead
{
    /// <summary>
    /// The results screen's hunter preview, drawn as a small window with its
    /// own camera cut into the corner of the frame.
    ///
    /// A partial of Scene because everything it needs -- the render-item
    /// lists, <c>RenderItem</c>, the shader locations -- is private to the
    /// renderer, and because this is a *pass*, not an entity: the model is
    /// collected like any other (see
    /// <see cref="Mods.Render.HunterPreviewEntity"/>) and then drawn again
    /// afterwards into a scissored rectangle whose depth buffer is its own.
    ///
    /// That separation is the whole design. Drawn in the world it would be
    /// occluded by whatever the camera happens to be behind, lit by whatever
    /// the room is lit by, and culled by the same portal walk that has already
    /// caused two crashes on this screen. Drawn as a pass it is none of those
    /// things -- and the cost is one scissored clear and a second pass over
    /// about forty meshes, on a screen where nobody is playing.
    /// </summary>
    public partial class Scene
    {
        private readonly List<RenderItem> _previewItems = new List<RenderItem>();
        private Mods.Render.HunterPreviewEntity? _preview;

        /// <summary>
        /// True while the preview's items are being built, so
        /// <c>AddRenderItem</c> puts them aside instead of into the world's
        /// three lists. Set for exactly one call.
        /// </summary>
        private bool _collectingPreview;

        /// <summary>The hunter whose meshes have been handed to GL.</summary>
        private Hunter _previewInited = Hunter.Random;

        /// <summary>
        /// Where the preview goes, in fractions of the window: the panel's
        /// portrait slot. Published by the HUD, because the panel decides its
        /// own layout and this only has to land in the hole it left.
        /// </summary>
        public static float PreviewLeft { get; set; }
        public static float PreviewTop { get; set; }
        public static float PreviewRight { get; set; }
        public static float PreviewBottom { get; set; }

        /// <summary>Whether the HUD asked for a preview this frame.</summary>
        public static bool PreviewWanted { get; set; }

        /// <summary>
        /// The launcher is asking, rather than the results screen.
        ///
        /// The two want the same picture for the same reason -- "who am I
        /// going in as" is the same question before a match and between two --
        /// and the pass below does not care which of them is asking. What
        /// differs is only where the hunter and the suit are read from, and
        /// that the launcher's frame has no world in it at all: see
        /// <see cref="ModDrawPreviewAlone"/>.
        /// </summary>
        public static bool LauncherPreview { get; set; }

        /// <summary>Who the launcher is showing, while it is the one asking.</summary>
        public static Hunter LauncherHunter { get; set; } = Hunter.Samus;

        public static int LauncherSuit { get; set; }

        /// <summary>Did anybody ask for a preview this frame?</summary>
        private static bool PreviewAsked => Mods.EndScreen.Available || LauncherPreview;

        /// <summary>
        /// Whether a hunter was actually put on the screen last frame, by
        /// either path.
        ///
        /// What the screens read to decide between leaving a hole and drawing
        /// their own boxes. Static because the control asking is in the
        /// launcher's tree and has no scene to ask.
        /// </summary>
        public static bool PreviewDrawnLastFrame { get; private set; }

        /// <summary>Who that frame actually had in it. See HunterPreviewEntity.Shown.</summary>
        public static Hunter PreviewDrawnHunter { get; private set; } = Hunter.Random;

        public static int PreviewDrawnSuit { get; private set; } = -1;

        /// <summary>
        /// Turn the model, once a simulation step. Called from the step rather
        /// than the draw for the reason everything else here is: a picture with
        /// no step behind it must not advance anything, or the hunter spins at
        /// the frame rate.
        /// </summary>
        public void ModStepPreview()
        {
            if (!PreviewAsked)
            {
                _preview?.Reset();
                // So a rectangle from the last results screen cannot be used
                // by the next one before the panel has published its own.
                PreviewWanted = false;
                PreviewLeft = PreviewRight = PreviewTop = PreviewBottom = 0;
                return;
            }
            _preview ??= new Mods.Render.HunterPreviewEntity(this);
            Hunter want = LauncherPreview ? LauncherHunter : Mods.EndScreen.Hunter;
            _preview.SetUp(want, LauncherPreview ? LauncherSuit : Mods.EndScreen.Suit);
            // Textures and display lists, which nobody else is going to make.
            //
            // In a match this is free and invisible: the player standing in
            // the room is the same hunter model, `Read` caches the Model, and
            // `GenerateLists` writes the list id onto that shared Model -- so
            // the preview has always been reusing lists the player's own
            // entity generated. On the launcher there is no player and no
            // room, so nothing ever did, and the first draw died looking a
            // palette up by an id that was never registered.
            if (_previewInited != want)
            {
                _previewInited = want;
                if (_preview.Ready)
                {
                    InitEntity(_preview);
                }
            }
            _preview.Step();
        }

        /// <summary>
        /// Build the preview's render items, after every entity has built
        /// theirs. Nothing else may add items while this runs.
        /// </summary>
        private void ModCollectPreview()
        {
            _previewItems.Clear();
            if (!PreviewAsked || _preview == null || !_preview.Ready)
            {
                return;
            }
            _collectingPreview = true;
            try
            {
                _preview.GetDrawInfo();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[endscreen] preview draw failed: {ex.Message}");
                _previewItems.Clear();
            }
            finally
            {
                _collectingPreview = false;
            }
        }

        /// <summary>
        /// Whether the panel should leave its slot empty for the model rather
        /// than filling it with the sprite portrait.
        ///
        /// Needs a rectangle as well as items, because the rectangle is
        /// published by the panel and so arrives a frame after the first set
        /// of items. One frame of sprite at the top of a ten-second screen is
        /// not something anybody sees; a hole with nothing in it would be.
        /// </summary>
        public bool ModPreviewDrawn => _previewItems.Count > 0
            && PreviewRight - PreviewLeft > 0.001f && PreviewBottom - PreviewTop > 0.001f;

        /// <summary>
        /// How far the camera stands from the hunter, and what it looks at.
        ///
        /// A hunter biped is about two units tall with its feet near the
        /// origin, so the eye sits at chest height and backs off far enough
        /// for the whole of it to fit a square window at a 40-degree field --
        /// wide enough to read the shape, narrow enough not to distort it the
        /// way a wide angle at this distance would.
        /// </summary>
        private static readonly Vector3 _previewEye = new Vector3(0, 1.05f, 3.15f);
        private static readonly Vector3 _previewTarget = new Vector3(0, 0.95f, 0);
        private const float PreviewFov = 40;

        /// <summary>The window's own background, behind the model.</summary>
        private static readonly Color4 _previewBack = new Color4(0.05f, 0.055f, 0.07f, 1f);

        /// <summary>
        /// Draw it. Called once the world's passes are finished and the HUD's
        /// dimming filter is on, so the preview is the one thing on the screen
        /// that is not dimmed -- which is right, it is the thing being asked
        /// about.
        /// </summary>
        /// <summary>
        /// The same hunter, in a frame with no match behind it: the launcher's
        /// own screens.
        ///
        /// <para>
        /// Everything the pass below needs was set up by <see cref="OnLoad"/>
        /// -- the shader, the toon table, the shift table -- and none of it
        /// needs a room. What a match's frame adds is the world, and this
        /// draws no world: it is one scissored rectangle with its own camera
        /// and its own cleared depth, which is what the pass already was. So
        /// the launcher stands a scene up with nothing in it and calls this.
        /// </para>
        /// <para>
        /// Straight into the back buffer rather than into the scene's own
        /// offscreen target, because nothing is going to present that target:
        /// the launcher's frame is the photograph, this, and the screens'
        /// texture over the top. <paramref name="windowSize"/> is therefore
        /// the window's, and it is what the rectangle is measured against.
        /// </para>
        /// <para>
        /// Returns whether anything was actually drawn, and that answer is
        /// load-bearing: the screens only leave a hole where the model goes
        /// once this has said yes, so a hunter whose model will not load, or
        /// the frames before it has, show the drawn stand instead of a hole
        /// with nothing in it.
        /// </para>
        /// </summary>
        public bool ModDrawPreviewAlone(Vector2i windowSize)
        {
            if (!LauncherPreview || windowSize.X <= 0 || windowSize.Y <= 0)
            {
                return false;
            }
            _targetSize = windowSize;
            try
            {
                ModStepPreview();
                ModCollectPreview();
                if (!ModPreviewDrawn)
                {
                    return false;
                }
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                GL.UseProgram(_shaderProgramId);
                ModDrawPreview();
                GL.UseProgram(0);
                return true;
            }
            catch (Exception ex)
            {
                // A preview that will not draw is the launcher's boxes again,
                // not a dead launcher. Said once: this is a per-frame path.
                if (!_previewComplained)
                {
                    _previewComplained = true;
                    Mods.DebugLog.Line("ui", $"the hunter preview could not be drawn: {ex.Message}");
                }
                LauncherPreview = false;
                return false;
            }
        }

        private bool _previewComplained;

        private void ModDrawPreview()
        {
            if (_previewItems.Count == 0 || !PreviewWanted)
            {
                PreviewDrawnLastFrame = false;
                return;
            }
            // Not from inside the world's render while the deck panel is up.
            // That draws the model *under* the screens, and the panel it goes
            // in is opaque -- so it would be a hunter behind a card, drawn for
            // nothing. The desktop's UiOverlay draws it over the screens
            // instead, once the texture is down (see LauncherHunter); the head
            // with no window under its screens has the picture inside the
            // panel already (see HunterShot).
            if (Mods.EndScreen.PanelUp && !LauncherPreview)
            {
                return;
            }
            Vector2i target = _targetSize;
            // The rectangle, in the render target's pixels rather than the
            // window's: the scene may be rendered smaller than the window and
            // stretched (see RenderSize), and this pass is inside that target.
            // Y flips, because a HUD measures down from the top and OpenGL
            // measures up from the bottom.
            int x = (int)MathF.Round(PreviewLeft * target.X);
            int y = (int)MathF.Round((1 - PreviewBottom) * target.Y);
            int width = (int)MathF.Round((PreviewRight - PreviewLeft) * target.X);
            int height = (int)MathF.Round((PreviewBottom - PreviewTop) * target.Y);
            if (width < 4 || height < 4)
            {
                return;
            }
            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(x, y, width, height);
            GL.ClearColor(_previewBack);
            // Depth as well as colour, and this is the point of the whole
            // pass: the world's depth buffer is full of a level the model is
            // nowhere near, and without clearing it the hunter would be behind
            // a wall it is not standing near.
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            GL.ClearColor(0, 0, 0, 0);
            GL.Viewport(x, y, width, height);
            Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(PreviewFov),
                width / (float)height, 0.1f, 100f);
            Matrix4 view = Matrix4.LookAt(_previewEye, _previewTarget, Vector3.UnitY);
            GL.UniformMatrix4(_shaderLocations.ProjectionMatrix, transpose: false, ref projection);
            GL.UniformMatrix4(_shaderLocations.ViewMatrix, transpose: false, ref view);
            // No fog, whatever the room does with it: a preview window is not
            // in the room, and the far end of a foggy level would have the
            // hunter fade into the panel.
            GL.Uniform1(_shaderLocations.UseFog, 0);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Less);
            GL.DepthMask(true);
            GL.Disable(EnableCap.StencilTest);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Disable(EnableCap.AlphaTest);
            for (int i = 0; i < _previewItems.Count; i++)
            {
                RenderItem(_previewItems[i]);
            }
            // Everything back the way the HUD expects to find it.
            GL.Disable(EnableCap.ScissorTest);
            GL.Viewport(0, 0, target.X, target.Y);
            GL.UniformMatrix4(_shaderLocations.ProjectionMatrix, transpose: false, ref _perspectiveMatrix);
            GL.UniformMatrix4(_shaderLocations.ViewMatrix, transpose: false, ref _viewMatrix);
            GL.Uniform1(_shaderLocations.UseFog, _hasFog && FogOn ? 1 : 0);
            GL.PolygonMode(TriangleFace.FrontAndBack, OpenTK.Graphics.OpenGL.PolygonMode.Fill);
            PreviewDrawnLastFrame = true;
            PreviewDrawnHunter = _preview?.Shown ?? Hunter.Random;
            PreviewDrawnSuit = _preview?.ShownSuit ?? -1;
        }
    }
}
