using System.Collections.Generic;

namespace MphRead.Entities
{
    /// <summary>Where the drawing sits inside one frame of a HUD sheet, in that frame's own pixels.</summary>
    public readonly struct IconBounds
    {
        public readonly int MinX;
        public readonly int MinY;
        public readonly int MaxX;
        public readonly int MaxY;

        public IconBounds(int minX, int minY, int maxX, int maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        public int Width => MaxX - MinX + 1;
        public int Height => MaxY - MinY + 1;
        public float CentreX => (MinX + MaxX + 1) / 2f;
        public float CentreY => (MinY + MaxY + 1) / 2f;
    }

    public partial class PlayerEntity
    {
        /// <summary>
        /// The box the actual drawing occupies in one frame of a HUD sheet.
        ///
        /// The weapon list centres and sizes each icon on *this*, not on the
        /// frame around it. The frames come off the touchscreen weapon wheel,
        /// where each weapon is drawn wherever it sits on that ring -- the
        /// Magmaul high and left, the Judicator low, several of them a good
        /// deal smaller than their frame. Centring the frame therefore
        /// centres the padding, which is what left a column of icons that
        /// were all differently placed and differently sized in boxes that
        /// were identical.
        ///
        /// Read straight out of the character data rather than the decoded
        /// texture, so it can be taken once when the HUD is built rather than
        /// after a colour has been applied: palette index 0 is the
        /// transparent one (see HudObjectInstance.DoTexture), so anything else
        /// is ink. The data is tiled 8x8, which is what the index arithmetic
        /// below unpicks.
        /// </summary>
        internal static IconBounds ModIconBounds(IReadOnlyList<byte> data, int frame, int width, int height)
        {
            int tilesX = width / 8;
            int image = frame * width * height;
            int minX = width;
            int minY = height;
            int maxX = -1;
            int maxY = -1;
            for (int y = 0; y < height; y++)
            {
                int ty = y / 8;
                int py = y % 8;
                for (int x = 0; x < width; x++)
                {
                    int index = image + ty * tilesX * 64 + x / 8 * 64 + py * 8 + x % 8;
                    if (index < 0 || index >= data.Count || data[index] == 0)
                    {
                        continue;
                    }
                    if (x < minX)
                    {
                        minX = x;
                    }
                    if (x > maxX)
                    {
                        maxX = x;
                    }
                    if (y < minY)
                    {
                        minY = y;
                    }
                    if (y > maxY)
                    {
                        maxY = y;
                    }
                }
            }
            if (maxX < minX || maxY < minY)
            {
                // An empty frame. The whole of it, so the caller's arithmetic
                // has something with a size in it rather than a division by
                // zero.
                return new IconBounds(0, 0, width - 1, height - 1);
            }
            return new IconBounds(minX, minY, maxX, maxY);
        }
    }
}
