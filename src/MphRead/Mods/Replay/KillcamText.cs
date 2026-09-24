#if MPHREAD_AVALONIA
using System;
using System.Linq;
using MphRead.Formats;
using SkiaSharp;

namespace MphRead.Mods.Replay;

/// <summary>One immutable CPU glyph set; each replay scene owns its texture bindings.
/// Text changes never rasterize or upload another copy of an already-used glyph.</summary>
internal sealed class KillcamText
{
    private const int Cell = 48;
    private sealed record Glyph(ColorRgba[] Pixels, float Advance);
    private static readonly Lazy<Glyph[]> Glyphs = new(Build);
    private readonly Scene _scene;
    private readonly int[] _textures = new int[95];
    internal KillcamText(Scene scene) { _scene = scene; _ = Glyphs.Value; }
    private static Glyph[] Build()
    {
        var assembly = typeof(KillcamText).Assembly;
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("Roboto-Bold.ttf", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var face = SKTypeface.FromStream(stream);
        using var font = new SKFont(face, 32) { Edging = SKFontEdging.Antialias };
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var bitmap = new SKBitmap(Cell, Cell, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bitmap);
        var glyphs = new Glyph[95];
        for (int i = 0; i < glyphs.Length; i++)
        {
            string character = ((char)(i + 32)).ToString();
            canvas.Clear(SKColors.Transparent);
            canvas.DrawText(character, 1, -font.Metrics.Ascent, font, paint);
            var pixels = new ColorRgba[Cell * Cell];
            for (int y = 0; y < Cell; y++) for (int x = 0; x < Cell; x++)
                pixels[y * Cell + x] = new(244, 249, 255, bitmap.GetPixel(x, y).Alpha);
            glyphs[i] = new(pixels, font.MeasureText(character) / 4);
        }
        return glyphs;
    }
    internal void Draw(float x, float y, string text, float scale, float alpha, float right)
    {
        foreach (char character in text)
        {
            int index = character is >= ' ' and <= '~' ? character - 32 : '?' - 32;
            var glyph = Glyphs.Value[index];
            float advance = glyph.Advance * scale;
            if (x + advance > right) break;
            if (character != ' ')
            {
                if (_textures[index] == 0) _textures[index] = _scene.BindGetTexture(glyph.Pixels, Cell, Cell);
                _scene.DrawHudTexture(x, y, x + Cell / 4f * scale, y + Cell / 4f * scale, _textures[index], alpha);
            }
            x += advance;
        }
    }
}
#endif
