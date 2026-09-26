using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MphRead.Editor;
using MphRead.Formats.Collision;
using MphRead.Utility;
using OpenTK.Mathematics;

namespace MphRead.Mods.MapGen;

/// <summary>
/// Compiles a shipped MPH room into the same BuiltMap representation used by
/// Q3 imports, without modifying the extracted source. Native architecture
/// stays immutable; Project Prime geometry/entities are layered on top.
/// </summary>
public static class NativeRoomImport
{
    private readonly record struct Vtx(Vector3 Position,Vector2 Uv,Vector3 Normal,float Shade);

    public static BuiltMap Build(MapDefinition definition,CancellationToken cancellation=default)
    {
        MapNativeRoomSource source=definition.NativeRoom
            ?? throw new MapAuthoringException("FP-MAP-013","Native room source is missing.");
        if(!Metadata.RoomMetadata.TryGetValue(source.Room,out RoomMetadata? meta))
            throw new MapAuthoringException("FP-MAP-013",$"Unknown built-in room {source.Room}.");

        cancellation.ThrowIfCancellationRequested();
        Model model=Read.GetRoomModelInstance(meta.Name).Model.CreateSceneCopy();
        if(source.MultiplayerLayerOnly)
        {
            int layer=SceneSetup.GetNodeLayer(GameMode.Battle,meta.NodeLayer,2);
            model.FilterNodes(layer);
        }

        var map=new BuiltMap(definition);
        foreach(Node node in model.Nodes)
        {
            cancellation.ThrowIfCancellationRequested();
            if(!node.Enabled||!model.NodeParentsEnabled(node))continue;
            foreach(int meshId in node.GetMeshIds())
            {
                if(meshId<0||meshId>=model.Meshes.Count)continue;
                Mesh mesh=model.Meshes[meshId];
                foreach(BuiltFace face in Decode(model,mesh,cancellation))map.Faces.Add(face);
            }
        }
        map.ImportedFaceCount=map.Faces.Count;

        if(source.UseNativeCollision)
            AddCollision(map,Collision.GetCollision(meta,roomLayerMask:source.MultiplayerLayerOnly
                ? SceneSetup.GetNodeLayer(GameMode.Battle,meta.NodeLayer,2) : -1));
        map.ImportedCollisionFaceCount=map.Solid.Count;

        MapBuilder.AddAuthoredGeometry(map,definition,cancellation);

        if(source.PreserveEntities)
        {
            foreach(EntityEditorBase entity in Repack.ReadRoomEntities(meta.Name,
                source.MultiplayerLayerOnly?RepackFilter.Multiplayer:RepackFilter.All))
            {
                if(source.EditableSpawns&&entity is PlayerSpawnEntityEditor)continue;
                if(source.EditableItems&&entity is ItemSpawnEntityEditor item
                    && MapBuilder.MultiplayerItems.Contains(item.ItemType))continue;
                map.Entities.Add(entity);
            }
        }
        MapBuilder.AddEntities(map,definition);
        return map;
    }

    private static void AddCollision(BuiltMap map,CollisionInstance collision)
    {
        if(collision.Info is MphCollisionInfo mph)
        {
            foreach(CollisionData data in mph.Data)
            {
                if(data.PointIndexCount<3)continue;
                Vector3[] points=new Vector3[data.PointIndexCount];
                for(int i=0;i<points.Length;i++)
                    points[i]=mph.Points[mph.PointIndices[data.PointStartIndex+i]]+collision.Translation;
                Vector3 normal=mph.Planes[data.PlaneIndex].Xyz;
                map.Solid.Add(new BuiltFace(points,new Vector2[points.Length],normal,0,1)
                {
                    Damaging=data.Flags.TestFlag(CollisionFlags.Damaging),
                    Terrain=data.Terrain,
                    Slipperiness=data.Slipperiness,
                    ReflectBeams=data.Flags.TestFlag(CollisionFlags.ReflectBeams),
                    IgnorePlayers=data.IgnorePlayers,
                    IgnoreBeams=data.IgnoreBeams,
                    IgnoreScan=data.Flags.TestFlag(CollisionFlags.IgnoreScan)
                });
            }
            return;
        }
        if(collision.Info is FhCollisionInfo fh)
        {
            for(int dataIndex=fh.Portals.Count;dataIndex<fh.Data.Count;dataIndex++)
            {
                FhCollisionData data=fh.Data[dataIndex];
                if(data.VectorCount<3)continue;
                Vector3[] points=new Vector3[data.VectorCount];
                for(int i=0;i<points.Length;i++)
                    points[i]=fh.Points[fh.Vectors[data.VectorStartIndex+i].Point2Index]+collision.Translation;
                Vector3 normal=fh.Planes[data.PlaneIndex].Xyz;
                map.Solid.Add(new BuiltFace(points,new Vector2[points.Length],normal,0,1));
            }
        }
    }

