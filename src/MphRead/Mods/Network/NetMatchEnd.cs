using System;
using MphRead.Entities;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// The end of a networked match, and the move to the next map.
    ///
    /// Offline, a match that ends is a match that is over: GameState runs the
    /// winner's camera, then the scoreboard, then fades to black and quits to
    /// the launcher. On a server that is the wrong ending in two ways at
    /// once. The server has a rotation and never heard that anybody won, so
    /// it kept counting down the old map's clock; and every client dropped
    /// back to its own front screen, so a match that ended scattered the
    /// people playing it instead of taking them somewhere together.
    ///
    /// What happens instead: the authority tells the server the match is
    /// over, the server holds a short intermission while every client shows
    /// its results, and then it rotates. Clients follow the new room key the
    /// way they already follow a rotation that came from the clock -- through
    /// <see cref="NetRoomChange"/>, which fades to black and loads it. The
    /// fade a player sees at the end of a match is therefore the fade into
    /// the next one.
    ///
    /// The server is told rather than asked to work it out. It has no
    /// simulation and no scoreboard: only the authority knows a player
    /// reached the goal, and the shortest honest path is for it to say so.
    /// </summary>
    public static class NetMatchEnd
    {
        /// <summary>Frames between repeats of the report while it goes unacknowledged.</summary>
        private const uint ReportInterval = 15;

        private static uint _lastReport;
        private static bool _reported;
        /// <summary>
        /// The server has heard, and nothing more should be said until a new
        /// match is actually running.
        ///
        /// Without this the report outlived the match it was about. A client
        /// stays in its results sequence for a second or two after the server
        /// has rotated -- it has the new map to load first -- and during that
        /// window MatchState still says "not in progress" while the server's
        /// state has stopped saying "ending". So the authority reported the
        /// end of the *new* match the instant the rotation landed and the
        /// server dutifully ended it: a whole map skipped every time, visible
        /// in the server log as two "match over" lines in the same second.
        ///
        /// Cleared when the match state goes back to in progress, which is
        /// what loading the next room does.
        /// </summary>
        private static bool _acknowledged;

        /// <summary>Server frames this client has been showing results the server does not agree with.</summary>
        private static uint _strandedSince;

        public static void Reset()
        {
            _lastReport = 0;
            _reported = false;
            _acknowledged = false;
            _strandedSince = 0;
        }

        /// <summary>
        /// Whether this machine may decide, from its own scoreboard, that the
        /// match is over.
        ///
        /// Offline, always. Connected, only the machine that keeps the score:
        /// every other client's Points come out of the authority's snapshot,
        /// so a client that reaches the point goal a moment before or after
        /// the authority does is not seeing something the authority missed --
        /// it is disagreeing with the only copy of the scoreboard that counts,
        /// and it has no way back. Nothing returns MatchState to InProgress
        /// until the next rotation, so a client that ends its match early
        /// spends the rest of the round in its results screen while its player
        /// stands motionless in everybody else's game.
        ///
        /// Two ways that happened in one real match on 2026-08-23:
        ///
        /// - a replayed kill counted the kill a second time for one frame
        ///   (see <see cref="NetDamage"/>), so the sixth kill of a
        ///   seven-point match ended it on the client;
        /// - after a rotation a client that finished loading before the
        ///   authority did applied one last snapshot from the match that had
        ///   just been won, whose scores were still the winning ones, and
        ///   ended the brand new match on the frame it started.
        ///
        /// Both are fixed at their source. This is the rule that makes a
        /// third one a hiccup rather than a lost player: the authority
        /// reaches the goal, tells the server, and every client ends its
        /// match a round trip later through <see cref="Sync"/>, which is the
        /// path that was already here.
        /// </summary>
        public static bool MayEndOnScore => !NetSession.Active
            || NetSession.IsAuthority || NetSession.IsHost;

        /// <summary>
        /// True while this client is showing the results of a networked match
        /// and waiting for the server to move everyone on. The match clock is
        /// not adopted during it -- see <see cref="NetMatchSync"/> -- because
        /// the results sequence runs on the same counter.
        /// </summary>
        public static bool InIntermission => NetSession.Active
            && (GameState.MatchState != MatchState.InProgress
                || NetSession.ServerMatch?.Ending == true);

        public static void Sync()
        {
            if (!NetSession.Active)
            {
                return;
            }
            bool serverEnding = NetSession.ServerMatch?.Ending == true;
            if (serverEnding && GameState.MatchState == MatchState.InProgress)
            {
                // The server ended the match for a reason this client has not
                // reached on its own -- the clock, or a score whose last kill
                // has not been replayed here yet. Zeroing the timer is how a
                // match ends everywhere in this engine, so the results play
                // out normally rather than being cut to.
                GameState.MatchTime = 0;
            }
            RecoverIfStranded(serverEnding);
            if (!NetSession.IsAuthority && !NetSession.IsHost)
            {
                return;
            }
            if (GameState.MatchState == MatchState.InProgress)
            {
                _reported = false;
                _acknowledged = false;
                return;
            }
            if (serverEnding)
            {
                _reported = true;
                _acknowledged = true;
                return;
            }
            if (_acknowledged)
            {
                return;
            }
            // Repeated until the server's own state comes back with the flag
            // set. One lost datagram would otherwise leave the whole session
            // sitting on a finished match until the old map's clock ran out.
            if (_reported && NetSession.NetFrame - _lastReport < ReportInterval)
            {
                return;
            }
            _reported = true;
            _lastReport = NetSession.NetFrame;
            NetSession.SendMatchEnd();
        }

        /// <summary>
        /// Frames a client may sit in its results while the server says the
        /// match is running before it is put back into play.
        ///
        /// Longer than the whole results sequence (3 s of the winner's camera
        /// and 5 s of the scoreboard) so a legitimate ending is never cut
        /// short, and the server's own intermission is only a second longer
        /// than that -- so if this expires the server is not ending anything
        /// and this client is on its own.
        /// </summary>
        private const uint StrandedFrames = 60 * 12;

        /// <summary>
        /// Put a client back into a match the server never stopped running.
        ///
        /// The last line of defence, not the fix for anything: a client whose
        /// match state disagrees with the server's has already lost the
        /// difference between them, and the causes are dealt with where they
        /// happen. What this stops is the disagreement lasting the rest of the
        /// round. Before it, the only way out of a results screen the server
        /// did not ask for was the next rotation, which on a long map is
        /// several minutes of standing still.
        /// </summary>
        private static void RecoverIfStranded(bool serverEnding)
        {
            MatchStatePacket? state = NetSession.ServerMatch;
            bool serverRunning = state.HasValue && !serverEnding
                && (state.Value.Flags & MatchStatePacket.FlagInProgress) != 0;
            if (!serverRunning || GameState.MatchState == MatchState.InProgress)
            {
                _strandedSince = 0;
                return;
            }
            if (_strandedSince == 0)
            {
                _strandedSince = Math.Max(NetSession.NetFrame, 1);
                return;
            }
            if (NetSession.NetFrame - _strandedSince < StrandedFrames)
            {
                return;
            }
            _strandedSince = 0;
            Console.WriteLine("[net] the server's match is still running; leaving the results screen");
            NetLog.Event("recovered from a results screen the server did not ask for");
            GameState.ResetMatchProgress();
            GameState.MatchTime = state!.Value.TimeRemaining;
        }

        /// <summary>
        /// Whether the engine should quit to the launcher now that the
        /// results have finished.
        ///
        /// Offline, yes -- that is what the end of a match means. Connected,
        /// no: the server is about to say which map is next, and quitting
        /// would take this player out of a session that is still running.
        /// </summary>
        public static bool ShouldLeaveAfterMatch => !NetSession.Active;
    }
}
