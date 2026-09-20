using System;
using MphRead.Hud;
using MphRead.Mods.Input;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    /// <summary>
    /// The DS's bottom screen, drawn on the top one.
    ///
    /// Faint on purpose. A player with a tablet is reaching for a button with
    /// their hand, not looking for it with their eyes -- what they need is
    /// enough of an outline to know where the row is and that their zone is
    /// where they left it. Anything more solid is a second HUD sitting on top
    /// of the game.
    ///
    /// It goes bright for one thing only: while the zone is being drawn. That
    /// is the one moment the player *is* looking at it, and a rectangle you
    /// are dragging out has to be visible to be dragged.
    ///
    /// Circles rather than boxes, because the layout is a picture the hand
    /// learns and the DS's buttons are round. There is no circle primitive in
    /// the HUD -- it draws flat boxes -- so each one is a stack of horizontal
    /// spans, which is a few dozen quads apiece and costs nothing beside the
    /// room behind it.
    /// </summary>
    public partial class PlayerEntity
    {
        private static readonly Vector4 _stylusInk = new Vector4(0.85f, 0.30f, 0.30f, 1);
        private static readonly Vector4 _stylusFill = new Vector4(0.55f, 0.16f, 0.16f, 1);
        private static readonly Vector4 _stylusLit = new Vector4(1f, 0.72f, 0.35f, 1);

        internal void ModDrawStylusZone()
        {
            if (!IsMainPlayer)
            {
                return;
            }

            // The platform cursor has no portable opacity control. During
            // stylus gameplay the window hides it without grabbing it, and
            // this HUD cursor takes its place. Menus, results and placement
            // keep the normal platform cursor instead.
            bool drawCursor = PointerInput.StylusMode && !StylusZone.Placing
                && (_scene.CameraMode == CameraMode.Player || _scene.IsFreeCam)
                && !_scene.FrameAdvance && !Mods.Network.DemoPlayback.IsActive
                && !Mods.PauseMenu.Open && !Mods.EndScreen.Available
                && !GameState.DialogPause && !GameState.MenuPause;

            if (StylusZone.Enabled || StylusZone.Placing)
            {
                // The overlay is drawn in the HUD's 256x192 space, and the zone is
                // stated in window fractions, so one is turned into the other
                // here. HudAspectFix is not wanted: a fraction of the window is
                // already a fraction of the window, whatever shape it is.
                float left = StylusZone.Left * 256f;
                float top = StylusZone.Top * 192f;
                float width = StylusZone.Width * 256f;
                float height = StylusZone.Height * 192f;
                if (width > 1 && height > 1)
                {
                    // Placement is deliberately visible even if the normal
                    // overlay has been set to 0%; otherwise an invisible
                    // rectangle could not be positioned.
                    float outlineAlpha = StylusZone.Placing ? 0.55f : StylusZone.OutlineOpacity;
                    float buttonAlpha = StylusZone.Placing ? 0.275f : StylusZone.ButtonOpacity;
                    float line = Math.Max(0.5f, height / 96f);

                    if (outlineAlpha > 0)
                    {
                        var edge = new Vector4(_stylusInk.Xyz, outlineAlpha);
                        _scene.DrawHudFlatBox(left, top, left + width, top + line, edge);
                        _scene.DrawHudFlatBox(left, top + height - line, left + width, top + height, edge);
                        _scene.DrawHudFlatBox(left, top, left + line, top + height, edge);
                        _scene.DrawHudFlatBox(left + width - line, top, left + width, top + height, edge);
                    }

                    if (buttonAlpha > 0)
                    {
                        float scaleX = width / StylusZone.DsWidth;
                        float scaleY = height / StylusZone.DsHeight;
                        foreach (StylusZone.Button button in StylusZone.Buttons)
                        {
                            bool lit = !StylusZone.Placing && StylusZone.Contact
                                && StylusZone.Region == button.Region;
                            float alpha = lit ? Math.Min(1, buttonAlpha * 6) : buttonAlpha;
                            Vector4 colour = lit
                                ? new Vector4(_stylusLit.Xyz, alpha)
                                : new Vector4(_stylusFill.Xyz, alpha);
                            DrawStylusCircle(left + button.X * scaleX, top + button.Y * scaleY,
                                button.Radius * scaleX, button.Radius * scaleY, colour);
                        }
                    }
                }
            }

            if (drawCursor)
            {
                DrawStylusCursor();
            }
        }

        private void DrawStylusCursor()
        {
            float alpha = Math.Clamp(StylusZone.CursorOpacity, 0, 1);
            float pointerX = Mods.EndScreen.PointerX;
            float pointerY = Mods.EndScreen.PointerY;
            if (alpha <= 0 || pointerX < 0 || pointerX > 1 || pointerY < 0 || pointerY > 1)
            {
                return;
            }

            float x = pointerX * 256f;
            float y = pointerY * 192f;
            const float pixel = 0.42f;
            // One offset black copy gives the white pixel arrow enough edge
            // contrast to stay readable on both bright and dark rooms.
            DrawStylusCursorShape(x + pixel, y + pixel, pixel,
                new Vector4(0, 0, 0, alpha * 0.7f));
            DrawStylusCursorShape(x, y, pixel, new Vector4(1, 1, 1, alpha));
        }

        private void DrawStylusCursorShape(float x, float y, float pixel, Vector4 colour)
        {
            // A small stepped arrow with the hotspot at its top-left corner.
            // Flat HUD boxes keep it crisp at the same logical resolution as
            // the DS overlay and avoid adding a cursor texture/resource.
            for (int row = 0; row < 7; row++)
            {
                _scene.DrawHudFlatBox(x, y + row * pixel,
                    x + (row + 1) * pixel, y + (row + 1) * pixel, colour);
            }
            _scene.DrawHudFlatBox(x + 2 * pixel, y + 6 * pixel,
                x + 4 * pixel, y + 10 * pixel, colour);
        }

        /// <summary>
        /// Put the weapon wheel where the bottom screen is, and say how big to
        /// draw it.
        ///
        /// The wheel is the DS's touch screen: a quarter-arc in the corner of
        /// it, chosen by putting the stylus on a segment. The port drew it
        /// across the whole window, which is the right answer when the window
        /// *is* the bottom screen and the wrong one the moment the player has
        /// marked out a rectangle and mapped a tablet to it -- the picture was
        /// then in one place and the hand in another, and the arc filled a
        /// screen it had no business covering. So with a zone, the wheel is
        /// drawn in the zone: the same six positions, the same shape, in the
        /// rectangle the hand already knows.
        ///
        /// Returns the scale for <c>DrawHudObject</c>'s mode 1, which derives
        /// its size from the window's height -- the zone's own height as a
        /// fraction of the window is exactly the factor that turns "as big as
        /// the screen" into "as big as the zone", and it goes on both axes so
        /// the icons keep their shape.
        ///
        /// <see cref="PlayerEntity.UpdateWeaponArc"/> measures the arc in the
        /// same rectangle, from the same four numbers. Two descriptions of
        /// where the wheel is would drift, and the one that drifts is the
        /// invisible one.
        /// </summary>
        internal float ModPlaceWeaponSelect()
        {
            for (int i = 0; i < _weaponSelectHome.Length; i++)
            {
                Vector2 home = _weaponSelectHome[i];
                float x = home.X;
                float y = home.Y;
                if (GamepadInput.WheelHeld)
                {
                    float angle = (i + .5f) * MathF.PI / 3;
                    x = .5f + MathF.Sin(angle) * .23f * _scene.Size.Y / Math.Max(1, _scene.Size.X);
                    y = .5f - MathF.Cos(angle) * .23f;
                }
                else if (StylusZone.Enabled)
                {
                    x = StylusZone.Left + home.X * StylusZone.Width;
                    y = StylusZone.Top + home.Y * StylusZone.Height;
                }
                _weaponSelectInsts[i].PositionX = x;
                _weaponSelectInsts[i].PositionY = y;
                _selectBoxInsts[i].PositionX = x;
                _selectBoxInsts[i].PositionY = y;
            }
            return StylusZone.Enabled && !GamepadInput.WheelHeld ? StylusZone.Height : 1;
        }

        /// <summary>
        /// A filled ellipse, as a stack of horizontal spans.
        ///
        /// Two radii rather than one: the zone is a fraction of the window in
        /// x and of its height in y, and a window is not square, so a circle
        /// in DS units is an ellipse in these.
        /// </summary>
        private void DrawStylusCircle(float centreX, float centreY, float radiusX, float radiusY,
            Vector4 colour)
        {
            int rows = (int)MathF.Ceiling(radiusY * 2);
            if (rows < 2 || radiusX <= 0)
            {
                return;
            }
            rows = Math.Min(rows, 96);
            float step = radiusY * 2 / rows;
            for (int i = 0; i < rows; i++)
            {
                float y = -radiusY + (i + 0.5f) * step;
                float t = y / radiusY;
                float half = radiusX * MathF.Sqrt(Math.Max(0, 1 - t * t));
                if (half <= 0)
                {
                    continue;
                }
                _scene.DrawHudFlatBox(centreX - half, centreY + y,
                    centreX + half, centreY + y + step, colour);
            }
        }
    }
}
