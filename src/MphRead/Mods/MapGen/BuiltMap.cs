using System.Collections.Generic;
using MphRead.Editor;
using OpenTK.Mathematics;

namespace MphRead.Mods.MapGen
{
    /// <summary>
    /// What the packer actually consumes: polygons, collision faces and
    /// entities, with no memory of where they came from.
    ///
    /// Hand-written maps arrive here from a MapDefinition's boxes; a converted
    /// map arrives from another engine's level. Keeping the packer behind this
    /// means an importer only has to produce geometry, and gets the model
    /// format, the collision grid and the room registration for free.
    /// </summary>
    public class BuiltMap
    {
        public MapDefinition Definition { get; }
        public List<BuiltFace> Faces { get; } = new List<BuiltFace>();
        public List<BuiltFace> Solid { get; } = new List<BuiltFace>();
        public List<EntityEditorBase> Entities { get; } = new List<EntityEditorBase>();

        public BuiltMap(MapDefinition definition)
        {
            Definition = definition;
        }
    }

    /// <summary>
    /// One convex polygon. Texture coordinates are in texels, which is what
    /// the hardware wants; the caller decides whether they come from a
    /// projection or from the source data.
    /// </summary>
    public class BuiltFace
    {
        public Vector3[] Points { get; }
        public Vector2[] Texcoords { get; }
        public Vector3 Normal { get; }
        public int Material { get; }
        public float Shade { get; }
        public bool Damaging { get; set; }
        public Terrain Terrain { get; set; } = Terrain.Metal;

        // The rest of what the collision format holds per face. Nothing in the
        // importers sets these -- a converted level says nothing about them --
        // but a hand-edited OBJ does, and CollisionObj is where they come
        // from. IgnoreBeams is the one that earns its keep: a Quake
        // player-clip brush is a wall that shots are meant to fly through,
        // and without it every clip in a converted level stops bullets.
        /// <summary>
        /// Drawn sky, which is never collision -- so the check that every
        /// drawn surface has something solid behind it has to know to skip it,
        /// or a level with a sky shell reports a hole the size of the sky.
        /// </summary>
        public bool Sky { get; set; }

        public int Slipperiness { get; set; }
        public bool ReflectBeams { get; set; }
        public bool IgnorePlayers { get; set; }
        public bool IgnoreBeams { get; set; }
        public bool IgnoreScan { get; set; }

        public BuiltFace(Vector3[] points, Vector2[] texcoords, Vector3 normal, int material, float shade)
        {
            Points = points;
            Texcoords = texcoords;
            Normal = normal;
            Material = material;
            Shade = shade;
        }
    }
}
