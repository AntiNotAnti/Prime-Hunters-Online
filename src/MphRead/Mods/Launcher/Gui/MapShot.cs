#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// The launcher's own map renders, cached, for the screens that want one
    /// behind something rather than beside it.
    ///
    /// <see cref="PlayScreen"/> already loads a preview for the map selected;
    /// what this is for is the rows, where every visible row wants a different
    /// one at once and decoding a PNG per row per frame is not a thing to do
    /// in <c>Render</c>. Decoded once per room key and held: there are thirty
    /// of them and the launcher is already holding the backdrop.
    ///
    /// <para>
    /// Nothing is generated here and nothing ships. These are the files the
    /// thumbnail pass writes out of the player's own game files, so a machine
    /// that has not run it gets no picture and the row is the row it always
    /// was -- which is why every caller has to handle null rather than being
    /// handed a placeholder.
    /// </para>
    /// </summary>
    internal static class MapShot
    {
        private static readonly Dictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// How wide these are decoded, against the 1600x900 the thumbnail pass
        /// writes.
        ///
        /// Nothing on any screen draws one bigger than a third of the frame --
        /// a card in the offline grid is about 180 points, a server row's
        /// ground about 400 -- and a 1600x900 source is therefore being
        /// downscaled by four to ten every time it is drawn. Decoding at 512
        /// costs a tenth of the memory (21 maps at full size is thirty million
        /// pixels held for the life of the process) and it is the scale itself
        /// that was the work: Skia resamples the *source* rectangle, so a card
        /// was resampling 1.4 million pixels to fill thirty thousand, twenty-
        /// one times a redraw.
        ///
        /// 512 rather than the card's own size because one decode has to serve
        /// every caller, and the server rows and the results screen are wider.
        /// </summary>
        private const int DecodeWidth = 512;

        public static Bitmap? For(string? roomKey)
        {
            if (string.IsNullOrEmpty(roomKey))
            {
                return null;
            }
            if (_cache.TryGetValue(roomKey, out Bitmap? hit))
            {
                return hit;
            }
            Bitmap? shot = null;
            try
            {
                string path = ThumbnailGenerator.PathFor(roomKey);
                if (File.Exists(path))
                {
                    // Through a MemoryStream so the file is not held open: the
                    // preview generator rewrites these while the launcher is up.
                    using var stream = new MemoryStream(File.ReadAllBytes(path));
                    shot = Bitmap.DecodeToWidth(stream, DecodeWidth);
                }
            }
            catch (Exception)
            {
                // A truncated PNG from an interrupted batch is no picture, not
                // a dead launcher.
            }
            _cache[roomKey] = shot;
            return shot;
        }

        /// <summary>Drop the lot: the thumbnail pass has rewritten them.</summary>
        public static void Forget()
        {
            _cache.Clear();
        }
    }
}
#endif
