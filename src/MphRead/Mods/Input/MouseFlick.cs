using System;

namespace MphRead.Mods.Input
{
    /// <summary>
    /// The morph ball's boost, asked for by whipping a mouse or stylus.
    ///
    /// The gesture already exists on the touch head -- a flick on the aim
    /// side boosts, the way a flick of the stylus did on the DS -- and the
    /// desktop has the same free hand: the ball is steered with the roll
    /// binds against the camera's basis and the camera trails it by itself,
    /// so **nothing in <c>ProcessAlt</c> reads a mouse delta at all** for the
    /// four hunters that roll. The hand on the mouse is idle for as long as
    /// the player is a ball, and a boost is the one thing it can be asked for
    /// that costs no bind and collides with nothing.
    ///
    /// **The threshold is a turn, not a distance.** A number of pixels is a
    /// statement about the player's mouse rather than about their hand -- the
    /// same whip is 200 px on one desk and 2000 on the next -- and a fraction
    /// of the window is a statement about their monitor, which has even less
    /// to do with it. What a flick actually is, is "the movement that would
    /// have spun me round if I were on foot", so that is what is measured:
    /// the delta is converted with the game's own aim arithmetic
    /// (<c>delta / 4 * sensitivity</c> degrees) and compared against
    /// <see cref="TurnDegrees"/>. It then self-calibrates to whatever
    /// sensitivity the player already chose, which is the one number in the
    /// program that does describe their hand.
    ///
    /// **What is measured is a straight burst ending on this frame, not the
    /// window's largest displacement.** The first version summed whichever
    /// run of recent frames came out longest, which fires on a hand that
    /// merely wandered a long way and -- worse -- hands back the direction of
    /// that wandering rather than of the whip the player just made. Flicking
    /// down while the sum was still dominated by an earlier upward drift gave
    /// a boost upwards, which is exactly what "the mouse goes down and Samus
    /// still goes up" was. So the burst is grown backwards from the newest
    /// frame and stops at the first sample that is slow (the hand was not
    /// moving yet) or that points somewhere else (<see cref="Coherence"/>):
    /// what it adds up is one movement in one direction, and the direction it
    /// reports is that movement's -- weighted towards the fast part of it,
    /// since the frames a hand spends breaking out of rest point wherever the
    /// wrist was rather than where the player is throwing it.
    ///
    /// **The hand has to come to rest between flicks.** A cooldown alone
    /// still lets one long sweep fire repeatedly, once per cooldown, for as
    /// long as it goes on. A frame slower than <see cref="RestDegrees"/> is
    /// what arms the next one, which also covers the frame a cursor is warped
    /// in -- unpausing, regaining focus -- where the delta is enormous and
    /// means nothing.
    ///
    /// The gesture is optional per desktop pointer source. Settings can disable
    /// mouse movement boost and stylus movement boost independently; that only
    /// gates this movement gesture. Manual Zoom/right-click boost and the explicit
    /// Boost binding stay live, matching melonPrime's separation between swipe
    /// assist and button-driven boost.
    /// </summary>
    public static class MouseFlick
    {
        /// <summary>
        /// How many frames a flick may be spread over. Five is 83 ms at the
        /// fixed 60 Hz; the burst is searched rather than the newest frame
        /// alone, because a whip that lands in one frame on one machine lands
        /// in three on another and the player did the same thing both times.
        /// </summary>
        private const int Burst = 5;

        /// <summary>
        /// The turn the burst would have made on foot, in degrees, for it to
        /// count. A third of a turn inside 83 ms is a whip nobody makes by
        /// accident -- and at the default sensitivity it is about a
        /// centimetre and a half of desk, since 180 degrees of this game's
        /// aim is under an inch.
        /// </summary>
        private const float TurnDegrees = 120;

        /// <summary>
        /// A frame that would have turned less than this is the hand at rest:
        /// it ends the burst behind it, and it is what arms the next flick.
        /// </summary>
        private const float RestDegrees = 3;

        /// <summary>
        /// How straight the burst has to be -- the cosine of the angle an
        /// older frame may make with the run already gathered. About 30
        /// degrees: generous enough for a whip that curves, tight enough that
        /// two movements never add up to one, and tight enough that the
        /// direction handed back is the direction the hand went. It was 45,
        /// which is defensible while the answer is snapped to one of four
        /// directions and far too loose once it is not: a frame 44 degrees
        /// off the run is usually the hand breaking out of rest, and letting
        /// it into the sum tilts the whole flick by the part of it nobody
        /// meant.
        /// </summary>
        private const float Coherence = 0.86f;

        /// <summary>
        /// Frames before another flick is read, matching the touch head's
        /// 350 ms.
        /// </summary>
        private const int Cooldown = 21;

        private static readonly float[] _deltaX = new float[Burst];
        private static readonly float[] _deltaY = new float[Burst];
        private static int _count;
        private static int _newest = -1;
        private static ulong _lastFrame;
        private static bool _fed;
        private static bool _armed;
        private static ulong _cooldownUntil;

