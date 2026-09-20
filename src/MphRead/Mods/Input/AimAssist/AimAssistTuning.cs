namespace MphRead.Mods.Input.AimAssist
{
    // Deliberately not player preferences. Changes require regression and balance validation.
    public static class AimAssistTuning
    {
        public const float AcquireCone = 7, ReleaseCone = 9, InnerCone = 2.4f;
        public const float MinimumFriction = .62f, HorizontalRotation = .24f, VerticalRotation = .15f;
        public const float ChallengerRatio = 1.30f, HeadDelay = .120f, MaxHeadBlend = .80f;
    }
    public enum AimAssistWeaponClass { Standard, Tracking, Precision, Projectile, Splash }
    public readonly record struct AimAssistWeaponProfile(float Cone, float ReleaseCone, float Inner,
        float Rotation, float MaxSpeed, bool Head)
    {
        public static AimAssistWeaponProfile For(AimAssistWeaponClass weapon, bool scoped)
        {
            if (scoped) return new(3.5f, 4.75f, 1.25f, .5f, 8, weapon is AimAssistWeaponClass.Standard or AimAssistWeaponClass.Precision);
            return weapon switch {
                AimAssistWeaponClass.Tracking => new(7, 9, 2.4f, 1, 24, false),
                AimAssistWeaponClass.Precision => new(5, 7, 1.8f, .6f, 12, true),
                AimAssistWeaponClass.Splash => new(7, 9, 2.4f, .45f, 12, false),
                AimAssistWeaponClass.Projectile => new(7, 9, 2.4f, .6f, 16, false),
                _ => new(7, 9, 2.4f, .8f, 20, true) };
        }
    }
}
