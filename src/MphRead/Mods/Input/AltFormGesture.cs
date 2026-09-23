using System;

namespace MphRead.Mods.Input
{
    [Flags]
    public enum AltMoveDirection
    {
        None = 0,
        Up = 1,
        Down = 2,
        Left = 4,
        Right = 8
    }

    public enum AltFlickAction
    {
        None,
        SamusBoost,
        SpireAttack
    }

    /// <summary>
    /// Shared policy for pointer-driven alternate-form input.
    ///
    /// Rolling hunters consume directional drags as the same Roll binds used by
    /// keyboard, controller and the Android movement stick. Trace, Sylux and
    /// Weavel keep pointer motion for transformed aiming instead.
    /// </summary>
    public static class AltFormGesture
    {
        public static bool UsesRollMovement(Hunter hunter)
            => hunter is Hunter.Samus or Hunter.Kanden or Hunter.Spire or Hunter.Noxus;

        public static AltFlickAction FlickAction(Hunter hunter) => hunter switch
        {
            Hunter.Samus => AltFlickAction.SamusBoost,
            Hunter.Spire => AltFlickAction.SpireAttack,
            _ => AltFlickAction.None
        };

        /// <summary>
        /// Convert a screen-space drag (X right, Y down) to the engine's
        /// eight-way digital movement surface. The caller chooses a dead zone
        /// in its own coordinate system.
        /// </summary>
        public static AltMoveDirection Direction(float x, float y, float deadZone)
        {
            if (!Single.IsFinite(x) || !Single.IsFinite(y) || !Single.IsFinite(deadZone))
            {
                return AltMoveDirection.None;
            }
            deadZone = MathF.Max(0, deadZone);
            float lengthSq = x * x + y * y;
            if (!Single.IsFinite(lengthSq) || lengthSq <= deadZone * deadZone)
            {
                return AltMoveDirection.None;
            }

            float angle = MathF.Atan2(-y, x) * (180f / MathF.PI);
            if (angle < 0)
            {
                angle += 360f;
            }

            AltMoveDirection direction = AltMoveDirection.None;
            if (angle > 22.5f && angle < 157.5f)
            {
                direction |= AltMoveDirection.Up;
            }
            if (angle > 202.5f && angle < 337.5f)
            {
                direction |= AltMoveDirection.Down;
            }
            if (angle > 112.5f && angle < 247.5f)
            {
                direction |= AltMoveDirection.Left;
            }
            if (angle < 67.5f || angle > 292.5f)
            {
                direction |= AltMoveDirection.Right;
            }
            return direction;
        }
    }
}
