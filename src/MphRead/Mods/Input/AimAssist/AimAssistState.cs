using System.Numerics;

namespace MphRead.Mods.Input.AimAssist
{
    public sealed class AimAssistState
    {
        public AimAssistTrackingState TrackingState;
        public Vector2 PreviousStick, FlickDirection;
        public Vector2 StickHistory0, StickHistory1, StickHistory2, StickHistory3;
        public Vector2 PreviousRaw, PreviousCameraVelocity;
        public float FlickPeak, FlickSpeed, FlickAge, SecondsSinceLookIntent;
        public float BodyTrackingConfidence, HeadTrackingConfidence;
        public float TrackingConfidence { get => BodyTrackingConfidence; set => BodyTrackingConfidence = value; }
        public float ShotCommitSeconds, PreviousClosingSpeed;
        public bool FlickActive, FlickConsumed, FlickBraking, PreviousFiring;
        public bool PreviousInsideBody, PreviousInsideHead;
        public int FlickTarget = -1;
        public int TargetSlot = -1;
        public long TargetLife;
        public float RetainedSeconds, HeadBlend, OccludedSeconds, HeadCandidateSeconds, PreviousDeltaTime;
        public bool PreviousBodyVisible, PreviousHeadVisible;
        public Vector2 PreviousError, PreviousHeadError, PreviousOutput;
        public Vector2 AngularVelocity, HeadAngularVelocity;
        public Vector2 AngularAcceleration, HeadAngularAcceleration;
        public Vector2 MotionDirection, HeadMotionDirection;
        public AimAssistMotionPhase MotionPhase;

        public void PushStick(Vector2 sample)
        {
            StickHistory3 = StickHistory2;
            StickHistory2 = StickHistory1;
            StickHistory1 = StickHistory0;
            StickHistory0 = sample;
        }

        public void Reset()
        {
            TrackingState = AimAssistTrackingState.None;
            PreviousStick = FlickDirection = default;
            StickHistory0 = StickHistory1 = StickHistory2 = StickHistory3 = default;
            PreviousRaw = PreviousCameraVelocity = default;
            FlickPeak = FlickSpeed = FlickAge = SecondsSinceLookIntent = 0;
            BodyTrackingConfidence = HeadTrackingConfidence = 0;
            ShotCommitSeconds = PreviousClosingSpeed = 0;
            FlickActive = FlickConsumed = FlickBraking = PreviousFiring = false;
            PreviousInsideBody = PreviousInsideHead = false;
            FlickTarget = -1;
            TargetSlot = -1;
            TargetLife = 0;
            RetainedSeconds = HeadBlend = OccludedSeconds = HeadCandidateSeconds = PreviousDeltaTime = 0;
            PreviousBodyVisible = PreviousHeadVisible = false;
            PreviousError = PreviousHeadError = PreviousOutput = default;
            AngularVelocity = HeadAngularVelocity = default;
            AngularAcceleration = HeadAngularAcceleration = default;
            MotionDirection = HeadMotionDirection = default;
            MotionPhase = AimAssistMotionPhase.None;
        }
    }
}
