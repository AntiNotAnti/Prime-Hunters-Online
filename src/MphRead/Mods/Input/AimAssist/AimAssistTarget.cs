using System.Numerics;

namespace MphRead.Mods.Input.AimAssist
{
    public readonly record struct AimAssistRegion(float MinYaw, float MaxYaw, float MinPitch, float MaxPitch)
    {
        public Vector2 Center => new((MinYaw + MaxYaw) / 2, (MinPitch + MaxPitch) / 2);
        public AimAssistRegion Inset(float fraction) => new(
            MinYaw + (MaxYaw - MinYaw) * fraction, MaxYaw - (MaxYaw - MinYaw) * fraction,
            MinPitch + (MaxPitch - MinPitch) * fraction, MaxPitch - (MaxPitch - MinPitch) * fraction);
    }

    public enum AimAssistTrackingState { None, AcquiringBody, TrackingBody, RefiningHead,
        TrackingHead, FlickCapturingHead, OccludedRetention }

    public enum AimAssistPointType { CenterMass, UpperChest, Head }

    public readonly record struct AimAssistTarget(int Slot, long Life, Vector2 BodyError, Vector2 HeadError,
        float Distance, bool BodyVisible, bool HeadVisible, bool Eligible = true,
        AimAssistPointType BodyPointType = AimAssistPointType.UpperChest,
        float HeadRadiusDegrees = .6f, AimAssistRegion? BodyRegion = null, AimAssistRegion? HeadRegion = null);

    public readonly record struct AimAssistResult(float X, float Y, int TargetSlot = -1, float Friction = 1,
        float RotationStrength = 0, AimAssistPointType PointType = AimAssistPointType.UpperChest,
        float HeadBlend = 0, float Score = 0, float InputAlignment = 0, float HeadPrediction = 0,
        bool Occluded = false, bool Saturated = false, float TargetAge = 0, AimAssistTrackingState TrackingState = AimAssistTrackingState.None,
        Vector2 PositionCorrection = default, Vector2 TrackingCorrection = default,
        Vector2 StickIntent = default, bool FlickActive = false, float FlickAge = 0,
        float FlickAlignment = 0, bool StrafeTracking = false, bool OpposingBreak = false,
        bool Firing = false);
}
