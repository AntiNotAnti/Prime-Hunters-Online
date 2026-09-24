using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MphRead.Mods.Network;

namespace MphRead.Mods.Launcher
{
    /// <summary>
    /// One directory row after this machine has asked that server directly.
    /// Presentation-neutral: no Avalonia types and no assumptions about how a
    /// browser lays rows out.
    /// </summary>
    public readonly record struct ServerBrowserEntry(
        MasterListing Listing,
        ServerStatus Status)
    {
        public string Name => Status.ServerName.Length > 0
            ? Status.ServerName
            : Listing.ServerName.Length > 0 ? Listing.ServerName : Listing.Endpoint;

        public string Endpoint => Listing.Endpoint;
        public bool Live => Status.Online;
        public bool Compatible => Status.Protocol == 0
            || Status.Protocol == NetConfig.ProtocolVersion;
    }

    public readonly record struct ServerDiscoveryResult(
        bool DirectoryAnswered,
        int Listed,
        int Live,
        string Message);

    public readonly record struct OnlineJoinResult(
        bool Joined,
        LaunchPlan Plan,
        string Error);

    public readonly record struct QuickPlaySearchResult(
        bool Found,
        ServerBrowserEntry Entry,
        ServerDiscoveryResult Discovery,
        string Message);

    public readonly record struct OnlinePopulationResult(
        bool DirectoryAnswered,
        int Players,
        int Servers);

    /// <summary>
    /// Application-layer server discovery and joining shared by every launcher
    /// presentation. The callbacks deliberately run off the UI thread; a GUI
    /// renderer marshals them onto its own dispatcher.
    /// </summary>
    public static class ServerBrowserService
    {
        public static async Task<ServerDiscoveryResult> DiscoverAsync(
            Action<ServerBrowserEntry>? onEntry = null,
            CancellationToken cancellationToken = default)
        {
            string host = LauncherPrefs.MasterHost;
            int port = LauncherPrefs.MasterPort;
            MasterListResult result;
            try
            {
                result = await Task.Run(
                    () => NetMasterClient.Query(host, port), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new(false, 0, 0, "Cancelled.");
            }

            if (cancellationToken.IsCancellationRequested)
                return new(false, 0, 0, "Cancelled.");

            if (!result.Answered)
            {
                return new(false, 0, 0,
                    $"The directory at {host}:{port} did not answer.");
            }

            IReadOnlyList<MasterListing> listed =
                result.Servers ?? Array.Empty<MasterListing>();
            if (listed.Count == 0)
            {
                return new(true, 0, 0,
                    "The directory is online and has no servers listed.");
            }

            Task<ServerBrowserEntry>[] jobs = listed.Select(listing => Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ServerStatus status = NetStatus.Query(listing.Address, listing.Port,
                    allowJoinProbe: false);
                if (!status.Online && !cancellationToken.IsCancellationRequested)
                {
                    // Directory removal and browser refresh are independent UDP
                    // exchanges. Confirm a dead row once before hiding it so a
                    // single lost status reply does not erase a healthy server.
                    Thread.Sleep(100);
                    cancellationToken.ThrowIfCancellationRequested();
                    status = NetStatus.Query(listing.Address, listing.Port,
                        allowJoinProbe: false, timeoutMs: 350);
                }
                var entry = new ServerBrowserEntry(listing, status);
                // A directory row is only a discovery hint. Once the endpoint
                // itself has twice confirmed that it is gone, do not render a
                // ghost lobby while the master's expiry/farewell catches up.
                if (entry.Live && !cancellationToken.IsCancellationRequested)
                    onEntry?.Invoke(entry);
                return entry;
            }, cancellationToken)).ToArray();

            ServerBrowserEntry[] entries;
            try
            {
                entries = await Task.WhenAll(jobs);
            }
            catch (OperationCanceledException)
            {
                return new(true, listed.Count, 0, "Cancelled.");
            }

            int live = entries.Count(e => e.Live);
            return new(true, listed.Count, live,
                live == 1
                    ? "1 server answered."
                    : $"{live} of {listed.Count} servers answered.");
        }

