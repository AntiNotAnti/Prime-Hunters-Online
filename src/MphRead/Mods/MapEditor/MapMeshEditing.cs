using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using MphRead.Mods.MapGen;
using NVector3 = System.Numerics.Vector3;

namespace MphRead.Mods.MapEditor;

public static class MapMeshEditing
{
    public static MapMesh Convert(MapGeometry geometry,float texScale)
    {
        var faces=GeometryCompiler.Compile(geometry,texScale);
        var vertices=new List<float[]>();
        var map=new Dictionary<(int,int,int),int>();
        int Vertex(OpenTK.Mathematics.Vector3 point)
        {
            const float precision=10000;
            var key=((int)MathF.Round(point.X*precision),(int)MathF.Round(point.Y*precision),(int)MathF.Round(point.Z*precision));
            if(map.TryGetValue(key,out int index))return index;
            index=vertices.Count;vertices.Add(new[]{point.X,point.Y,point.Z});map.Add(key,index);return index;
        }
        var mesh=new MapMesh
        {
            Id=geometry.Id,Label=geometry.Label+" Mesh",Material=geometry.Material,Solid=geometry.Solid,
            Damaging=geometry.Damaging,Terrain=geometry.Terrain,Shade=geometry.Shade,Layer=geometry.Layer,
            Hidden=geometry.Hidden,Locked=geometry.Locked,Transform=new(),Uv=geometry.Uv
        };
        foreach(var face in faces)
        {
            mesh.Faces.Add(face.Points.Select(Vertex).ToArray());
            mesh.FaceMaterials.Add(face.Material);
            mesh.FaceTexcoords.Add(face.Texcoords.Select(uv=>new[]{uv.X,uv.Y}).ToArray());
        }
        mesh.Vertices=vertices;
        return mesh;
    }

    public static void MoveVertex(MapMesh mesh,int vertex,NVector3 delta)
    {
        RequireVertex(mesh,vertex);
        if(!Finite(delta))throw new ArgumentOutOfRangeException(nameof(delta));
        float[] value=mesh.Vertices[vertex];
        value[0]+=delta.X;value[1]+=delta.Y;value[2]+=delta.Z;
    }

    public static void FlipFace(MapMesh mesh,int face)
    {
        RequireFace(mesh,face);EnsureChannels(mesh);
        Array.Reverse(mesh.Faces[face]);
        if(mesh.FaceTexcoords[face] is {} uv)Array.Reverse(uv);
    }

    public static void DeleteFace(MapMesh mesh,int face)
    {
        RequireFace(mesh,face);EnsureChannels(mesh);
        mesh.Faces.RemoveAt(face);
        mesh.FaceMaterials.RemoveAt(face);
        mesh.FaceTexcoords.RemoveAt(face);
        Compact(mesh);
    }

    public static int SubdivideFace(MapMesh mesh,int face)
    {
        RequireFace(mesh,face);EnsureChannels(mesh);
        int[] source=(int[])mesh.Faces[face].Clone();
        int material=Material(mesh,face);
        float[][]? sourceUv=CloneUv(mesh.FaceTexcoords[face]);
        NVector3 center=source.Select(i=>V(mesh.Vertices[i])).Aggregate(NVector3.Zero,(a,b)=>a+b)/source.Length;
        int centerId=mesh.Vertices.Count;mesh.Vertices.Add(A(center));
        float[]? centerUv=sourceUv==null?null:new[]{
            sourceUv.Average(v=>v[0]),sourceUv.Average(v=>v[1])};
        mesh.Faces.RemoveAt(face);mesh.FaceMaterials.RemoveAt(face);mesh.FaceTexcoords.RemoveAt(face);
        int first=mesh.Faces.Count;
        for(int i=0;i<source.Length;i++)
        {
            int n=(i+1)%source.Length;
            mesh.Faces.Add(new[]{source[i],source[n],centerId});
            mesh.FaceMaterials.Add(material);
            mesh.FaceTexcoords.Add(sourceUv==null?null:new[]{
                (float[])sourceUv[i].Clone(),(float[])sourceUv[n].Clone(),(float[])centerUv!.Clone()});
        }
        return first;
    }