        /// <summary>
        /// Flicks read since the process started. Only ever reported in a
        /// log: it is how "the boost fires when I do not ask for it" and "the
        /// flick does nothing" are told apart at all.
        /// </summary>
        public static int Fired { get; private set; }

        /// <summary>
        /// Forget the gesture in progress, and require the hand to come to
        /// rest before another one counts. Called whenever the mouse is
        /// saying something else, so that what it said is not read as a whip
        /// on the frame it stops saying it.
        /// </summary>
        public static void Reset()
        {
            ClearSamples();
            _fed = false;
            _armed = false;
        }

        private static void ClearSamples()
        {
            _count = 0;
            _newest = -1;
        }

        /// <summary>
        /// One frame of mouse movement, and whether it completed a flick.
        /// The direction comes back as the screen saw it -- X to the right,
        /// Y downwards, unit length -- which is the same thing the touch head
        /// reports, so the engine turns both into a world direction the one
        /// way.
        /// </summary>
        public static bool Check(float deltaX, float deltaY, ulong frame, out float dirX, out float dirY)
        {
            dirX = 0;
            dirY = 0;
            // A gap means the player was not a ball a moment ago (or was
            // frozen, or paused): the samples on the other side of it belong
            // to a different gesture, and the first delta after one is quite
            // often a cursor that was warped rather than moved.
            if (!_fed || frame != _lastFrame + 1)
            {
                Reset();
            }
            _fed = true;
            _lastFrame = frame;
            _newest = (_newest + 1) % Burst;
            _deltaX[_newest] = deltaX;
            _deltaY[_newest] = deltaY;
            if (_count < Burst)
            {
                _count++;
            }
            float sensitivity = InputSettings.MouseSensitivity;
            if (sensitivity <= 0)
            {
                return false;
            }
            // The aim arithmetic in ProcessBiped, run backwards: this many
            // pixels is that many degrees of turn at the sensitivity in force.
            float rest = RestDegrees * 4 / sensitivity;
            float threshold = TurnDegrees * 4 / sensitivity;
            float sumX = deltaX;
            float sumY = deltaY;
            float magnitude = MathF.Sqrt(sumX * sumX + sumY * sumY);
            if (magnitude < rest)
            {
                _armed = true;
                return false;
            }
            // The direction is taken from the fast part of the whip and the
            // threshold from the whole of it, which are two different
            // questions about the same burst. A flick starts from rest, so
            // its first frames are the hand breaking away -- slow, and
            // pointing wherever the wrist happened to be -- and they are the
            // least representative thing in the sample. Summing displacement
            // gives them a vote in proportion to how far they went; summing
            // each frame weighted by its own speed gives them one in
            // proportion to the square of it, so the peak of the whip decides
            // and the run-up barely registers. That is what "it never goes in
            // the exact direction of my mouse" was, once there was no snap
            // left to hide it.
            float dirSumX = sumX * magnitude;
            float dirSumY = sumY * magnitude;
            if (!_armed || frame < _cooldownUntil)
            {
                return false;
            }
            // Grow the burst backwards while it stays one movement: stop at
            // the first frame the hand was not moving in, and at the first
            // that points somewhere else.
            for (int i = 1; i < _count; i++)
            {
                int index = (_newest - i + Burst) % Burst;
                float olderX = _deltaX[index];
                float olderY = _deltaY[index];
                float older = MathF.Sqrt(olderX * olderX + olderY * olderY);
                if (older < rest)
                {
                    break;
                }
                if (olderX * sumX + olderY * sumY < Coherence * older * magnitude)
                {
                    break;
                }
                sumX += olderX;
                sumY += olderY;
                dirSumX += olderX * older;
                dirSumY += olderY * older;
                magnitude = MathF.Sqrt(sumX * sumX + sumY * sumY);
            }
            if (magnitude < threshold)
            {
                return false;
            }
            // A direction and nothing else: how hard the flick was does not
            // set how hard the boost is -- it is always a full charge, as it
            // is on the touch head -- only which way it goes.
            float dirMag = MathF.Sqrt(dirSumX * dirSumX + dirSumY * dirSumY);
            if (dirMag > 0)
            {
                dirX = dirSumX / dirMag;
                dirY = dirSumY / dirMag;
            }
            else
            {
                dirX = sumX / magnitude;
                dirY = sumY / magnitude;
            }
            _cooldownUntil = frame + Cooldown;
            Fired++;
            if (DebugLog.Active)
            {
                // Every flick, not only the first. "It does not go where I
                // flicked" is a question about one gesture, and the first one
                // of the session is never the one being complained about.
                DebugLog.Line("input", $"mouse flick read as a boost: {magnitude:0} px, "
                    + $"{magnitude / 4 * sensitivity:0} degrees of turn, direction "
                    + $"({dirX:0.00}, {dirY:0.00}), {MathF.Atan2(-dirY, dirX) * 180 / MathF.PI:0} deg "
                    + "anticlockwise from screen right");
            }
            // The whip is spent. Its tail is not a second one, and the hand
            // has to stop before another is read.
            ClearSamples();
            _armed = false;
            return true;
        }
    }
}
