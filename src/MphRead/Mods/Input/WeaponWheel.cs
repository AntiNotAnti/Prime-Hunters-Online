using System;

namespace MphRead.Mods.Input
{
    /// <summary>
    /// The DS's weapon wheel, held open and answered by dragging.
    ///
    /// On the cartridge the wheel is a quarter-arc in the corner of the touch
    /// screen and the weapon is whichever segment the stylus is on when it
    /// lifts: an absolute question, asked of a device that has an absolute
    /// answer. A mouse does not. The port kept the arc and the arithmetic
    /// behind it -- the cursor's position on the window against five angle
    /// thresholds -- which meant releasing the pointer in the middle of a
    /// match and reaching for the top right corner of the screen with it,
    /// through a wheel drawn over a fight, with the view unable to follow.
    /// The cursor had to be somewhere when it appeared, and where it appeared
    /// already chose a weapon.
    ///
    /// So on a mouse the gesture is the one every game with a wheel uses:
    /// hold the button, move up or down, let go. The pointer stays grabbed --
    /// there is no cursor to place and none is shown -- the aim is locked for
    /// the length of the hold, which it already was, and each step of travel
    /// moves to the next weapon the player can actually equip. Nothing wraps:
    /// a drag has two ends and running off one of them and back onto the other
    /// is not something a hand does on purpose.
    ///
    /// The arc stays for the two devices it was written for, and
    /// <see cref="Absolute"/> is that fork: a pen tablet mapped to a bottom
    /// screen (<see cref="StylusZone"/>) and a phone's touchscreen both point
    /// at a place, which is what the arc reads.
    /// </summary>
    public static class WeaponWheel
    {
        /// <summary>
        /// Whether the pointer says *where* rather than *how far*: a pen on a
        /// tablet, or a finger on glass. Everything else drags.
        /// </summary>
        public static bool Absolute => StylusZone.Enabled || OperatingSystem.IsAndroid();

        /// <summary>How many weapons the wheel holds -- the six beams that are
        /// neither the Power Beam, the missile, nor the Omega Cannon.</summary>
        public const int Slots = 6;

        private static float _travel;
        private static int _index = -1;
        private static bool _open;

        /// <summary>Which slot the drag is on, or -1 for none of them.</summary>
        public static int Selection => _open ? _index : -1;

        /// <summary>
        /// The wheel is down again. Called when the menu closes, from every
        /// path that closes it -- a release, a pause, a dialog, a death.
        /// </summary>
        public static void Close()
        {
            _open = false;
            _travel = 0;
            _index = -1;
        }

        /// <summary>
        /// One frame of the hold. <paramref name="deltaY"/> is the pointer's
        /// vertical movement in window pixels, <paramref name="step"/> how far
        /// it travels for one weapon, and <paramref name="current"/> the slot
        /// the player is already holding, or -1 when the weapon in their hands
        /// is not on the wheel at all (the Power Beam, usually, which is
        /// exactly when somebody opens this).
        ///
        /// Returns the slot to highlight, or -1 to change nothing.
        /// </summary>
        public static int Drag(float deltaY, float step, bool[] available, int current)
        {
            if (!_open)
            {
                _open = true;
                _travel = 0;
                _index = current;
            }
            if (step <= 0)
            {
                step = 64;
            }
            // Rounded to whole steps rather than to a proportion of the way
            // between two: the highlight is a discrete thing and a wheel that
            // changes its mind halfway through a movement reads as a wobble.
            _travel += deltaY;
            while (_travel >= step)
            {
                _travel -= step;
                _index = Step(_index, 1, available);
            }
            while (_travel <= -step)
            {
                _travel += step;
                _index = Step(_index, -1, available);
            }
            return _index;
        }

        /// <summary>
        /// One weapon along, skipping what the player cannot equip and
        /// stopping at the ends.
        ///
        /// Skipping rather than stopping on an empty weapon is the rule the
        /// next/previous cycle already follows for its own reason (see
        /// <c>PlayerEntity.CanCycleToWeapon</c>): a selection that refuses to
        /// equip is a gesture that did nothing.
        /// </summary>
        private static int Step(int from, int direction, bool[] available)
        {
            if (from < 0)
            {
                // Nothing held yet: come onto the wheel from the end the
                // movement is travelling away from.
                int start = direction > 0 ? 0 : Slots - 1;
                for (int i = start; i >= 0 && i < Slots; i += direction)
                {
                    if (Has(available, i))
                    {
                        return i;
                    }
                }
                return -1;
            }
            for (int i = from + direction; i >= 0 && i < Slots; i += direction)
            {
                if (Has(available, i))
                {
                    return i;
                }
            }
            return from;
        }

        private static bool Has(bool[] available, int index)
        {
            return index >= 0 && index < available.Length && available[index];
        }
    }
}
