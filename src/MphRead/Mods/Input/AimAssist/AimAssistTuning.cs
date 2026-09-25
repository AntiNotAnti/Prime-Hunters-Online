namespace MphRead.Mods.Input.AimAssist
{
    // Deliberately not player preferences. Changes require regression and balance validation.
    public static class AimAssistTuning
    {
        public const float HeadFlickCaptureSeconds = .090f, HeadFlickSnapGain = 55f;
        public const float FlickDirectionalSpeed = 14f, FlickDirectionalMinMagnitude = .45f;
        public const float FlickTargetAlignment = .62f;
        public const float FlickLandingMinSeconds = .045f, FlickLandingMaxSeconds = .080f;
        public const float FlickRadiusMinScale = .80f, FlickRadiusMaxScale = 1.28f;
        public const float FlickBrakeRatio = .72f;
        public const float HeadSafeInset = .18f, HeadSafePositionScale = .28f;
        public const float HeadSafeMotionBias = .12f, HeadSafeMotionSpeed = 45f;
        public const float HeadHorizontalPositionGain = 1f, HeadVerticalPositionGain = 1.7f;
        public const float HeadHorizontalTrackingGain = 1f, HeadVerticalTrackingGain = 1.2f;
        public const float AcquireCone = 7, ReleaseCone = 9, InnerCone = 2.4f;
        public const float MinimumFriction = .62f;
        public const float HorizontalRotation = .24f, VerticalRotation = .24f, RotationErrorGain = 4f;
        public const float MotionTracking = .30f;
        public const float RotationAssistMultiplier = 4f;
        public const float ChallengerRatio = 1.30f, InputAlignmentWeight = .12f;
        public const float HeadDelay = .120f, IntentionalHeadDelay = .050f;
        public const float MaxHeadBlend = .80f, IntentionalMaxHeadBlend = 1f;
        public const float HeadAcquireCone = 1.5f, HeadReleaseCone = 2.25f;
        public const float HeadPredictionSeconds = 0f, MaxHeadPrediction = .60f;
        public const float OcclusionGrace = .060f;
        public const float TrackingConfidenceMin = .30f, TrackingConfidenceRiseRate = 8f;
        public const float TrackingConfidenceDecayRate = 2.5f, TrackingConfidenceStrafeDecayRate = .7f;
        public const float HeadConfidenceRiseRate = 10f, HeadConfidenceDecayRate = 4f;
        public const float StrafeTrackingMinimum = .18f, StrafeTrackingMaximum = .35f;
        public const float OccludedMotionDecayRate = 8f;
        public const float ShotCommitSeconds = .050f, ShotCommitFrictionScale = 1.15f;
        public const float TrajectoryHorizon = .080f, TrajectoryScoreWeight = .16f;
        public const float PrecisionFilterReleaseRate = 2.5f;
        public const float VelocityFilterRate = 12f, HeadVelocityFilterRate = 16f;
        public const float MotionAccelerationRate = 18f, MaxTrackedAcceleration = 900f;
        public const float MotionServoLookahead = .025f, MotionDirectionRate = 18f;
        public const float MotionPhaseSpeed = 5f, MotionMatchedSpeed = 2f;
        public const float HeadBlendRate = 10f, HeadFallbackRate = 18f;
        public const float IntentStart = .04f, IntentFull = .20f;
        public const float MaxTrackedSpeed = 120f, MotionDiscontinuity = 12f;
    }

    public enum AimAssistWeaponClass { Standard, Tracking, Precision, Projectile, Splash }

    public readonly record struct AimAssistWeaponProfile(float Cone, float ReleaseCone, float Inner,
        float Rotation, float MaxSpeed, bool Head)
    {
        public float FrictionStrength { get; init; } = .38f;
        public float PositionGain { get; init; } = 1f;
        public float TrackingGain { get; init; } = 1.05f;
        public float MaxPositionSpeed { get; init; } = 12;
        public float MaxTrackingSpeed { get; init; } = 30;
        public bool Precision { get; init; }
        public bool Scoped { get; init; }
        public float HeadRange => Precision ? 60 : 15;
        public static AimAssistWeaponProfile For(AimAssistWeaponClass weapon, bool scoped)
        {
            if (scoped)
            {
                return new(3.5f, 4.75f, 1.25f, .5f, 8,
                    weapon is AimAssistWeaponClass.Standard or AimAssistWeaponClass.Precision) { Precision = weapon == AimAssistWeaponClass.Precision, Scoped = true, MaxPositionSpeed = 4, MaxTrackingSpeed = 18 };
            }
            return weapon switch
            {
                AimAssistWeaponClass.Tracking => new(AimAssistTuning.AcquireCone, AimAssistTuning.ReleaseCone,
                    AimAssistTuning.InnerCone, 1, 24, false),
                AimAssistWeaponClass.Precision => new(5, 7, 1.8f, .6f, 12, true) { Precision = true, TrackingGain = 1.1f },
                AimAssistWeaponClass.Splash => new(AimAssistTuning.AcquireCone, AimAssistTuning.ReleaseCone,
                    AimAssistTuning.InnerCone, .45f, 12, false),
                AimAssistWeaponClass.Projectile => new(AimAssistTuning.AcquireCone, AimAssistTuning.ReleaseCone,
                    AimAssistTuning.InnerCone, .6f, 16, false),
                _ => new(AimAssistTuning.AcquireCone, AimAssistTuning.ReleaseCone,
                    AimAssistTuning.InnerCone, .8f, 20, true)
            };
        }
    }
}
