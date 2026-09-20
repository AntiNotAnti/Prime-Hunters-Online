using System;
using System.Diagnostics;

namespace MphRead.Mods.Render
{
    /// <summary>
    /// The front screen's moving ground, as numbers: a small field of
    /// domain-warped value noise, stepped on a clock and handed out as RGB
    /// bytes.
    ///
    /// <para>
    /// This is the reference's <c>#backdrop</c> canvas and it is the one thing
    /// on the front screen that is never still. Without it the screen is a
    /// photograph with a menu on it; with it the lava under the wordmark
    /// breathes, which is the whole reason that photograph was chosen.
    /// </para>
    ///
    /// <para>
    /// <b>Why it is a file of its own.</b> There are two heads and they put
    /// the same field on the screen two different ways. The desktop game has a
    /// GL window under the launcher, so <c>LauncherNoise.cs</c> uploads
    /// this as a texture and blends it with the photograph in one fragment
    /// (the photograph is GL's there too -- see <c>LauncherPhoto.cs</c>).
    /// Android has no GL under the launcher at all: the screens are a real
    /// Avalonia view over the window, so <c>MovingBackdrop</c> draws these
    /// bytes into a bitmap and lets Skia do the blend. Neither of those files
    /// is compiled on the other platform, and the arithmetic below must not be
    /// written twice -- the moment it is, one of the two phones is looking at
    /// different lava.
    /// </para>
    ///
    /// <para>
    /// Everything here is the reference's own arithmetic: a 64x64 random grid,
    /// smoothstepped bilinear lookups, the field warped by two more lookups of
    /// itself, a radial falloff, and the two colours it mixes between. The
    /// numbers are not adjustable and are not meant to be -- they are what
    /// makes it the same picture.
    /// </para>
    /// </summary>
    public sealed class NoiseField
    {
        /// <summary>
        /// Window points per noise cell. The reference's <c>CELL</c>, and the
        /// canvas is then magnified with <c>image-rendering: pixelated</c> --
        /// which is why both heads filter the result nearest.
        /// </summary>
        public const int Cell = 6;

        /// <summary>
        /// The widest field that is worked out cell by cell.
        ///
        /// 1920 points at six to a cell is 320, so every window up to that is
        /// the reference's own resolution exactly and only a larger one gets
        /// coarser cells. It is a CPU loop: at 320x180 it is fifty-odd
        /// thousand cells thirty times a second, which does not show; letting
        /// a 4K window ask for four times that would start to.
        /// </summary>
        public const int MaxCells = 320;

        /// <summary>Milliseconds between fields. The reference's own 33.</summary>
        public const double Gap = 33;

        private const int GridSize = 64;

        private static readonly float[] _grid = new float[GridSize * GridSize];
        private static bool _seeded;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _width;
        private int _height;
        private byte[] _pixels = Array.Empty<byte>();
        private float[] _fall = Array.Empty<float>();
        private double _filledAt = Double.NegativeInfinity;

        /// <summary>The field, three bytes a cell, row by row.</summary>
        public byte[] Pixels => _pixels;

        public int Width => _width;

        public int Height => _height;

        /// <summary>The cells a window of this many points wants.</summary>
        public static int CellsFor(double points) =>
            Math.Clamp((int)(points / Cell), 32, MaxCells);

        /// <summary>
        /// Step the field for a window of this size, at most once every
        /// <see cref="Gap"/> milliseconds however fast the caller is going.
        ///
        /// Returns true when there is something to draw. False is the caller's
        /// cue to draw the photograph the plain way -- a front screen with a
        /// still backdrop is a front screen, and a front screen that does not
        /// come up is not.
        /// </summary>
        /// <param name="still">
        /// Pin it at the beginning of the loop rather than stepping it, for
        /// the screen captures. See <c>Deck.Still</c>.
        /// </param>
        public bool Step(double windowWidth, double windowHeight, bool still = false)
        {
            if (windowWidth <= 0 || windowHeight <= 0)
            {
                return false;
            }
            Resize(CellsFor(windowWidth), CellsFor(windowHeight));
            if (_pixels.Length == 0)
            {
                return false;
            }
            double now = still ? 0 : _clock.Elapsed.TotalMilliseconds;
            if (_filledAt > Double.NegativeInfinity && Math.Abs(now - _filledAt) < Gap)
            {
                // The field is less than a frame old. Leave it.
                return true;
            }
            _filledAt = now;
            Fill(now / 1000.0);
            return true;
        }

