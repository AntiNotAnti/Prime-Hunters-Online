using System.Numerics;
namespace MphRead.Mods.Input.AimAssist
{
    public sealed class AimAssistState
    {
        public int TargetSlot = -1;
        public long TargetLife;
        public float RetainedSeconds, HeadBlend;
        public Vector2 PreviousError, PreviousOutput, AngularVelocity;
        public void Reset() { TargetSlot = -1; TargetLife = 0; RetainedSeconds = HeadBlend = 0; PreviousError = PreviousOutput = AngularVelocity = default; }
    }
}
