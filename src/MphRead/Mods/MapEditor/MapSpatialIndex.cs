using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace MphRead.Mods.MapEditor;

/// <summary>
/// Immutable spatial acceleration for large imported maps. The BSP side of a
/// project does not move during ordinary editing, so building this once turns
/// viewport culling, floor queries and diagnostics from whole-map scans into
/// bounded spatial queries.
/// </summary>
public sealed class MapFaceSpatialIndex
{
    public readonly record struct Bounds3(Vector3 Min, Vector3 Max)
    {
        public Vector3 Center => (Min + Max) * .5f;
        public Vector3 Size => Max - Min;
        public bool Intersects(Bounds3 other)
            => Min.X <= other.Max.X && Max.X >= other.Min.X
            && Min.Y <= other.Max.Y && Max.Y >= other.Min.Y
            && Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;
        public bool Contains(Vector3 p)
            => p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y
            && p.Z >= Min.Z && p.Z <= Max.Z;
        public Vector3[] Corners() => new[]
        {
            new Vector3(Min.X,Min.Y,Min.Z),new Vector3(Max.X,Min.Y,Min.Z),
            new Vector3(Min.X,Max.Y,Min.Z),new Vector3(Max.X,Max.Y,Min.Z),
            new Vector3(Min.X,Min.Y,Max.Z),new Vector3(Max.X,Min.Y,Max.Z),
            new Vector3(Min.X,Max.Y,Max.Z),new Vector3(Max.X,Max.Y,Max.Z)
        };
        public static Bounds3 Merge(Bounds3 a, Bounds3 b)
            => new(Vector3.Min(a.Min,b.Min),Vector3.Max(a.Max,b.Max));
    }

    private sealed class Node
    {
        public Bounds3 Bounds;
        public Node? Left;
        public Node? Right;
        public int[]? Items;
    }

    private readonly MapViewportFace[] _faces;
    private readonly Bounds3[] _bounds;
    private readonly Node? _root;
    public Bounds3 Bounds { get; }

    public MapFaceSpatialIndex(IEnumerable<MapViewportFace> faces)
    {
        _faces=faces.ToArray();
        _bounds=_faces.Select(FaceBounds).ToArray();
        if(_faces.Length==0){Bounds=new(Vector3.Zero,Vector3.Zero);return;}
        int[] ids=Enumerable.Range(0,_faces.Length).ToArray();
        _root=Build(ids,0);
        Bounds=_root.Bounds;
    }

    public IReadOnlyList<MapViewportFace> Query(Bounds3 bounds)
    {
        if(_root==null)return Array.Empty<MapViewportFace>();
        var ids=new List<int>();Query(_root,bounds,ids);
        return ids.Select(i=>_faces[i]).ToArray();
    }

    /// <summary>Faces whose X/Z footprint can be under this point.</summary>
    public IReadOnlyList<MapViewportFace> Column(Vector3 point,float radius=.05f)
    {
        if(_root==null)return Array.Empty<MapViewportFace>();
        var query=new Bounds3(
            new(point.X-radius,Bounds.Min.Y-1,point.Z-radius),
            new(point.X+radius,point.Y+.1f,point.Z+radius));
        return Query(query);
    }

    private void Query(Node node,Bounds3 bounds,List<int> result)
    {
        if(!node.Bounds.Intersects(bounds))return;
        if(node.Items!=null){result.AddRange(node.Items);return;}
        if(node.Left!=null)Query(node.Left,bounds,result);
        if(node.Right!=null)Query(node.Right,bounds,result);
    }

    private Node Build(int[] ids,int depth)
    {
        Bounds3 bounds=_bounds[ids[0]];
        for(int i=1;i<ids.Length;i++)bounds=Bounds3.Merge(bounds,_bounds[ids[i]]);
        var node=new Node{Bounds=bounds};
        if(ids.Length<=24||depth>=24){node.Items=ids;return node;}
        Vector3 size=bounds.Size;
        int axis=size.X>=size.Y&&size.X>=size.Z?0:size.Y>=size.Z?1:2;
        Array.Sort(ids,(a,b)=>Component(_bounds[a].Center,axis).CompareTo(Component(_bounds[b].Center,axis)));
        int half=ids.Length/2;
        node.Left=Build(ids[..half],depth+1);
        node.Right=Build(ids[half..],depth+1);
        return node;
    }

    private static float Component(Vector3 p,int axis)=>axis==0?p.X:axis==1?p.Y:p.Z;
    public static Bounds3 FaceBounds(MapViewportFace face)
    {
        if(face.Points.Length==0)return new(Vector3.Zero,Vector3.Zero);
        Vector3 min=face.Points[0],max=face.Points[0];
        for(int i=1;i<face.Points.Length;i++){min=Vector3.Min(min,face.Points[i]);max=Vector3.Max(max,face.Points[i]);}
        return new(min,max);
    }
}

