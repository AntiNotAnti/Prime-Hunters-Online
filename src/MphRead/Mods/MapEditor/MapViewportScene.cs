using System;
using System.Collections.Generic;
using System.Numerics;
using MphRead.Mods.MapGen;

namespace MphRead.Mods.MapEditor
{
    public interface IMapViewport
    {
        Vector3 CameraPosition { get; }
        Vector3 CameraTarget { get; }
        void FrameAll();
        void FrameSelection();
    }
    public sealed record MapViewportFace(Guid ObjectId, Vector3[] Points, float Shade, int Material, bool Solid);
    public sealed class MapViewportScene
    {
        public List<MapViewportFace> Faces { get; } = new();
        public static Vector3 Vector(float[] p) => new(p[0],p[1],p[2]);
        public static MapViewportScene Create(MapDefinition definition)
        {
            var scene = new MapViewportScene();
            void Add(Guid id, IEnumerable<BuiltFace> faces, bool solid)
            {
                foreach(var face in faces)
                {
                    var points=new Vector3[face.Points.Length];
                    for(int i=0;i<points.Length;i++) points[i]=new(face.Points[i].X,face.Points[i].Y,face.Points[i].Z);
                    scene.Faces.Add(new(id,points,face.Shade,face.Material,solid));
                }
            }
            foreach(var b in definition.Brushes)
            {
                var d=new MapDefinition { Materials=definition.Materials, Brushes=new(){b}, Spawns=new(){new()} };
                Add(b.Id,MapBuilder.Build(d).Faces,b.Solid);
            }
            foreach(var g in definition.Geometry)
            {
                if(g.Hidden) continue;
                try { Add(g.Id,GeometryCompiler.Compile(g,g.Material>=0&&g.Material<definition.Materials.Count?definition.Materials[g.Material].TexScale:16),g.Solid); }
                catch(MapAuthoringException) { /* Invalid objects remain selectable in the hierarchy. */ }
            }
            return scene;
        }
    }
}
