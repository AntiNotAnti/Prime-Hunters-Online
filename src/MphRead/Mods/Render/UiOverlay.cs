using System;
using OpenTK.Graphics.OpenGL;

namespace MphRead.Mods.Render
{
    /// <summary>
    /// The launcher's own picture, drawn inside the game window.
    ///
    /// This is the GL half of the unified window and knows nothing about the
    /// toolkit that produced the pixels: it is handed a buffer of RGBA and
    /// puts it on the screen as one quad over whatever the frame already
    /// holds. <see cref="Launcher.Gui.UiSurface"/> is the other half.
    ///
    /// Why a texture at all: the front screen, the pause menu and the settings
    /// are Avalonia, and Avalonia cannot draw into a GL context the engine
    /// owns. They used to be real windows laid over the game -- borderless,
    /// topmost, chasing the game window on every drag -- which is a second
    /// thing on the desktop pretending to be part of the first, and reads as
    /// exactly that. Rendered offscreen and uploaded, they are part of the
    /// frame: they cannot be alt-tabbed away from the game, cannot be left
    /// behind when it moves, and go fullscreen with it because there is only
    /// one window to go fullscreen.
    ///
    /// Premultiplied alpha, because that is what Avalonia renders. Blending
    /// with SrcAlpha instead would darken every edge of every glyph against
    /// the match behind the pause menu.
    /// </summary>
    public static class UiOverlay
    {
        private static int _texture;
        private static int _width;
        private static int _height;
        private static bool _hasFrame;

        /// <summary>Whether the last uploaded frame should be drawn.</summary>
        public static bool Visible { get; set; }

        /// <summary>True once a frame has been uploaded and not released.</summary>
        public static bool HasFrame => _hasFrame;

