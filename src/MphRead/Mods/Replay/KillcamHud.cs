using MphRead.Hud;
using MphRead.Mods.Chat;
using OpenTK.Mathematics;

namespace MphRead.Mods.Replay;

/// <summary>Only presentation resources; no live player visor or global font owner.</summary>
internal sealed class KillcamHud
{
    private readonly Scene _scene;
    private readonly HudObjectInstance _font;
    internal KillcamHud(Scene scene)
    {
        _scene = scene; _font = new(ChatFont.Cell, ChatFont.Cell);
        _font.SetPaletteData(new ColorRgba[] { new(), new(255, 255, 255, 255) }, scene);
        _font.SetCharacterData(ChatFont.Pixels, scene); _font.Enabled = true;
    }
    internal void Draw(string title, string detail, string weapon, float progress)
    {
        _scene.DrawHudFlatBox(51, 8, 205, 41, new Vector4(0, 0, 0, .78f));
        _scene.DrawHudFlatBox(51, 8, 205, 9, new Vector4(.35f, .95f, 1, 1));
        Text(57, 12, title); Text(57, 20, detail); Text(57, 28, weapon);
        Text(57, 35, "FIRE / BACK TO SKIP");
        _scene.DrawHudFlatBox(51, 40, 51 + 154 * progress, 41, new Vector4(.35f, .95f, 1, 1));
    }
    private void Text(float x, float y, string text)
    {
        _font.Alpha = 1;
        foreach (char ch in text)
        {
            int glyph = ChatFont.Index(ch);
            if (glyph < 0) continue;
            _font.PositionX = x / 256; _font.PositionY = y / 192;
            _font.SetData(glyph, new ColorRgba(225, 245, 255, 255), _scene);
            _scene.DrawHudObject(_font, mode: 1, scale: .55f);
            x += ChatFont.Widths[glyph] * .55f;
            if (x > 199) break;
        }
    }
}
