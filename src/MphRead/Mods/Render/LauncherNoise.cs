using System;
using System.Diagnostics;
using OpenTK.Graphics.OpenGL;

namespace MphRead.Mods.Render
{
    /// <summary>
    /// The front screen's moving ground: a small field of domain-warped value
    /// noise, laid over the photograph in <c>overlay</c> at sixty-two per
    /// cent.
    ///
    /// <para>
    /// This is the reference's <c>#backdrop</c> canvas, and it is the one
    /// thing on the front screen that is never still. Without it the screen is
    /// a photograph with a menu on it; with it the lava under the wordmark
    /// breathes, which is the whole reason that photograph was chosen.
    /// </para>
    ///
    /// <para>
    /// <b>Why it is here and not in the screens' own bitmap.</b> Two reasons,
    /// and either would be enough. The photograph is already GL's (see
    /// <see cref="LauncherPhoto"/>) and a layer that blends with it has to be
    /// on the same side of the fence. And the screens are rasterised by Skia
    /// on the CPU and only when something has happened -- an animated layer in
    /// there would mean re-rasterising the whole window thirty times a second
    /// for ever, which is exactly the cost <see cref="Launcher.Gui.BakedBackdrop"/>
    /// exists to avoid. Here it is a texture upload of a few tens of
    /// kilobytes and one quad, and the screens above it go on costing nothing
    /// while nobody touches them.
    /// </para>
    ///
    /// <para>
    /// <b>Why it needs a shader.</b> <c>mix-blend-mode: overlay</c> is
    /// multiply where the backdrop is dark and screen where it is light, and
    /// which one applies is decided per pixel <i>by the destination</i>. Fixed
    /// function blending cannot ask that question -- it can do multiply
    /// (<c>DstColor, Zero</c>) or screen (<c>OneMinusDstColor, One</c>) but
    /// not choose between them -- so the photograph and this are sampled
    /// together in one fragment and combined there. That also makes it one
    /// draw rather than two, and no read of the framebuffer at all.
    /// </para>
    ///
    /// <para>
    /// The arithmetic itself is <see cref="NoiseField"/>, which is shared
    /// with the Android head: that platform has no GL under the launcher and
    /// draws the same field through Skia instead, and the one thing that must
    /// not happen is the two of them growing different lava.
    /// </para>
    /// </summary>
    public static class LauncherNoise
    {
        private static readonly NoiseField _field = new();

        private static int _texture;
        private static int _width;
        private static int _height;
        private static double _drawnAt = -1000;

        /// <summary>The field's texture name, or zero when there is nothing to draw.</summary>
        public static int Texture => _texture;

        /// <summary>
        /// Step the field and hand it to GL, at most once every
        /// <see cref="NoiseField.Gap"/> milliseconds however fast the window
        /// is going.
        ///
        /// Returns false when there is nothing usable, which is the caller's
        /// cue to draw the photograph the plain way. That fallback is the
        /// point: a front screen with a still backdrop is a front screen, and
        /// a front screen that does not come up is not.
        /// </summary>
        public static bool Step(int windowWidth, int windowHeight)
        {
            if (!_field.Step(windowWidth, windowHeight))
            {
                return false;
            }
            bool reshaped = _field.Width != _width || _field.Height != _height;
            _width = _field.Width;
            _height = _field.Height;
            double now = _clock.Elapsed.TotalMilliseconds;
            if (_texture != 0 && !reshaped && now - _drawnAt < NoiseField.Gap)
            {
                // The field on the GPU is less than a frame old. Leave it.
                return true;
            }
            _drawnAt = now;
            return Upload();
        }

        private static readonly Stopwatch _clock = Stopwatch.StartNew();

        private static bool Upload()
        {
            try
            {
                GL.ActiveTexture(TextureUnit.Texture0);
                bool fresh = _texture == 0;
                if (fresh)
                {
                    _texture = GL.GenTexture();
                }
                GL.BindTexture(TextureTarget.Texture2D, _texture);
                // Every piece of unpack state said out loud, for the reason
                // LauncherPhoto gives: these are context-wide, the thumbnail
                // sweeps and the screen capture both leave them somewhere
                // else, and a row length left behind by one of them starts
                // every row of this upload in the wrong place.
                GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
                GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
                GL.PixelStore(PixelStoreParameter.UnpackSkipPixels, 0);
                GL.PixelStore(PixelStoreParameter.UnpackSkipRows, 0);
                GL.PixelStore(PixelStoreParameter.UnpackSwapBytes, 0);
                GL.PixelStore(PixelStoreParameter.UnpackLsbFirst, 0);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb,
                    _width, _height, 0, PixelFormat.Rgb, PixelType.UnsignedByte, _field.Pixels);
                // Nearest, both ways: the reference magnifies this canvas with
                // `image-rendering: pixelated`, and the blocky cells are the
                // look rather than an artefact of it being small.
                GL.TexParameter(TextureTarget.Texture2D,
                    TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D,
                    TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D,
                    TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D,
                    TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                GL.BindTexture(TextureTarget.Texture2D, 0);
                return true;
            }
            catch (Exception ex)
            {
                Mods.DebugLog.Line("ui", $"the moving backdrop could not be uploaded: {ex.Message}");
                _texture = 0;
                return false;
            }
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
            _drawnAt = -1000;
        }
    }
}
