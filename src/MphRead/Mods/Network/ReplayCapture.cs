using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MphRead.Entities;

namespace MphRead.Mods.Network
{
    internal static class ReplayCapture
    {
        internal static ReplayRecorder Recorder { get; } = new();
        internal static ReplayLiveWorld WorldCapture { get; private set; } = new(Recorder);
        private static readonly byte[] Snapshot = new byte[NetConfig.MaxPacketSize];
        private static int _snapshotLength;
        private static uint _historyFrames;
        private static string? _room;
        private static ulong _mapHash;
        private static readonly PlayerState[] Previous = new PlayerState[RosterPacket.MaxSlots];
        private static readonly ReplayAuthorityWire AuthorityWire = new();
        private static readonly ReplayAuthorityCaptureScratch AuthorityScratch = new();
        internal static ReplayAuthorityWorld? LatestAuthorityWorld { get; private set; }
        private static ReplayKillIdentity? _authorityKill;
        private static bool _worldCaptureFailed;
        private sealed record DropIdentity(int Value);
        private static ConditionalWeakTable<ItemInstanceEntity, DropIdentity> DropIdentities = new();
        private static int _nextDropIdentity;
        private static int IdentifyDrop(ItemInstanceEntity item)
            => DropIdentities.GetValue(item, _ => new(checked(++_nextDropIdentity))).Value;
        private static readonly bool[] Known = new bool[RosterPacket.MaxSlots];

        static ReplayCapture()
        {
            Recorder.Resetting += () =>
            {
                AuthorityWire.Reset(); LatestAuthorityWorld = null; _authorityKill = null; _worldCaptureFailed = false;
                Array.Clear(Known); DropIdentities = new(); _nextDropIdentity = 0;
            };
        }

        internal static bool NeedsWorld => (!Headless.Active && (Mods.Launcher.LauncherPrefs.KillCamEnabled
            || Mods.Launcher.LauncherPrefs.FinalKillCamEnabled || DemoClip.Active))
            || DemoClip.IsSaving || DemoRecorder.IsRecording || ServerReplayRecorder.IsRecording;

