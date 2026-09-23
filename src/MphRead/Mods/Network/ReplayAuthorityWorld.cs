using System;
using System.Collections.Generic;
using System.IO;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

internal readonly record struct ReplayActorRef(byte Slot, ushort Generation, ushort Life)
{
    internal static ReplayActorRef Capture(PlayerEntity? player)
    {
        if (player == null) return new(255, 0, 0);
        ushort generation = NetPlayerLifecycle.Generation(player.SlotIndex), life = NetPlayerLifecycle.Get(player.SlotIndex);
        return generation == 0 || life == 0 ? new(255, 0, 0) : new((byte)player.SlotIndex, generation, life);
    }
    internal PlayerEntity? Resolve(Scene scene, ReplayReplicaState state) => Slot < 8 && state.MatchesLife(Slot, Generation, Life)
        ? scene.Players.Items[Slot] : null;
}
internal readonly record struct ReplayFlagState(int Id, Vector3 Position, ReplayActorRef Carrier,
    ReplayActorRef LastCarrier, bool AtBase, bool Grounded, float ResetTimer, float Gravity);
internal readonly record struct ReplayNodeState(int Id, sbyte Team, sbyte Occupying, byte Occupants,
    ReplayActorRef Capturer, float Progress, float ScoreTimer, float BlinkTimer, float Rotation, float Spin,
    bool Contested, bool InProgress);
internal readonly record struct ReplayPickupState(short Id, HealthSpawnState State);
internal readonly record struct ReplayDropState(ItemType Type, Vector3 Position, int DespawnTimer, int Identity = 1);
internal readonly record struct ReplayDoorState(int Id, DoorFlags Flags, bool Portal, bool Collision, bool ConnectorInactive);
internal enum ReplayEndCause : byte { None, Time, Kill, Objective, Other }

/// <summary>Versioned value contract for authority-only state that protocol 16's player
/// snapshot cannot describe. It never changes live gameplay and contains no object graph.</summary>
internal sealed class ReplayAuthorityWorld
{
    internal const int MaximumBytes = 48 * 1024, MaximumEntities = 512;
    internal ushort MatchId { get; set; }
    internal ulong Epoch { get; set; }
    internal uint Tick { get; set; }
    internal ReplayActorRef Prime { get; set; } = new(255, 0, 0);
    internal MatchState Phase { get; set; }
    internal float MatchTime { get; set; }
    internal int[] TeamPoints { get; set; } = new int[8];
    internal int[] FlagScores { get; set; } = new int[8];
    internal int[] NodesCaptured { get; set; } = new int[8];
    internal ReplayFlagState[] Flags { get; set; } = [];
    internal ReplayNodeState[] Nodes { get; set; } = [];
    internal ReplayPickupState[] Pickups { get; set; } = [];
    internal ReplayDropState[] Drops { get; set; } = [];
    internal ReplayDoorState[] Doors { get; set; } = [];
    internal int FlagCount = -1, NodeCount = -1, PickupCount = -1, DropCount = -1, DoorCount = -1;
    internal ReplayEndCause EndCause { get; set; }
    internal ReplayKillIdentity? EndingKill { get; set; }

