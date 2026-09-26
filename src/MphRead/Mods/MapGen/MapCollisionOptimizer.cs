using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Utility;
using OpenTK.Mathematics;

namespace MphRead.Mods.MapGen;

/// <summary>
/// Lossless collision compaction for the MPH 16-bit collision format.
/// Removes redundant polygon vertices first, then greedily merges adjacent
/// coplanar convex polygons that carry identical collision behavior.
/// </summary>
public static class MapCollisionOptimizer
{
    public sealed record Result(IReadOnlyList<CollisionDataEditor> Editors,
        int OriginalFaces,int OriginalPointIndices,int OptimizedFaces,int OptimizedPointIndices,
        int RemovedVertices,int MergedFaces);

    public readonly record struct Metrics(long Faces,long Points,long PointIndices,long GridCells,long References);

    private readonly record struct PointKey(int X,int Y,int Z);
    private readonly record struct PlaneKey(int X,int Y,int Z,int W,ushort LayerMask,ushort Flags);
    private readonly record struct EdgeKey(PointKey A,PointKey B)
    {
        public static EdgeKey Create(PointKey a,PointKey b)
            => Compare(a,b)<=0?new(a,b):new(b,a);
        private static int Compare(PointKey a,PointKey b)
        {
            int x=a.X.CompareTo(b.X);if(x!=0)return x;
            int y=a.Y.CompareTo(b.Y);return y!=0?y:a.Z.CompareTo(b.Z);
        }
    }

    public static List<CollisionDataEditor> FromFaces(IEnumerable<BuiltFace> faces)
    {
        var editors=new List<CollisionDataEditor>();
        foreach(BuiltFace face in faces)
        {
            foreach(BuiltFace part in MapPacker.CollisionParts(face))
            {
                var editor=new CollisionDataEditor
                {
                    LayerMask=(ushort)(4|MapPacker.GetPrimaryAxis(part.Normal)),
                    Plane=new Vector4(part.Normal,Vector3.Dot(part.Normal,part.Points[0])),
                    Damaging=face.Damaging,
                    Terrain=face.Terrain,
                    Slipperiness=face.Slipperiness,
                    Reflect=face.ReflectBeams,
                    Players=!face.IgnorePlayers,
                    Beams=!face.IgnoreBeams,
                    Scan=!face.IgnoreScan
                };
                editor.Points.AddRange(part.Points);
                editors.Add(editor);
            }
        }
        return editors;
    }

    public static Result Optimize(IReadOnlyList<CollisionDataEditor> input,int maxPointIndices=ushort.MaxValue-1)
    {
        int originalFaces=input.Count;
        int originalIndices=input.Sum(e=>e.Points.Count);
        int removedVertices=0;
        var groups=new Dictionary<PlaneKey,List<CollisionDataEditor>>();

        foreach(CollisionDataEditor source in input)
        {
            CollisionDataEditor editor=Clone(source);
            int before=editor.Points.Count;
            Simplify(editor.Points);
            removedVertices+=before-editor.Points.Count;
            if(editor.Points.Count<3)continue;
            PlaneKey key=Key(editor);
            if(!groups.TryGetValue(key,out var list))groups.Add(key,list=new());
            list.Add(editor);
        }

        int current=groups.Values.Sum(g=>g.Sum(e=>e.Points.Count));
        int mergedFaces=0;
        if(current>maxPointIndices)
        {
            bool changed=true;
            while(current>maxPointIndices&&changed)
            {
                changed=false;
                foreach(PlaneKey key in groups.Keys.ToArray())
                {
                    List<CollisionDataEditor> group=groups[key];
                    if(group.Count<2)continue;
                    var edgeMap=new Dictionary<EdgeKey,List<(int Face,int Edge)>>();
                    for(int faceIndex=0;faceIndex<group.Count;faceIndex++)
                    {
                        var points=group[faceIndex].Points;
                        for(int edge=0;edge<points.Count;edge++)
                        {
                            EdgeKey edgeKey=EdgeKey.Create(PKey(points[edge]),PKey(points[(edge+1)%points.Count]));
                            if(!edgeMap.TryGetValue(edgeKey,out var refs))edgeMap.Add(edgeKey,refs=new());
                            refs.Add((faceIndex,edge));
                        }
                    }

                    var consumed=new bool[group.Count];
                    var next=new List<CollisionDataEditor>(group.Count);
                    for(int i=0;i<group.Count;i++)
                    {
                        if(consumed[i])continue;
                        CollisionDataEditor first=group[i];
                        CollisionDataEditor? merged=null;
                        int partner=-1;
                        for(int edge=0;edge<first.Points.Count&&merged==null;edge++)
                        {
                            EdgeKey edgeKey=EdgeKey.Create(PKey(first.Points[edge]),PKey(first.Points[(edge+1)%first.Points.Count]));
                            if(!edgeMap.TryGetValue(edgeKey,out var refs))continue;
                            foreach(var candidate in refs)
                            {
                                if(candidate.Face==i||consumed[candidate.Face])continue;
                                merged=TryMerge(first,edge,group[candidate.Face],candidate.Edge);
                                if(merged!=null){partner=candidate.Face;break;}
                            }
                        }
                        if(merged!=null)
                        {
                            consumed[i]=true;consumed[partner]=true;next.Add(merged);
                            current-=first.Points.Count+group[partner].Points.Count-merged.Points.Count;
                            mergedFaces++;changed=true;
                        }
                        else
                        {
                            consumed[i]=true;next.Add(first);
                        }
                        if(current<=maxPointIndices)
                        {
                            for(int rest=i+1;rest<group.Count;rest++)
                                if(!consumed[rest]){consumed[rest]=true;next.Add(group[rest]);}
                            break;
                        }
                    }
                    groups[key]=next;
                    if(current<=maxPointIndices)break;
                }
            }
        }

        var output=groups.OrderBy(g=>g.Key.X).ThenBy(g=>g.Key.Y).ThenBy(g=>g.Key.Z).ThenBy(g=>g.Key.W)
            .ThenBy(g=>g.Key.LayerMask).ThenBy(g=>g.Key.Flags)
            .SelectMany(g=>g.Value).ToArray();
        return new(output,originalFaces,originalIndices,output.Length,
            output.Sum(e=>e.Points.Count),removedVertices,mergedFaces);
    }

