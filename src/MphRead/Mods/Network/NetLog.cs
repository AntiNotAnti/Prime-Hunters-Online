using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MphRead.Entities;
using MphRead.Formats.Culling;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// Per-client debug log, written to netlog-&lt;name&gt;.txt beside the
    /// executable.
    ///
    /// Two clients disagreeing about what is on screen cannot be diagnosed
    /// from one side, and console output scrolls away. Each client writing
    /// its own file makes the two views directly comparable: the same frame,
    /// the same slots, seen from each machine.
    ///
    /// What it records per slot is chosen to answer "why can I not see the
    /// other player": whether an entity exists, whether it is active, where
    /// it thinks it is, and -- the part that actually decides rendering --
    /// whether its NodeRef still refers to the room node it occupies. A
    /// remote player whose position is written straight in keeps the NodeRef
    /// it spawned with, and room culling then hides it from anywhere else.
    /// </summary>
    public static class NetLog
    {
        private static StreamWriter? _writer;
        private static double _lastWrite;
        private static bool _failed;

        /// <summary>Seconds between periodic snapshots. Events are written immediately.</summary>
        /// <summary>
        /// Seconds between roster dumps. One a second is right for reading by
        /// eye; a diagnostic that compares two clients' idea of where a
        /// player is needs them far closer together than that, because the
        /// time between the two samples being compared is itself counted as
        /// disagreement. MPHREAD_NETLOG_INTERVAL overrides it.
        /// </summary>
        public static readonly double Interval = ReadInterval();

        private static double ReadInterval()
        {
            string? value = Environment.GetEnvironmentVariable("MPHREAD_NETLOG_INTERVAL");
            if (value != null && Double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double parsed)
                && parsed > 0 && parsed <= 10)
            {
                return parsed;
            }
            return 1.0;
        }

        public static bool Enabled { get; private set; }

        public static void Open(string clientName)
        {
            if (_failed || _writer != null)
            {
                return;
            }
            try
            {
                string safe = string.Concat(clientName.Select(c =>
                    char.IsLetterOrDigit(c) ? c : '_'));
                string path = Path.Combine(Platform.AppPaths.UserDataDirectory, $"netlog-{safe}.txt");
                _writer = new StreamWriter(path, append: false) { AutoFlush = true };
                Enabled = true;
                Line($"=== MphRead net log for \"{clientName}\" ===");
                Line($"started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
            catch (IOException ex)
            {
                // Logging must never stop a session from running.
                _failed = true;
                Console.WriteLine($"[netlog] could not open log: {ex.Message}");
            }
        }

        public static void Close()
        {
            _writer?.Dispose();
            _writer = null;
            Enabled = false;
            _lastWrite = 0;
        }

        /// <summary>
        /// Diagnostic hook for the collision-candidate pool. The pool holds
        /// 2048 entries and is recycled on entry to every query, so draining
        /// it means one query is asking for an enormous grid range -- which
        /// happens when a player's position or PrevPosition is far outside
        /// the room. Recording both is what distinguishes "bad position" from
        /// "pool genuinely too small".
        /// </summary>
        public static void CollisionRange(int slot, string label,
            OpenTK.Mathematics.Vector3 prev, OpenTK.Mathematics.Vector3 current)
        {
            Event($"collision {label} slot={slot} "
                + $"prev=({prev.X:0.00},{prev.Y:0.00},{prev.Z:0.00}) "
                + $"cur=({current.X:0.00},{current.Y:0.00},{current.Z:0.00}) "
                + $"delta={(current - prev).Length:0.00}");
        }

        /// <summary>A one-off event, written the moment it happens.</summary>
        public static void Event(string message)
        {
            Line($"[{DateTime.Now:HH:mm:ss.fff}] EVENT  {message}");
        }

        /// <summary>
        /// Periodic snapshot of everything needed to compare two clients.
        /// </summary>
        public static void Snapshot(double time, Scene? scene = null)
        {
            if (_writer == null || time - _lastWrite < Interval)
            {
                return;
            }
            _lastWrite = time;

            var sb = new StringBuilder();
            sb.Append($"[{DateTime.Now:HH:mm:ss.fff}] STATE  ");
            sb.Append($"role={NetSession.Role} slot={NetSession.LocalSlot} ");
            sb.Append($"authority={NetSession.IsAuthority} ");
            sb.Append($"main={PlayerEntity.MainPlayerIndex} ");
            sb.Append($"mode={GameState.Mode} matchTime={GameState.MatchTime:0.0} ");
            // The two numbers that decide whether this client is still
            // playing. A client that ended its match early looks, in every
            // other field here, exactly like one whose player has stopped
            // moving -- which is a fortnight of the wrong investigation. The
            // goal is logged with the state because the interesting failure
            // is a client whose scoreboard reached it and whose authority's
            // did not.
            sb.Append($"matchState={GameState.MatchState} goal={GameState.PointGoal} ");
            MatchStatePacket? match = NetSession.ServerMatch;
            if (match != null)
            {
                sb.Append($"serverTime={match.Value.TimeRemaining:0.0} ");
                sb.Append($"serverMap={match.Value.RoomKey} ");
                sb.Append($"serverPeers={match.Value.PlayerCount} ");
            }
            Line(sb.ToString());
            HitReg();

            for (int slot = 0; slot < PlayerEntity.MaxPlayers; slot++)
            {
                PlayerEntity? p = PlayerEntity.Players[slot];
                if (p == null)
                {
                    Line($"           slot {slot}: (no entity)");
                    continue;
                }
                bool active = p.LoadFlags.TestFlag(LoadFlags.Active);
                bool occupied = slot < NetSession.SlotOccupied.Length
                    && NetSession.SlotOccupied[slot];
                if (!active && !occupied)
                {
                    continue; // empty slot, nothing to compare
                }
                var line = new StringBuilder();
                line.Append($"           slot {slot}: ");
                line.Append($"name={GameState.Nicknames[slot],-10} ");
                line.Append($"occupied={(occupied ? "y" : "n")} ");
                line.Append($"active={(active ? "y" : "n")} ");
                line.Append($"spawned={(p.LoadFlags.TestFlag(LoadFlags.Spawned) ? "y" : "n")} ");
                line.Append($"bot={(p.IsBot ? "y" : "n")} ");
                line.Append($"hp={p.Health,-3} ");
                line.Append($"score={GameState.Points[slot]}/{GameState.TeamPoints[slot]}p ");
                line.Append($"{GameState.Kills[slot]}k{GameState.Deaths[slot]}d ");
                // The respawn path is guarded by `_health == 0 &&
                // _respawnTimer == 0 && EnemySpawner == null`. A player stuck
                // at the origin with hp=0 is waiting on one of these, so log
                // them rather than inferring which.
                line.Append($"respawnTimer={p.RespawnTimer,-5} ");
                line.Append($"pos=({p.Position.X:0.00},{p.Position.Y:0.00},{p.Position.Z:0.00}) ");
                // The rendering-relevant part: a stale or absent NodeRef is
                // what makes an otherwise healthy remote player invisible.
                line.Append($"form={p.ModFormState(),-24} ");
                line.Append($"nodeRef={DescribeNodeRef(p)} ");
                var health = NetHudHealth.Sample(p);
                line.Append($"health slot={p.SlotIndex} generation={NetPlayerLifecycle.Generation(p.SlotIndex)} life={NetPlayerLifecycle.Get(p.SlotIndex)} authorityHP={(health.Authoritative ? health.Health.ToString() : "unknown")} entityHP={p.Health} hpFrame={health.SnapshotFrame} snapshotAge={NetSession.SnapshotAge} {NetHitPrediction.HealthDetails(p.SlotIndex)} ");
                if (NetSession.RemoteStateValid[p.SlotIndex])
                {
                    var state = NetSession.RemoteStates[p.SlotIndex];
                    line.Append($"lastDamageEvent={state.DamageEventId} lastAttacker={state.AttackerSlot} ");
                }
                // Whether the engine will call Process on this player at all.
                // A slot can be occupied, active and flagged correctly and
                // still be absent from the scene's entity list, in which case
                // nothing simulates it and it never leaves the origin -- the
                // failure this whole feature has hit twice.
                line.Append($"inScene={(InScene(scene, p) ? "y" : "n")} ");
                line.Append($"stateValid={(NetSession.RemoteStateValid[slot] ? "y" : "n")} ");
                line.Append($"intentValid={(NetSession.RemoteIntentValid[slot] ? "y" : "n")}");
                Line(line.ToString());
            }
        }

        /// <summary>
        /// The three questions "my shot went through him" is actually asking,
        /// on one line, so that a run can be told which of them it is.
        ///
        /// They are genuinely separate and nothing else in this log separates
        /// them. A shot that did not register is either (a) aimed at a world
        /// the shooter was never shown, which is the playout clock -- how far
        /// behind it is reading and how much of that is buffer; (b) resolved
        /// against a frame the authority could not go back to, which is the
        /// rewind and its ceiling; or (c) resolved correctly by the shooter
        /// and found by nobody, which is what a hit claim is and what its
        /// verdict says. On a jump pad, where a target crosses 0.3 units a
        /// frame against a headshot band 0.3 units tall, all three are live at
        /// once and guessing between them is hopeless.
        /// </summary>
        private static void HitReg()
        {
            var line = new StringBuilder();
            line.Append("           hitreg ");
            if (NetSmoothing.AckPoint(out uint readFrame, out byte readSub))
            {
                line.Append($"read={readFrame}+{readSub / 256.0:0.00} ");
                line.Append($"buffer={NetSmoothing.Delay:F2}f ");
                line.Append($"jitter={NetSmoothing.JitterFrames * 1000.0 / 60.0:F1}ms ");
                line.Append($"newestSnap={NetSession.LastSnapshotFrame} ");
                line.Append($"starved={NetSmoothing.Starved} snaps={NetSmoothing.Snaps} ");
                if (NetSession.StatePacketsCoalesced > 0)
                    line.Append($"coalesced={NetSession.StatePacketsCoalesced} ");
            }
            else
            {
                line.Append($"read=off ack={NetSession.AppliedSnapshotFrame} ");
            }
            if (NetUnlagged.ShotsCompensated > 0)
            {
                line.Append($"rewound={NetUnlagged.ShotsCompensated} ");
                line.Append($"meanRewind={(double)NetUnlagged.FramesRewound / NetUnlagged.ShotsCompensated:0.0}f ");
                line.Append($"worst={NetUnlagged.WorstRewind}f ");
                line.Append($"rewindRawCap={NetUnlagged.MaxRewindFrames}f ");
                line.Append($"rewindReduced={NetUnlagged.ShotsClamped} ");
                line.Append($"historyMiss={NetUnlagged.HistoryMisses} ");
            }
            if (NetHitClaims.Declared > 0)
            {
                line.Append($"claims={NetHitClaims.Declared} applied={NetHitClaims.Applied} ");
                line.Append($"dup={NetHitClaims.Duplicate} ");
                line.Append($"voidShooter={NetHitClaims.RefusedDeadShooter} ");
                line.Append($"voidVictim={NetHitClaims.RefusedDeadVictim} ");
                line.Append($"refused={NetHitClaims.RefusedOther} ");
                line.Append($"unanswered={NetHitClaims.Unanswered} ");
            }
            if (NetHitClaims.Received > 0)
            {
                line.Append($"claimsIn={NetHitClaims.Received} ");
                line.Append($"rescued={NetHitClaims.AppliedHere} ");
                line.Append($"({NetHitClaims.RescuedKills}k {NetHitClaims.RescuedHeadshots}hs) ");
                line.Append($"dupIn={NetHitClaims.DuplicateHere} ");
                line.Append($"voidShooterIn={NetHitClaims.VoidedDeadShooter} ");
                line.Append($"refusedIn={NetHitClaims.RefusedHere} ");
            }
            line.Append($"predicted={NetHitPrediction.Predicted} ");
            line.Append($"confirmed={NetHitPrediction.Confirmed} ");
            line.Append($"denied={NetHitPrediction.Denied} ");
            line.Append($"unpredicted={NetHitPrediction.Unpredicted}");
            line.Append($" unpredMoving={NetHitPrediction.UnpredictedMoving}");
            line.Append($" unpredStill={NetHitPrediction.UnpredictedStill}");
            line.Append($" firedMoving={NetDamage.FiredMoving}");
            line.Append($" firedStill={NetDamage.FiredStill}");
            if (NetPlayerBridge.PredictionCorrections > 0
                || NetPlayerBridge.PredictionHistoryMisses > 0
                || NetPlayerBridge.PredictionRecoveries > 0)
            {
                line.Append($" moveCorr={NetPlayerBridge.PredictionCorrections}");
                line.Append($" moveSnap={NetPlayerBridge.PredictionSnaps}");
                line.Append($" moveHistMiss={NetPlayerBridge.PredictionHistoryMisses}");
                line.Append($" moveRecover={NetPlayerBridge.PredictionRecoveries}");
                line.Append($" moveWorst={NetPlayerBridge.PredictionWorstError:F2}");
            }
            Line(line.ToString());
        }

        private static bool InScene(Scene? scene, PlayerEntity player)
        {
            if (scene == null)
            {
                return false;
            }
            foreach (EntityBase entity in scene.Entities)
            {
                if (entity == player)
                {
                    return true;
                }
            }
            return false;
        }

        private static string DescribeNodeRef(PlayerEntity player)
        {
            try
            {
                NodeRef nodeRef = player.NodeRef;
                // The node's own numbers, not NodeRef.ToString(): the struct
                // has no override, so the log used to read
                // "MphRead.Formats.Culling.NodeRef" for every player, which
                // said nothing about the culling this line exists to explain.
                return nodeRef == NodeRef.None
                    ? "none"
                    : $"{nodeRef.RoomName ?? "?"}:{nodeRef.PartIndex}/{nodeRef.NodeIndex}";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static void Line(string text)
        {
            try
            {
                _writer?.WriteLine(text);
            }
            catch (IOException)
            {
                // A full disk must not take the game down.
            }
        }
    }
}
