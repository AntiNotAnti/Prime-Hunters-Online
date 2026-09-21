using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// Watching a recorded match. A demo file is fed into
    /// <see cref="NetSession"/> exactly like a live connection would be --
    /// see <see cref="NetSession.StartPlayback"/> and
    /// <see cref="NetTransport.EnqueueForPlayback"/> -- so every packet-type
    /// handler, room transition and match-end sequence runs unchanged; this
    /// class only decides *when* each recorded packet gets handed over.
    ///
    /// "When" is a frame number, not a moment. <see cref="PumpFrame"/> is
    /// called once per simulated frame and releases exactly the packets the
    /// recorder saw on the matching frame of its own run, so the replay has
    /// the same packets-per-frame the recording did -- however fast this
    /// machine is drawing, and however long the room took to load in the
    /// middle. See <see cref="DemoFile"/> for the three ways the stopwatch
    /// this replaces got that wrong.
    ///
    /// There is no real local player during playback, so the viewer starts
    /// and stays in <see cref="SpectatorMode"/>; Space additionally toggles
    /// a free no-clip camera on top of that, for looking around rather than
    /// only following whoever is spectated.
    /// </summary>
    public static class DemoPlayback
    {
        private static DemoReader? _reader;
        private static DemoRecord? _pending;
        /// <summary>The frame of the recording about to be replayed.</summary>
        private static uint _frame;
        private static bool _started;

        public static bool IsActive { get; private set; }
        public static string? CurrentPath { get; private set; }
        public static IReadOnlyList<ReplayEvent> Events => _reader?.Metadata?.Events ?? Array.Empty<ReplayEvent>();
        internal static ReplayMetadata? Metadata => _reader?.Metadata;
        public static uint CurrentFrame => _frame;
        public static uint LastFrame { get; private set; }
        public static ReplayOpenResult LastResult { get; private set; }
        public static double CurrentSeconds => _frame / 60.0;
        public static double DurationSeconds => LastFrame / 60.0;

        /// <summary>True once the file has no more records -- the scene holds on the last state rather than closing itself.</summary>
        public static bool AtEnd => IsActive && _pending == null;

        /// <summary>
        /// Why the last <see cref="Join"/> failed, for a screen that is
        /// still open to show it on -- Console.WriteLine is where this used
        /// to only go, which is invisible on the Windows build outside a
        /// typed command.
        /// </summary>
        public static string? LastError { get; private set; }

        /// <summary>
        /// How far into the recording <see cref="Join"/> will look for the
        /// match info before giving up. Twenty seconds of recorded frames:
        /// the server repeats its match state once a second, so a file that
        /// has not said what room it is by then does not contain one.
        /// </summary>
        private const uint JoinSearchFrames = 60 * 20;

        /// <summary>
        /// Frames to keep pumping after the room key is known.
        ///
        /// BuildPlayers, called right after this, reads NetSession.SlotHunter
        /// and SlotOccupied to decide every player's hunter and whether their
        /// slot is even active, and those come from Roster packets that do not
        /// necessarily land in the same burst as the MatchState that answers
        /// ServerMatch first. Returning on the room key alone showed real
        /// players with the wrong hunter, or briefly not active at all. The
        /// roster repeats once a second, so two of those.
        /// </summary>
        private const uint JoinGraceFrames = 120;

        /// <summary>
        /// Synthetic receive timestamp for a recorded local frame.
        ///
        /// Replay packets must preserve the cadence they had in the recording,
        /// not inherit whatever wall-clock batching this machine happens to do
        /// while reviewing it. A catch-up draw can execute several 60 Hz replay
        /// frames back-to-back; stamping those packets with Stopwatch.Now makes
        /// NetSmoothing interpret the batch as new jitter and visibly hitch.
        /// Differential transit only needs a stable cadence, so an arbitrary
        /// epoch plus the recorded frame is sufficient.
        /// </summary>
        internal static long PlaybackArrivalTicks(uint frame)
            => 1 + (long)Math.Round(frame * (double)Stopwatch.Frequency / 60.0);

        /// <summary>
        /// Open the file and wind it forward to the first match info, the
        /// same shape as <see cref="NetLaunch.Join"/> -- true once
        /// <c>NetSession.ServerMatch</c> knows what room to load.
        ///
        /// Blocking, and called off the UI thread for that reason, but no
        /// longer *waiting*: a live join waits on a server, and this reads a
        /// file, so it costs a few hundred frames of parsing rather than the
        /// eight seconds the wall-clock version could spend.
        /// </summary>
        public static bool Join(string path, int timeoutMs = 8000)
        {
            _ = timeoutMs; // kept for the call site; nothing here waits on a clock
            Stop();
            LastError = null;
            _reader = DemoReader.Open(path, out ReplayOpenResult result);
            LastResult = result;
            if (_reader == null)
            {
                LastError = $"Cannot open replay: {result}.";
                Console.WriteLine($"[demo] \"{path}\": {LastError}");
                return false;
            }
            if (_reader.ProtocolVersion != NetConfig.ProtocolVersion)
            {
                LastResult = ReplayOpenResult.ProtocolMismatch;
                LastError = $"This replay uses network protocol {_reader.ProtocolVersion}. "
                    + $"This build uses protocol {NetConfig.ProtocolVersion}. "
                    + "This replay cannot be safely played by this build.";
                _reader.Dispose();
                _reader = null;
                return false;
            }
            // Reconstructed scenes must start from the same presentation/simulation
            // random streams, regardless of what was played earlier in this process.
            Rng.SetRng1(Rng.Rng1StartValue);
            Rng.SetRng2(Rng.Rng2StartValue);
            Entities.SpinningEntityBase.ResetReplayRotation();
            if (CurrentPath != path) { ReplayController.ClearSelection(); Replay.ReplayCamera.ClearBookmarks(); }
            CurrentPath = path;
            Replay.ReplayCheckpointManager.NoteReplay(path);
            LastFrame = _reader.FormatVersion == 3 ? _reader.DurationFrames : DemoLibrary.Duration(path);
            NetSession.StartPlayback();
            IsActive = true;
            _frame = 0;
            _started = false;
            Replay.ReplayNetworkDiagnostics.Reset();
            if (_reader.Metadata is ReplayMetadata metadata)
            {
                if (metadata.ExpectedHashes.Count > 0 && (metadata.HashSchema != ReplayStateHash.Schema || metadata.HashBuildId != ReplayStateHash.BuildId))
                    Console.WriteLine("[replay] Expected state hashes belong to a different engine build/schema; packet playback remains available, hash verification is skipped.");
                LastResult = ReplayMapIdentity.Validate(metadata);
                if (LastResult != ReplayOpenResult.Success)
                {
                    LastError = $"Cannot load replay map: {LastResult}.";
                    Stop();
                    return false;
                }
                foreach (byte[] packet in metadata.Bootstrap.Packets)
                    NetSession.InjectPlaybackPacket(packet, packet.Length,
                        PlaybackArrivalTicks(0));
                NetSession.Update(0);
                if (NetSession.ServerMatch?.RoomKey.Length is not > 0)
                {
                    LastResult = ReplayOpenResult.MissingMatchState;
                    LastError = "Replay bootstrap has no match state.";
                    Stop();
                    return false;
                }
                NetSession.RewindPlayback();
                foreach (byte[] packet in metadata.Bootstrap.Packets)
                    NetSession.InjectPlaybackPacket(packet, packet.Length,
                        PlaybackArrivalTicks(0));
                _pending = _reader.ReadNext();
                if (_pending == null)
                {
                    LastResult = _reader.LastResult == ReplayOpenResult.Success ? ReplayOpenResult.Empty : _reader.LastResult;
                    LastError = $"Cannot play replay: {LastResult}.";
                    Stop();
                    return false;
                }
                ReplayController.Begin();
                return true;
            }
            _pending = _reader.ReadNext();
            bool hadRecords = _pending != null;
            long knownAt = -1;
            while (_frame < JoinSearchFrames)
            {
                PumpFrame();
                NetSession.Update(_frame / 60.0);
                if (NetSession.ServerMatch?.RoomKey.Length > 0)
                {
                    if (knownAt < 0)
                    {
                        knownAt = _frame;
                    }
                    else if (_frame - knownAt >= JoinGraceFrames || AtEnd)
                    {
                        return Rewind(path);
                    }
                }
                else if (AtEnd)
                {
                    break;
                }
            }
            LastResult = _reader.LastResult != ReplayOpenResult.Success ? _reader.LastResult : !hadRecords ? ReplayOpenResult.Empty : ReplayOpenResult.MissingMatchState;
            LastError = !hadRecords
                ? "That replay file is empty -- nothing was ever recorded to it."
                : "That replay has no match info in its first few seconds -- "
                    + "the recording may have started before the server said what map it was running.";
            Console.WriteLine($"[demo] \"{path}\": {LastError}");
            Stop();
            return false;
        }

        /// <summary>
        /// Go back to the file's first frame, now that the room to load is
        /// known.
        ///
        /// The search above is not free: it hands its records to the session
        /// to be acted on, and there is no scene yet to act on them, so
        /// everything in the first second or three of the recording was
        /// consumed and then thrown away. The replay opened that far in --
        /// which is why the first thing anybody did after pressing record was
        /// missing from the file's playback while everything after it was
        /// fine. Reported as "the first shot is not in the demo", and it was
        /// in the demo; it was simply never played.
        ///
        /// Rewinding costs re-parsing a couple of hundred records. The state
        /// the search was for stays -- see
        /// <see cref="NetSession.RewindPlayback"/> for what has to go with it.
        /// </summary>
        private static bool Rewind(string path)
        {
            _reader?.Dispose();
            _reader = DemoReader.Open(path, out ReplayOpenResult result);
            LastResult = result;
            if (_reader == null)
            {
                LastError = "That replay could not be read a second time.";
                Console.WriteLine($"[demo] \"{path}\": {LastError}");
                Stop();
                return false;
            }
            _frame = 0;
            _started = false;
            _pending = _reader.ReadNext();
            NetSession.RewindPlayback();
            ReplayController.Begin();
            return true;
        }

        /// <summary>
        /// Reposition packet playback after an in-memory world checkpoint.
        /// V3 lands directly on the indexed chunk; v2 falls back to a sequential scan.
        /// No packets at or before the checkpoint are re-applied because the checkpoint
        /// already contains their resulting world state.
        /// </summary>
        internal static bool Reposition(uint frame, uint netFrame)
        {
            if (!IsActive || CurrentPath == null) return false;
            DemoReader? next = DemoReader.Open(CurrentPath, out ReplayOpenResult result);
            if (next == null || next.ProtocolVersion != NetConfig.ProtocolVersion)
            {
                next?.Dispose();
                LastResult = next == null ? result : ReplayOpenResult.ProtocolMismatch;
                return false;
            }

            DemoRecord? pending = next.SeekAfter(frame);
            if (pending == null && next.LastResult != ReplayOpenResult.Success && frame < LastFrame)
            {
                LastResult = next.LastResult;
                next.Dispose();
                return false;
            }

            _reader?.Dispose();
            _reader = next;
            _pending = pending;
            _frame = frame;
            _started = true;
            Replay.ReplayNetworkDiagnostics.Reset();
            LastResult = ReplayOpenResult.Success;
            LastError = null;
            NetSession.PreparePlaybackCheckpoint(netFrame);
            ReplayVerification.SeekTo(frame);
            return true;
        }

        /// <summary>
        /// Called once a frame: hands over every packet the recorder saw on
        /// this frame of its own run.
        /// </summary>
        public static void PumpFrame()
        {
            if (!IsActive || _reader == null || AtEnd)
            {
                return;
            }
            // The first pumped frame is frame 0 of the recording; every one
            // after it is the next. Advancing before the release instead
            // would skip whatever the recorder caught on its own first frame.
            if (_started)
            {
                _frame++;
            }
            _started = true;
            try
            {
                while (_pending is DemoRecord record && record.Frame <= _frame)
                {
                    Replay.ReplayNetworkDiagnostics.OnPacket(record.Frame, record.Data);
                    NetSession.InjectPlaybackPacket(record.Data, record.Data.Length,
                        PlaybackArrivalTicks(record.Frame));
                    _pending = _reader.ReadNext();
                }
            }
            catch (InvalidDataException ex)
            {
                LastResult = ReplayOpenResult.Corrupt;
                LastError = "Replay stopped: " + ex.Message;
                _pending = null;
                return;
            }
            if (_pending == null)
            {
                LastResult = _reader.LastResult;
                if (LastResult != ReplayOpenResult.Success) LastError = $"Replay stopped: {LastResult}.";
            }
        }

        public static bool TakeControl(int slot, out string? branchPath)
        {
            branchPath = null;
            if (!IsActive || CurrentPath == null || !SpectatorMode.IsSpectating
                || slot < 0 || slot >= Entities.PlayerEntity.Players.Count)
                return false;

            Entities.PlayerEntity player = Entities.PlayerEntity.Players[slot];
            if (!player.LoadFlags.TestFlag(Entities.LoadFlags.Active)
                || !player.LoadFlags.TestFlag(Entities.LoadFlags.Spawned))
                return false;

            string source = CurrentPath;
            uint frame = CurrentFrame;
            try
            {
                NetSession.DetachPlaybackForLab(slot);
                if (!SpectatorMode.TakeReplayControl(slot))
                    return false;

                _reader?.Dispose();
                _reader = null;
                _pending = null;
                if (Replay.ReplayVideoExporter.Active) Replay.ReplayVideoExporter.Cancel();
                Replay.ReplayStudio.ResetCache();
                IsActive = false;
                ReplayController.Stop();
                Replay.ReplayHud.Reset();
                Replay.ReplayCamera.Reset();
                ReplayVerification.Reset();
                Replay.ReplayCheckpointManager.NoteReplay(null);
                branchPath = Replay.ReplayLab.WriteBranch(source, frame, slot);
                Console.WriteLine($"[replay] replay lab branch created at frame {frame}: {branchPath}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidOperationException or ArgumentException)
            {
                LastError = "Could not take control of replay: " + ex.Message;
                Console.WriteLine("[replay] " + LastError);
                return false;
            }
        }

        public static void Stop()
        {
            ReplayVerification.Reset();
            if (Replay.ReplayVideoExporter.Active) Replay.ReplayVideoExporter.Cancel();
            Replay.ReplayStudio.ResetCache();
            Replay.ReplayCheckpointManager.NoteReplay(null);
            Replay.ReplayNetworkDiagnostics.Reset();
            IsActive = false;
            ReplayController.Stop();
            Replay.ReplayHud.Reset();
            Replay.ReplayCamera.Reset();
            _reader?.Dispose();
            _reader = null;
            _pending = null;
            _frame = 0;
            _started = false;
        }

        internal static void FailVerification(string error)
        {
            LastResult = ReplayOpenResult.StateMismatch;
            LastError = error;
            _pending = null;
            Console.WriteLine("[replay] " + error);
        }
    }
}
