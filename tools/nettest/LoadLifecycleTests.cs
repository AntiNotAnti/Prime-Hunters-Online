using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MphRead.Mods;
using MphRead.Mods.Network;
using MphRead.Mods.Multiplayer;

namespace MphRead.NetTest;

internal static class LoadLifecycleTests
{
    public static int Run()
    {
        try
        {
            StartAnnouncementBurst();

            SpectatorMode.Reset();
            SpectatorMode.SetSessionPreference(true);
            SpectatorMode.Reset(preservePreference: true);
            NetArchitectureTests.Check(SpectatorMode.PreferSpectator,
                "per-match reset preserves spectator session role");
            SpectatorMode.Reset();
            NetArchitectureTests.Check(!SpectatorMode.PreferSpectator,
                "full session reset clears spectator role");

            foreach (double lag in new[] { 0.0, .1, 2, 10 })
            {
                var start = new NetMatchStart(); start.Begin(42, 9, 255); var identity = start.Identity;
                start.MarkLoaded(0, identity);
                NetArchitectureTests.Check(!start.Advance(1), "server preparation gates countdown");
                start.AuthorityReady(1);
                for (int slot = 1; slot < 7; slot++) start.MarkLoaded(slot, identity);
                NetArchitectureTests.Check(!start.MarkLoaded(7, identity with { MatchId = 41 })
                    && !start.MarkLoaded(7, identity with { AuthorityEpoch = 8 })
                    && !start.MarkLoaded(7, identity with { StartGeneration = 99 }), "load identity fenced");
                NetArchitectureTests.Check(!start.Advance(1 + lag), "slow participant holds loading barrier");
                NetArchitectureTests.Check(start.MarkLoaded(7, identity), "last healthy participant accepted");
                NetArchitectureTests.Check(!start.MarkLoaded(7, identity), "duplicate loaded idempotent");
                NetArchitectureTests.Check(start.Advance(1 + lag) && start.Stage == StartStage.Synchronizing, "loaded only enters synchronization");
                NetArchitectureTests.Check(!start.Advance(1 + lag), "bootstrap gates countdown");
                for (int slot = 0; slot < 8; slot++) start.MarkWorldReady(slot, identity);
                NetArchitectureTests.Check(start.Advance(1 + lag) && start.Stage == StartStage.Countdown, "world readiness releases countdown");
                NetArchitectureTests.Check(!start.Advance(2.49 + lag), "no early simulation");
                NetArchitectureTests.Check(start.Advance(2.5 + lag) && start.Stage == StartStage.InMatch, "one authoritative start boundary");
                start.Begin(43, 9, 255);
                NetArchitectureTests.Check(start.Identity.StartGeneration != identity.StartGeneration && !start.MarkLoaded(0, identity), "rematch refuses old ACK");
            }
            var missing = new NetMatchStart(); missing.Begin(1, 1, 7); missing.AuthorityReady(0);
            missing.MarkLoaded(0, missing.Identity); missing.MarkLoaded(1, missing.Identity);
            NetArchitectureTests.Check(missing.MissingAtSlowDeadline(14.9) == 0
                && missing.MissingAtSlowDeadline(15) == 7, "15 seconds flags a slow loader without dropping it");
            NetArchitectureTests.Check(missing.MissingAtDeadline(59.9) == 0
                && missing.MissingAtDeadline(60) == 7, "60 second hard deadline identifies a stuck participant");
            missing.Remove(2); missing.Advance(60);
            missing.MarkWorldReady(0, missing.Identity); missing.MarkWorldReady(1, missing.Identity); missing.Advance(60);
            NetArchitectureTests.Check(missing.Expected == 3 && missing.Stage == StartStage.Countdown, "hard-timeout removal preserves healthy participants");
            missing.Remove(0);
            NetArchitectureTests.Check(missing.Expected == 2 && missing.Loaded == 2, "owner disconnect cannot poison countdown");
            NetArchitectureTests.Check(!missing.MarkLoaded(3, missing.Identity), "new join cannot enlarge frozen barrier");
            var bytes = new byte[MatchLoadedPacket.Size];
            new MatchLoadedPacket(42, 9, 17).Write(bytes);
            NetArchitectureTests.Check(MatchLoadedPacket.TryRead(bytes, out var loaded)
                && loaded.Identity == new MatchStartIdentity(42, 9, 17), "load wire identity");
            for (int size = 0; size < bytes.Length; size++)
                NetArchitectureTests.Check(!MatchLoadedPacket.TryRead(bytes.AsSpan(0, size), out _), "truncated load rejected");

            var commitBytes = new byte[MatchStartCommitPacket.Size];
            new MatchStartCommitPacket(42, 9, 17, 1234).Write(commitBytes);
            NetArchitectureTests.Check(MatchStartCommitPacket.TryRead(commitBytes, out var commit)
                && commit.Identity == loaded.Identity && commit.RemainingMilliseconds == 1234,
                "fresh start commitment round trip");
            NetArchitectureTests.Check(!NetReliableChannel.IsReliable(PacketType.MatchStartCommit),
                "start commitments stay disposable so retransmits cannot carry stale remaining time");
            NetArchitectureTests.Check(!NetMatchStart.ClientReleaseReady(StartStage.Loading, 10, 11)
                && !NetMatchStart.ClientReleaseReady(StartStage.Countdown, 10, 9.99)
                && NetMatchStart.ClientReleaseReady(StartStage.Countdown, 10, 10),
                "client releases on the committed countdown edge");

            var progressBytes = new byte[MatchLoadProgressPacket.Size];
            new MatchLoadProgressPacket(42, 9, 17, MatchLoadStage.PresentationLoad).Write(progressBytes);
            NetArchitectureTests.Check(MatchLoadProgressPacket.TryRead(progressBytes, out var progress)
                && progress.Identity == loaded.Identity && progress.Stage == MatchLoadStage.PresentationLoad,
                "load progress identity/stage round trip");

            Console.WriteLine("PASS: ready barrier, soft/hard loading, synchronized commitment, generation, failures, disconnect and rematch");
            return NetLobbyTest.Run();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void StartAnnouncementBurst()
    {
        using var server = new NetTransport(0);
        using var wire = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        wire.Client.ReceiveTimeout = 2000;
        var clientAddress = (IPEndPoint)wire.Client.LocalEndPoint!;
        var serverAddress = new IPEndPoint(IPAddress.Loopback, server.LocalPort);
        server.Send(clientAddress, PacketType.Welcome, new byte[17]);
        byte[] Read(PacketType type)
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            for (int i = 0; i < 100; i++)
            {
                byte[] bytes = wire.Receive(ref from);
                if (NetHeader.TryRead(bytes, out var header) && header.Type == type) return bytes;
            }
            throw new Exception("Expected startup datagram missing");
        }
        byte[] welcome = Read(PacketType.Welcome);
        NetHeader.TryRead(welcome, out var admission);
        var receiver = new NetConnection(serverAddress, admission.ConnectionId);
        receiver.Receive(admission, 0);
        void Ack()
        {
            var bytes = new byte[NetHeader.Size];
            receiver.Send(0, 0, NetHeaderFlags.AckOnly).Write(bytes);
            wire.Send(bytes, serverAddress);
        }
        Ack();
        NetArchitectureTests.Check(SpinWait.SpinUntil(() => server.ReliableStats(clientAddress)?.Pending == 0, 2000),
            "welcome acknowledged without application pumping");

        var state = new SessionStatePacket { Phase = SessionPhase.Starting, MatchId = 42,
            AuthorityEpoch = 9, StartGeneration = 17, StartStage = StartStage.Preparing,
            MaxPlayers = 8, OwnerSlot = 255, WorldProfile = MatchWorldProfile.Resolve(8),
            Match = new MatchDefinition { RoomKey = "test", Mode = MphRead.GameMode.Battle } };
        var payload = new byte[SessionStatePacket.Size]; state.Write(payload);
        server.Send(clientAddress, PacketType.SessionState, payload);
        byte[] lost = Read(PacketType.SessionState); // Deliberately drop the original notice.
        uint eventId = BinaryPrimitives.ReadUInt32LittleEndian(lost.AsSpan(NetHeader.Size));
        server.Send(clientAddress, PacketType.SessionState, payload, immediateCopies: 3);
        int applications = 0;
        var sequences = new System.Collections.Generic.HashSet<uint>();
        // No server Drain or simulation step: the entire burst is on the wire
        // before synchronous authority construction can block its owner.
        for (int i = 0; i < 3; i++)
        {
            byte[] bytes = Read(PacketType.SessionState);
            NetHeader.TryRead(bytes, out var header);
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(NetHeader.Size));
            NetArchitectureTests.Check(id == eventId && sequences.Add(header.Sequence),
                "burst retains one event identity with independent datagram sequences");
            receiver.Receive(header, 1);
            if (receiver.Reliable.Receive(id))
            {
                applications++;
                NetArchitectureTests.Check(SessionStatePacket.TryRead(bytes.AsSpan(NetHeader.Size + 4), out var received)
                    && received.MatchId == 42 && received.StartGeneration == 17,
                    "lost initial announcement still delivers frozen startup identity");
            }
        }
        Ack();
        NetArchitectureTests.Check(applications == 1
            && SpinWait.SpinUntil(() => server.ReliableStats(clientAddress)?.Pending == 0, 2000),
            "startup burst applies once and ACK of a surviving copy completes delivery");
    }
}