/// <summary>One spatially coherent imported GPU/editor chunk.</summary>
public sealed record MapViewportChunk(Guid Id,MapFaceSpatialIndex.Bounds3 Bounds,
    IReadOnlyList<MapViewportFace> Faces,IReadOnlyList<MapViewportFace> CollisionFaces)
{
    public MapViewportMesh Mesh { get; }=new(Id,Faces,CollisionFaces);

    public bool Visible(MapViewportCamera camera,MapViewportLayout layout)
    {
        if(!layout.IsValid)return true;
        if(Bounds.Contains(camera.Position))return true;
        var projected=Bounds.Corners().Select(p=>camera.Project(layout,p)).Where(p=>p.HasValue)
            .Select(p=>p!.Value).ToArray();
        if(projected.Length==0)return false;
        double minX=projected.Min(p=>p.X),maxX=projected.Max(p=>p.X);
        double minY=projected.Min(p=>p.Y),maxY=projected.Max(p=>p.Y);
        const double pad=80;
        return maxX>=-pad&&minX<=layout.Width+pad&&maxY>=-pad&&minY<=layout.Height+pad;
    }
}

public static class MapViewportChunker
{
    public const float DefaultCellSize=32f;

    public static IReadOnlyList<MapViewportChunk> Create(
        IReadOnlyList<MapViewportFace> render,IReadOnlyList<MapViewportFace> collision,
        float cellSize=DefaultCellSize)
    {
        var cells=new Dictionary<(int X,int Y,int Z),(List<MapViewportFace> Render,List<MapViewportFace> Collision)>();
        static Vector3 Center(MapViewportFace face)
        {
            if(face.Points.Length==0)return Vector3.Zero;
            Vector3 total=Vector3.Zero;foreach(var p in face.Points)total+=p;return total/face.Points.Length;
        }
        (int,int,int) Key(MapViewportFace face)
        {
            Vector3 c=Center(face);
            return((int)MathF.Floor(c.X/cellSize),(int)MathF.Floor(c.Y/cellSize),(int)MathF.Floor(c.Z/cellSize));
        }
        foreach(var face in render)
        {
            var key=Key(face);
            if(!cells.TryGetValue(key,out var lists))lists=(new(),new());
            lists.Render.Add(face);cells[key]=lists;
        }
        foreach(var face in collision)
        {
            var key=Key(face);
            if(!cells.TryGetValue(key,out var lists))lists=(new(),new());
            lists.Collision.Add(face);cells[key]=lists;
        }
        return cells.OrderBy(p=>p.Key.X).ThenBy(p=>p.Key.Y).ThenBy(p=>p.Key.Z).Select(pair=>
        {
            var all=pair.Value.Render.Concat(pair.Value.Collision).ToArray();
            var bounds=all.Length==0?new MapFaceSpatialIndex.Bounds3(Vector3.Zero,Vector3.Zero)
                :all.Select(MapFaceSpatialIndex.FaceBounds).Aggregate(MapFaceSpatialIndex.Bounds3.Merge);
            return new MapViewportChunk(Id(pair.Key.X,pair.Key.Y,pair.Key.Z),bounds,
                Array.AsReadOnly(pair.Value.Render.ToArray()),Array.AsReadOnly(pair.Value.Collision.ToArray()));
        }).ToArray();
    }

    private static Guid Id(int x,int y,int z)
    {
        Span<byte> bytes=stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes[0..4],x);
        BitConverter.TryWriteBytes(bytes[4..8],y);
        BitConverter.TryWriteBytes(bytes[8..12],z);
        BitConverter.TryWriteBytes(bytes[12..16],unchecked((int)0x50504348)); // PPCH
        return new Guid(bytes);
    }
}

/// <summary>Aggregated collision cost used by the Map Studio heat overlay.</summary>
public sealed record MapCollisionHeatCell(MapFaceSpatialIndex.Bounds3 Bounds,int Faces,long ReferenceCost)
{
    public double Severity=>Math.Clamp(ReferenceCost/4096d,0,1);
}

public static class MapCollisionHeatmap
{
    public static IReadOnlyList<MapCollisionHeatCell> Build(IReadOnlyList<MapViewportFace> faces,float cellSize=16f)
    {
        var groups=new Dictionary<(int,int,int),(Vector3 Min,Vector3 Max,int Faces,long Cost)>();
        foreach(var face in faces)
        {
            var b=MapFaceSpatialIndex.FaceBounds(face);Vector3 c=b.Center;
            var key=((int)MathF.Floor(c.X/cellSize),(int)MathF.Floor(c.Y/cellSize),(int)MathF.Floor(c.Z/cellSize));
            long cost=1;
            for(int axis=0;axis<3;axis++)
            {
                float span=axis==0?b.Size.X:axis==1?b.Size.Y:b.Size.Z;
                cost*=Math.Max(1,(long)MathF.Floor(span/4)+1);
            }
            if(!groups.TryGetValue(key,out var g))g=(b.Min,b.Max,0,0);
            g=(Vector3.Min(g.Min,b.Min),Vector3.Max(g.Max,b.Max),g.Faces+1,g.Cost+cost);
            groups[key]=g;
        }
        return groups.Values.Select(g=>new MapCollisionHeatCell(new(g.Min,g.Max),g.Faces,g.Cost))
            .OrderByDescending(c=>c.ReferenceCost).ToArray();
    }
}