        /// <summary>
        /// Take a rendered UI frame. Tightly packed RGBA, top row first, which
        /// is what the headless surface hands over.
        /// </summary>
        public static void Upload(IntPtr pixels, int width, int height)
        {
            if (pixels == IntPtr.Zero || width <= 0 || height <= 0)
            {
                return;
            }
            // Unit 0, said out loud. Everything here is fixed-function, which
            // only ever looks at unit 0, and the scene leaves the active unit
            // wherever its last shader wanted it -- unit 1, after the visor
            // mask. Uploading to whatever unit happened to be current is how
            // the pause menu came out as a zoomed-in picture of the helmet:
            // the bind landed on unit 1 and unit 0 still held the last thing
            // the HUD drew.
            GL.ActiveTexture(TextureUnit.Texture0);
            if (_texture == 0)
            {
                _texture = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, _texture);
                // Linear, alone in this program apart from the weapon icons:
                // the UI is drawn at the window's own resolution, so there is
                // nothing to magnify, and linear is what keeps the one frame
                // where a resize has not caught up yet from showing its seams.
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                    (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                    (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                    (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                    (int)TextureWrapMode.ClampToEdge);
            }
            else
            {
                GL.BindTexture(TextureTarget.Texture2D, _texture);
            }
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            if (width != _width || height != _height)
            {
                _width = width;
                _height = height;
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height,
                    0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            }
            else
            {
                // The size is the window's and changes only when the player
                // drags it; every other frame is the same rectangle, and
                // respecifying one is a reallocation the driver cannot batch.
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, width, height,
                    PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            }
            GL.BindTexture(TextureTarget.Texture2D, 0);
            _hasFrame = true;
        }

        /// <summary>
        /// Put it on the screen, over everything drawn so far.
        ///
        /// Fixed function rather than the scene's shader: what this draws is
        /// one screen-filling quad in normalised coordinates, which is the one
        /// thing the fixed pipeline does without setting anything up, and the
        /// scene's programs all expect uniforms that belong to a world.
        /// </summary>
        public static void Draw(int width, int height)
        {
            if (!Visible || !_hasFrame || _texture == 0)
            {
                return;
            }
            // Its own viewport, never the one the frame was left with. The
            // scene sets the viewport from its own idea of the window's size,
            // and a screen drawn into a viewport that is not the framebuffer
            // is a menu sitting off the edge of the window -- which is what
            // the pause menu did after F11, and what a resize did to the front
            // screen.
            if (width > 0 && height > 0)
            {
                GL.Viewport(0, 0, width, height);
            }
            GL.UseProgram(0);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.AlphaTest);
            GL.Disable(EnableCap.StencilTest);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
            // The second unit, which the visor mask uses and which the fixed
            // pipeline would otherwise still be combining into every fragment.
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.Disable(EnableCap.Texture2D);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.Enable(EnableCap.Texture2D);
            GL.BindTexture(TextureTarget.Texture2D, _texture);
            // MODULATE by the current colour is the default, and the current
            // colour is whatever the last thing drawn left behind -- the HUD
            // sets it per object. White, or the screens come out tinted.
            GL.TexEnv(TextureEnvTarget.TextureEnv, TextureEnvParameter.TextureEnvMode,
                (int)TextureEnvMode.Replace);
            GL.Color4(1f, 1f, 1f, 1f);
            // Identity both ways: the quad is already in normalised device
            // coordinates, and the fixed-function matrices belong to whoever
            // touched them last.
            GL.MatrixMode(MatrixMode.Projection);
            GL.PushMatrix();
            GL.LoadIdentity();
            GL.MatrixMode(MatrixMode.Modelview);
            GL.PushMatrix();
            GL.LoadIdentity();
            // Avalonia's first row is the top of the screen and GL's is the
            // bottom, so the quad is flipped in T rather than the buffer being
            // turned over on the CPU every frame.
            GL.Begin(PrimitiveType.TriangleStrip);
            GL.TexCoord2(1f, 0f);
            GL.Vertex3(1f, 1f, 0f);
            GL.TexCoord2(0f, 0f);
            GL.Vertex3(-1f, 1f, 0f);
            GL.TexCoord2(1f, 1f);
            GL.Vertex3(1f, -1f, 0f);
            GL.TexCoord2(0f, 1f);
            GL.Vertex3(-1f, -1f, 0f);
            GL.End();
            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Projection);
            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Modelview);
            GL.TexEnv(TextureEnvTarget.TextureEnv, TextureEnvParameter.TextureEnvMode,
                (int)TextureEnvMode.Modulate);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Enable(EnableCap.DepthTest);
        }

        /// <summary>
        /// A frame with no match behind it: the front screen is the whole
        /// picture, so there is nothing to compose it over.
        /// </summary>
        public static void DrawAlone(RenderWindow window, int width, int height)
        {
            GL.Viewport(0, 0, Math.Max(width, 1), Math.Max(height, 1));
            GL.ClearColor(0f, 0f, 0f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit
                | ClearBufferMask.StencilBufferBit);
            // The photograph under the screens, at the window's resolution
            // rather than at the raster cap theirs is drawn at. See
            // LauncherPhoto for why it is no longer in the same bitmap as the
            // washes over it -- and note that the composite is unchanged: the
            // overlay blends premultiplied, which is the same "over" Avalonia
            // applied when it owned both layers.
            LauncherPhoto.Draw(width, height);
#if MPHREAD_SHELL
            Mods.Launcher.Gui.UiSurface.Current?.DrawMapViewport(width, height);
#endif
            Draw(width, height);
            // The real hunter, *over* the screens rather than under them.
            //
            // Under was the obvious place and it does not work: the drawer the
            // stand sits in is an opaque panel, drawn by the screens, so a
            // model beneath the texture is a model behind a card. The stand
            // draws nothing at all where the model goes (see HunterStand), and
            // this fills that rectangle afterwards -- it clears it to its own
            // background and paints the hunter, so there is nothing for the
            // panel to have been covering.
            //
            // Only on this frame. With a match up, the results screen's own
            // pass owns the preview and draws it inside the world's target.
            LauncherHunter.Draw(window, width, height);
        }

        /// <summary>Give the texture back. The context has to be current.</summary>
        public static void Release()
        {
            if (_texture != 0)
            {
                GL.DeleteTexture(_texture);
                _texture = 0;
            }
            _width = 0;
            _height = 0;
            _hasFrame = false;
        }
    }
}
