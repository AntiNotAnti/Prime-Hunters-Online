using System.Numerics;

namespace MphRead.Mods.Input.AimAssist
{
    public sealed class AimAssistState
    {
        public int TargetSlot = -1;
        public long TargetLife;
        public float RetainedSeconds, HeadBlend, OccludedSeconds;
        public Vector2 PreviousError, PreviousHeadError, PreviousOutput;
        public Vector2 AngularVelocity, HeadAngularVelocity;

        public void Reset()
        {
            TargetSlot = -1;
            TargetLife = 0;
            RetainedSeconds = HeadBlend = OccludedSeconds = 0;
            PreviousError = PreviousHeadError = PreviousOutput = default;
            AngularVelocity = HeadAngularVelocity = default;
        }
    }
}
