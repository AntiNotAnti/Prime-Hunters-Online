using System;
using MphRead.Hud;
using MphRead.Mods;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    /// <summary>
    /// The next map, voted on the results screen, with a picture of every
    /// answer.
    ///
    /// Most votes wins and there is no threshold, so what a row has to say is
    /// how many want it, how many there are -- "3 OF 8" -- and which one is in
    /// front, which is the row marked NEXT.
    ///
    /// Under the hunter picker and in the same column, because the two are the
    /// same question asked twice -- who you come back as and where you come
    /// back to -- and because the rest of the frame is spoken for: the
    /// winner's camera is behind everything and the scoreboard is down the
    /// middle. <see cref="MapPick"/> owns the list, the tally and the input;
    /// this draws them and decides nothing, which is <c>ModDrawEndScreen</c>'s
    /// arrangement and its reason.
    ///
    /// <para>
    /// A row is a thumbnail, the map's name, and how many people want it. The
    /// thumbnail is the reason the feature exists: half these rooms are known
    /// by their shape rather than by what the cartridge calls them, and "MP7
    /// PROCESSOR CORE" answers nothing in the ten seconds somebody has to
    /// decide. The pictures are the ones the launcher's Play screen has always
    /// shown, rendered from the player's own files
    /// (<see cref="Mods.Render.MapThumbnail"/>).
    /// </para>
    ///
    /// <para>
    /// The list is every map and is scrolled; the panel shows four of them at
    /// a time. Maps somebody has voted for are pulled to the top by
    /// <see cref="MapPick"/>, so what the room is actually choosing between is
    /// always the part of the list you are already looking at -- a vote nobody
    /// can see is not a vote. Rows with votes are drawn on a lit band with
    /// their count; the row this machine picked wears the ring.
    /// </para>
    ///
    /// <para>
    /// The whole block is scaled to whatever is left under the picker rather
    /// than drawn at a size and hoped for. The picker's own height is derived
    /// from its contents and is half again as tall on a phone, so the space
    /// below it is not a number anybody can write down -- and the failure mode
    /// of guessing is the one that has already happened once on this screen,
    /// with READY hanging off the bottom edge of its panel.
    /// </para>
    /// </summary>
    public partial class PlayerEntity
    {
        private static readonly Vector4 _pickPanel = new Vector4(0, 0, 0, 0.62f);
        private static readonly Vector4 _pickEdge = new Vector4(1, 1, 1, 0.16f);
        private static readonly Vector4 _pickRing = new Vector4(1, 0.84f, 0.35f, 0.95f);
        private static readonly Vector4 _pickHover = new Vector4(1, 1, 1, 0.16f);
        private static readonly Vector4 _pickCursor = new Vector4(1, 1, 1, 0.09f);
        private static readonly Vector4 _pickVoted = new Vector4(1, 0.84f, 0.35f, 0.12f);
        private static readonly Vector4 _pickCarriedBand = new Vector4(0.35f, 0.85f, 0.4f, 0.16f);
        private static readonly Vector4 _pickWell = new Vector4(0, 0, 0, 0.5f);
        private static readonly Vector4 _pickBar = new Vector4(1, 1, 1, 0.22f);
        private static readonly ColorRgba _pickInk = new ColorRgba(235, 238, 245, 255);
        private static readonly ColorRgba _pickDim = new ColorRgba(165, 174, 190, 255);
        private static readonly ColorRgba _pickTally = new ColorRgba(255, 215, 90, 255);
        private static readonly ColorRgba _pickCarried = new ColorRgba(150, 240, 160, 255);

        /// <summary>The title over the rows, in design units.</summary>
        private const float PickTitle = 8;

        /// <summary>A thumbnail's height, and the gap under it.</summary>
        private const float PickThumb = 13;
        private const float PickGap = 2.5f;
        private const float PickRow = PickThumb + PickGap;

        /// <summary>
        /// A thumbnail's width, in the same height units everything else here
        /// is measured in, so the picture keeps 16:9 on any window -- the
        /// aspect correction is applied once, where the box is drawn.
        /// </summary>
        private const float PickThumbWidth = PickThumb * 16 / 9f;

        /// <summary>The bottom of the frame this may use. The HUD is 192 tall
        /// and the last two units of it are the edge of the screen.</summary>
        private const float PickFloor = 190;

        /// <summary>
        /// How many rows the panel will ever draw.
        ///
        /// Four is what fits under the picker at 1x on a 16:9 window, and the
        /// list is scrolled rather than grown: a taller panel would reach the
        /// bottom of the frame on one monitor and the middle of the scoreboard
        /// on another, and there are twenty-seven maps either way.
        /// </summary>
        private const int PickRowsMax = 4;

        private readonly EndScreen.Hit[] _pickHits = new EndScreen.Hit[PickRowsMax];

        internal void ModDrawMapPick(float panelBottom)
        {
            int total = MapPick.Order.Count;
            if (!MapPick.Available || total == 0)
            {
                return;
            }
            float top = panelBottom + 3;
            float room = PickFloor - top;
            int rows = PickRowsMax;
            float scale = 0;
            // As many rows as will fit at a size worth reading, fewest last:
            // a phone has room for two and a 16:9 desktop for four, and the
            // alternative -- one row count and a scale that shrinks to fit --
            // is how you end up with four rows of unreadable text.
            while (rows >= 1)
            {
                float needed = PickTitle + rows * PickRow + 2;
                scale = Math.Min(EndScale, room / needed);
                if (scale >= 0.5f)
                {
                    break;
                }
                rows--;
            }
            if (rows < 1 || scale < 0.5f)
            {
                return;
            }
            rows = Math.Min(rows, total);
            float aspect = HudAspectFix;
            float width = EndPanelWidth * scale;
            float right = 254;
            float left = right - width * aspect;
            float centre = left + width / 2 * aspect;
            float bottom = top + (PickTitle + rows * PickRow + 2) * scale;
            _scene.DrawHudFlatBox(left, top, right, bottom, _pickPanel);
            // The same hairline the picker wears down its inside edge, so the
            // two read as one column rather than as two panels that happen to
            // be stacked.
            _scene.DrawHudFlatBox(left, top, left + 0.6f * aspect, bottom, _pickEdge);
            // What the thing is and how it is decided, in the one line there
            // is room for. "Most wins" is worth the words: without it a list
            // reads as "click a map and it happens", which is what it does
            // with nobody else in the room and not what it does with seven.
            string title = MapPick.HasReturnToLobbyOption
                ? (MapPick.Eligible > 1 ? "CHOOSE NEXT  MOST WINS" : "CHOOSE NEXT")
                : (MapPick.Eligible > 1 ? "VOTE NEXT MAP  MOST WINS" : "VOTE NEXT MAP");
            if (Mods.Input.InputSourceTracker.Current == Mods.Input.InputSource.Gamepad)
                title = Mods.Input.GamepadGlyphs.Resolve(Mods.Input.GamepadButtons.RightBumper).ToUpperInvariant() + " VOTE  UP/DOWN SELECT";
            DrawText2D(centre, top + 1.5f * scale, Align.Center, palette: 0, title,
                color: _pickDim, fontSpacing: 8, scale: 0.42f * scale);
            int picked = MapPick.PickedIndex;
            int hovered = MapPick.Hovered();
            int cursor = MapPick.Cursor;
            int scroll = Math.Clamp(MapPick.Scroll, 0, Math.Max(0, total - rows));
            float rowTop = top + PickTitle * scale;
            for (int i = 0; i < rows; i++)
            {
                int index = scroll + i;
                DrawPickRow(i, index, left, rowTop, width, scale, aspect,
                    picked == index, hovered == index, cursor == index);
                rowTop += PickRow * scale;
            }
            DrawPickScrollBar(right, top + PickTitle * scale, rows * PickRow * scale,
                scroll, rows, total, aspect);
            MapPick.NoteLayout(_pickHits, rows);
        }

        /// <summary>
        /// A hairline down the panel's outer edge saying how far through
        /// twenty-seven maps this is.
        ///
        /// Not decoration: four rows of a long list look exactly like a short
        /// list, and somebody who does not know there is more will not scroll.
        /// </summary>
        private void DrawPickScrollBar(float right, float top, float height,
            int scroll, int rows, int total, float aspect)
        {
            if (total <= rows || height <= 0)
            {
                return;
            }
            float width = 0.8f * aspect;
            float thumb = Math.Max(height * rows / total, 3);
            float travel = height - thumb;
            float at = total > rows ? scroll / (float)(total - rows) : 0;
            _scene.DrawHudFlatBox(right - width, top + travel * at,
                right, top + travel * at + thumb, _pickBar);
        }

        private void DrawPickRow(int slot, int index, float left, float top, float width,
            float scale, float aspect, bool picked, bool hovered, bool cursor)
        {
            string key = MapPick.Order[index];
            int votes = MapPick.VotesFor(key);
            // The one in front, which is the one that will be loaded. Told
            // apart from the rest rather than merely counted, because "most
            // votes wins" makes the top of the list the answer and a row that
            // is only a bigger number does not say so.
            bool leading = votes > 0 && String.Equals(key, MapPick.Leader,
                StringComparison.OrdinalIgnoreCase);
            float height = PickThumb * scale;
            float rowRight = left + width * aspect;
            float bottom = top + height;
            float bandTop = top - PickGap / 2 * scale;
            float bandBottom = bottom + PickGap / 2 * scale;
            // A lit band for a map somebody wants, so the votes read as a
            // block at the top of the list rather than as four numbers to
            // find. Green once it has the room behind it, which is the one
            // state worth telling apart at a glance.
            if (votes > 0)
            {
                _scene.DrawHudFlatBox(left, bandTop, rowRight, bandBottom,
                    leading ? _pickCarriedBand : _pickVoted);
            }
            if (hovered)
            {
                _scene.DrawHudFlatBox(left, bandTop, rowRight, bandBottom, _pickHover);
            }
            else if (cursor)
            {
                _scene.DrawHudFlatBox(left, bandTop, rowRight, bandBottom, _pickCursor);
            }
            float thumbLeft = left + 1.5f * scale * aspect;
            float thumbRight = thumbLeft + PickThumbWidth * scale * aspect;
            // The well is drawn whether or not there is a picture: a room
            // whose preview has not been rendered yet still has a row, and an
            // empty slot with an outline reads as "no picture" rather than as
            // a hole in the panel.
            _scene.DrawHudFlatBox(thumbLeft, top, thumbRight, bottom, _pickWell);
            int texture = MapPick.IsReturnToLobby(key)
                ? 0
                : MphRead.Mods.Render.MapThumbnail.For(key, _scene);
            if (texture > 0)
            {
                _scene.DrawHudTexture(thumbLeft, top, thumbRight, bottom, texture);
            }
            if (picked)
            {
                DrawPickRing(thumbLeft, top, thumbRight, bottom, scale, aspect);
            }
            float textLeft = thumbRight + 2 * scale * aspect;
            string name = MapPick.NameOf(key).ToUpperInvariant();
            // Cut rather than shrunk: a column of names at four different
            // sizes is harder to read than one where they all stop in the same
            // place, and the picture beside it is doing most of the work.
            if (name.Length > 15)
            {
                name = name[..15];
            }
            DrawText2D(textLeft, top + 1.5f * scale, Align.Left, palette: 0, name,
                color: votes > 0 || picked ? _pickInk : _pickDim,
                fontSpacing: 8, scale: 0.4f * scale);
            if (votes > 0)
            {
                // Out of what it takes, not on its own: three votes means
                // nothing without the number beside it, and that number is the
                // difference between a poll and a vote.
                string tally = MapPick.Eligible > 1 ? $"{votes} OF {MapPick.Eligible}"
                    : votes == 1 ? "1 VOTE" : $"{votes} VOTES";
                DrawText2D(textLeft, top + 7 * scale, Align.Left, palette: 0,
                    leading
                        ? MapPick.IsReturnToLobby(key) ? $"{tally}  LOBBY" : $"{tally}  NEXT"
                        : tally,
                    color: leading ? _pickCarried : _pickTally,
                    fontSpacing: 8, scale: 0.36f * scale);
            }
            _pickHits[slot] = ModHudHit(left, bandTop, rowRight, bandBottom);
        }

        /// <summary>A ring around the chosen row's picture, in the same yellow
        /// the arrows and the READY button use.</summary>
        private void DrawPickRing(float left, float top, float right, float bottom,
            float scale, float aspect)
        {
            float line = Math.Max(0.5f, 0.8f * scale);
            _scene.DrawHudFlatBox(left - line * aspect, top - line, right + line * aspect, top, _pickRing);
            _scene.DrawHudFlatBox(left - line * aspect, bottom, right + line * aspect,
                bottom + line, _pickRing);
            _scene.DrawHudFlatBox(left - line * aspect, top, left, bottom, _pickRing);
            _scene.DrawHudFlatBox(right, top, right + line * aspect, bottom, _pickRing);
        }
    }
}