    private static IEnumerable<BuiltFace> Decode(Model model,Mesh mesh,CancellationToken cancellation)
    {
        if(mesh.DlistId<0||mesh.DlistId>=model.RenderInstructionLists.Count)yield break;
        var vertices=new List<Vtx>();
        Vector3 position=Vector3.Zero,normal=Vector3.UnitY;
        Vector2 uv=Vector2.Zero;
        float shade=1;
        int primitive=-1;
        float scale=model.Scale.X;

        IEnumerable<BuiltFace> Flush()
        {
            if(primitive<0||vertices.Count<3)yield break;
            BuiltFace? Face(Vtx a,Vtx b,Vtx c)
            {
                Vector3[] points={a.Position*scale,b.Position*scale,c.Position*scale};
                Vector3 n=Vector3.Cross(points[1]-points[0],points[2]-points[0]);
                if(n.LengthSquared<1e-10f)return null;
                n.Normalize();
                Vector3 authored=a.Normal+b.Normal+c.Normal;
                if(authored.LengthSquared>1e-8f&&Vector3.Dot(n,authored)<0)
                {
                    (points[1],points[2])=(points[2],points[1]);
                    (b,c)=(c,b);n=-n;
                }
                return new BuiltFace(points,new[]{a.Uv,b.Uv,c.Uv},n,mesh.MaterialId,
                    Math.Clamp((a.Shade+b.Shade+c.Shade)/3f,0,1));
            }
            if(primitive==0)
            {
                for(int i=0;i+2<vertices.Count;i+=3)
                    if(Face(vertices[i],vertices[i+1],vertices[i+2]) is {} f)yield return f;
            }
            else if(primitive==1)
            {
                for(int i=0;i+3<vertices.Count;i+=4)
                {
                    if(Face(vertices[i],vertices[i+1],vertices[i+2]) is {} a)yield return a;
                    if(Face(vertices[i+2],vertices[i+3],vertices[i]) is {} b)yield return b;
                }
            }
            else if(primitive==2)
            {
                for(int i=0;i+2<vertices.Count;i++)
                {
                    Vtx a=vertices[i],b=vertices[i+1],cc=vertices[i+2];
                    if((i&1)!=0)(a,cc)=(cc,a);
                    if(Face(a,b,cc) is {} f)yield return f;
                }
            }
            else if(primitive==3)
            {
                for(int i=0;i+3<vertices.Count;i+=2)
                {
                    Vtx a=vertices[i],b=vertices[i+1],cc=vertices[i+2],d=vertices[i+3];
                    if(Face(a,b,cc) is {} f1)yield return f1;
                    if(Face(d,cc,b) is {} f2)yield return f2;
                }
            }
        }

        void AddVertex()=>vertices.Add(new(position,uv,normal,shade));
        static int S16(uint value)=>unchecked((short)(value&0xFFFF));
        static int S10(uint value)
        {
            int v=(int)(value&0x3FF);
            return (v&0x200)!=0?v|unchecked((int)0xFFFFFC00):v;
        }

        foreach(RenderInstruction instruction in model.RenderInstructionLists[mesh.DlistId])
        {
            cancellation.ThrowIfCancellationRequested();
            switch(instruction.Code)
            {
                case InstructionCode.BEGIN_VTXS:
                    foreach(var face in Flush())yield return face;
                    vertices.Clear();primitive=(int)instruction.Arguments[0];break;
                case InstructionCode.END_VTXS:
                    foreach(var face in Flush())yield return face;
                    vertices.Clear();primitive=-1;break;
                case InstructionCode.COLOR:
                    {
                        uint rgb=instruction.Arguments[0];
                        float r=(rgb&31)/31f,g=((rgb>>5)&31)/31f,b=((rgb>>10)&31)/31f;
                        shade=(r+g+b)/3f;
                    }
                    break;
                case InstructionCode.NORMAL:
                    {
                        uint packed=instruction.Arguments[0];
                        normal=new(S10(packed)/512f,S10(packed>>10)/512f,S10(packed>>20)/512f);
                        if(normal.LengthSquared>1e-8f)normal.Normalize();
                    }
                    break;
                case InstructionCode.TEXCOORD:
                    {
                        uint st=instruction.Arguments[0];
                        uv=new(S16(st)/16f,S16(st>>16)/16f);
                    }
                    break;
                case InstructionCode.VTX_16:
                    {
                        uint xy=instruction.Arguments[0];
                        position=new(Fixed.ToFloat(S16(xy)),Fixed.ToFloat(S16(xy>>16)),
                            Fixed.ToFloat(S16(instruction.Arguments[1])));
                        AddVertex();
                    }
                    break;
                case InstructionCode.VTX_10:
                    {
                        uint xyz=instruction.Arguments[0];
                        position=new(S10(xyz)/64f,S10(xyz>>10)/64f,S10(xyz>>20)/64f);AddVertex();
                    }
                    break;
                case InstructionCode.VTX_XY:
                    {
                        uint xy=instruction.Arguments[0];position.X=Fixed.ToFloat(S16(xy));position.Y=Fixed.ToFloat(S16(xy>>16));AddVertex();
                    }
                    break;
                case InstructionCode.VTX_XZ:
                    {
                        uint xz=instruction.Arguments[0];position.X=Fixed.ToFloat(S16(xz));position.Z=Fixed.ToFloat(S16(xz>>16));AddVertex();
                    }
                    break;
                case InstructionCode.VTX_YZ:
                    {
                        uint yz=instruction.Arguments[0];position.Y=Fixed.ToFloat(S16(yz));position.Z=Fixed.ToFloat(S16(yz>>16));AddVertex();
                    }
                    break;
                case InstructionCode.VTX_DIFF:
                    {
                        uint xyz=instruction.Arguments[0];
                        position.X+=Fixed.ToFloat(S10(xyz));position.Y+=Fixed.ToFloat(S10(xyz>>10));position.Z+=Fixed.ToFloat(S10(xyz>>20));AddVertex();
                    }
                    break;
            }
        }
        foreach(var face in Flush())yield return face;
    }
}

