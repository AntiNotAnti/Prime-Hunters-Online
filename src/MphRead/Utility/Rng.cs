namespace MphRead
{
    /// <summary>Foreground compatibility facade. Entity simulation uses Scene.Random.</summary>
    public static class Rng
    {
        public const uint Rng1StartValue = MatchRandom.Rng1StartValue;
        public const uint Rng2StartValue = MatchRandom.Rng2StartValue;
        internal static MatchRandom Current { get; set; } = new();
        public static uint Rng1 => Current.Rng1;
        public static uint Rng2 => Current.Rng2;
        public static uint CallRng(ref uint rng, uint value) => MatchRandom.CallRng(ref rng, value);
        public static uint GetRandomInt1(int value) => Current.GetRandomInt1(value);
        public static uint GetRandomInt2(int value) => Current.GetRandomInt2(value);
        public static uint GetRandomInt1(uint value) => Current.GetRandomInt1(value);
        public static uint GetRandomInt2(uint value) => Current.GetRandomInt2(value);
        public static void SetRng1(uint value) => Current.SetRng1(value);
        public static void SetRng2(uint value) => Current.SetRng2(value);
        public static void DoDamageShake(int damage) => Current.DoDamageShake(damage);
        public static void DoCameraShake(int shake) => Current.DoCameraShake(shake);
    }
}
