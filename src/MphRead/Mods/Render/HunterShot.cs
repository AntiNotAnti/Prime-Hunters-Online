using System;
using System.Threading.Tasks;

namespace MphRead.Mods.Render
{
    /// <summary>
    /// A platform that can render the real hunter model without a game window
    /// to render it into.
    ///
    /// The desktop does not need this. Its launcher is a screen composited
    /// into the game's own GL window, so the engine is right there with no
    /// match loaded and <see cref="LauncherHunter"/> simply draws the model
    /// into the back buffer under the screens. Android has no window under the
    /// launcher at all -- the screens are a real Avalonia view -- so the model
    /// has to arrive as a picture instead: rendered offscreen, once per hunter
    /// and suit, and handed to <c>HunterStand</c> to draw.
    ///
    /// Left null -- which is every desktop head, and the captures -- the stand
    /// draws its own boxes, exactly as it always did.
    /// </summary>
    public interface IHunterShot
    {
        /// <summary>
        /// One hunter, in one suit, at this many device pixels. BGRA, top
        /// down, four bytes a pixel and no padding -- which is what a
        /// <c>WriteableBitmap</c> takes without a conversion.
        ///
        /// Null when it could not be rendered, which is never an error worth
        /// showing: the stand has boxes to fall back on.
        /// </summary>
        Task<byte[]?> RenderAsync(Hunter hunter, int suit, int width, int height);
    }

    /// <summary>The renderer this build has, or null. Set by the platform head.</summary>
    public static class HunterShot
    {
        public static IHunterShot? Current { get; set; }

        /// <summary>
        /// Whether the screens are being composited into the game's own frame
        /// rather than drawn on a view of their own.
        ///
        /// While they are, the model is not a picture at all: the engine is
        /// running, the results screen already steps a hunter preview, and it
        /// is drawn straight into the frame after the screens' texture is
        /// down -- into the hole the stand leaves. That is what the desktop
        /// does (see <see cref="LauncherHunter"/>), and it is why a phone had
        /// the real model on the results screen before the panel existed.
        /// </summary>
        public static bool InFrame { get; set; }

        /// <summary>
        /// Where the stand is, in fractions of the frame, and who is standing
        /// in it.
        ///
        /// Its own four numbers rather than <c>Scene.Preview*</c>, which the
        /// results HUD publishes too. Sharing them meant that on any frame
        /// the HUD had spoken last, the model was painted into the *HUD's*
        /// slot -- a black window over the scoreboard's deaths column, on the
        /// face of the panel that has no hunter on it at all.
        /// </summary>
        public static bool HoleWanted { get; set; }

        public static float HoleLeft { get; set; }
        public static float HoleTop { get; set; }
        public static float HoleRight { get; set; }
        public static float HoleBottom { get; set; }
        public static Hunter HoleHunter { get; set; } = Hunter.Samus;
        public static int HoleSuit { get; set; }

        /// <summary>
        /// The frame the screens are being composited into, in its own
        /// pixels.
        ///
        /// Published by the head rather than read off the top level: a root
        /// embedded in somebody else's frame is told its size through the
        /// platform's resize callback, and a <c>ClientSize</c> that has not
        /// caught up is a hole measured against the wrong rectangle, which is
        /// a black window somewhere near but not on the panel.
        /// </summary>
        public static double FrameWidth { get; set; }

        public static double FrameHeight { get; set; }

        /// <summary>
        /// What the screens are scaled by inside that frame.
        ///
        /// A control's own coordinates are its layout's, and the layout is
        /// measured through the inverse of this -- so a point translated up
        /// to the root is still in layout units and has to be carried across
        /// by hand. Getting that wrong is a hole at four tenths of the right
        /// distance from the corner, which is a black window in the middle of
        /// the scoreboard.
        /// </summary>
        public static double FrameScale { get; set; } = 1;
    }
}
