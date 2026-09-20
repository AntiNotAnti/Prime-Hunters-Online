using System.Collections.Generic;
using MphRead.Editor;
using MphRead.Formats.Collision;

namespace MphRead.Utility
{
    // The packers for entities and collision are private, because until now
    // the only caller was the round-trip test sitting next to them. Reaching
    // them through the partial class rather than editing their files keeps
    // every upstream pull a fast-forward: the only change in upstream code is
    // the word "partial" on RepackCollision's declaration.
    public static partial class Repack
    {
        public static byte[] PackEntities(IReadOnlyList<EntityEditorBase> entities)
        {
            return RepackEntities(entities);
        }
    }

    public static partial class RepackCollision
    {
        public static byte[] PackMphCollision(IReadOnlyList<CollisionDataEditor> data, IReadOnlyList<Portal> portals)
        {
            return RepackMphCollision(data, portals);
        }
    }
}
