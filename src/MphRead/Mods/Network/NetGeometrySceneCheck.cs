using System;
using System.Collections.Generic;
using MphRead.Entities;
using OpenTK.Mathematics;
namespace MphRead.Mods.Network;

/// <summary>Real asset collision traces before enabling the production rewind path.</summary>
public static class NetGeometrySceneCheck
{
    public static int Run(string room)
    {
        var sim = new ServerSim();
        bool production = NetDynamicGeometryHistory.ProductionEnabled, shadow = NetDynamicGeometryHistory.ShadowEnabled;
        if (!sim.Start(room, GameMode.SinglePlayer, 2, _ => { }, () => { })) return 1;
        try
        {
            var scene = PlayerEntity.Players[0].OwningScene;
            var doors = new List<DoorEntity>(); var fields = new List<ForceFieldEntity>(); int meshes = 0;
            foreach (var door in scene.GetDoorEntities()) doors.Add(door);
            foreach (var field in scene.GetForceFieldEntities()) fields.Add(field);
            foreach (var entity in scene.Entities)
                if (entity.Type is EntityType.Platform or EntityType.Object)
                    foreach (var collision in entity.EntityCollision) if (collision?.Collision != null) meshes++;
            Console.WriteLine($"GEOMETRY room={room} doors={doors.Count} fields={fields.Count} meshes={meshes}");
            if (doors.Count == 0) throw new InvalidOperationException("Fixture room needs a real door");
            NetDynamicGeometryHistory.ShadowEnabled = true;
            NetDynamicGeometryHistory.ProductionEnabled = true;
            foreach (var door in doors)
            {
                var original = door.Flags; bool connector = door.ConnectorInactive;
                door.ConnectorInactive = false; door.Flags |= DoorFlags.Open;
                NetDynamicGeometryHistory.RecordWorld(10);
                door.Flags &= ~DoorFlags.Open;
                var origin = door.LockPosition + door.FacingVector * .7f;
                long before = NetDynamicGeometryHistory.ShadowCurrentBlocked;
                NetDynamicGeometryHistory.CompareShadow(scene, origin, -door.FacingVector, 10, 1.4f);
                if (NetDynamicGeometryHistory.ShadowCurrentBlocked != before + 1 || door.Flags.TestFlag(DoorFlags.Open))
                    throw new InvalidOperationException($"Door {door.Id}: historical open/current closed trace or exact restore failed");
                NetDynamicGeometryHistory.RecordWorld(11);
                door.Flags |= DoorFlags.Open;
                before = NetDynamicGeometryHistory.ShadowHistoricalBlocked;
                NetDynamicGeometryHistory.CompareShadow(scene, origin, -door.FacingVector, 11, 1.4f);
                if (NetDynamicGeometryHistory.ShadowHistoricalBlocked != before + 1 || !door.Flags.TestFlag(DoorFlags.Open))
                    throw new InvalidOperationException($"Door {door.Id}: historical closed/current open trace or exact restore failed");
                door.Flags = original; door.ConnectorInactive = connector;
            }
            foreach (var field in fields)
            {
                bool original = field.Active;
                field.ModSetNetworkCollisionActive(false); NetDynamicGeometryHistory.RecordWorld(12);
                field.ModSetNetworkCollisionActive(true);
                long before = NetDynamicGeometryHistory.ShadowCurrentBlocked;
                NetDynamicGeometryHistory.CompareShadow(scene, field.Position + field.FacingVector * .2f, -field.FacingVector, 12, .4f);
                if (NetDynamicGeometryHistory.ShadowCurrentBlocked != before + 1 || !field.Active)
                    throw new InvalidOperationException($"Field {field.Id}: trace or restore failed");
                field.ModSetNetworkCollisionActive(original);
            }
            Console.WriteLine($"GEOMETRY PASS: real asset traces, historical blocked={NetDynamicGeometryHistory.ShadowHistoricalBlocked}, current blocked={NetDynamicGeometryHistory.ShadowCurrentBlocked}, exact restoration");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { sim.Stop(); NetDynamicGeometryHistory.ProductionEnabled = production; NetDynamicGeometryHistory.ShadowEnabled = shadow; }
    }
}