    public static int ExtrudeFace(MapMesh mesh,int face,float distance)
    {
        RequireFace(mesh,face);EnsureChannels(mesh);
        if(!float.IsFinite(distance)||MathF.Abs(distance)<.0001f)throw new ArgumentOutOfRangeException(nameof(distance));
        int[] source=(int[])mesh.Faces[face].Clone();
        int material=Material(mesh,face);
        float[][]? sourceUv=CloneUv(mesh.FaceTexcoords[face]);
        NVector3 normal=Normal(mesh,source);
        int[] lifted=new int[source.Length];
        for(int i=0;i<source.Length;i++)
        {
            NVector3 point=V(mesh.Vertices[source[i]])+normal*distance;
            lifted[i]=mesh.Vertices.Count;mesh.Vertices.Add(A(point));
        }
        mesh.Faces[face]=lifted;SetMaterial(mesh,face,material);mesh.FaceTexcoords[face]=sourceUv;
        for(int i=0;i<source.Length;i++)
        {
            int n=(i+1)%source.Length;
            mesh.Faces.Add(new[]{source[i],source[n],lifted[n],lifted[i]});
            mesh.FaceMaterials.Add(material);
            mesh.FaceTexcoords.Add(null);
        }
        return face;
    }

    public static int InsetFace(MapMesh mesh,int face,float ratio)
    {
        RequireFace(mesh,face);EnsureChannels(mesh);
        if(!float.IsFinite(ratio)||ratio<=0||ratio>=.95f)throw new ArgumentOutOfRangeException(nameof(ratio));
        int[] source=(int[])mesh.Faces[face].Clone();
        int material=Material(mesh,face);
        float[][]? sourceUv=CloneUv(mesh.FaceTexcoords[face]);
        NVector3 center=source.Select(i=>V(mesh.Vertices[i])).Aggregate(NVector3.Zero,(a,b)=>a+b)/source.Length;
        float[]? centerUv=sourceUv==null?null:new[]{
            sourceUv.Average(v=>v[0]),sourceUv.Average(v=>v[1])};
        int[] inner=new int[source.Length];
        float[][]? innerUv=sourceUv==null?null:new float[source.Length][];
        for(int i=0;i<source.Length;i++)
        {
            NVector3 point=NVector3.Lerp(V(mesh.Vertices[source[i]]),center,ratio);
            inner[i]=mesh.Vertices.Count;mesh.Vertices.Add(A(point));
            if(innerUv!=null)innerUv[i]=new[]{
                sourceUv![i][0]+(centerUv![0]-sourceUv[i][0])*ratio,
                sourceUv[i][1]+(centerUv[1]-sourceUv[i][1])*ratio};
        }
        mesh.Faces[face]=inner;SetMaterial(mesh,face,material);mesh.FaceTexcoords[face]=innerUv;
        for(int i=0;i<source.Length;i++)
        {
            int n=(i+1)%source.Length;
            mesh.Faces.Add(new[]{source[i],source[n],inner[n],inner[i]});
            mesh.FaceMaterials.Add(material);
            mesh.FaceTexcoords.Add(sourceUv==null?null:new[]{
                (float[])sourceUv[i].Clone(),(float[])sourceUv[n].Clone(),
                (float[])innerUv![n].Clone(),(float[])innerUv[i].Clone()});
        }
        return face;
    }

    public static int BevelFace(MapMesh mesh,int face,float inset,float distance)
    {
        int inner=InsetFace(mesh,face,inset);
        return ExtrudeFace(mesh,inner,distance);
    }

