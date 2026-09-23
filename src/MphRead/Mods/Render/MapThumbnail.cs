using System;
using System.Collections.Generic;
using System.IO;
using ReFuel.Stb;

namespace MphRead.Mods.Render
{
    /// <summary>
    /// The launcher's map previews, on the HUD.
    ///
    /// The pictures already exist and are already the player's own: the first
    /// run renders every room to a PNG in the thumbnail cache
    /// (<see cref="ThumbnailGenerator"/>) and the Play screen has been showing
    /// them since it was written. What was missing was a way to put one in
    /// front of somebody who is *in* a match, which is where the question
    /// "which map next" is actually asked. A name on its own does not answer
    /// it -- half of these rooms are known by their shape and not by what the
    /// cartridge calls them.
    ///
    /// <para>
    /// Loaded into a texture owned by the scene. Entries are invalidated
    /// between matches and GL resources are released with their owning scene.
    /// </para>
    ///
    /// <para>
    /// One decode per frame, never four. A full-size preview is 1600x900 and
    /// four of them arriving in the frame the results screen comes up is a
    /// visible stall on exactly the frame the match ends. They fill in over
    /// the first few frames instead, which nobody can see happening.
    /// </para>
    /// </summary>
    public static class MapThumbnail
    {
        /// <summary>
        /// How big the reduced picture is kept, in texels. The box it lands in
        /// is about a tenth of the screen's width, so 256 across is more than
        /// a 4K display can show of it and a sixth of what the file holds.
        /// </summary>
        private const int Width = 256;
        private const int Height = 144;

        private sealed class Entry
        {
            public int BindingId;
            /// <summary>Tried and there is no picture: an uncached room, or a
            /// file that will not decode. Asked once, not once a frame.</summary>
            public bool Missing;
        }

        private static readonly Dictionary<string, Entry> _cache =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private static bool _decodedThisFrame;

        /// <summary>
        /// Forget every binding. Called when a results screen comes up, which
        /// is once a match and the only moment the cache can be stale.
        /// </summary>
        public static void Clear()
        {
            _cache.Clear();
        }

        /// <summary>One decode allowed again. Called once per drawn frame.</summary>
        public static void BeginFrame()
        {
            _decodedThisFrame = false;
        }

        /// <summary>
        /// The texture for a room, or 0 -- for a room with no preview, for one
        /// whose turn to be decoded has not come round yet, and for anything
        /// that went wrong. The caller draws a plain box in its place, which is
        /// what it would have drawn behind it anyway.
        /// </summary>
        public static int For(string roomKey, Scene scene)
        {
            if (String.IsNullOrEmpty(roomKey) || Headless.Active)
            {
                return 0;
            }
            if (_cache.TryGetValue(roomKey, out Entry? entry))
            {
                return entry.Missing ? 0 : entry.BindingId;
            }
            if (_decodedThisFrame)
            {
                return 0;
            }
            _decodedThisFrame = true;
            var made = new Entry();
            _cache[roomKey] = made;
            ColorRgba[]? pixels = Decode(roomKey);
            if (pixels == null)
            {
                made.Missing = true;
                DebugLog.Line("hud", $"no map preview for {roomKey}");
                return 0;
            }
            try
            {
                made.BindingId = scene.BindGetTexture(pixels, Width, Height);
            }
            catch (Exception ex)
            {
                made.Missing = true;
                DebugLog.Line("hud", $"map preview for {roomKey} would not bind: {ex.Message}");
            }
            DebugLog.Line("hud", $"map preview for {roomKey} bound as {made.BindingId}");
            return made.Missing ? 0 : made.BindingId;
        }

        /// <summary>
        /// The file, reduced. Box-filtered rather than sampled: this is a
        /// six-to-one reduction of a rendered room, and taking every sixth
        /// pixel of one turns a handrail into a dotted line.
        /// </summary>
        private static ColorRgba[]? Decode(string roomKey)
        {
            string path;
            try
            {
                path = ThumbnailGenerator.PathFor(roomKey);
            }
            catch (Exception)
            {
                return null;
            }
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                using FileStream file = File.OpenRead(path);
                // Rgb, not Rgba, and that is the whole of the "the previews
                // are not visible" bug.
                //
                // The span this library hands back reports its length from the
                // *file's* channel count, not from the format asked for. Ask
                // for RGBA on an opaque PNG -- which every one of these is --
                // and the buffer is four bytes a pixel while the span says
                // three quarters of that, so every read at a three-byte stride
                // lands a channel further into the row than the last: a
                // picture of vertical red, green and blue stripes, which is
                // exactly what appeared in the list. Asking for what the file
                // already is makes the two agree, and is the call
                // MapTextureBake has been making correctly all along.
                using StbImage image = StbImage.Load(file, StbiImageFormat.Rgb);
                if (image.Width <= 0 || image.Height <= 0)
                {
                    return null;
                }
                ReadOnlySpan<byte> source = image.AsSpan<byte>();
                // Measured, not assumed. Asking for RGBA does not mean the
                // span comes back four bytes to a pixel -- these files are
                // opaque PNGs and arrive with three -- and reading a
                // three-channel buffer at a four-channel stride walks off the
                // end a row at a time: a quarter of the picture came out as
                // the default pixel, which is transparent black, and the rest
                // came out as somebody else's colour channels. That is what
                // "the previews are not visible" was.
                const int channels = 3;
                int stride = image.Width * channels;
                // Never past what the buffer actually holds, whatever the span
                // says: the length is the one number here that has already
                // been wrong once.
                int limit = Math.Min(source.Length, image.Width * image.Height * channels);
                if (limit < stride)
                {
                    return null;
                }
                var result = new ColorRgba[Width * Height];
                for (int y = 0; y < Height; y++)
                {
                    int y0 = y * image.Height / Height;
                    int y1 = Math.Max(y0 + 1, (y + 1) * image.Height / Height);
                    for (int x = 0; x < Width; x++)
                    {
                        int x0 = x * image.Width / Width;
                        int x1 = Math.Max(x0 + 1, (x + 1) * image.Width / Width);
                        int red = 0;
                        int green = 0;
                        int blue = 0;
                        int count = 0;
                        for (int sy = y0; sy < y1; sy++)
                        {
                            int row = sy * stride;
                            for (int sx = x0; sx < x1; sx++)
                            {
                                int at = row + sx * channels;
                                if (at + 2 >= limit)
                                {
                                    continue;
                                }
                                red += source[at];
                                green += source[at + 1];
                                blue += source[at + 2];
                                count++;
                            }
                        }
                        if (count == 0)
                        {
                            continue;
                        }
                        result[y * Width + x] = new ColorRgba(
                            (byte)(red / count), (byte)(green / count), (byte)(blue / count), 255);
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                DebugLog.Line("hud", $"map preview for {roomKey} would not load: {ex.Message}");
                return null;
            }
        }
    }
}
