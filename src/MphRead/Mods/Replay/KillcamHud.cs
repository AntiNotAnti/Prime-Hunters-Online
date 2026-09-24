using System;
using MphRead.Hud;
using MphRead.Mods.Chat;
using OpenTK.Mathematics;

namespace MphRead.Mods.Replay;

/// <summary>Private replay presentation; all identity and health comes from the replica.</summary>
internal sealed class KillcamHud
{
    private readonly Scene _scene;
    private readonly HudObjectInstance _font;
#if MPHREAD_AVALONIA
    private readonly KillcamText? _text;
#endif
    private static readonly ColorRgba White = new(239, 247, 255, 255);
    private static readonly ColorRgba Muted = new(157, 179, 199, 255);
    internal KillcamHud(Scene scene)
    {
        _scene = scene; _font = new(ChatFont.Cell, ChatFont.Cell);
        _font.SetPaletteData(new ColorRgba[] { new(), new(255, 255, 255, 255) }, scene);
        _font.SetCharacterData(ChatFont.Pixels, scene); _font.Enabled = true;
#if MPHREAD_AVALONIA
        try { _text = new KillcamText(scene); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { Console.WriteLine("[killcam] Smooth text unavailable: " + ex.Message); }
#endif
    }
    internal void Draw(bool final, string killer, string victim, string weapon, bool headshot,
        int health, float progress, float killProgress, float secondsToKill)
    {
        Vector4 accent = final ? new(1, .73f, .28f, 1) : new(.28f, .84f, 1, 1);
        Card(10, 10, 84, 29, accent);
        Box(16, 17, 18, 19, accent);
        Text(22, 12, final ? "FINAL ELIMINATION" : "ELIMINATION REPLAY", .62f, White, 81);
        Text(22, 22, "RECORDED PERSPECTIVE", .43f, Muted, 81);
        Card(212, 10, 246, 26, accent);
        Text(218, 13, secondsToKill > 0 ? $"-{secondsToKill:0.0}s" : $"+{-secondsToKill:0.0}s", .85f, White, 243);

        Card(10, 148, 170, 181, accent);
        Card(174, 148, 246, 181, accent);
        Text(17, 151, final ? "FINAL KILL" : "KILLED BY", .53f, Muted, 164);
        Text(17, 159, killer, 1.12f, White, 164);
        Text(17, 173, weapon + (headshot ? "  /  HEADSHOT" : ""), .57f, White, 164);
        Text(181, 152, "ELIMINATED", .48f, Muted, 240);
        Text(181, 160, victim, .8f, White, 240);
        Text(181, 173, $"ATTACKER HP  {health}", .43f, Muted, 240);
        Box(10, 183, 246, 184, new(.55f, .72f, .85f, .22f));
        Box(10, 183, 10 + 236 * Math.Clamp(progress, 0, 1), 184, accent);
        float killX = 10 + 236 * Math.Clamp(killProgress, 0, 1);
        Box(killX - .45f, 181.5f, killX + .45f, 185, new(1, 1, 1, .9f));
        Text(10, 186, "REPLAY", .38f, Muted, 55);
        Text(174, 186, "FIRE / BACK TO SKIP", .43f, White, 246);
    }
    private void Card(float left, float top, float right, float bottom, Vector4 accent)
    {
        // Soft slate glass with cut corners and a fine highlight, never an opaque black block.
        const int bands = 12;
        float height = (bottom - top) / bands;
        for (int i = 0; i < bands; i++)
        {
            float t = i / (float)(bands - 1);
            float inset = i is 0 or bands - 1 ? 1.4f : 0;
            Box(left + inset, top + i * height, right - inset, top + (i + 1) * height,
                new(.08f + t * .025f, .15f + t * .03f, .22f + t * .035f, .88f - t * .16f));
        }
        Box(left + 2, top, right - 2, top + .35f, new(accent.X, accent.Y, accent.Z, .5f));
        Box(left, top + 3, left + .65f, bottom - 3, new(accent.X, accent.Y, accent.Z, .65f));
    }
    private void Box(float x, float y, float right, float bottom, Vector4 color)
        => _scene.DrawHudFlatBox(x, y, right, bottom, color);
    private void Text(float x, float y, string text, float scale, ColorRgba color, float right)
    {
#if MPHREAD_AVALONIA
        if (_text != null) { _text.Draw(x, y - 1, text, scale, color.Equals(Muted) ? .72f : 1, right); return; }
#endif
        _font.Alpha = 1;
        foreach (char ch in text)
        {
            int glyph = ChatFont.Index(ch);
            if (glyph < 0) continue;
            float width = ChatFont.Widths[glyph] * scale;
            if (x + width > right) break;
            _font.PositionX = x / 256; _font.PositionY = y / 192;
            _font.SetData(glyph, color, _scene);
            _scene.DrawHudObject(_font, mode: 1, scale: scale);
            x += width;
        }
    }
}