        internal static void AfterSimulation(Scene scene)
        {
            if (scene.Services.IsReplica) return;
            using var perf = ReplayPerfTelemetry.Measure(ReplayPerfOperation.Capture);
            DemoClip.Tick(scene.Size);
            if (DemoPlayback.IsActive || !NetSession.Active || !scene.GameState.Multiplayer) return;
            uint historyFrames = (uint)Math.Max(45, DemoClip.Seconds + DemoClip.PostRollSeconds) * 60;
            if (historyFrames != _historyFrames)
            {
                Recorder.Timeline.SetHistoryFrames(historyFrames);
                _historyFrames = historyFrames;
            }
            if (NetSession.IsAuthority && !_worldCaptureFailed
                && (NetSession.NetFrame % 6 == 0 || LatestAuthorityWorld?.Phase != scene.GameState.MatchState))
            {
                try
                {
                    using var authorityPerf = ReplayPerfTelemetry.Measure(ReplayPerfOperation.AuthorityCapture);
                    var world = AuthorityScratch.Capture(scene, NetSession.CurrentMatchId, NetSession.AuthorityEpoch, NetSession.NetFrame, IdentifyDrop);
                    ClassifyEnd(scene, world);
                    AcceptWorld(world, AuthorityScratch.Encode(world), send: true);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                { _worldCaptureFailed = true; Console.WriteLine("[replay] Authority world capture unavailable: " + ex.Message); }
            }
            WorldCapture.SetEnabled(NeedsWorld);
            WorldCapture.Advance(NetSession.NetFrame, scene.Size);
            DemoRecorder.Tick();
            ServerReplayRecorder.Tick();
        }

        internal static void ReleaseWorld()
        {
            DemoClip.CompletePending(WorldCapture.World?.Scene.Size ?? new OpenTK.Mathematics.Vector2i(256, 192));
            WorldCapture.Dispose(); WorldCapture = new(Recorder);
            Recorder.Reset();
        }

        public static void Reset()
        {
            _snapshotLength = 0; _room = null; _mapHash = 0;
            Array.Clear(Known); AuthorityWire.Reset(); LatestAuthorityWorld = null; _authorityKill = null; _worldCaptureFailed = false;
            Recorder.Reset();
        }

        public static void Observe(ReadOnlySpan<byte> packet)
        {
            if (DemoPlayback.IsActive || packet.Length < 1) return;
            if ((PacketType)packet[0] == PacketType.Snapshot && packet.Length <= Snapshot.Length)
            {
                packet.CopyTo(Snapshot);
                _snapshotLength = packet.Length;
            }
        }

        public static ReplayMetadata Capture(ReplayType type, bool hashMap = true)
        {
            var packets = new List<byte[]>();
            var players = new List<ReplayPlayerInfo>();
            MatchStatePacket? match = NetSession.ServerMatch;
            string room = match?.RoomKey ?? "";
            if (_room != room || _mapHash == 0)
            {
                ulong hash = hashMap && room.Length > 0 ? ReplayMapIdentity.Compute(room) : 0;
                _room = room;
                _mapHash = hash;
            }
            if (NetSession.ServerSession is { } session)
            {
                byte[] packet = new byte[1 + SessionStatePacket.Size];
                packet[0] = (byte)PacketType.SessionState; session.Write(packet.AsSpan(1));
                packets.Add(packet);
            }
            if (match is { } state)
            {
                byte[] packet = new byte[1 + MatchStatePacket.Size];
                packet[0] = (byte)PacketType.MatchState; state.Write(packet.AsSpan(1));
                packets.Add(packet);
            }
            // Use the current wire roster instead of reconstructing an obsolete
            // packet shape. This carries stream identity, slot generations, teams
            // and lobby state, all of which playback needs before the first snapshot.
            RosterPacket roster = NetSession.LobbyRoster();
            for (int i = 0; i < roster.Count; i++)
            {
                players.Add(new(roster.Slots[i], roster.Hunters[i], roster.Teams[i], roster.Names[i]));
            }
            byte[] rosterBytes = new byte[1 + RosterPacket.Size];
            rosterBytes[0] = (byte)PacketType.Roster; roster.Write(rosterBytes.AsSpan(1));
            packets.Add(rosterBytes);
            if (_snapshotLength > 0) packets.Add(Snapshot.AsSpan(0, _snapshotLength).ToArray());
            return new ReplayMetadata
            {
                Type = type, RoomKey = room, Mode = (GameMode)(match?.Mode ?? 0), MapHash = _mapHash,
                Players = players, Bootstrap = new ReplayBootstrap { Packets = packets }
            };
        }

        internal static void AcceptedMatch(in MatchStatePacket state)
        {
            if (DemoPlayback.IsActive) return;
            Recorder.AcceptMatch(state, NetSession.NetFrame);
            if (NetSession.ServerSession is { } configuration)
                Recorder.AcceptConfiguration(configuration, NetSession.NetFrame);
        }

        internal static void AcceptedConfiguration(in SessionStatePacket state)
        {
            if (!DemoPlayback.IsActive) Recorder.AcceptConfiguration(state, NetSession.NetFrame);
        }

        internal static void AcceptedIntent(int slot, in IntentPacket intent)
        {
            if (!DemoPlayback.IsActive) Recorder.AcceptIntent(slot, intent, NetSession.NetFrame);
        }

        internal static void AcceptedRoster(in RosterPacket roster)
        {
            if (!DemoPlayback.IsActive) Recorder.AcceptRoster(roster, NetSession.NetFrame);
        }

        internal static void AcceptedSnapshot(ReadOnlySpan<byte> packet, uint tick)
        {
            if (!DemoPlayback.IsActive) Recorder.AcceptSnapshot(packet, NetSession.NetFrame, tick);
        }

        internal static void AcceptWorldPacket(ReadOnlySpan<byte> payload)
        {
            if (AuthorityWire.Accept(payload, NetSession.CurrentMatchId, NetSession.AuthorityEpoch) is { } world) AcceptWorld(world);
        }
        private static void AcceptWorld(ReplayAuthorityWorld world) => AcceptWorld(world, world.Encode());
        private static void AcceptWorld(ReplayAuthorityWorld world, ReadOnlySpan<byte> encoded, bool send = false)
        {
            var old = LatestAuthorityWorld;
            if (old != null && (old.MatchId != world.MatchId || old.Epoch != world.Epoch)) old = null;
            Recorder.AcceptWorld(world, NetSession.NetFrame, encoded, send);
            void Marker(ReplayMarkerKind kind, int actor = 255, int target = 255, int value = 0)
                => Recorder.Marker(NetSession.NetFrame, world.Tick, new(kind, (byte)actor, (byte)target, value));
            if (old != null)
            {
                if (world.Prime != old.Prime) Marker(ReplayMarkerKind.PrimeChange, world.Prime.Slot, old.Prime.Slot);
                for (int i = 0; i < 8; i++)
                {
                    if (world.FlagScores[i] > old.FlagScores[i]) Marker(ReplayMarkerKind.FlagCapture, i, value: world.FlagScores[i]);
                    if (world.NodesCaptured[i] > old.NodesCaptured[i]) Marker(ReplayMarkerKind.NodeCapture, i, value: world.NodesCaptured[i]);
                    int goal = NetSession.ServerMatch?.PointGoal ?? 0;
                    if (goal > 1 && world.TeamPoints[i] == goal - 1 && old.TeamPoints[i] < goal - 1)
                        Marker(ReplayMarkerKind.MatchPoint, value: i);
                }
                if (old.EndCause == ReplayEndCause.None && world.EndCause != ReplayEndCause.None)
                    Recorder.Marker(NetSession.NetFrame, world.Tick, new(ReplayMarkerKind.MatchEnd,
                        world.EndingKill?.KillerSlot ?? 255, world.EndingKill?.VictimSlot ?? 255,
                        (int)world.EndCause, world.EndingKill));
            }
            LatestAuthorityWorld = world;
        }
        private static void ClassifyEnd(Scene scene, ReplayAuthorityWorld world)
        {
            if (world.Phase == MatchState.InProgress) return;
            if (LatestAuthorityWorld is { EndCause: not ReplayEndCause.None } previous
                && previous.MatchId == world.MatchId && previous.Epoch == world.Epoch)
            { world.EndCause = previous.EndCause; world.EndingKill = previous.EndingKill; return; }
            world.EndCause = ReplayEndCause.Other;
            if (scene.GameState.ForceEndGame) return;
            bool combat = scene.GameState.Mode is GameMode.Battle or GameMode.BattleTeams or GameMode.InstaGib
                or GameMode.Survival or GameMode.SurvivalTeams;
            if (combat && _authorityKill is { } kill && kill.MatchId == world.MatchId && kill.AuthorityEpoch == world.Epoch
                && world.Tick >= kill.ServerTick && world.Tick - kill.ServerTick <= 1
                && (!scene.GameState.Teams || scene.Players.Items[kill.KillerSlot].TeamIndex != scene.Players.Items[kill.VictimSlot].TeamIndex)
                && (scene.GameState.Mode is GameMode.Survival or GameMode.SurvivalTeams
                    && scene.GameState.TeamDeaths[scene.Players.Items[kill.VictimSlot].TeamIndex] > scene.GameState.PointGoal
                    || scene.GameState.Mode is GameMode.Battle or GameMode.BattleTeams or GameMode.InstaGib
                    && scene.GameState.TeamPoints[scene.Players.Items[kill.KillerSlot].TeamIndex] >= scene.GameState.PointGoal))
            { world.EndCause = ReplayEndCause.Kill; world.EndingKill = kill; }
            else if (LatestAuthorityWorld is { Phase: MatchState.InProgress, MatchTime: > 0 and <= 0.12f })
                world.EndCause = ReplayEndCause.Time;
            else if (!combat) world.EndCause = ReplayEndCause.Objective;
        }

        public static void Event(ReplayEventType type, int actor = -1, int target = -1, int value = 0)
        {
            if (!NetSession.Active || DemoPlayback.IsActive) return;
            byte Actor(int slot) => slot is >= 0 and < RosterPacket.MaxSlots ? (byte)slot : byte.MaxValue;
            ReplayMarkerKind? marker = type switch
            {
                ReplayEventType.PlayerSpawn => ReplayMarkerKind.Spawn,
                ReplayEventType.PlayerDeath => ReplayMarkerKind.Death,
                ReplayEventType.Damage => ReplayMarkerKind.Damage,
                ReplayEventType.ScoreChanged => ReplayMarkerKind.Score,
                ReplayEventType.PlayerJoined => ReplayMarkerKind.Join,
                ReplayEventType.PlayerLeft => ReplayMarkerKind.Leave,
                // Objective markers are derived once from accepted authority
                // counters below, never duplicated by speculative entity contacts.
                ReplayEventType.MatchEnded => ReplayMarkerKind.MatchEnd,
                ReplayEventType.MatchStarted => ReplayMarkerKind.MatchStart,
                _ => null
            };
            if (marker is { } kind) Recorder.Marker(NetSession.NetFrame,
                Recorder.Timeline.LastServerTick ?? NetSession.NetFrame, new(kind, Actor(actor), Actor(target), value));
        }

        // Called where authoritative state is accepted, after normal validation. Annotations
        // describe confirmed transitions, never inferred projectile hits or local predictions.
        public static void AcceptedState(in PlayerState state, uint? authoritativeFrame = null)
        {
            int slot = state.SlotIndex;
            if (DemoPlayback.IsActive || slot >= Known.Length) return;
            if (Known[slot] && Previous[slot].SlotGeneration == state.SlotGeneration)
            {
                var old = Previous[slot];
                if (old.Points != state.Points) Event(ReplayEventType.ScoreChanged, slot, value: state.Points);
                if ((old.Flags & PlayerState.FlagSpawned) == 0 && (state.Flags & PlayerState.FlagSpawned) != 0)
                    Event(ReplayEventType.PlayerSpawn, slot);
                if (state.Deaths > old.Deaths)
                {
                    Event(ReplayEventType.PlayerDeath, slot, state.AttackerSlot);
                    if (state.AttackerSlot < RosterPacket.MaxSlots && state.AttackerSlot != slot)
                        Event(ReplayEventType.Kill, state.AttackerSlot, slot);
                    ushort attackerGeneration = 0;
                    for (int i = 0; i < PlayerState.DamageHistory; i++)
                    {
                        var damage = state.EventAt(i);
                        if (damage.EventId == state.DamageEventId && damage.AttackerSlot == state.AttackerSlot)
                        { attackerGeneration = damage.AttackerGeneration; break; }
                    }
                    uint tick = authoritativeFrame ?? NetSession.NetFrame;
                    var identity = new ReplayKillIdentity(NetSession.CurrentMatchId, NetSession.AuthorityEpoch,
                        tick, state.DamageEventId, state.AttackerSlot, attackerGeneration,
                        state.SlotIndex, state.SlotGeneration, state.LifeId);
                    // A later-life snapshot or a jump in cumulative deaths does not
                    // identify the exact death. Keep the coarse Studio annotation,
                    // but never advertise it as a fenced killcam candidate.
                    if (state.LifeId == old.LifeId && state.Health == 0
                        && state.Deaths == old.Deaths + 1 && attackerGeneration != 0
                        && state.AttackerSlot < RosterPacket.MaxSlots && state.AttackerSlot != slot)
                    {
                        var marker = new ReplayMarker(ReplayMarkerKind.Kill,
                            state.AttackerSlot, state.SlotIndex, Kill: identity,
                            Weapon: state.DamageBeam, DamageFlags: state.DamageFlags);
                        Recorder.Marker(NetSession.NetFrame, tick, marker);
                        if (NetSession.IsAuthority) _authorityKill = identity;
                        Mods.KillCam.NoteKill(marker, NetSession.NetFrame);
                    }
                }
                if (old.DamageEventId != state.DamageEventId)
                {
                    Event(ReplayEventType.Damage, state.AttackerSlot, slot, Math.Max(0, old.Health - state.Health));
                    if ((state.DamageFlags & (byte)MphRead.Entities.DamageFlags.Headshot) != 0 && state.AttackerSlot < 8)
                        Recorder.Marker(NetSession.NetFrame, authoritativeFrame ?? NetSession.NetFrame,
                            new(ReplayMarkerKind.Headshot, state.AttackerSlot, state.SlotIndex,
                                Weapon: state.DamageBeam, DamageFlags: state.DamageFlags));
                }
            }
            Previous[slot] = state; Known[slot] = true;
        }
    }
}
