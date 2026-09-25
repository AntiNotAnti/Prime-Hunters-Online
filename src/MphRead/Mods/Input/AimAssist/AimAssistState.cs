using System.Numerics;

namespace MphRead.Mods.Input.AimAssist
{
    public sealed class AimAssistState
    {
        public AimAssistTrackingState TrackingState;
        public Vector2 PreviousStick, FlickDirection;
        public float FlickPeak, FlickAge, SecondsSinceLookIntent, TrackingConfidence;
        public bool FlickActive, FlickConsumed;
        public int FlickTarget = -1;
        public int TargetSlot = -1;
        public long TargetLife;
        public float RetainedSeconds, HeadBlend, OccludedSeconds, HeadCandidateSeconds, PreviousDeltaTime;
        public bool PreviousBodyVisible, PreviousHeadVisible;
        public Vector2 PreviousError, PreviousHeadError, PreviousOutput;
        public Vector2 AngularVelocity, HeadAngularVelocity;
        public Vector2 AngularAcceleration, HeadAngularAcceleration;

        public void Reset()
        {
            TrackingState = AimAssistTrackingState.None;
            PreviousStick = FlickDirection = default;
            FlickPeak = FlickAge = SecondsSinceLookIntent = TrackingConfidence = 0;
            FlickActive = FlickConsumed = false;
            FlickTarget = -1;
            TargetSlot = -1;
            TargetLife = 0;
            RetainedSeconds = HeadBlend = OccludedSeconds = HeadCandidateSeconds = PreviousDeltaTime = 0;
            PreviousBodyVisible = PreviousHeadVisible = false;
            PreviousError = PreviousHeadError = PreviousOutput = default;
            AngularVelocity = HeadAngularVelocity = default;
            AngularAcceleration = HeadAngularAcceleration = default;
        }
    }
}
