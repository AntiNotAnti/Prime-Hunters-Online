using System;
using MphRead.Mods.Multiplayer;

namespace MphRead.Mods.Network
{
    public static class LobbyRules
    {
        public static TeamLayout ResolveTeamLayout(MatchDefinition match) => !GameState.IsTeamMode(match.Mode) ? default
            : match.Format switch
            {
                MatchFormat.OneVsOne => new(2, 1, 1),
                MatchFormat.TwoVsTwo => new(2, 2, 2),
                MatchFormat.ThreeVsThree => new(2, 3, 3),
                MatchFormat.TwoVsTwoVsTwoVsTwo => new(4, 2, 2, 2, 2),
                MatchFormat.Custom => match.CustomTeams,
                _ => new(2, 4, 4)
            };
        public static int TeamCount(MatchDefinition match) => ResolveTeamLayout(match).TeamCount;
        public static int TeamCapacity(MatchDefinition match, int team) => ResolveTeamLayout(match).Capacity(team);
        public static bool ExactTeams(MatchDefinition match) => GameState.IsTeamMode(match.Mode) && match.Format != MatchFormat.Auto;
        public static MatchWorldProfile ResolveWorldProfile(MatchDefinition match, int maxPlayers) =>
            MatchWorldProfile.Resolve(ExactTeams(match) ? ResolveTeamLayout(match).TotalPlayers : maxPlayers);

        public static LobbyResultCode ValidateDefinition(MatchDefinition match, out string reason)
        {
            reason = "";
            if (String.IsNullOrWhiteSpace(match.RoomKey) || match.RoomKey.Length > HostRequestPacket.MaxRoomBytes
                || !Enum.IsDefined(match.Format) || !Enum.IsDefined(match.Mode)
                || match.Mode is GameMode.SinglePlayer or GameMode.None or GameMode.Unknown15)
                reason = "Choose a multiplayer map and mode.";
            else if (match.Format != MatchFormat.Auto
                && GameState.IsTeamMode(match.Mode) != (match.Format != MatchFormat.FreeForAll))
                reason = "Choose a team mode for a team format, or a free-for-all mode for FFA.";
            else if (GameState.IsTeamMode(match.Mode) && !ResolveTeamLayout(match).IsValid)
                reason = "Use 2 to 4 nonempty teams, zero inactive capacities, and at most 8 players.";
            else if (match.Mode == GameMode.Capture && TeamCount(match) != 2)
                reason = "Capture requires exactly two teams because maps have two bases.";
            return reason.Length == 0 ? LobbyResultCode.Ok : LobbyResultCode.InvalidConfiguration;
        }

        public static LobbyResultCode Validate(MatchDefinition match, RosterPacket roster,
            bool requireReady, out string reason)
        {
            var result = ValidateDefinition(match, out reason);
            if (result != LobbyResultCode.Ok) return result;
            TeamLayout layout = ResolveTeamLayout(match);
            bool exact = ExactTeams(match);
            int required = exact ? layout.TotalPlayers : match.Format == MatchFormat.FreeForAll ? 2 : 1;
            if (roster.Count < required || (exact && roster.Count != required))
            {
                reason = exact ? $"{layout} requires exactly {required} players." : $"At least {required} players must join.";
                return LobbyResultCode.NotEnoughPlayers;
            }
            Span<int> counts = stackalloc int[4];
            for (int i = 0; i < roster.Count; i++)
            {
                if (layout.TeamCount > 0)
                {
                    int team = roster.Teams[i];
                    if (team < 0 || team >= layout.TeamCount) { reason = "Every player needs a valid team."; return LobbyResultCode.InvalidTeam; }
                    counts[team]++;
                }
                if (requireReady && !roster.LobbyReady[i])
                { reason = $"Waiting for {roster.Names[i]} to ready."; return LobbyResultCode.PlayersNotReady; }
            }
            for (int team = 0; team < layout.TeamCount; team++)
            {
                int capacity = layout.Capacity(team);
                if (counts[team] > capacity || (exact && counts[team] != capacity))
                { reason = $"Team {(char)('A' + team)} needs {capacity} players (currently {counts[team]})."; return LobbyResultCode.InvalidTeam; }
            }
            reason = "Ready to start.";
            return LobbyResultCode.Ok;
        }
    }
}
