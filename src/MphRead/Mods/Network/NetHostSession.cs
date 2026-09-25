using System;
using System.Diagnostics;
using MphRead.Entities;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// "Host: this computer" implemented as a real local dedicated server
    /// process and a normal client connection to it.
    ///
    /// This used to run DedicatedServer on a thread in the player's process.
    /// NetSession is static, so that server could not own a second simulation
    /// and promoted the local player to authority. That made the host the only
    /// player with zero-latency authoritative hit resolution. A child process
    /// has its own NetSession, so the server simulates and the host is an
    /// ordinary client like everybody else.
    /// </summary>
    public static class NetHostSession
    {
        private static Process? _process;

        public static bool Running
        {
            get
            {
                try { return _process != null && !_process.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
        }

        /// <summary>Why the server would not start, for the screen to show.</summary>
        public static string? LastError { get; private set; }

        /// <param name="listing">
        /// Where to announce this game, or null to keep it off every list.
        /// </param>
        public static bool StartAndJoin(int port, string playerName, Hunter hunter,
            string roomKey, GameMode mode, float timeLimit, int pointGoal,
            int maxPlayers = PlayerEntity.SlotCapacity,
            (string Host, int Port, string Name)? listing = null)
        {
            Stop();
            LastError = null;

            string serverName = listing?.Name ?? $"{playerName}'s game";
            string masterHost = listing?.Host ?? NetMasterConfig.DefaultHost;
            int masterPort = listing?.Port ?? NetMasterConfig.DefaultPort;
            var rotation = new[] { (roomKey, mode) };
            int started = LocalServer.Start(serverName, rotation, maxPlayers,
                timeLimit, pointGoal, masterHost, masterPort,
                listed: listing != null,
                requestedPort: port,
                friendlyFire: GameState.FriendlyFire,
                shadowFreeze: GameState.ShadowFreeze,
                affinityWeapons: GameState.AffinityWeapons,
                spawnProtection: GameState.SpawnProtection);
            if (started < 0 || LocalServer.Running == null)
            {
                LastError = LocalServer.LastError ?? "the local server would not start";
                return false;
            }
            _process = LocalServer.Running;
            if (!NetLaunch.Join("127.0.0.1", started, playerName, hunter))
            {
                LastError = "the local server started but the client could not join it";
                Stop();
                return false;
            }
            return true;
        }

        public static void Stop()
        {
            Process? process = _process;
            _process = null;
            if (process == null)
            {
                return;
            }
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
            }
            catch (Exception)
            {
                // Already gone, or the operating system reaped it first.
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