    public static int Weld(MapMesh mesh,float tolerance=.001f)
    {
        if(!float.IsFinite(tolerance)||tolerance<=0)throw new ArgumentOutOfRangeException(nameof(tolerance));
        var replacement=new int[mesh.Vertices.Count];
        var kept=new List<float[]>();
        float squared=tolerance*tolerance;
        for(int i=0;i<mesh.Vertices.Count;i++)
        {
            NVector3 point=V(mesh.Vertices[i]);int match=-1;
            for(int j=0;j<kept.Count;j++)
                if(NVector3.DistanceSquared(point,V(kept[j]))<=squared){match=j;break;}
            if(match<0){match=kept.Count;kept.Add((float[])mesh.Vertices[i].Clone());}
            replacement[i]=match;
        }
        foreach(int[] face in mesh.Faces)
            for(int i=0;i<face.Length;i++)face[i]=replacement[face[i]];
        int removed=mesh.Vertices.Count-kept.Count;mesh.Vertices=kept;
        EnsureChannels(mesh);
        var faces=new List<int[]>();var materials=new List<int>();var texcoords=new List<float[][]?>();
        for(int i=0;i<mesh.Faces.Count;i++)
        {
            int[] face=mesh.Faces[i];
            if(face.Distinct().Count()<3)continue;
            faces.Add(face);
            materials.Add(mesh.FaceMaterials[i]);
            texcoords.Add(CloneUv(mesh.FaceTexcoords[i]));
        }
        mesh.Faces=faces;mesh.FaceMaterials=materials;mesh.FaceTexcoords=texcoords;
        return removed;
    }

    public static void Compact(MapMesh mesh)
    {
        var used=mesh.Faces.SelectMany(f=>f).Distinct().Order().ToArray();
        var remap=used.Select((old,index)=>(old,index)).ToDictionary(x=>x.old,x=>x.index);
        mesh.Vertices=used.Select(i=>mesh.Vertices[i]).ToList();
        foreach(int[] face in mesh.Faces)
            for(int i=0;i<face.Length;i++)face[i]=remap[face[i]];
    }

    private static void EnsureChannels(MapMesh mesh)
    {
        while(mesh.FaceMaterials.Count<mesh.Faces.Count)mesh.FaceMaterials.Add(mesh.Material);
        while(mesh.FaceTexcoords.Count<mesh.Faces.Count)mesh.FaceTexcoords.Add(null);
        while(mesh.FaceMaterials.Count>mesh.Faces.Count)mesh.FaceMaterials.RemoveAt(mesh.FaceMaterials.Count-1);
        while(mesh.FaceTexcoords.Count>mesh.Faces.Count)mesh.FaceTexcoords.RemoveAt(mesh.FaceTexcoords.Count-1);
    }
    private static float[][]? CloneUv(float[][]? uv)
        =>uv?.Select(value=>(float[])value.Clone()).ToArray();

    private static int Material(MapMesh mesh,int face)
        =>face<mesh.FaceMaterials.Count?mesh.FaceMaterials[face]:mesh.Material;
    private static void SetMaterial(MapMesh mesh,int face,int material)
    {
        while(mesh.FaceMaterials.Count<=face)mesh.FaceMaterials.Add(mesh.Material);
        mesh.FaceMaterials[face]=material;
    }
    private static NVector3 Normal(MapMesh mesh,int[] face)
    {
        NVector3 a=V(mesh.Vertices[face[0]]),b=V(mesh.Vertices[face[1]]),c=V(mesh.Vertices[face[2]]);
        NVector3 n=NVector3.Cross(b-a,c-a);
        if(n.LengthSquared()<1e-10f)throw new InvalidOperationException("Face is degenerate.");
        return NVector3.Normalize(n);
    }
    private static NVector3 V(float[] value)=>new(value[0],value[1],value[2]);
    private static float[] A(NVector3 value)=>new[]{value.X,value.Y,value.Z};
    private static bool Finite(NVector3 value)=>float.IsFinite(value.X)&&float.IsFinite(value.Y)&&float.IsFinite(value.Z);
    private static void RequireVertex(MapMesh mesh,int index)
    {if(index<0||index>=mesh.Vertices.Count)throw new ArgumentOutOfRangeException(nameof(index));}
    private static void RequireFace(MapMesh mesh,int index)
    {if(index<0||index>=mesh.Faces.Count)throw new ArgumentOutOfRangeException(nameof(index));}
}

