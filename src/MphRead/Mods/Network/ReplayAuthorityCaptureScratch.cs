using System;
using System.IO;
using MphRead.Entities;
using MphRead.Mods.Replay;

namespace MphRead.Mods.Network;

/// <summary>Owner-thread double buffer: LatestAuthorityWorld is borrowed until the
/// next capture. Only immutable encoded facts escape into timeline/network owners.</summary>
internal sealed class ReplayAuthorityCaptureScratch
{
    private readonly ReplayAuthorityWorld[] _frames = [Create(), Create()];
    private int _next;
    private readonly ReplayCheckpointWriter _buffer = new(ReplayAuthorityWorld.MaximumBytes, ReplayAuthorityWorld.MaximumBytes);
    private readonly BinaryWriter _writer;
    internal ReplayAuthorityCaptureScratch() => _writer = new(_buffer);
    private static ReplayAuthorityWorld Create() => new()
    {
        Flags = new ReplayFlagState[ReplayAuthorityWorld.MaximumEntities],
        Nodes = new ReplayNodeState[ReplayAuthorityWorld.MaximumEntities],
        Pickups = new ReplayPickupState[ReplayAuthorityWorld.MaximumEntities],
        Drops = new ReplayDropState[ReplayAuthorityWorld.MaximumEntities],
        Doors = new ReplayDoorState[ReplayAuthorityWorld.MaximumEntities]
    };
    internal ReplayAuthorityWorld Capture(Scene scene, ushort match, ulong epoch, uint tick, Func<ItemInstanceEntity, int> identify)
    {
        var world = _frames[_next]; _next ^= 1;
        world.MatchId = match; world.Epoch = epoch; world.Tick = tick;
        world.EndCause = ReplayEndCause.None; world.EndingKill = null;
        world.Phase = scene.GameState.MatchState; world.MatchTime = scene.GameState.MatchTime;
        int prime = scene.GameState.PrimeHunter;
        world.Prime = ReplayActorRef.Capture(prime is >= 0 and < 8 ? scene.Players.Items[prime] : null);
        Array.Copy(scene.GameState.TeamPoints, world.TeamPoints, 8);
        Array.Copy(scene.GameState.OctolithScores, world.FlagScores, 8);
        Array.Copy(scene.GameState.NodesCaptured, world.NodesCaptured, 8);
        world.FlagCount = world.NodeCount = world.PickupCount = world.DropCount = world.DoorCount = 0;
        foreach (var entity in scene.Entities)
        {
            switch (entity)
            {
                case OctolithFlagEntity flag: Add(world.Flags, ref world.FlagCount, flag.CaptureReplayAuthority()); break;
                case NodeDefenseEntity node: Add(world.Nodes, ref world.NodeCount, node.CaptureReplayAuthority()); break;
                case ItemSpawnEntity pickup: Add(world.Pickups, ref world.PickupCount, new(checked((short)pickup.Id), pickup.ModHealthState)); break;
                case ItemInstanceEntity { Owner: null, DespawnTimer: not 0 } drop:
                    Add(world.Drops, ref world.DropCount, new(drop.ItemType, drop.Position, drop.DespawnTimer, identify(drop))); break;
                case DoorEntity door: Add(world.Doors, ref world.DoorCount, new(door.Id, door.Flags,
                    door.Portal?.Active == true, door.ConnectorCollision?.Active == true, door.ConnectorInactive)); break;
            }
        }
        return world;
    }
    private static void Add<T>(T[] values, ref int count, T value)
    {
        if (count == values.Length) throw new InvalidDataException("Authority replay entity budget exceeded.");
        values[count++] = value;
    }
    internal ReadOnlySpan<byte> Encode(ReplayAuthorityWorld world)
    { _buffer.Reset(); world.Encode(_writer); return _buffer.Written; }
}