        public static async Task<OnlinePopulationResult> CountOnlinePlayersAsync(
            CancellationToken cancellationToken = default)
        {
            var live = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            ServerDiscoveryResult discovery = await DiscoverAsync(entry =>
            {
                if (entry.Live && entry.Compatible)
                    live[entry.Endpoint] = Math.Max(0, entry.Status.Players);
            }, cancellationToken);

            return new OnlinePopulationResult(
                discovery.DirectoryAnswered,
                live.Values.Sum(),
                live.Count);
        }

        public static async Task<QuickPlaySearchResult> FindBestAsync(
            CancellationToken cancellationToken = default)
        {
            var found = new ConcurrentBag<ServerBrowserEntry>();
            ServerDiscoveryResult discovery = await DiscoverAsync(
                entry => found.Add(entry), cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return new(false, default, discovery, "Cancelled.");

            ServerBrowserEntry[] candidates = found
                .Where(entry => entry.Live
                    && entry.Compatible
                    && (entry.Status.MaxPlayers <= 0
                        || entry.Status.Players < entry.Status.MaxPlayers))
                .OrderBy(entry => entry.Status.Latency < 0
                    ? Int32.MaxValue : entry.Status.Latency)
                .ThenByDescending(entry => entry.Status.Players)
                .ToArray();

            if (candidates.Length == 0)
            {
                return new(false, default, discovery,
                    discovery.DirectoryAnswered
                        ? "No compatible open server answered."
                        : discovery.Message);
            }

            ServerBrowserEntry best = candidates[0];
            string ping = best.Status.Latency >= 0
                ? $"{best.Status.Latency} ms"
                : "latency unavailable";
            return new(true, best, discovery,
                $"{best.Name} · {ping}");
        }

        public static Task<ServerStatus> ProbeAsync(string host, int port,
            bool allowJoinProbe = true, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return NetStatus.Query(host, port, allowJoinProbe);
            }, cancellationToken);
        }

        public static async Task<OnlineJoinResult> JoinAsync(
            string host, int port, string playerName, Hunter hunter, int suit,
            CancellationToken cancellationToken = default)
        {
            host = host.Trim();
            playerName = playerName.Trim();
            if (host.Length == 0)
                return new(false, default, "Enter a server address.");
            if (playerName.Length == 0)
                playerName = "Player";

            suit = Math.Clamp(suit, 0, 3);
            LauncherPrefs.PlayerName = playerName;
            LauncherPrefs.LastHunter = hunter;
            LauncherPrefs.LastColor = suit;
            LauncherPrefs.ServerAddress = host;
            LauncherPrefs.ServerPort = port;
            LauncherPrefs.LastKind = (int)LaunchKind.Online;
            LauncherPrefs.Save();

            bool joined;
            try
            {
                joined = await Task.Run(() => NetLaunch.Connect(host, port,
                    playerName, hunter, color: suit,
                    cancellationToken: cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                joined = false;
            }

            if (!joined)
            {
                string error = cancellationToken.IsCancellationRequested
                    ? "Join cancelled."
                    : NetLaunch.LastJoinError;
                NetSession.Stop();
                return new(false, default, error);
            }

            return new(true, new LaunchPlan
            {
                Kind = LaunchKind.Online,
                Hunter = hunter,
                PlayerName = playerName,
                RoomKey = "",
                Mode = GameMode.Battle,
                Port = port
            }, "");
        }

        /// <summary>Parse host or host:port without mutating the fallback on error.</summary>
        public static bool TryParseEndpoint(string text, string fallbackHost,
            int fallbackPort, out string host, out int port)
        {
            host = fallbackHost;
            port = fallbackPort;
            text = text.Trim();
            if (text.Length == 0)
                return false;

            int colon = text.LastIndexOf(':');
            if (colon > 0)
            {
                if (colon == text.Length - 1
                    || !Int32.TryParse(text[(colon + 1)..],
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out int parsed)
                    || parsed < 1 || parsed > 65535)
                    return false;
                host = text[..colon].Trim();
                port = parsed;
            }
            else
            {
                host = text;
            }
            return host.Length > 0 && !host.Contains(':');
        }
    }
}
