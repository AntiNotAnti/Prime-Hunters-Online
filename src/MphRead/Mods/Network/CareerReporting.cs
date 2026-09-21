using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MphRead.Entities;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// Durable authoritative match reporting.
    ///
    /// The dedicated server captures facts from the same simulation that owns
    /// damage and the scoreboard, writes an outbox record before touching the
    /// network, and posts it to Supabase in the background. A failed request
    /// leaves the file in place for the next retry/restart.
    /// </summary>
    internal static class CareerReportOutbox
    {
        private const string DefaultUrl =
            "https://hwcjaygoistufktorbmf.supabase.co/functions/v1/career-report";
        private const string PublishableKey =
            "sb_publishable_EVT45OPl638kA_j8vZ0ebg_sw3aWaVz";

        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        private static int _draining;
        private static int _warnedDisabled;

        private static string DirectoryPath => Path.Combine(
            Platform.AppPaths.UserDataDirectory, "career-outbox");

        private static string ReportUrl =>
            Environment.GetEnvironmentVariable("PROJECT_PRIME_CAREER_REPORT_URL")
            ?? DefaultUrl;

        private static string ServerKey =>
            Environment.GetEnvironmentVariable("PROJECT_PRIME_CAREER_SERVER_KEY")
            ?? "";

        public static bool Enabled => ServerKey.Length >= 32;

        public static void Start()
        {
            if (!Enabled)
            {
                if (Interlocked.Exchange(ref _warnedDisabled, 1) == 0)
                {
                    Console.WriteLine("[career] reporting disabled: "
                        + "PROJECT_PRIME_CAREER_SERVER_KEY is not configured");
                }
                return;
            }
            Kick();
        }

        public static void Enqueue(CareerMatchReport report)
        {
            if (!Enabled)
            {
                Start();
                return;
            }
            try
            {
                System.IO.Directory.CreateDirectory(DirectoryPath);
                string path = Path.Combine(DirectoryPath, $"{report.MatchId:N}.json");
                string temp = path + ".tmp";
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, Json);
                if (bytes.Length > 512 * 1024)
                {
                    Console.WriteLine($"[career] report {report.MatchId} exceeds the 512 KiB limit");
                    return;
                }
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
                Console.WriteLine($"[career] queued authoritative match {report.MatchId}");
                Kick();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[career] could not spool match {report.MatchId}: {ex.Message}");
            }
        }

        private static void Kick()
        {
            if (!Enabled || Interlocked.CompareExchange(ref _draining, 1, 0) != 0)
            {
                return;
            }
            _ = Task.Run(DrainAsync);
        }

        private static async Task DrainAsync()
        {
            try
            {
                System.IO.Directory.CreateDirectory(DirectoryPath);
                foreach (string path in System.IO.Directory.EnumerateFiles(
                    DirectoryPath, "*.json").OrderBy(p => p, StringComparer.Ordinal))
                {
                    byte[] bytes;
                    try { bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Console.WriteLine($"[career] could not read {Path.GetFileName(path)}: {ex.Message}");
                        continue;
                    }

                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post, ReportUrl);
                        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ServerKey);
                        request.Headers.TryAddWithoutValidation("apikey", PublishableKey);
                        request.Content = new ByteArrayContent(bytes);
                        request.Content.Headers.ContentType =
                            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                        using HttpResponseMessage response = await Http.SendAsync(request)
                            .ConfigureAwait(false);
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (response.IsSuccessStatusCode)
                        {
                            File.Delete(path);
                            Console.WriteLine($"[career] accepted {Path.GetFileNameWithoutExtension(path)}");
                            continue;
                        }

                        int status = (int)response.StatusCode;
                        Console.WriteLine($"[career] report refused ({status}): {Trim(body, 240)}");
                        if (status is 400 or 401 or 403 or 409 or 413)
                        {
                            string rejected = path + ".rejected";
                            File.Move(path, rejected, overwrite: true);
                        }
                        // A configuration/auth error will refuse every file.
                        if (status is 401 or 403) break;
                    }
                    catch (Exception ex) when (ex is HttpRequestException
                        or TaskCanceledException or IOException)
                    {
                        Console.WriteLine($"[career] report delivery deferred: {ex.Message}");
                        break;
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _draining, 0);
            }
        }

        private static string Trim(string text, int max)
        {
            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= max ? text : text[..max] + "...";
        }
    }

    internal sealed class CareerMatchReport
    {
        public int Version { get; set; } = 1;
        public Guid MatchId { get; set; }
        public ushort WireMatchId { get; set; }
        public Guid ServerIncarnation { get; set; }
        public string BuildVersion { get; set; } = "";
        public int ProtocolVersion { get; set; }
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset EndedAtUtc { get; set; }
        public long PlayedTicks { get; set; }
        public string EndReason { get; set; } = "completed";
        public string RoomKey { get; set; } = "";
        public int Mode { get; set; }
        public bool Teams { get; set; }
        public int TeamCount { get; set; }
        public bool RatingEligible { get; set; }
        public List<CareerParticipantReport> Participants { get; set; } = new();
    }

    internal sealed class CareerParticipantReport
    {
        public Guid ParticipantId { get; set; }
        public uint ClientId { get; set; }
        public string CareerTicket { get; set; } = "";
        public string DisplayName { get; set; } = "Player";
        public int Hunter { get; set; }
        public bool SingleHunter { get; set; } = true;
        public int Team { get; set; }
        public bool StartedMatch { get; set; }
        public bool Departed { get; set; }
        public long PlayedTicks { get; set; }
        public int Standing { get; set; } = 7;
        public int TeamStanding { get; set; } = 7;
        public bool Won { get; set; }
        public bool Tied { get; set; }
        public CareerMetricsReport Metrics { get; set; } = new();
    }

    internal sealed class CareerMetricsReport
    {
        public long Kills { get; set; }
        public long Deaths { get; set; }
        public long Assists { get; set; }
        public long Damage { get; set; }
        public long Headshots { get; set; }
        public long OctolithScores { get; set; }
        public long NodesCaptured { get; set; }
        public long KillsAsPrime { get; set; }
        public long LongestKillStreak { get; set; }
        public long[] BeamKills { get; set; } = new long[9];
    }

    public sealed partial class DedicatedServer
    {
        private readonly Guid _careerServerIncarnation = Guid.NewGuid();
        private CareerMatchState? _careerMatch;

        private sealed class CareerMatchState
        {
            public Guid MatchId = Guid.NewGuid();
            public ushort WireMatchId;
            public long StartedFrame;
            public DateTimeOffset StartedAtUtc;
            public string RoomKey = "";
            public GameMode Mode;
            public bool Teams;
            public int TeamCount;
            public bool RatingEligible;
            public readonly Dictionary<uint, CareerParticipantState> Participants = new();
        }

        private sealed class CareerParticipantState
        {
            public Guid ParticipantId = Guid.NewGuid();
            public uint ClientId;
            public string CareerTicket = "";
            public string DisplayName = "Player";
            public int Hunter;
            public bool SingleHunter = true;
            public int Team;
            public bool StartedMatch;
            public bool Active;
            public bool Departed;
            public int Slot = -1;
            public long JoinedFrame;
            public CareerCounterSnapshot Start;
            public CareerMetricsReport Metrics = new();
            public long PlayedTicks;
            public int Standing = 7;
            public int TeamStanding = 7;
        }

        private readonly record struct CareerCounterSnapshot(
            long Kills, long Deaths, long Assists, long Damage, long Headshots,
            long OctolithScores, long NodesCaptured, long KillsAsPrime,
            long LongestKillStreak, long[] BeamKills);

        private long CareerFrame => _sim?.Frames ?? 0;

        /// <summary>
        /// Begin only once the authoritative room transition has completed.
        /// Starting earlier would snapshot the previous map's counters and make
        /// the new match look like they ran backwards when NetRoomChange clears them.
        /// </summary>
        private void EnsureCareerMatchStarted(double now)
        {
            if (_careerMatch != null || _sim == null || _phase != SessionPhase.InMatch
                || _peers.Count == 0 || !NetRoomChange.GameplayReady)
            {
                return;
            }

            CareerMatchStats.Reset();
            MatchDefinition match = CurrentDefinition;
            _careerMatch = new CareerMatchState
            {
                WireMatchId = _matchId,
                StartedFrame = CareerFrame,
                StartedAtUtc = DateTimeOffset.UtcNow
                    - TimeSpan.FromSeconds(Math.Max(0, now - _matchStarted)),
                RoomKey = match.RoomKey,
                Mode = match.Mode,
                Teams = GameState.IsTeamMode(match.Mode),
                TeamCount = Math.Max(1, LobbyRules.TeamCount(match)),
                // The public continuous rotation is the verified rules lane.
                // Player-created persistent lobbies still build career history,
                // but never change rating points.
                RatingEligible = SessionPolicy == ServerSessionPolicy.Continuous
            };
            foreach (Peer peer in _peers)
            {
                CareerActivate(peer, startedMatch: true);
            }
            Console.WriteLine($"[career] tracking match {_careerMatch.MatchId} "
                + $"(wire {_matchId}) on {match.RoomKey}");
        }

        private uint CareerKey(Peer peer)
            => peer.ClientId != 0 ? peer.ClientId : 0x80000000u | (uint)(peer.SlotIndex + 1);

        private void CareerActivate(Peer peer, bool startedMatch)
        {
            CareerMatchState? match = _careerMatch;
            if (match == null) return;
            uint key = CareerKey(peer);
            if (!match.Participants.TryGetValue(key, out CareerParticipantState? p))
            {
                p = new CareerParticipantState
                {
                    ClientId = peer.ClientId != 0 ? peer.ClientId : key,
                    StartedMatch = startedMatch
                };
                match.Participants.Add(key, p);
            }
            else
            {
                p.StartedMatch |= startedMatch;
                if (p.Active) return;
            }

            p.CareerTicket = peer.CareerTicket;
            p.DisplayName = peer.Name.Length > 0 ? peer.Name : $"Player {peer.SlotIndex + 1}";
            if (p.Active && p.Hunter != peer.Hunter) p.SingleHunter = false;
            if (p.PlayedTicks > 0 && p.Hunter != peer.Hunter) p.SingleHunter = false;
            p.Hunter = Math.Clamp((int)peer.Hunter, 0, Launcher.Hunters.Playable - 1);
            p.Team = Math.Clamp((int)peer.TeamIndex, 0, 7);
            p.Slot = peer.SlotIndex;
            p.JoinedFrame = CareerFrame;
            p.Start = CareerCounters(peer.SlotIndex);
            p.Active = true;
            p.Departed = false;
        }

        private void CareerIdentityChanged(Peer peer, int previousHunter)
        {
            if (_careerMatch == null) return;
            uint key = CareerKey(peer);
            if (!_careerMatch.Participants.TryGetValue(key, out CareerParticipantState? p)) return;
            if (previousHunter != peer.Hunter) p.SingleHunter = false;
            p.Hunter = Math.Clamp((int)peer.Hunter, 0, Launcher.Hunters.Playable - 1);
            p.Team = Math.Clamp((int)peer.TeamIndex, 0, 7);
            p.DisplayName = peer.Name;
            p.CareerTicket = peer.CareerTicket;
        }

        private void CareerTicketChanged(Peer peer)
        {
            if (_careerMatch == null) return;
            if (_careerMatch.Participants.TryGetValue(CareerKey(peer), out CareerParticipantState? p))
                p.CareerTicket = peer.CareerTicket;
        }

        private void CareerPeerJoined(Peer peer)
        {
            if (_careerMatch != null && _phase == SessionPhase.InMatch)
                CareerActivate(peer, startedMatch: false);
        }

        private void CareerPeerLeaving(Peer peer)
        {
            if (_careerMatch == null) return;
            uint key = CareerKey(peer);
            if (_careerMatch.Participants.TryGetValue(key, out CareerParticipantState? p))
            {
                CareerCaptureSegment(p);
                p.Departed = true;
            }
            CareerMatchStats.ForgetSlot(peer.SlotIndex);
        }

        private CareerCounterSnapshot CareerCounters(int slot)
        {
            long[] beams = new long[9];
            if ((uint)slot < PlayerEntity.SlotCapacity)
            {
                for (int beam = 0; beam < beams.Length; beam++)
                    beams[beam] = Math.Max(0, GameState.BeamKills[slot, beam]);
                return new CareerCounterSnapshot(
                    Math.Max(0, GameState.Kills[slot]),
                    Math.Max(0, GameState.Deaths[slot]),
                    Math.Max(0, CareerMatchStats.Assists[slot]),
                    Math.Max(0, CareerMatchStats.Damage[slot]),
                    Math.Max(0, GameState.HeadshotKills[slot]),
                    Math.Max(0, GameState.OctolithScores[slot]),
                    Math.Max(0, GameState.NodesCaptured[slot]),
                    Math.Max(0, GameState.KillsAsPrime[slot]),
                    Math.Max(0, CareerMatchStats.LongestKillStreak[slot]),
                    beams);
            }
            return new CareerCounterSnapshot(0,0,0,0,0,0,0,0,0,beams);
        }

        private static long Delta(long current, long start) => Math.Max(0, current - start);

        private void CareerCaptureSegment(CareerParticipantState p)
        {
            if (!p.Active || p.Slot < 0) return;
            CareerCounterSnapshot now = CareerCounters(p.Slot);
            p.Metrics.Kills += Delta(now.Kills, p.Start.Kills);
            p.Metrics.Deaths += Delta(now.Deaths, p.Start.Deaths);
            p.Metrics.Assists += Delta(now.Assists, p.Start.Assists);
            p.Metrics.Damage += Delta(now.Damage, p.Start.Damage);
            p.Metrics.Headshots += Delta(now.Headshots, p.Start.Headshots);
            p.Metrics.OctolithScores += Delta(now.OctolithScores, p.Start.OctolithScores);
            p.Metrics.NodesCaptured += Delta(now.NodesCaptured, p.Start.NodesCaptured);
            p.Metrics.KillsAsPrime += Delta(now.KillsAsPrime, p.Start.KillsAsPrime);
            p.Metrics.LongestKillStreak = Math.Max(p.Metrics.LongestKillStreak,
                now.LongestKillStreak);
            for (int beam = 0; beam < p.Metrics.BeamKills.Length; beam++)
                p.Metrics.BeamKills[beam] += Delta(now.BeamKills[beam], p.Start.BeamKills[beam]);
            p.PlayedTicks += Math.Max(0, CareerFrame - p.JoinedFrame);
            p.Active = false;
        }

        private void CompleteCareerMatch(double now, string reason)
        {
            CareerMatchState? match = _careerMatch;
            if (match == null || _sim == null)
            {
                // Never manufacture a zero-stat report by taking the baseline
                // at the same instant the round ends. If tracking could not
                // start while gameplay was actually ready, skip this result.
                Console.WriteLine("[career] no authoritative career baseline; match not reported");
                _careerMatch = null;
                return;
            }

            GameState.UpdateStandings();
            foreach (Peer peer in _peers)
            {
                uint key = CareerKey(peer);
                if (!match.Participants.TryGetValue(key, out CareerParticipantState? p))
                {
                    CareerActivate(peer, startedMatch: false);
                    p = match.Participants[CareerKey(peer)];
                }
                p.CareerTicket = peer.CareerTicket;
                p.DisplayName = peer.Name.Length > 0 ? peer.Name : p.DisplayName;
                p.Hunter = Math.Clamp((int)peer.Hunter, 0, Launcher.Hunters.Playable - 1);
                p.Team = Math.Clamp((int)peer.TeamIndex, 0, 7);
                p.Standing = GameState.Teams
                    ? Math.Clamp(GameState.TeamStandings[peer.SlotIndex], 0, 7)
                    : Math.Clamp(GameState.Standings[peer.SlotIndex], 0, 7);
                p.TeamStanding = Math.Clamp(GameState.Standings[peer.SlotIndex], 0, 7);
                CareerCaptureSegment(p);
                p.Departed = false;
            }

            var reports = new List<CareerParticipantReport>(match.Participants.Count);
            foreach (CareerParticipantState p in match.Participants.Values)
            {
                reports.Add(new CareerParticipantReport
                {
                    ParticipantId = p.ParticipantId,
                    ClientId = p.ClientId,
                    CareerTicket = p.CareerTicket,
                    DisplayName = SanitizeCareerName(p.DisplayName),
                    Hunter = p.Hunter,
                    SingleHunter = p.SingleHunter,
                    Team = p.Team,
                    StartedMatch = p.StartedMatch,
                    Departed = p.Departed,
                    PlayedTicks = Math.Min(p.PlayedTicks,
                        Math.Max(0, CareerFrame - match.StartedFrame)),
                    Standing = p.Standing,
                    TeamStanding = p.TeamStanding,
                    Metrics = p.Metrics
                });
            }

            foreach (CareerParticipantReport p in reports)
            {
                if (!p.StartedMatch || p.Departed) continue;
                int rank = match.Teams ? p.TeamStanding : p.Standing;
                List<CareerParticipantReport> opponents = reports.Where(o =>
                    o.StartedMatch && o.ClientId != p.ClientId
                    && (!match.Teams || o.Team != p.Team)).ToList();
                if (opponents.Count == 0) continue;
                p.Tied = opponents.Any(o => !o.Departed
                    && (match.Teams ? o.TeamStanding : o.Standing) == rank);
                p.Won = rank == 0 && !p.Tied;
            }

            var report = new CareerMatchReport
            {
                MatchId = match.MatchId,
                WireMatchId = match.WireMatchId,
                ServerIncarnation = _careerServerIncarnation,
                BuildVersion = Update.BuildVersion.Display,
                ProtocolVersion = NetConfig.ProtocolVersion,
                StartedAtUtc = match.StartedAtUtc,
                EndedAtUtc = DateTimeOffset.UtcNow,
                PlayedTicks = Math.Max(0, CareerFrame - match.StartedFrame),
                EndReason = "completed",
                RoomKey = match.RoomKey,
                Mode = (int)match.Mode,
                Teams = match.Teams,
                TeamCount = match.TeamCount,
                RatingEligible = match.RatingEligible,
                Participants = reports
            };
            CareerReportOutbox.Enqueue(report);
            _careerMatch = null;
            CareerMatchStats.Reset();
        }

        private void AbandonCareerMatch()
        {
            _careerMatch = null;
            CareerMatchStats.Reset();
        }

        private static string SanitizeCareerName(string name)
        {
            var text = new StringBuilder(Math.Min(name.Length, 64));
            foreach (char c in name)
            {
                if (!Char.IsControl(c)) text.Append(c);
                if (text.Length == 64) break;
            }
            return text.Length == 0 ? "Player" : text.ToString();
        }

        private void HandleCareerIdentity(ReceivedPacket packet, double now)
        {
            Peer? peer = Find(packet.Sender);
            if (peer == null || packet.Payload.Length is < 20 or > 768) return;
            for (int i = 0; i < packet.Payload.Length; i++)
            {
                byte b = packet.Payload[i];
                if (b < 0x21 || b > 0x7e) return;
            }
            string ticket = Encoding.ASCII.GetString(packet.Payload);
            if (!ticket.StartsWith("pp1.", StringComparison.Ordinal)) return;
            peer.LastSeen = now;
            if (peer.CareerTicket == ticket) return;
            peer.CareerTicket = ticket;
            CareerTicketChanged(peer);
        }
    }
}
