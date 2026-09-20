using System.Numerics;
namespace MphRead.Mods.Input.AimAssist
{
    public enum AimAssistPointType { CenterMass, UpperChest, Head }
    public readonly record struct AimAssistTarget(int Slot, long Life, Vector2 BodyError, Vector2 HeadError,
        float Distance, bool BodyVisible, bool HeadVisible, bool Eligible = true,
        AimAssistPointType BodyPointType = AimAssistPointType.UpperChest);
    public readonly record struct AimAssistResult(float X, float Y, int TargetSlot = -1, float Friction = 1,
        float RotationStrength = 0, AimAssistPointType PointType = AimAssistPointType.UpperChest, float HeadBlend = 0, float Score = 0);
}
