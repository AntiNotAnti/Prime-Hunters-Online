using System;
using System.IO;
using Android.Graphics;

namespace MphRead.Droid
{
    /// <summary>
    /// Writing a PNG on a phone.
    ///
    /// <see cref="MphRead.Mods.ScreenCapture"/> uses ReFuel's STB binding
    /// everywhere else; that package ships natives for Linux and Windows and
    /// none for Android, so the first preview rendered perfectly and then died
    /// in the encoder's type initializer. Android has its own encoder in the
    /// framework, which is one class away.
    /// </summary>
    internal static class AndroidPng
    {
        /// <summary>The pixels come off the GL target bottom-up and in RGB.</summary>
        public static void Write(byte[] rgb, int width, int height, string path)
        {
            var pixels = new int[width * height];
            for (int y = 0; y < height; y++)
            {
                int source = (height - 1 - y) * width * 3;
                int target = y * width;
                for (int x = 0; x < width; x++)
                {
                    int i = source + x * 3;
                    pixels[target + x] = unchecked((int)0xFF000000)
                        | (rgb[i] << 16) | (rgb[i + 1] << 8) | rgb[i + 2];
                }
            }
            using Bitmap? bitmap = Bitmap.CreateBitmap(pixels, width, height,
                Bitmap.Config.Argb8888!);
            if (bitmap == null)
            {
                throw new InvalidOperationException("the bitmap could not be allocated");
            }
            using FileStream stream = File.Create(path);
            bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream);
        }
    }
}