/// <summary>Creates an editable Project Prime project from a shipped room.</summary>
public static class NativeRoomProject
{
    public static MapProject Create(string sourceRoom,string newName)
    {
        if(!Metadata.RoomMetadata.TryGetValue(sourceRoom,out RoomMetadata? meta))
            throw new MapAuthoringException("FP-MAP-013",$"Unknown built-in room {sourceRoom}.");
        if(!MapValidator.ValidRuntimeName(newName)||Metadata.IsBuiltInRoom(newName))
            throw new MapAuthoringException("FP-MAP-010","Choose a new runtime name that does not replace a built-in room.");

        Model model=Read.GetRoomModelInstance(meta.Name).Model;
        int scaleFactor=(int)Math.Clamp(Math.Round(Math.Log2(Math.Max(1,model.Scale.X))),0,16);
        var definition=new MapDefinition
        {
            FormatVersion=2,MapId=Guid.NewGuid(),Name=newName,
            InGameName=(meta.InGameName??meta.Name)+" Remix",TextureSource=meta.Name,
            ScaleFactor=scaleFactor,KillHeight=meta.KillHeight,FarClip=meta.FarClip,
            FogEnabled=meta.FogEnabled,FogColor=new[]{(int)meta.FogColor.Red,(int)meta.FogColor.Green,(int)meta.FogColor.Blue},
            FogSlope=meta.FogSlope,FogOffset=meta.FogOffset,
            Light1Color=new[]{(int)meta.Light1Color.Red,(int)meta.Light1Color.Green,(int)meta.Light1Color.Blue},
            Light1Vector=new[]{meta.Light1Vector.X,meta.Light1Vector.Y,meta.Light1Vector.Z},
            Light2Color=new[]{(int)meta.Light2Color.Red,(int)meta.Light2Color.Green,(int)meta.Light2Color.Blue},
            Light2Vector=new[]{meta.Light2Vector.X,meta.Light2Vector.Y,meta.Light2Vector.Z},
            BattleTimeLimit=meta.BattleTimeLimit,PointLimit=meta.PointLimit,
            NativeRoom=new(){Room=meta.Name,PreserveEntities=true,EditableSpawns=true,EditableItems=true,
                UseNativeCollision=true,MultiplayerLayerOnly=meta.Multiplayer}
        };
        for(int i=0;i<model.Materials.Count;i++)
            definition.Materials.Add(new(){Id=Guid.NewGuid(),Name=String.IsNullOrWhiteSpace(model.Materials[i].Name)
                ?$"Native {i}":model.Materials[i].Name,SourceMaterial=i,TexScale=16});

        foreach(EntityEditorBase entity in Repack.ReadRoomEntities(meta.Name,
            meta.Multiplayer?RepackFilter.Multiplayer:RepackFilter.All))
        {
            if(entity is PlayerSpawnEntityEditor spawn)
            {
                float yaw=MathHelper.RadiansToDegrees(MathF.Atan2(spawn.Facing.X,spawn.Facing.Z));
                definition.Spawns.Add(new(){Id=Guid.NewGuid(),Label=$"Native spawn {spawn.Id}",
                    Position=new[]{spawn.Position.X,spawn.Position.Y,spawn.Position.Z},Yaw=yaw,Team=spawn.TeamIndex});
            }
            else if(entity is ItemSpawnEntityEditor item&&MapBuilder.MultiplayerItems.Contains(item.ItemType))
            {
                definition.Items.Add(new(){Id=Guid.NewGuid(),Label=$"Native pickup {item.Id}",
                    Position=new[]{item.Position.X,item.Position.Y,item.Position.Z},Type=item.ItemType.ToString(),
                    HasBase=item.HasBase,SpawnInterval=item.SpawnInterval});
            }
        }
        return new MapProject(definition);
    }
}
