using System.Numerics;

namespace MphRead.Mods.Input.AimAssist
{
    public readonly record struct AimAssistRegion(float MinYaw, float MaxYaw, float MinPitch, float MaxPitch)
    {
        public Vector2 Center => new((MinYaw + MaxYaw) / 2, (MinPitch + MaxPitch) / 2);
        public float Width => MaxYaw - MinYaw;
        public float Height => MaxPitch - MinPitch;
        public AimAssistRegion Inset(float fraction) => new(
            MinYaw + Width * fraction, MaxYaw - Width * fraction,
            MinPitch + Height * fraction, MaxPitch - Height * fraction);
        public AimAssistRegion Shift(float yaw, float pitch) => new(
            MinYaw + yaw, MaxYaw + yaw, MinPitch + pitch, MaxPitch + pitch);
    }

    /// <summary>
    /// Nearest angular correction to a ray that actually intersects the target's
    /// cylindrical hit surface inside the requested vertical band. The broad
    /// AimAssistRegion remains useful for scoring/debug drawing; this is the
    /// geometry used for precision correction and containment.
    /// </summary>
    public readonly record struct AimAssistSurface(Vector2 Error, bool Inside);

    public enum AimAssistTrackingState { None, AcquiringBody, TrackingBody, RefiningHead,
        TrackingHead, FlickCapturingHead, OccludedRetention }

    public enum AimAssistPointType { CenterMass, UpperChest, Head }
    public enum AimAssistMotionPhase { None, Approaching, Braking, Matched, Overshooting, Escaping }

    public readonly record struct AimAssistTarget(int Slot, long Life, Vector2 BodyError, Vector2 HeadError,
        float Distance, bool BodyVisible, bool HeadVisible, bool Eligible = true,
        AimAssistPointType BodyPointType = AimAssistPointType.UpperChest,
        float HeadRadiusDegrees = .6f, AimAssistRegion? BodyRegion = null, AimAssistRegion? HeadRegion = null,
        AimAssistSurface? BodySurface = null, AimAssistSurface? HeadSurface = null,
        float BodyVisibility = 1f, float HeadVisibility = 1f);

    public readonly record struct AimAssistResult(float X, float Y, int TargetSlot = -1, float Friction = 1,
        float RotationStrength = 0, AimAssistPointType PointType = AimAssistPointType.UpperChest,
        float HeadBlend = 0, float Score = 0, float InputAlignment = 0, float HeadPrediction = 0,
        bool Occluded = false, bool Saturated = false, float TargetAge = 0, AimAssistTrackingState TrackingState = AimAssistTrackingState.None,
        Vector2 PositionCorrection = default, Vector2 TrackingCorrection = default,
        Vector2 StickIntent = default, bool FlickActive = false, float FlickAge = 0,
        float FlickAlignment = 0, bool StrafeTracking = false, bool OpposingBreak = false,
        bool Firing = false, AimAssistMotionPhase MotionPhase = AimAssistMotionPhase.None,
        float BodyTrackingConfidence = 0, float HeadTrackingConfidence = 0,
        float FlickLandingError = 0, bool FlickBraking = false, bool ShotCommitted = false,
        float VisibilityCoverage = 0, float FilterRelease = 0);
}
