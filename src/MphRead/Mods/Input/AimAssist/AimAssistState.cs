using System.Numerics;

namespace MphRead.Mods.Input.AimAssist
{
    public sealed class AimAssistState
    {
        public AimAssistTrackingState TrackingState;
        public Vector2 PreviousStick, FlickDirection;
        public Vector2 StickHistory0, StickHistory1, StickHistory2, StickHistory3;
        public Vector2 PreviousRaw, PreviousCameraVelocity, ServoVelocity;
        public Vector2 CameraVelocity0, CameraVelocity1, CameraVelocity2, CameraVelocity3;
        public float FlickPeak, FlickSpeed, FlickAge, SecondsSinceLookIntent;
        public float BodyTrackingConfidence, HeadTrackingConfidence;
        public float TrackingConfidence { get => BodyTrackingConfidence; set => BodyTrackingConfidence = value; }
        public float ShotCommitSeconds, PreviousClosingSpeed;
        public float SmoothedBodyVisibility, SmoothedHeadVisibility;
        public float CorrectionBudgetUsed, ScopeBlend, MotionTransitionSeconds;
        public bool FlickActive, FlickConsumed, FlickBraking, PreviousFiring, MotionTransition;
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

        public void PushCameraVelocity(Vector2 sample)
        {
            CameraVelocity3 = CameraVelocity2;
            CameraVelocity2 = CameraVelocity1;
            CameraVelocity1 = CameraVelocity0;
            CameraVelocity0 = sample;
        }

        public void BeginScopeTransition(float blend)
        {
            ScopeBlend = blend;
            // Scope changes camera gain/FOV, not target identity. Keep target,
            // confidence and target-motion history; discard only transient
            // landing/shot states whose geometry was measured in the old view.
            FlickActive = FlickConsumed = FlickBraking = false;
            FlickTarget = -1;
            FlickAge = FlickSpeed = FlickPeak = 0;
            ShotCommitSeconds = 0;
        }

        public void Reset()
        {
            TrackingState = AimAssistTrackingState.None;
            PreviousStick = FlickDirection = default;
            StickHistory0 = StickHistory1 = StickHistory2 = StickHistory3 = default;
            PreviousRaw = PreviousCameraVelocity = ServoVelocity = default;
            CameraVelocity0 = CameraVelocity1 = CameraVelocity2 = CameraVelocity3 = default;
            FlickPeak = FlickSpeed = FlickAge = SecondsSinceLookIntent = 0;
            BodyTrackingConfidence = HeadTrackingConfidence = 0;
            ShotCommitSeconds = PreviousClosingSpeed = 0;
            SmoothedBodyVisibility = SmoothedHeadVisibility = 0;
            CorrectionBudgetUsed = ScopeBlend = MotionTransitionSeconds = 0;
            FlickActive = FlickConsumed = FlickBraking = PreviousFiring = MotionTransition = false;
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
