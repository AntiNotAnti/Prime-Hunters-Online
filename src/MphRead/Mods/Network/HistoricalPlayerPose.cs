using OpenTK.Mathematics;
using MphRead.Formats;
using MphRead.Formats.Culling;

namespace MphRead.Mods.Network;

// Value-only collision data. No animation, input or gameplay state is rewound.
public readonly record struct AltCollisionPose(Vector3 Seg1, Vector3 Seg2, Vector3 Seg3)
{
    public static AltCollisionPose Lerp(in AltCollisionPose a, in AltCollisionPose b, float t) =>
        new(Vector3.Lerp(a.Seg1, b.Seg1, t), Vector3.Lerp(a.Seg2, b.Seg2, t), Vector3.Lerp(a.Seg3, b.Seg3, t));
}
public readonly record struct HistoricalPlayerPose(Vector3 Position, bool AltForm, AltCollisionPose AltPose);
internal readonly record struct HistoricalCollisionState(Vector3 Position, Vector3 Previous,
    CollisionVolume Volume, CollisionVolume Untransformed, NodeRef Node, bool? Form, AltCollisionPose Segments);
