using System;

namespace MphRead.Mods.Chat
{
    /// <summary>
    /// A compact proportional pixel font, for the chat log alone.
    ///
    /// The game's own font is the reason this exists. It is 8x8, it is bold,
    /// and it has **one alphabet**: everything drawn with it comes out in
    /// capitals, whatever the string says. That is right for a HUD shouting
    /// ENERGY at you and wrong for a chat log, where it turns every line
    /// somebody types into a line somebody is shouting, at roughly a third
    /// more width than the same sentence needs. Scaling it down does not fix
    /// either of those -- it just makes shouting harder to read.
    ///
    /// So: real lowercase, real descenders, proportional widths (2 units for
    /// an i, 6 for an m), caps six rows tall against the game font's eight.
    /// A sentence lands in about half the width, which is the whole point --
    /// a chat line has to fit across a 256-unit screen next to a name.
    ///
    /// Authored as pixel art below rather than shipped as a file, for three
    /// reasons: it is 95 glyphs and about six kilobytes expanded, so a file
    /// would be the larger thing; it is legible and editable exactly where it
    /// is used; and an asset in this repository has to answer to
    /// `tools/check-no-game-assets.sh`, which is a question this never has to
    /// be asked.
    ///
    /// The layout, in an 8x8 cell:
    ///
    ///   rows 0-5   caps, digits and ascenders (b d f h k l t)
    ///   rows 1-5   lowercase x-height
    ///   row  6     descenders (g j p q y) and the underscore
    ///
    /// Every glyph is drawn hard against column 0 and advances by its own
    /// ink width plus one, so the spacing is the art's rather than a table's
    /// -- a glyph edited here needs nothing else changed.
    /// </summary>
    internal static class ChatFont
    {
        public const int Cell = 8;
        public const char First = ' ';
        public const char Last = '~';
        private const int Count = Last - First + 1;

        /// <summary>
        /// One byte per pixel, <see cref="Cell"/> squared per glyph, in
        /// character order from <see cref="First"/>. 0 is transparent and 1 is
        /// ink -- a palette index, which is what <c>HudObjectInstance</c>
        /// reads, though the chat HUD always overrides the colour anyway.
        /// </summary>
        public static readonly byte[] Pixels = new byte[Count * Cell * Cell];

        /// <summary>How far the pen moves after each glyph, in cell columns.</summary>
        public static readonly int[] Widths = new int[Count];

        /// <summary>The glyph index for a character, or -1 for one we cannot draw.</summary>
        public static int Index(char ch)
        {
            return ch < First || ch > Last ? -1 : ch - First;
        }

        /// <summary>The width of a run, in cell columns.</summary>
        public static int Measure(ReadOnlySpan<char> text)
        {
            int width = 0;
            for (int i = 0; i < text.Length; i++)
            {
                int index = Index(text[i]);
                if (index >= 0)
                {
                    width += Widths[index];
                }
            }
            return width;
        }

        private static void Define(char ch, int advance)
        {
            Widths[ch - First] = advance;
        }

        private static void Define(char ch, params string[] rows) => Define(ch, 0, rows);

        private static void Define(char ch, int top, params string[] rows)
        {
            int glyph = (ch - First) * Cell * Cell;
            int width = 0;
            for (int r = 0; r < rows.Length; r++)
            {
                string row = rows[r];
                for (int c = 0; c < row.Length && c < Cell; c++)
                {
                    if (row[c] == '#')
                    {
                        Pixels[glyph + (top + r) * Cell + c] = 1;
                        width = Math.Max(width, c + 1);
                    }
                }
            }
            Widths[ch - First] = width + 1;
        }