    public static Metrics Measure(IReadOnlyList<CollisionDataEditor> data)
    {
        if(data.Count==0)return default;
        var unique=new HashSet<Vector3>();
        Vector3 min=new(float.MaxValue),max=new(float.MinValue);
        foreach(var face in data)
            foreach(Vector3 point in face.Points)
            {
                unique.Add(point);min=Vector3.ComponentMin(min,point);max=Vector3.ComponentMax(max,point);
            }
        long cells=1;
        for(int axis=0;axis<3;axis++)
            cells=SaturatingMultiply(cells,(long)Math.Floor((max[axis]-min[axis])/4.0)+1);

        long refs=0;
        foreach(var face in data)
        {
            long count=1;
            for(int axis=0;axis<3;axis++)
            {
                int a=axis;
                double low=face.Points.Min(p=>p[a]),high=face.Points.Max(p=>p[a]);
                count=SaturatingMultiply(count,
                    (long)(Math.Floor((high-min[axis])/4)-Math.Floor((low-min[axis])/4)+1));
            }
            refs=count>long.MaxValue-refs?long.MaxValue:refs+count;
        }
        return new(data.Count,unique.Count,data.Sum(e=>(long)e.Points.Count),cells,refs);
    }

    private static CollisionDataEditor? TryMerge(CollisionDataEditor a,int edgeA,
        CollisionDataEditor b,int edgeB)
    {
        Vector3 a0=a.Points[edgeA],a1=a.Points[(edgeA+1)%a.Points.Count];
        Vector3 b0=b.Points[edgeB],b1=b.Points[(edgeB+1)%b.Points.Count];
        if(PKey(a0)!=PKey(b1)||PKey(a1)!=PKey(b0))return null;

        var points=new List<Vector3>(a.Points.Count+b.Points.Count-2);
        AppendPath(points,a.Points,(edgeA+1)%a.Points.Count,edgeA,includeFirst:true,includeLast:true);
        AppendPath(points,b.Points,(edgeB+1)%b.Points.Count,edgeB,includeFirst:false,includeLast:false);
        Simplify(points);
        if(points.Count is <3 or >10||points.Select(PKey).Distinct().Count()!=points.Count)return null;
        Vector3 normal=a.Plane.Xyz;
        if(!Convex(points,normal))return null;

        var merged=Clone(a);merged.Points.Clear();merged.Points.AddRange(points);
        return merged;
    }

    private static void AppendPath(List<Vector3> output,IReadOnlyList<Vector3> points,
        int start,int end,bool includeFirst,bool includeLast)
    {
        int index=start;bool first=true;
        while(true)
        {
            bool last=index==end;
            if((includeFirst||!first)&&(includeLast||!last))output.Add(points[index]);
            if(last)break;
            index=(index+1)%points.Count;first=false;
        }
    }

    private static bool Convex(IReadOnlyList<Vector3> points,Vector3 planeNormal)
    {
        float sign=0;
        for(int i=0;i<points.Count;i++)
        {
            Vector3 a=points[i],b=points[(i+1)%points.Count],c=points[(i+2)%points.Count];
            float turn=Vector3.Dot(Vector3.Cross(b-a,c-b),planeNormal);
            if(MathF.Abs(turn)<1e-5f)continue;
            float s=MathF.Sign(turn);
            if(sign==0)sign=s;
            else if(s!=sign)return false;
        }
        return sign!=0;
    }

    private static void Simplify(List<Vector3> points)
    {
        if(points.Count<3)return;
        for(int i=points.Count-1;i>=0;i--)
            if(PKey(points[i])==PKey(points[(i+1)%points.Count])&&points.Count>3)
                points.RemoveAt(i);
        bool changed=true;
        while(changed&&points.Count>3)
        {
            changed=false;
            for(int i=0;i<points.Count;i++)
            {
                Vector3 a=points[(i-1+points.Count)%points.Count];
                Vector3 b=points[i];
                Vector3 c=points[(i+1)%points.Count];
                Vector3 ab=b-a,bc=c-b;
                float product=ab.LengthSquared*bc.LengthSquared;
                if(product>0&&Vector3.Cross(ab,bc).LengthSquared<=product*1e-8f
                    &&Vector3.Dot(ab,bc)>=0)
                {
                    points.RemoveAt(i);changed=true;break;
                }
            }
        }
    }

    private static CollisionDataEditor Clone(CollisionDataEditor source)
    {
        var copy=new CollisionDataEditor{Plane=source.Plane,LayerMask=source.LayerMask,Flags=source.Flags};
        copy.Points.AddRange(source.Points);return copy;
    }

    private static PlaneKey Key(CollisionDataEditor editor)
        =>new(Q(editor.Plane.X),Q(editor.Plane.Y),Q(editor.Plane.Z),Q(editor.Plane.W),
            editor.LayerMask,(ushort)editor.Flags);
    private static PointKey PKey(Vector3 point)=>new(Q(point.X),Q(point.Y),Q(point.Z));
    private static int Q(float value)=>(int)MathF.Round(value*10000f);
    private static long SaturatingMultiply(long a,long b)
        =>a<=0||b<=0||a>long.MaxValue/b?long.MaxValue:a*b;
}
