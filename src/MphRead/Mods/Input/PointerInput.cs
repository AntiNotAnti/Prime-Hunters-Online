namespace MphRead.Mods.Input
{
    /// <summary>Optional absolute-pointer handling. Ordinary mouse input is unchanged by default.</summary>
    public static class PointerInput
    {
        public static bool StylusMode { get; set; }
        public static bool GuardJumps { get; set; } = true;
        public static float JumpPixels { get; set; } = 600;
        public static int JumpsIgnored { get; private set; }
        public static bool JumpingPointerSeen { get; private set; }

        /// <summary>Reject a whole sample, so a diagonal teleport cannot leave an axis of camera kick.</summary>
        public static (float X, float Y) Filter(float x, float y)
        {
            if (!StylusMode || !GuardJumps || JumpPixels <= 0
                || x * x + y * y < JumpPixels * JumpPixels)
            {
                return (x, y);
            }
            JumpsIgnored++;
            if (!JumpingPointerSeen)
            {
                JumpingPointerSeen = true;
                DebugLog.Line("input", $"pointer jumped ({x:0}, {y:0}) px in a frame; sample ignored");
            }
            return (0, 0);
        }

        public static void Reset()
        {
            JumpsIgnored = 0;
            JumpingPointerSeen = false;
        }
    }
}