        static ChatFont()
        {
            Define(' ', 3);
            Define('!', "#", "#", "#", "#", ".", "#");
            Define('"', "#.#", "#.#");
            Define('#', top: 1, ".#.#.", "#####", ".#.#.", "#####", ".#.#.");
            Define('$', "..#..", ".####", "##...", ".###.", "...##", "####.", "..#..");
            Define('%', top: 1, "##..#", "##.#.", "..#..", ".#.##", "#..##");
            Define('&', top: 1, ".##..", "#..#.", ".##..", "#..##", ".##.#");
            Define('\'', "#", "#");
            Define('(', ".#", "#.", "#.", "#.", "#.", ".#");
            Define(')', "#.", ".#", ".#", ".#", ".#", "#.");
            Define('*', top: 1, "#.#", ".#.", "#.#");
            Define('+', top: 2, ".#.", "###", ".#.");
            Define(',', top: 5, ".#", "#.");
            Define('-', top: 3, "###");
            Define('.', top: 5, "#");
            Define('/', top: 1, "...#", "..#.", "..#.", ".#..", "#...");
            Define('0', ".##.", "#..#", "#.##", "##.#", "#..#", ".##.");
            Define('1', ".#.", "##.", ".#.", ".#.", ".#.", "###");
            Define('2', ".##.", "#..#", "...#", "..#.", ".#..", "####");
            Define('3', "###.", "...#", ".##.", "...#", "#..#", ".##.");
            Define('4', "..#.", ".##.", "#.#.", "####", "..#.", "..#.");
            Define('5', "####", "#...", "###.", "...#", "#..#", ".##.");
            Define('6', ".##.", "#...", "###.", "#..#", "#..#", ".##.");
            Define('7', "####", "...#", "..#.", "..#.", ".#..", ".#..");
            Define('8', ".##.", "#..#", ".##.", "#..#", "#..#", ".##.");
            Define('9', ".##.", "#..#", "#..#", ".###", "...#", ".##.");
            Define(':', top: 2, "#", ".", ".", "#");
            Define(';', top: 2, ".#", "..", "..", ".#", "#.");
            Define('<', top: 1, "..#", ".#.", "#..", ".#.", "..#");
            Define('=', top: 2, "####", "....", "####");
            Define('>', top: 1, "#..", ".#.", "..#", ".#.", "#..");
            Define('?', ".##.", "#..#", "...#", "..#.", "....", "..#.");
            Define('@', ".###.", "#...#", "#.###", "#.#.#", "#....", ".###.");
            Define('A', ".###.", "#...#", "#...#", "#####", "#...#", "#...#");
            Define('B', "####.", "#...#", "####.", "#...#", "#...#", "####.");
            Define('C', ".###.", "#...#", "#....", "#....", "#...#", ".###.");
            Define('D', "####.", "#...#", "#...#", "#...#", "#...#", "####.");
            Define('E', "#####", "#....", "####.", "#....", "#....", "#####");
            Define('F', "#####", "#....", "####.", "#....", "#....", "#....");
            Define('G', ".###.", "#...#", "#....", "#..##", "#...#", ".###.");
            Define('H', "#...#", "#...#", "#####", "#...#", "#...#", "#...#");
            Define('I', "###", ".#.", ".#.", ".#.", ".#.", "###");
            Define('J', "...##", "....#", "....#", "....#", "#...#", ".###.");
            Define('K', "#...#", "#..#.", "##...", "##...", "#..#.", "#...#");
            Define('L', "#....", "#....", "#....", "#....", "#....", "#####");
            Define('M', "#...#", "##.##", "#.#.#", "#...#", "#...#", "#...#");
            Define('N', "#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#");
            Define('O', ".###.", "#...#", "#...#", "#...#", "#...#", ".###.");
            Define('P', "####.", "#...#", "#...#", "####.", "#....", "#....");
            Define('Q', ".###.", "#...#", "#...#", "#...#", "#..#.", ".##.#");
            Define('R', "####.", "#...#", "#...#", "####.", "#..#.", "#...#");
            Define('S', ".####", "#....", ".###.", "....#", "....#", "####.");
            Define('T', "#####", "..#..", "..#..", "..#..", "..#..", "..#..");
            Define('U', "#...#", "#...#", "#...#", "#...#", "#...#", ".###.");
            Define('V', "#...#", "#...#", "#...#", ".#.#.", ".#.#.", "..#..");
            Define('W', "#...#", "#...#", "#...#", "#.#.#", "#.#.#", ".#.#.");
            Define('X', "#...#", "#...#", ".#.#.", ".#.#.", "#...#", "#...#");
            Define('Y', "#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..");
            Define('Z', "#####", "....#", "...#.", "..#..", ".#...", "#####");
            Define('[', "##", "#.", "#.", "#.", "#.", "##");
            Define('\\', top: 1, "#...", ".#..", ".#..", "..#.", "...#");
            Define(']', "##", ".#", ".#", ".#", ".#", "##");
            Define('^', ".#.", "#.#");
            Define('_', top: 6, "####");
            Define('`', "#.", ".#");
            Define('a', top: 1, ".##.", "...#", ".###", "#..#", ".###");
            Define('b', "#...", "#...", "###.", "#..#", "#..#", "###.");
            Define('c', top: 1, ".##.", "#..#", "#...", "#..#", ".##.");
            Define('d', "...#", "...#", ".###", "#..#", "#..#", ".###");
            Define('e', top: 1, ".##.", "#..#", "####", "#...", ".###");
            Define('f', "..##", ".#..", "###.", ".#..", ".#..", ".#..");
            Define('g', top: 1, ".###", "#..#", "#..#", ".###", "...#", "###.");
            Define('h', "#...", "#...", "###.", "#..#", "#..#", "#..#");
            Define('i', "#", ".", "#", "#", "#", "#");
            Define('j', ".#", "..", ".#", ".#", ".#", ".#", "#.");
            Define('k', "#...", "#..#", "#.#.", "##..", "#.#.", "#..#");
            Define('l', "##", ".#", ".#", ".#", ".#", ".#");
            Define('m', top: 1, "##.#.", "#.#.#", "#.#.#", "#.#.#", "#.#.#");
            Define('n', top: 1, "###.", "#..#", "#..#", "#..#", "#..#");
            Define('o', top: 1, ".##.", "#..#", "#..#", "#..#", ".##.");
            Define('p', top: 1, "###.", "#..#", "#..#", "###.", "#...", "#...");
            Define('q', top: 1, ".###", "#..#", "#..#", ".###", "...#", "...#");
            Define('r', top: 1, "#.##", "##..", "#...", "#...", "#...");
            Define('s', top: 1, ".###", "#...", ".##.", "...#", "###.");
            Define('t', ".#..", ".#..", "###.", ".#..", ".#..", "..##");
            Define('u', top: 1, "#..#", "#..#", "#..#", "#..#", ".###");
            Define('v', top: 1, "#...#", "#...#", ".#.#.", ".#.#.", "..#..");
            Define('w', top: 1, "#...#", "#...#", "#.#.#", "#.#.#", ".#.#.");
            Define('x', top: 1, "#..#", "#..#", ".##.", "#..#", "#..#");
            Define('y', top: 1, "#..#", "#..#", "#..#", ".###", "...#", "###.");
            Define('z', top: 1, "####", "...#", ".##.", "#...", "####");
            Define('{', "..#", ".#.", "##.", ".#.", ".#.", "..#");
            Define('|', "#", "#", "#", "#", "#", "#");
            Define('}', "#..", ".#.", ".##", ".#.", ".#.", "#..");
            Define('~', top: 3, ".#..#", "#..#.");
        }
    }
}
