using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MphRead.Mods.MapGen
{
    public sealed class MapTransform
    {
        public float[] Position { get; set; } = new float[3];
        public float[] Rotation { get; set; } = new[] { 0f, 0, 0, 1 };
        public float[] Scale { get; set; } = new[] { 1f, 1, 1 };
    }

    public sealed class MapUv
    {
        public float[] Scale { get; set; } = new[] { 1f, 1 };
        public float[] Offset { get; set; } = new float[2];
        public float Rotation { get; set; }
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(MapBox), "box")]
    [JsonDerivedType(typeof(MapWedge), "wedge")]
    [JsonDerivedType(typeof(MapPrism), "prism")]
    [JsonDerivedType(typeof(MapConvexBrush), "convex")]
    public abstract class MapGeometry
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Label { get; set; } = "Geometry";
        public MapTransform Transform { get; set; } = new();
        public int Material { get; set; }
        public bool Solid { get; set; } = true;
        public bool Damaging { get; set; }
        public string Terrain { get; set; } = "Metal";
        public float Shade { get; set; } = 1;
        public MapUv Uv { get; set; } = new();
        public Dictionary<int, MapUv> FaceUv { get; set; } = new();
        public bool Hidden { get; set; }
        public bool Locked { get; set; }
        public string Layer { get; set; } = "Architecture";
    }
    public sealed class MapBox : MapGeometry { }
    public sealed class MapWedge : MapGeometry { }
    public sealed class MapPrism : MapGeometry { public int Sides { get; set; } = 6; }
    public sealed class MapConvexBrush : MapGeometry
    {
        public List<float[]> Vertices { get; set; } = new();
        public List<int[]> Faces { get; set; } = new();
    }
}