/// <summary>Deterministic first-pass CSG for unrotated authored boxes.</summary>
public static class MapBoxCsg
{
    public static MapBox? Intersect(MapBox a,MapBox b)
    {
        if(!AxisAligned(a)||!AxisAligned(b))throw new InvalidOperationException("Box CSG currently requires unrotated boxes.");
        Bounds(a,out var amin,out var amax);Bounds(b,out var bmin,out var bmax);
        NVector3 min=NVector3.Max(amin,bmin),max=NVector3.Min(amax,bmax);
        return min.X>=max.X||min.Y>=max.Y||min.Z>=max.Z?null:Box(min,max,a);
    }

    /// <summary>Subtract B from A, returning up to six non-overlapping boxes.</summary>
    public static IReadOnlyList<MapBox> Subtract(MapBox a,MapBox b)
    {
        if(!AxisAligned(a)||!AxisAligned(b))throw new InvalidOperationException("Box CSG currently requires unrotated boxes.");
        Bounds(a,out var amin,out var amax);Bounds(b,out var bmin,out var bmax);
        NVector3 min=NVector3.Max(amin,bmin),max=NVector3.Min(amax,bmax);
        if(min.X>=max.X||min.Y>=max.Y||min.Z>=max.Z)return new[]{Clone(a)};
        var result=new List<MapBox>();
        void Add(NVector3 lo,NVector3 hi){if(lo.X<hi.X&&lo.Y<hi.Y&&lo.Z<hi.Z)result.Add(Box(lo,hi,a));}
        Add(amin,new(min.X,amax.Y,amax.Z));
        Add(new(max.X,amin.Y,amin.Z),amax);
        Add(new(min.X,amin.Y,amin.Z),new(max.X,min.Y,amax.Z));
        Add(new(min.X,max.Y,amin.Z),new(max.X,amax.Y,amax.Z));
        Add(new(min.X,min.Y,amin.Z),new(max.X,max.Y,min.Z));
        Add(new(min.X,min.Y,max.Z),new(max.X,max.Y,amax.Z));
        return result;
    }

    public static MapBox UnionBounds(MapBox a,MapBox b)
    {
        if(!AxisAligned(a)||!AxisAligned(b))throw new InvalidOperationException("Box CSG currently requires unrotated boxes.");
        Bounds(a,out var amin,out var amax);Bounds(b,out var bmin,out var bmax);
        return Box(NVector3.Min(amin,bmin),NVector3.Max(amax,bmax),a);
    }

    private static bool AxisAligned(MapBox box)
    {
        float[] q=box.Transform.Rotation;
        return q.Length==4&&MathF.Abs(q[0])<1e-5f&&MathF.Abs(q[1])<1e-5f&&MathF.Abs(q[2])<1e-5f&&MathF.Abs(q[3]-1)<1e-5f;
    }
    private static void Bounds(MapBox box,out NVector3 min,out NVector3 max)
    {
        NVector3 p=new(box.Transform.Position[0],box.Transform.Position[1],box.Transform.Position[2]);
        NVector3 half=new(box.Transform.Scale[0]/2,box.Transform.Scale[1]/2,box.Transform.Scale[2]/2);
        min=p-half;max=p+half;
    }
    private static MapBox Box(NVector3 min,NVector3 max,MapBox source)
    {
        NVector3 size=max-min,center=(min+max)/2;
        return new(){Id=Guid.NewGuid(),Label=source.Label+" CSG",Material=source.Material,Solid=source.Solid,
            Damaging=source.Damaging,Terrain=source.Terrain,Shade=source.Shade,Layer=source.Layer,
            Transform=new(){Position=new[]{center.X,center.Y,center.Z},Scale=new[]{size.X,size.Y,size.Z}}};
    }
    private static MapBox Clone(MapBox source)=>Box(
        new(source.Transform.Position[0]-source.Transform.Scale[0]/2,source.Transform.Position[1]-source.Transform.Scale[1]/2,source.Transform.Position[2]-source.Transform.Scale[2]/2),
        new(source.Transform.Position[0]+source.Transform.Scale[0]/2,source.Transform.Position[1]+source.Transform.Scale[1]/2,source.Transform.Position[2]+source.Transform.Scale[2]/2),source);
}