    internal static ReplayAuthorityWorld Capture(Scene scene, ushort matchId, ulong epoch, uint tick, Func<ItemInstanceEntity, int>? dropIdentity = null)
    {
        var flags = new List<ReplayFlagState>(); var nodes = new List<ReplayNodeState>();
        var pickups = new List<ReplayPickupState>(); var drops = new List<ReplayDropState>(); var doors = new List<ReplayDoorState>();
        foreach (var entity in scene.Entities)
        {
            switch (entity)
            {
                case OctolithFlagEntity flag: flags.Add(flag.CaptureReplayAuthority()); break;
                case NodeDefenseEntity node: nodes.Add(node.CaptureReplayAuthority()); break;
                case ItemSpawnEntity pickup: pickups.Add(new(checked((short)pickup.Id), pickup.ModHealthState)); break;
                case ItemInstanceEntity { Owner: null, DespawnTimer: not 0 } drop:
                    drops.Add(new(drop.ItemType, drop.Position, drop.DespawnTimer, dropIdentity?.Invoke(drop) ?? drops.Count + 1)); break;
                case DoorEntity door: doors.Add(new(door.Id, door.Flags, door.Portal?.Active == true,
                    door.ConnectorCollision?.Active == true, door.ConnectorInactive)); break;
            }
        }
        int prime = scene.GameState.PrimeHunter;
        return new() { MatchId = matchId, Epoch = epoch, Tick = tick,
            Prime = ReplayActorRef.Capture(prime is >= 0 and < 8 ? scene.Players.Items[prime] : null),
            Phase = scene.GameState.MatchState, MatchTime = scene.GameState.MatchTime,
            TeamPoints = (int[])scene.GameState.TeamPoints.Clone(), FlagScores = (int[])scene.GameState.OctolithScores.Clone(),
            NodesCaptured = (int[])scene.GameState.NodesCaptured.Clone(), Flags = flags.ToArray(), Nodes = nodes.ToArray(),
            Pickups = pickups.ToArray(), Drops = drops.ToArray(), Doors = doors.ToArray() };
    }
    internal byte[] Encode()
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        Encode(w);
        return stream.ToArray();
    }
    internal void Encode(BinaryWriter w)
    {
        using var perf = ReplayPerfTelemetry.Measure(ReplayPerfOperation.AuthorityEncode);
        long start = w.BaseStream.Position;
        w.Write((byte)2); w.Write(MatchId); w.Write(Epoch); w.Write(Tick);
        Actor(Prime); w.Write((byte)Phase); w.Write(MatchTime); w.Write((byte)EndCause); w.Write(EndingKill.HasValue);
        if (EndingKill is { } kill)
        {
            w.Write(kill.MatchId); w.Write(kill.AuthorityEpoch); w.Write(kill.ServerTick); w.Write(kill.EventId);
            w.Write(kill.KillerSlot); w.Write(kill.KillerGeneration); w.Write(kill.VictimSlot); w.Write(kill.VictimGeneration); w.Write(kill.VictimLifeId);
        }
        Scores(TeamPoints); Scores(FlagScores); Scores(NodesCaptured);
        Count(FlagCount < 0 ? Flags.Length : FlagCount); foreach (var v in Flags.AsSpan(0, FlagCount < 0 ? Flags.Length : FlagCount))
        { w.Write(v.Id); Vector(v.Position); Actor(v.Carrier); Actor(v.LastCarrier); w.Write(v.AtBase); w.Write(v.Grounded); w.Write(v.ResetTimer); w.Write(v.Gravity); }
        Count(NodeCount < 0 ? Nodes.Length : NodeCount); foreach (var v in Nodes.AsSpan(0, NodeCount < 0 ? Nodes.Length : NodeCount))
        { w.Write(v.Id); w.Write(v.Team); w.Write(v.Occupying); w.Write(v.Occupants); Actor(v.Capturer); w.Write(v.Progress); w.Write(v.ScoreTimer); w.Write(v.BlinkTimer); w.Write(v.Rotation); w.Write(v.Spin); w.Write(v.Contested); w.Write(v.InProgress); }
        Count(PickupCount < 0 ? Pickups.Length : PickupCount); foreach (var v in Pickups.AsSpan(0, PickupCount < 0 ? Pickups.Length : PickupCount))
        { w.Write(v.Id); w.Write(v.State.Available); w.Write(v.State.Active); w.Write(v.State.Cooldown); w.Write(v.State.SpawnCount); w.Write(v.State.PickerSlot); }
        Count(DropCount < 0 ? Drops.Length : DropCount); foreach (var v in Drops.AsSpan(0, DropCount < 0 ? Drops.Length : DropCount)) { w.Write(v.Identity); w.Write((byte)v.Type); Vector(v.Position); w.Write(v.DespawnTimer); }
        Count(DoorCount < 0 ? Doors.Length : DoorCount); foreach (var v in Doors.AsSpan(0, DoorCount < 0 ? Doors.Length : DoorCount))
        { w.Write(v.Id); w.Write((uint)v.Flags); w.Write(v.Portal); w.Write(v.Collision); w.Write(v.ConnectorInactive); }
        if (w.BaseStream.Position - start > MaximumBytes) throw Invalid();
        void Scores(int[] values) { if (values.Length != 8) throw Invalid(); foreach (int value in values) w.Write(value); }
        void Count(int n) { if (n > MaximumEntities) throw Invalid(); w.Write((ushort)n); }
        void Actor(ReplayActorRef v) { w.Write(v.Slot); w.Write(v.Generation); w.Write(v.Life); }
        void Vector(Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    }
    internal static ReplayAuthorityWorld Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw Invalid();
        using var stream = new MemoryStream(bytes.ToArray(), false); using var r = new BinaryReader(stream);
        try
        {
            byte version = r.ReadByte(); if (version is < 1 or > 2) throw Invalid();
            ushort match = r.ReadUInt16(); ulong epoch = r.ReadUInt64(); uint tick = r.ReadUInt32();
            var prime = Actor(); var phase = (MatchState)r.ReadByte(); float time = Float();
            var cause = (ReplayEndCause)r.ReadByte(); ReplayKillIdentity? kill = null;
            if (Bool()) kill = new(r.ReadUInt16(), r.ReadUInt64(), r.ReadUInt32(), r.ReadUInt16(), r.ReadByte(), r.ReadUInt16(), r.ReadByte(), r.ReadUInt16(), r.ReadUInt16());
            if (!Enum.IsDefined(phase) || !Enum.IsDefined(cause) || match == 0
                || (cause == ReplayEndCause.Kill) != kill.HasValue
                || kill is { } k && (k.MatchId != match || k.AuthorityEpoch != epoch || k.KillerSlot >= 8 || k.VictimSlot >= 8
                    || k.KillerGeneration == 0 || k.VictimGeneration == 0 || k.VictimLifeId == 0 || k.ServerTick > tick)) throw Invalid();
            int[] points = Scores(), flagscores = Scores(), captured = Scores();
            var flags = new ReplayFlagState[Count()]; var ids = new HashSet<int>();
            for (int i = 0; i < flags.Length; i++)
            { int id = Id(ids); flags[i] = new(id, Vector(), Actor(), Actor(), Bool(), Bool(), Float(), Float()); }
            var nodes = new ReplayNodeState[Count()]; ids.Clear();
            for (int i = 0; i < nodes.Length; i++)
            { int id = Id(ids); sbyte team = Team(), occupying = Team(); nodes[i] = new(id, team, occupying, r.ReadByte(), Actor(), Float(), Float(), Float(), Float(), Float(), Bool(), Bool()); }
            var pickups = new ReplayPickupState[Count()]; ids.Clear();
            for (int i = 0; i < pickups.Length; i++)
            {
                short id = r.ReadInt16(); if (!ids.Add(id)) throw Invalid();
                pickups[i] = new(id, new(Bool(), Bool(), r.ReadUInt16(), r.ReadUInt16(), Team()));
            }
            var drops = new ReplayDropState[Count()]; ids.Clear();
            for (int i = 0; i < drops.Length; i++)
            {
                int identity = version >= 2 ? r.ReadInt32() : i + 1;
                var type = (ItemType)r.ReadByte(); Vector3 position = Vector(); int timer = r.ReadInt32();
                if ((uint)type >= 22 || timer is < -1 or > 1000000 || identity is < 1 or > int.MaxValue - 2 || !ids.Add(identity)) throw Invalid();
                drops[i] = new(type, position, timer, identity);
            }
            var doors = new ReplayDoorState[Count()]; ids.Clear();
            for (int i = 0; i < doors.Length; i++) doors[i] = new(Id(ids), (DoorFlags)r.ReadUInt32(), Bool(), Bool(), Bool());
            if (stream.Position != stream.Length) throw Invalid();
            return new() { MatchId = match, Epoch = epoch, Tick = tick, Prime = prime, Phase = phase, MatchTime = time,
                EndCause = cause, EndingKill = kill, TeamPoints = points, FlagScores = flagscores, NodesCaptured = captured,
                Flags = flags, Nodes = nodes, Pickups = pickups, Drops = drops, Doors = doors };
        }
        catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated authoritative replay world.", ex); }
        int Count() { int n = r.ReadUInt16(); if (n > MaximumEntities) throw Invalid(); return n; }
        int Id(HashSet<int> ids) { int id = r.ReadInt32(); if (!ids.Add(id)) throw Invalid(); return id; }
        bool Bool() { byte v = r.ReadByte(); if (v > 1) throw Invalid(); return v != 0; }
        sbyte Team() { sbyte v = r.ReadSByte(); if (v is < -1 or >= 8) throw Invalid(); return v; }
        float Float() { float v = r.ReadSingle(); if (!float.IsFinite(v) || Math.Abs(v) > 1000000) throw Invalid(); return v; }
        Vector3 Vector() => new(Float(), Float(), Float());
        ReplayActorRef Actor()
        {
            var v = new ReplayActorRef(r.ReadByte(), r.ReadUInt16(), r.ReadUInt16());
            if (v.Slot != 255 && (v.Slot >= 8 || v.Generation == 0) || v.Slot == 255 && (v.Generation != 0 || v.Life != 0)) throw Invalid();
            return v;
        }
        int[] Scores() { var values = new int[8]; for (int i = 0; i < 8; i++) values[i] = r.ReadInt32(); return values; }
    }
    internal void Apply(Scene scene, ReplayReplicaState state)
    {
        if (!scene.Services.IsReplica) throw new InvalidOperationException("Replay world facts require a private scene.");
        scene.GameState.PrimeHunter = Prime.Resolve(scene, state)?.SlotIndex ?? -1;
        TeamPoints.CopyTo(scene.GameState.TeamPoints, 0); FlagScores.CopyTo(scene.GameState.OctolithScores, 0);
        NodesCaptured.CopyTo(scene.GameState.NodesCaptured, 0);
        foreach (var flag in Flags)
            if (scene.TryGetEntity(flag.Id, out var entity) && entity is OctolithFlagEntity target) target.ApplyReplayAuthority(flag, state);
        foreach (var node in Nodes)
            if (scene.TryGetEntity(node.Id, out var entity) && entity is NodeDefenseEntity target) target.ApplyReplayAuthority(node, state);
        foreach (var door in Doors)
            if (scene.TryGetEntity(door.Id, out var entity) && entity is DoorEntity target)
            {
                target.Flags = door.Flags; target.ConnectorInactive = door.ConnectorInactive;
                if (target.Portal != null) target.Portal.Active = door.Portal;
                if (target.ConnectorCollision != null) target.ConnectorCollision.Active = door.Collision;
            }
        var existing = new Dictionary<int, ItemInstanceEntity>();
        foreach (var item in scene.GetItemInstanceEntities())
            if (item.Owner == null)
            {
                if (item.Id >= -1) item.DespawnTimer = 0; // discard contact-inferred drops
                else existing.Add(-2 - item.Id, item);
            }
        foreach (var drop in Drops)
        {
            existing.Remove(drop.Identity, out var item);
            if (item != null && item.ItemType != drop.Type) throw new InvalidDataException("Replay drop identity changed type.");
            if (item == null)
            { item = ItemInstanceEntity.CreateReplayDrop(drop, scene); scene.AddEntity(item); }
            item.Position = drop.Position; item.DespawnTimer = drop.DespawnTimer;
        }
        foreach (var item in existing.Values) item.DespawnTimer = 0;
    }
    private static InvalidDataException Invalid() => new("Invalid authoritative replay world.");
}
