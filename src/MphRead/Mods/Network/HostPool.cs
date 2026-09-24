using System;
using System.Collections.Generic;
using System.Net;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// The matches a machine is running on somebody else's behalf, on ports
    /// of its own.
    ///
    /// Lifted out of <see cref="MasterServer"/>, which is where it used to
    /// live and where it did not belong. Starting a match on a box is a
    /// property of *being that box*, not of being the directory: a player who
    /// wants their game hosted in Tokyo is asking Tokyo, and only a process
    /// running there can spawn a server there. Tying it to the directory
    /// meant either one region could host, or every region had to run a
    /// second directory that listed nothing and existed purely to be asked --
    /// which is a component invented to satisfy a layering mistake.
    ///
    /// So an ordinary <see cref="DedicatedServer"/> carries one of these and
    /// answers <see cref="PacketType.HostRequest"/> on its own port. There is
    /// still exactly one directory in the world; it keeps the list, and every
    /// server on that list can be asked to open a game beside itself.
    ///
    /// Each match it starts runs in a child server process. NetSession is
    /// static, so an extra match cannot share this process's simulation, but
    /// making a player authoritative is not an acceptable fallback: it gives
    /// that player zero-latency hit resolution. One process per hosted match
    /// gives every lobby its own server authority instead.
    /// </summary>
    public sealed class HostPool
    {
        private sealed class Hosted
        {
            public HostedServerProcess Process = null!;
            public int Port;
            public string Name = "";
            public double StartedAt;
            /// <summary>When it last had anybody in it, so an abandoned game can be reaped.</summary>
            public double LastOccupied;
        }

        private readonly List<Hosted> _hosted = new();
        private readonly Dictionary<int, double> _cooling = new();
        private int _first;
        private int _last = -1;

        /// <summary>Where this pool's log lines go, so a server and a directory read alike.</summary>
        public Action<string> Log { get; init; } = _ => { };

        /// <summary>
        /// Announce a hosted match to a directory, if this machine reports to
        /// one. The games a *server* starts have to be findable the same way
        /// the server itself is.
        /// </summary>
        public Func<MasterReporter?>? ReporterFactory { get; init; }

        /// <summary>Told when a game stops, so its listing can be dropped at once.</summary>
        public Action<int>? OnStopped { get; init; }

        /// <summary>See <see cref="MasterServer"/> for why a freed port waits.</summary>
        private const double PortCooldownSeconds = 5;

        /// <summary>How long a game may sit empty before anybody has ever joined.</summary>
        private const double StartupSeconds = 180;

        /// <summary>And how long once it has been played and everyone has left.</summary>
        private const double EmptySeconds = 45;

        public void SetPorts(int first, int last)
        {
            _first = first;
            _last = last;
        }

        public bool CanHost => _last >= _first && _first > 0;

        public int Count => _hosted.Count;

        public string Describe() => CanHost
            ? $"can start games on ports {_first}-{_last} for players who cannot open one of their own"
            : "not starting games for anybody (no host port range)";

        /// <summary>
        /// Start a match for somebody, and say where it is listening.
        /// </summary>
        public HostReplyPacket Start(HostRequestPacket request, IPEndPoint asker, double now)
        {
            // Do not treat a public IP address as a player identity. Multiple
            // people behind one NAT legitimately share it, so replacing an
            // empty game merely because the next request came from that IP can
            // destroy somebody else's lobby. Abandoned games are bounded by
            // the normal startup/empty reaper instead.
            int port = FreePort(now);
            if (port < 0)
            {
                return new HostReplyPacket
                {
                    Reason = $"all {_last - _first + 1} game slots are busy"
                };
            }
            GameMode mode = Enum.IsDefined(typeof(GameMode), request.Mode)
                ? (GameMode)request.Mode
                : GameMode.Battle;
            string name = request.ServerName.Length > 0 ? request.ServerName : "Hosted game";
            Guid ownerToken = request.Policy == ServerSessionPolicy.Lobby
                ? new Guid(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))
                : Guid.Empty;
            MasterReporter? reporter = ReporterFactory?.Invoke();
            HostedServerProcess? process = HostedServerProcess.Start(port, request, name,
                reporter?.Host ?? NetMasterConfig.DefaultHost,
                reporter?.Port ?? NetMasterConfig.DefaultPort,
                listed: reporter != null, ownerToken, out string reason);
            if (process == null)
            {
                return new HostReplyPacket { Reason = reason };
            }
            var entry = new Hosted
            {
                Process = process,
                Port = port,
                Name = name,
                StartedAt = now,
                LastOccupied = now
            };
            _hosted.Add(entry);
            int mapCount = request.Rotation != null && request.Rotation.Count > 0
                ? request.Rotation.Count : 1;
            Log($"started \"{name}\" on port {port} for {asker.Address} "
                + $"({request.RoomKey}, {mode}, {mapCount} map(s), server authority)");
            return new HostReplyPacket { Started = true, Port = (ushort)port,
                Reason = "", OwnerToken = ownerToken };
        }

        private int FreePort(double now)
        {
            for (int port = _first; port <= _last; port++)
            {
                bool taken = false;
                for (int i = 0; i < _hosted.Count; i++)
                {
                    if (_hosted[i].Port == port)
                    {
                        taken = true;
                        break;
                    }
                }
                if (taken)
                {
                    continue;
                }
                if (_cooling.TryGetValue(port, out double freedAt))
                {
                    if (now - freedAt < PortCooldownSeconds)
                    {
                        continue;
                    }
                    _cooling.Remove(port);
                }
                if (!LocalServer.PortAvailable(port))
                {
                    continue;
                }
                return port;
            }
            return -1;
        }

        /// <summary>
        /// Shut down games nobody is playing. Without this a machine left
        /// running for a week has every port allocated to a match that ended
        /// on Tuesday.
        /// </summary>
        public void Reap(double now)
        {
            for (int i = _hosted.Count - 1; i >= 0; i--)
            {
                Hosted entry = _hosted[i];
                if (!entry.Process.Running)
                {
                    Stop(entry, "server process exited", now);
                    continue;
                }
                if (entry.Process.ProbePlayers(now) > 0)
                {
                    entry.LastOccupied = now;
                    continue;
                }
                bool played = entry.Process.EverOccupied;
                double grace = played ? EmptySeconds : StartupSeconds;
                if (now - entry.LastOccupied > grace)
                {
                    Stop(entry, played ? "everyone left" : "nobody joined", now);
                }
            }
        }

        public void StopAll(string why)
        {
            for (int i = _hosted.Count - 1; i >= 0; i--)
            {
                Stop(_hosted[i], why);
            }
        }

        private void Stop(Hosted entry, string why, double now = 0)
        {
            Log($"stopping \"{entry.Name}\" on port {entry.Port}: {why}");
            entry.Process.Dispose();
            _hosted.Remove(entry);
            _cooling[entry.Port] = now;
            OnStopped?.Invoke(entry.Port);
        }
    }
}
