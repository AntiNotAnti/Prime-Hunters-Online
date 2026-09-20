using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// A hosted lobby in its own server process.
    ///
    /// NetSession is intentionally static, so an in-process HostPool can only
    /// simulate the process's primary match. The old fallback made the first
    /// joining player authoritative for every extra lobby, giving that player
    /// zero-latency hit resolution. A child process gives each hosted match a
    /// separate NetSession and therefore a server authority of its own.
    /// </summary>
    internal sealed class HostedServerProcess : IDisposable
    {
        private readonly Process _process;
        private int _players;
        private double _nextProbe;

        public int Port { get; }
        public bool EverOccupied { get; private set; }
        public bool Running
        {
            get
            {
                try { return !_process.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
        }

        private HostedServerProcess(Process process, int port)
        {
            _process = process;
            Port = port;
        }

        public static HostedServerProcess? Start(int port, in HostRequestPacket request,
            string name, string masterHost, int masterPort, bool listed,
            Guid ownerToken, out string reason)
        {
            GameMode mode = Enum.IsDefined(typeof(GameMode), request.Mode)
                ? (GameMode)request.Mode
                : GameMode.Battle;
            IReadOnlyList<(string RoomKey, GameMode Mode)> maps;
            if (request.Rotation != null && request.Rotation.Count > 0)
            {
                maps = request.Rotation;
            }
            else
            {
                maps = new[] { (request.RoomKey, mode) };
            }

            int started = LocalServer.Start(name, maps,
                Math.Clamp((int)request.MaxPlayers, 2, MphRead.Entities.PlayerEntity.SlotCapacity),
                request.TimeLimit, request.PointGoal,
                masterHost, masterPort, listed,
                CancellationToken.None,
                lobby: request.Policy == ServerSessionPolicy.Lobby,
                requestedPort: port,
                ownerToken: ownerToken,
                format: request.Format,
                requireReady: request.RequireReady,
                allowJoinInProgress: request.AllowJoinInProgress,
                waitUntilReady: false);
            if (started < 0 || LocalServer.Running == null)
            {
                reason = LocalServer.LastError ?? $"could not start server on port {port}";
                return null;
            }
            reason = "";
            return new HostedServerProcess(LocalServer.Running, started);
        }

        /// <summary>
        /// Read occupancy from the child's ordinary status endpoint. Probes
        /// are throttled because Reap runs every server loop; loopback replies
        /// normally arrive immediately, and a transient timeout keeps the last
        /// known occupancy instead of ejecting a live match.
        /// </summary>
        public int ProbePlayers(double now, bool force = false)
        {
            if (!Running)
            {
                _players = 0;
                return 0;
            }
            if (!force && now < _nextProbe)
            {
                return _players;
            }
            _nextProbe = now + 1.0;
            ServerStatus status = NetStatus.Query("127.0.0.1", Port,
                allowJoinProbe: false, timeoutMs: 150);
            if (status.Online)
            {
                _players = status.Players;
                if (_players > 0)
                {
                    EverOccupied = true;
                }
            }
            return _players;
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(2000);
                }
            }
            catch (Exception)
            {
                // Already gone, or the operating system reaped it first.
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}
