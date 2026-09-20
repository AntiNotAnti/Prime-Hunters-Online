using System;
using MphRead.Entities;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead
{
    public partial class Scene
    {
        // Audit only. Read before the next OnDrawFrame returns these Points arrays to the pool.
        internal (ulong Signature, int TrailCount) ModLockjawTrailSignature()
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong signature = offset;
            int trailCount = 0;

            foreach (RenderItem item in _usedRenderItems)
            {
                if (item.Type != RenderItemType.TrailMulti || item.ItemCount != 40)
                {
                    continue;
                }

                bool lockjawTrail = false;
                foreach (BombEntity bomb in GetBombEntities())
                {
                    if (bomb.Active && bomb.BombType == BombType.Lockjaw
                        && bomb.BombIndex > 0 && bomb.ModLockjawTrailBindingId > 0
                        && item.TextureBindingId == bomb.ModLockjawTrailBindingId
                        && item.Transform.M41 == bomb.Position.X
                        && item.Transform.M42 == bomb.Position.Y
                        && item.Transform.M43 == bomb.Position.Z)
                    {
                        lockjawTrail = true;
                        break;
                    }
                }
                if (!lockjawTrail)
                {
                    continue;
                }

                trailCount++;
                // ItemCount is the number of Vector3 entries, not the pool array length.
                signature = (signature ^ (uint)item.ItemCount) * prime;
                signature = (signature ^ (uint)item.TextureBindingId) * prime;
                signature = (signature ^ BitConverter.SingleToUInt32Bits(item.Transform.M41)) * prime;
                signature = (signature ^ BitConverter.SingleToUInt32Bits(item.Transform.M42)) * prime;
                signature = (signature ^ BitConverter.SingleToUInt32Bits(item.Transform.M43)) * prime;
                for (int i = 0; i < item.ItemCount; i++)
                {
                    Vector3 point = item.Points[i];
                    signature = (signature ^ BitConverter.SingleToUInt32Bits(point.X)) * prime;
                    signature = (signature ^ BitConverter.SingleToUInt32Bits(point.Y)) * prime;
                    signature = (signature ^ BitConverter.SingleToUInt32Bits(point.Z)) * prime;
                }
            }
            return (signature, trailCount);
        }
    }
}
