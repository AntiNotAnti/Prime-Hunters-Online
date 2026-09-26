using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Formats.Collision;
using OpenTK.Mathematics;

namespace MphRead.Mods.MapGen;

public sealed record MapRuntimePartition(int Index,int X,int Z,string RoomNodeName,
    Vector3 Min,Vector3 Max,IReadOnlyList<BuiltFace> Faces);

public sealed record MapRuntimePartitionPlan(IReadOnlyList<MapRuntimePartition> Parts,
    IReadOnlyList<Portal> Portals,bool PortalCullingApplied,float CellSize)
{
    public int FaceCount=>Parts.Sum(p=>p.Faces.Count);
}

/// <summary>
/// Deterministic X/Z partition plan shared by the model packer, portal builder,
/// budget diagnostics and Map Studio. Vertical geometry stays in the same
/// room part so stairs/shafts do not create ambiguous stacked portal volumes.
/// </summary>
public static class MapRuntimePartitioner
{
    public static MapPartitionSettings Effective(MapPartitionSettings? settings)
        => settings??new MapPartitionSettings();

    public static MapRuntimePartitionPlan Create(IReadOnlyList<BuiltFace> faces,
        MapPartitionSettings? configured)
    {
        MapPartitionSettings settings=Effective(configured);
        if(faces.Count==0)return new(Array.Empty<MapRuntimePartition>(),
            Array.Empty<Portal>(),false,settings.CellSize);

        float cell=Math.Clamp(settings.CellSize,8,512);
        bool split=settings.Enabled&&faces.Count>=Math.Clamp(settings.FaceThreshold,256,1_000_000);
        var groups=faces.GroupBy(face=>split?Cell(face,cell):(0,0))
            .OrderBy(g=>g.Key.Item1).ThenBy(g=>g.Key.Item2).ToArray();
        var parts=new List<MapRuntimePartition>(groups.Length);
        for(int i=0;i<groups.Length;i++)
        {
            BuiltFace[] group=groups[i].ToArray();
            Bounds(group,out Vector3 min,out Vector3 max);
            parts.Add(new(i,groups[i].Key.Item1,groups[i].Key.Item2,$"rmP{i:D4}",min,max,group));
        }

        if(!settings.PortalCulling||parts.Count<2)
            return new(parts.AsReadOnly(),Array.Empty<Portal>(),false,cell);

        var byCell=parts.ToDictionary(p=>(p.X,p.Z));
        var links=new List<(MapRuntimePartition A,MapRuntimePartition B,Vector3 Normal)>();
        foreach(MapRuntimePartition part in parts)
        {
            if(byCell.TryGetValue((part.X+1,part.Z),out var east))
                links.Add((part,east,Vector3.UnitX));
            if(byCell.TryGetValue((part.X,part.Z+1),out var north))
                links.Add((part,north,Vector3.UnitZ));
        }
        if(!Connected(parts,links))
            return new(parts.AsReadOnly(),Array.Empty<Portal>(),false,cell);

        var portals=new List<Portal>(links.Count);
        foreach(var link in links)
            portals.Add(MakePortal(link.A,link.B,link.Normal,cell,
                Math.Clamp(settings.PortalVerticalMargin,0,64)));
        return new(parts.AsReadOnly(),portals.AsReadOnly(),true,cell);
    }

    private static (int,int) Cell(BuiltFace face,float cell)
    {
        Vector3 center=Vector3.Zero;
        foreach(Vector3 point in face.Points)center+=point;
        if(face.Points.Length>0)center/=face.Points.Length;
        return((int)MathF.Floor(center.X/cell),(int)MathF.Floor(center.Z/cell));
    }

    private static void Bounds(IEnumerable<BuiltFace> faces,out Vector3 min,out Vector3 max)
    {
        min=new(float.MaxValue);max=new(float.MinValue);
        foreach(BuiltFace face in faces)
            foreach(Vector3 p in face.Points)
            {min=Vector3.ComponentMin(min,p);max=Vector3.ComponentMax(max,p);}
    }

    private static bool Connected(IReadOnlyList<MapRuntimePartition> parts,
        IReadOnlyList<(MapRuntimePartition A,MapRuntimePartition B,Vector3 Normal)> links)
    {
        var adjacent=parts.ToDictionary(p=>p.Index,_=>new List<int>());
        foreach(var link in links){adjacent[link.A.Index].Add(link.B.Index);adjacent[link.B.Index].Add(link.A.Index);}
        var seen=new HashSet<int>();var queue=new Queue<int>();queue.Enqueue(parts[0].Index);seen.Add(parts[0].Index);
        while(queue.TryDequeue(out int current))
            foreach(int next in adjacent[current])if(seen.Add(next))queue.Enqueue(next);
        return seen.Count==parts.Count;
    }

    private static Portal MakePortal(MapRuntimePartition a,MapRuntimePartition b,
        Vector3 normal,float cell,float verticalMargin)
    {
        float minY=MathF.Min(a.Min.Y,b.Min.Y)-verticalMargin;
        float maxY=MathF.Max(a.Max.Y,b.Max.Y)+verticalMargin;
        if(maxY-minY<4)maxY=minY+4;
        Vector3 center;
        float halfWidth=cell*.5f;
        if(MathF.Abs(normal.X)>.5f)
            center=new((a.X+1)*cell,(minY+maxY)/2,(a.Z+.5f)*cell);
        else
            center=new((a.X+.5f)*cell,(minY+maxY)/2,(a.Z+1)*cell);
        Vector3 up=Vector3.UnitY*(maxY-minY)/2;
        Vector3 right=Vector3.Cross(Vector3.UnitY,normal).Normalized()*halfWidth;
        var points=new[]{center-right-up,center+right-up,center+right+up,center-right+up};
        var planes=new Vector4[4];
        for(int i=0;i<4;i++)
        {
            Vector3 p1=points[i==0?3:i-1],p2=points[i];
            Vector3 edge=(p1-p2).Normalized();
            Vector3 edgeNormal=Vector3.Cross(edge,normal).Normalized();
            planes[i]=new(edgeNormal,Vector3.Dot(edgeNormal,p1));
        }
        Vector4 plane=new(normal,Vector3.Dot(normal,center));
        return new Portal(a.RoomNodeName,b.RoomNodeName,points,planes,plane);
    }
}