        /// <summary>
        /// The grid, the buffers and the falloff for a size just arrived at.
        ///
        /// The falloff is the reference's <c>fall[]</c>: the field is pulled
        /// down towards the corners so the picture keeps a middle, and it
        /// depends only on the shape, so it is worked out on a resize rather
        /// than thirty times a second.
        /// </summary>
        private void Resize(int w, int h)
        {
            if (!_seeded)
            {
                // A fixed seed, not the clock's. Two windows of the same
                // program should be looking at the same lava, and a picture
                // that cannot be reproduced is a picture nobody can report a
                // fault in.
                var random = new Random(0x46505250);
                for (int i = 0; i < _grid.Length; i++)
                {
                    _grid[i] = (float)random.NextDouble();
                }
                _seeded = true;
            }
            if (w == _width && h == _height && _pixels.Length != 0)
            {
                return;
            }
            _width = w;
            _height = h;
            _pixels = new byte[w * h * 3];
            _fall = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double dx = (x / (double)w - 0.5) * 2;
                    double dy = (y / (double)h - 0.5) * 2;
                    double d = Math.Min(1, Math.Sqrt(dx * dx * 0.78 + dy * dy) / 1.18);
                    _fall[y * w + x] = (float)(1 - d * d * 0.45);
                }
            }
            // The shape changed, so whatever the caller is holding is the
            // wrong one: force the next step to fill rather than keep it.
            _filledAt = Double.NegativeInfinity;
        }

        private static double Smooth(double t) => t * t * (3 - 2 * t);

        /// <summary>
        /// One lookup into the 64x64 grid: bilinear between four corners, with
        /// the fractions smoothstepped and the indices wrapped.
        /// </summary>
        private static double Noise(double x, double y)
        {
            int xi = (int)Math.Floor(x);
            int yi = (int)Math.Floor(y);
            double xf = Smooth(x - xi);
            double yf = Smooth(y - yi);
            int y0 = (yi & 63) * GridSize;
            int y1 = ((yi + 1) & 63) * GridSize;
            int x0 = xi & 63;
            int x1 = (xi + 1) & 63;
            double a = _grid[y0 + x0];
            double b = _grid[y0 + x1];
            double c = _grid[y1 + x0];
            double d = _grid[y1 + x1];
            double t = a + (b - a) * xf;
            return t + ((c + (d - c) * xf) - t) * yf;
        }

        // The two colours the field mixes between and the floor under both.
        private static readonly double[] _hot = { 196, 96, 88 };
        private static readonly double[] _cold = { 48, 112, 186 };
        private static readonly double[] _floor = { 14, 20, 30 };

        /// <summary>
        /// The field at <paramref name="seconds"/>. Two lookups warp the
        /// coordinates a third is then read at, which is what turns a bed of
        /// blobs into something that flows.
        /// </summary>
        private void Fill(double seconds)
        {
            double time = seconds * 0.26;
            int w = _width, h = _height;
            int p = 0, q = 0;
            for (int y = 0; y < h; y++)
            {
                double v = y * 0.055;
                for (int x = 0; x < w; x++)
                {
                    double u = x * 0.055;
                    double wx = Noise(u + time * 2.0, v) * 4.4;
                    double wy = Noise(u, v - time * 1.6) * 4.4;
                    double n = Noise(u + wx, v + wy);
                    n = n * n * (3 - 2 * n);
                    double shade = (0.18 + n * 0.95) * _fall[q++];
                    for (int c = 0; c < 3; c++)
                    {
                        _pixels[p++] = (byte)Math.Clamp(
                            _floor[c] + (_hot[c] * n + _cold[c] * (1 - n)) * shade, 0, 255);
                    }
                }
            }
        }
    }
}
