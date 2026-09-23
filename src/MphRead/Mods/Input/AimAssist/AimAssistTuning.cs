namespace MphRead.Mods.Input.AimAssist
{
    // Deliberately not player preferences. Changes require regression and balance validation.
    public static class AimAssistTuning
    {
        public const float AcquireCone = 7, ReleaseCone = 9, InnerCone = 2.4f;
        public const float MinimumFriction = .62f;
        public const float HorizontalRotation = .24f, VerticalRotation = .15f, RotationErrorGain = 4f;
        public const float RotationAssistMultiplier = 4f;
        public const float ChallengerRatio = 1.30f, InputAlignmentWeight = .12f;
        public const float HeadDelay = .120f, IntentionalHeadDelay = .050f;
        public const float MaxHeadBlend = .80f, IntentionalMaxHeadBlend = 1f;
        public const float HeadAcquireCone = 1.5f, HeadReleaseCone = 2.25f;
        public const float HeadPredictionSeconds = .050f, MaxHeadPrediction = .60f;
        public const float OcclusionGrace = .060f;
        public const float VelocityFilterRate = 12f, HeadVelocityFilterRate = 16f;
        public const float HeadBlendRate = 10f, HeadFallbackRate = 18f;
    }

    public enum AimAssistWeaponClass { Standard, Tracking, Precision, Projectile, Splash }

    public readonly record struct AimAssistWeaponProfile(float Cone, float ReleaseCone, float Inner,
        float Rotation, float MaxSpeed, bool Head)
    {
        public static AimAssistWeaponProfile For(AimAssistWeaponClass weapon, bool scoped)
        {
            if (scoped)
            {
                return new(3.5f, 4.75f, 1.25f, .5f, 8,
                    weapon is AimAssistWeaponClass.Standard or AimAssistWeaponClass.Precision);
            }
            return weapon switch
            {
                AimAssistWeaponClass.Tracking => new(AimAssistTuning.AcquireCone, AimAssistTuning.ReleaseCone,
                    AimAssistTuning.InnerCone, 1, 24, false),
                AimAssistWeaponClass.Precision => new(5, 7, 1.8f, .6f, 12, true),
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
