using System;
using System.IO;
using System.Reflection;
using OpenTK.Windowing.Common.Input;
using ReFuel.Stb;

namespace MphRead.Mods.Render
{
    /// <summary>
    /// The mark in the corner of the window, on the taskbar and in the alt-tab
    /// list.
    ///
    /// It used to be the *launcher* window's -- an Avalonia window with
    /// <c>Icon = GuiTheme.AppIcon</c> -- and the game window never had one,
    /// which nobody noticed while the launcher was the window a session
    /// started in. One window means one icon, and it has to be this one's.
    ///
    /// Decoded here rather than through Avalonia: this is set on the GL window,
    /// which is created before the toolkit is and is compiled into builds that
    /// have no toolkit at all. The PNG travels as a plain embedded resource for
    /// the same reason -- avares:// is Avalonia's own scheme and needs Avalonia
    /// to read it.
    /// </summary>
    public static class AppIcon
    {
        private static WindowIcon? _icon;
        private static bool _tried;

        /// <summary>
        /// The icon, or null on a build or a platform that cannot produce one.
        /// Read once: a failure here is not worth repeating per window.
        /// </summary>
        public static WindowIcon? Load()
        {
            if (_tried)
            {
                return _icon;
            }
            _tried = true;
            try
            {
                Assembly assembly = typeof(AppIcon).Assembly;
                string? name = Array.Find(assembly.GetManifestResourceNames(),
                    n => n.EndsWith("fruity-prime-mark.png", StringComparison.OrdinalIgnoreCase));
                if (name == null)
                {
                    Mods.DebugLog.Line("window", "no icon resource in this build");
                    return null;
                }
                using Stream? stream = assembly.GetManifestResourceStream(name);
                if (stream == null)
                {
                    return null;
                }
                // RGBA, top row first, which is what GLFW wants -- the one
                // image in this program that is not handed to GL.
                using StbImage image = StbImage.Load(stream, StbiImageFormat.Rgba);
                var pixels = new byte[image.Width * image.Height * 4];
                image.AsSpan<byte>().Slice(0, pixels.Length).CopyTo(pixels);
                // Several sizes, not just the one in the file. The art is 552
                // square and a title bar wants 16: GLFW picks the closest size
                // it is given and leaves the rest to the platform, and a
                // 552-to-16 reduction done by Windows is a smudge. Box-filtered
                // here instead, which is what the three sizes an .ico carries
                // are for.
                _icon = new WindowIcon(
                    Scaled(pixels, image.Width, image.Height, 16),
                    Scaled(pixels, image.Width, image.Height, 32),
                    Scaled(pixels, image.Width, image.Height, 48),
                    new Image(image.Width, image.Height, pixels));
                Mods.DebugLog.Line("window", $"window icon {image.Width}x{image.Height}");
                return _icon;
            }
            catch (Exception ex)
            {
                // A window with the wrong icon is a window; one that could not
                // be created is not.
                Mods.DebugLog.Line("window", $"no window icon: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// One square of the mark, box-filtered down, alpha included -- the
        /// art is a cherry on nothing, so averaging colour without averaging
        /// coverage would give every edge a halo of opaque red.
        /// </summary>
        private static Image Scaled(byte[] source, int width, int height, int size)
        {
            var result = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            {
                int y0 = y * height / size;
                int y1 = Math.Max(y0 + 1, (y + 1) * height / size);
                for (int x = 0; x < size; x++)
                {
                    int x0 = x * width / size;
                    int x1 = Math.Max(x0 + 1, (x + 1) * width / size);
                    long r = 0;
                    long g = 0;
                    long b = 0;
                    long a = 0;
                    int count = 0;
                    for (int sy = y0; sy < y1 && sy < height; sy++)
                    {
                        for (int sx = x0; sx < x1 && sx < width; sx++)
                        {
                            int o = (sy * width + sx) * 4;
                            int alpha = source[o + 3];
                            // Weighted by coverage: a transparent texel has no
                            // colour worth averaging in.
                            r += source[o] * alpha;
                            g += source[o + 1] * alpha;
                            b += source[o + 2] * alpha;
                            a += alpha;
                            count++;
                        }
                    }
                    int destination = (y * size + x) * 4;
                    if (a > 0)
                    {
                        result[destination] = (byte)(r / a);
                        result[destination + 1] = (byte)(g / a);
                        result[destination + 2] = (byte)(b / a);
                    }
                    result[destination + 3] = count > 0 ? (byte)(a / count) : (byte)0;
                }
            }
            return new Image(size, size, result);
        }
    }
}
