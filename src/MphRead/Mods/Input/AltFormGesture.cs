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

        // At 1x, 24 filtered mouse counts in one fixed step reaches full
        // deflection. The shared swipe sensitivity expands that to 96 counts at
        // 0.25x or contracts it to 6 at 4x without involving normal aim
        // sensitivity.
        public const float MouseDriveFullScale = 24f;

        /// <summary>
        /// Relative mouse movement has no anchor to hold away from centre, so
        /// each simulation step is its own virtual-stick sample. Zero movement
        /// means centre; stopping the mouse therefore stops normal rolling on
        /// the next simulation step, matching the precision touch/pen path.
        /// </summary>
        public static (float X, float Y) MouseDrive(float deltaX, float deltaY,
            float sensitivity)
            => Drive(deltaX, deltaY, deadZone: 0, MouseDriveFullScale, sensitivity);

        /// <summary>
        /// Convert anchored screen-space displacement to an analogue virtual
        /// stick. The dead zone stays physically stable while sensitivity
        /// changes the travel required to reach full deflection.
        /// </summary>
        public static (float X, float Y) Drive(float x, float y, float deadZone,
            float fullScale, float sensitivity)
        {
            if (!Single.IsFinite(x) || !Single.IsFinite(y)
                || !Single.IsFinite(deadZone) || !Single.IsFinite(fullScale))
            {
                return (0, 0);
            }
            deadZone = MathF.Max(0, deadZone);
            sensitivity = Single.IsFinite(sensitivity)
                ? Math.Clamp(sensitivity, 0.25f, 4f) : 1f;
            fullScale = MathF.Max(deadZone + 0.001f, fullScale / sensitivity);

            float lengthSq = x * x + y * y;
            if (!Single.IsFinite(lengthSq) || lengthSq <= deadZone * deadZone)
            {
                return (0, 0);
            }
            float length = MathF.Sqrt(lengthSq);
            float magnitude = Math.Clamp((length - deadZone) / (fullScale - deadZone), 0, 1);
            return (x / length * magnitude, y / length * magnitude);
        }

        /// <summary>
        /// Direct-control velocity for the normal rolling-speed envelope.
        /// Boosts and other large impulses deliberately fall outside that
        /// envelope so precise swipe steering cannot erase them.
        /// </summary>
        public static bool TryPrecisionVelocity(float currentX, float currentZ,
            float driveX, float driveZ, float normalSpeed, out float x, out float z)
        {
            x = currentX;
            z = currentZ;
            if (!Single.IsFinite(currentX) || !Single.IsFinite(currentZ)
                || !Single.IsFinite(driveX) || !Single.IsFinite(driveZ)
                || !Single.IsFinite(normalSpeed) || normalSpeed <= 0)
            {
                return false;
            }

            float currentSq = currentX * currentX + currentZ * currentZ;
            float controlledLimit = normalSpeed * 1.25f;
            if (!Single.IsFinite(currentSq) || currentSq > controlledLimit * controlledLimit)
            {
                return false;
            }

            float driveSq = driveX * driveX + driveZ * driveZ;
            if (!Single.IsFinite(driveSq))
            {
                return false;
            }
            if (driveSq > 1)
            {
                float inv = 1f / MathF.Sqrt(driveSq);
                driveX *= inv;
                driveZ *= inv;
            }
            x = driveX * normalSpeed;
            z = driveZ * normalSpeed;
            return true;
        }

        /// <summary>
        /// Convert a screen-space drag (X right, Y down) to the engine's
        /// eight-way digital movement surface. Kept for input diagnostics and
        /// any discrete callers; precision swipe movement uses <see cref="Drive"/>.
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
