using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;

namespace MphRead.Mods.Network
{
    /// <summary>Instance-owned replay reader and clock. All compatibility side effects
    /// are delegated to the explicit host; passive sessions never use NetSession.</summary>
    internal sealed class ReplayPlaybackSession : IDisposable
    {
        private readonly IReplaySessionHost _host;
        internal event Action<uint, byte[]>? FactRead;
        public ReplayTransport Transport { get; }
        public IReplaySessionHost Host => _host;
        public ReplayPlaybackSession(IReplaySessionHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            Transport = new ReplayTransport(this);
        }
        public void Dispose() => Stop();
        private DemoReader? _reader;
        private DemoRecord? _pending;
        private ReplayTimelineClip? _clip;
        private int _clipIndex;
        private bool _live;
        private ReplayMetadata? _liveMetadata;
        /// <summary>The frame of the recording about to be replayed.</summary>
        private uint _frame;
        private bool _started;

        public bool IsActive { get; private set; }
        public string? CurrentPath { get; private set; }
        public IReadOnlyList<ReplayEvent> Events => _reader?.Metadata?.Events ?? Array.Empty<ReplayEvent>();
        internal ReplayMetadata? Metadata => _reader?.Metadata ?? _liveMetadata;
        private uint LeadInFrames => _reader?.Metadata?.LeadInFrames ?? 0;
        public uint CurrentFrame => _frame >= LeadInFrames ? _frame - LeadInFrames : 0;
        internal uint RecordingFrame => checked(_frame + (_reader?.Metadata?.OriginRecordingFrame ?? 0));
        internal bool IsWarming => LeadInFrames > 0 && (!_started || _frame < LeadInFrames);
        internal bool HasSimulatedFrame => _started && !IsWarming;
        public uint LastFrame { get; private set; }
        public ReplayOpenResult LastResult { get; private set; }
        public double CurrentSeconds => CurrentFrame / 60.0;
        public double DurationSeconds => LastFrame / 60.0;

        /// <summary>True once the file has no more records -- the scene holds on the last state rather than closing itself.</summary>
        public bool AtEnd => IsActive && !_live && (_clip != null ? _started && _frame >= LastFrame : _pending == null);

        internal void JoinLive(ReplayReplicaCheckpoint construction, uint frame, ulong mapHash)
        {
            if (_host is not PassiveReplaySessionHost passive) throw new InvalidOperationException("Live capture requires a private replica.");
            Stop(); _host.Start(); passive.State.RestoreCheckpoint(construction);
            _frame = frame; _live = true; _started = false; IsActive = true; LastFrame = frame;
            _liveMetadata = new ReplayMetadata { MapHash = mapHash, RoomKey = passive.State.Match?.RoomKey ?? "" };
            LastResult = ReplayOpenResult.Success; LastError = null;
        }

        internal void AdvanceLive(uint frame, IReadOnlyList<ReplayTimelineRecord> records)
        {
            if (!_live || _started && frame != _frame + 1) throw new InvalidOperationException("Live replica frames must be contiguous.");
            foreach (var record in records)
                if (record.Kind is ReplayFactKind.Match or ReplayFactKind.Roster or ReplayFactKind.Snapshot or ReplayFactKind.Intent)
                    _host.Inject(record.Payload.ToArray(), record.RecordingFrame);
            _frame = LastFrame = frame; _started = true;
        }

        internal void Join(ReplayTimelineClip clip, ReplayReplicaCheckpoint construction)
        {
            if (_host is not PassiveReplaySessionHost passive || clip.RestorePoint.Kind != ReplayRestoreKind.ReplicaCheckpoint)
                throw new InvalidOperationException("A frozen world clip requires a passive replica host and complete checkpoint.");
            Stop();
            _host.Start(); passive.State.RestoreCheckpoint(construction);
            _clip = clip; _clipIndex = 0; _frame = clip.RestorePoint.RecordingFrame; CurrentPath = null;
            LastFrame = clip.EndRecordingFrame; IsActive = true; _started = false;
            LastResult = ReplayOpenResult.Success; LastError = null;
            Transport.Begin();
        }

        /// <summary>
        /// Why the last <see cref="Join"/> failed, for a screen that is
        /// still open to show it on -- Console.WriteLine is where this used
        /// to only go, which is invisible on the Windows build outside a
        /// typed command.
        /// </summary>
        public string? LastError { get; private set; }

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
        public bool Join(string path, int timeoutMs = 8000)
        {
            try { return OpenFile(path, timeoutMs); }
            catch (Exception ex) when (ex is InvalidDataException or IOException
                or UnauthorizedAccessException or ArgumentException)
            {
                LastResult = ReplayOpenResult.Corrupt;
                LastError = "Cannot open replay: " + ex.Message;
                Stop();
                return false;
            }
        }

        private bool OpenFile(string path, int timeoutMs)
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
            bool pathChanged = CurrentPath != path;
            if (pathChanged) Transport.ClearSelection();
            CurrentPath = path;
            _host.Prepare(path, pathChanged);
            LastFrame = _reader.FormatVersion >= 3 ? _reader.DurationFrames : DemoLibrary.Duration(path);
            _host.Start();
            IsActive = true;
            _frame = 0;
            _started = false;
            _host.ResetDiagnostics();
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
                if (metadata.WorldCheckpoint.Length > 0)
                {
                    if (_host is not PassiveReplaySessionHost passive)
                        throw new InvalidDataException("This replay requires the isolated world player.");
                    var world = Replay.ReplayWorldCheckpoint.FromBytes(metadata.WorldCheckpoint);
                    if (world.Frame != metadata.OriginRecordingFrame)
                        throw new InvalidDataException("Replay origin differs from its initial world.");
                    passive.State.RestoreCheckpoint(world.ConstructionState());
                    _pending = _reader.ReadNext();
                    if (_pending == null) throw new InvalidDataException("Replay contains no completed frames.");
                    Transport.Begin();
                    return true;
                }
                if (metadata.Bootstrap.Packets.Count > 0)
                {
                foreach (byte[] packet in metadata.Bootstrap.Packets)
                    _host.Inject(packet, 0);
                _host.Advance(0);
                if (_host.Match?.RoomKey.Length is not > 0)
                {
                    LastResult = ReplayOpenResult.MissingMatchState;
                    LastError = "Replay bootstrap has no match state.";
                    Stop();
                    return false;
                }
                _host.Rewind();
                foreach (byte[] packet in metadata.Bootstrap.Packets)
                    _host.Inject(packet, 0);
                _pending = _reader.ReadNext();
                if (_pending == null)
                {
                    LastResult = _reader.LastResult == ReplayOpenResult.Success ? ReplayOpenResult.Empty : _reader.LastResult;
                    LastError = $"Cannot play replay: {LastResult}.";
                    Stop();
                    return false;
                }
                Transport.Begin();
                return true;
                }
            }
            _pending = _reader.ReadNext();
            bool hadRecords = _pending != null;
            long knownAt = -1;
            while (_frame < JoinSearchFrames)
            {
                PumpFrame();
                _host.Advance(_frame / 60.0);
                if (_host.Match?.RoomKey.Length > 0)
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
        private bool Rewind(string path)
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
            _host.Rewind();
            Transport.Begin();
            return true;
        }

        /// <summary>
        /// Reposition packet playback after an in-memory world checkpoint.
        /// V3 lands directly on the indexed chunk; v2 falls back to a sequential scan.
        /// No packets at or before the checkpoint are re-applied because the checkpoint
        /// already contains their resulting world state.
        /// </summary>
        internal bool Reposition(uint frame, uint netFrame, bool sourceClock = false)
        {
            if (IsActive && _clip != null)
            {
                if (frame < _clip.RestorePoint.RecordingFrame || frame > _clip.EndRecordingFrame) return false;
                _clipIndex = 0;
                while (_clipIndex < _clip.Records.Count && _clip.Records[_clipIndex].RecordingFrame <= frame) _clipIndex++;
                _frame = frame; _started = true; _host.RestoreClock(netFrame); _host.SeekTo(frame);
                return true;
            }
            if (!IsActive || CurrentPath == null) return false;
            DemoReader? next = DemoReader.Open(CurrentPath, out ReplayOpenResult result);
            if (next == null || next.ProtocolVersion != NetConfig.ProtocolVersion)
            {
                next?.Dispose();
                LastResult = next == null ? result : ReplayOpenResult.ProtocolMismatch;
                return false;
            }

            uint sourceFrame = sourceClock ? frame : checked(frame + LeadInFrames);
            DemoRecord? pending = next.SeekAfter(sourceFrame);
            if (pending == null && next.LastResult != ReplayOpenResult.Success && frame < LastFrame)
            {
                LastResult = next.LastResult;
                next.Dispose();
                return false;
            }

            _reader?.Dispose();
            _reader = next;
            _pending = pending;
            _frame = sourceFrame;
            _started = true;
            _host.ResetDiagnostics();
            LastResult = ReplayOpenResult.Success;
            LastError = null;
            _host.RestoreClock(netFrame);
            _host.SeekTo(frame);
            return true;
        }

        /// <summary>
        /// Called once a frame: hands over every packet the recorder saw on
        /// this frame of its own run.
        /// </summary>
        public void PumpFrame()
        {
            if (IsActive && _clip != null)
            {
                if (AtEnd) return;
                if (_started) _frame++; else _started = true;
                while (_clipIndex < _clip.Records.Count && _clip.Records[_clipIndex].RecordingFrame <= _frame)
                {
                    var record = _clip.Records[_clipIndex++];
                    if (record.Kind is ReplayFactKind.Match or ReplayFactKind.Roster or ReplayFactKind.Snapshot or ReplayFactKind.Intent)
                        _host.Inject(record.Payload.ToArray(), record.RecordingFrame);
                }
                _host.Advance(_frame / 60.0);
                return;
            }
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
                    _host.Inject(record.Data, checked(record.Frame + (_reader.Metadata?.OriginRecordingFrame ?? 0)));
                    FactRead?.Invoke(record.Frame >= LeadInFrames ? record.Frame - LeadInFrames : 0, record.Data);
                    _pending = _reader.ReadNext();
                }
            }
            catch (InvalidDataException ex)
            {
                LastResult = ReplayOpenResult.Corrupt;
                LastError = $"Replay stopped at frame {_frame}: " + ex.Message;
                _pending = null;
                return;
            }
            if (_pending == null)
            {
                LastResult = _reader.LastResult;
                if (LastResult != ReplayOpenResult.Success) LastError = $"Replay stopped: {LastResult}.";
            }
        }

        public bool TakeControl(int slot, out string? branchPath)
        {
            branchPath = null;
            if (!IsActive || CurrentPath == null || !_host.CanTakeControl(slot)) return false;
            string source = CurrentPath;
            uint frame = CurrentFrame;
            try
            {
                if (!_host.TakeControl(slot)) return false;
                CloseReader();
                Transport.Stop();
                _host.Detached();
                branchPath = Replay.ReplayLab.WriteBranch(source, frame, slot);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidOperationException or ArgumentException)
            {
                LastError = "Could not take control of replay: " + ex.Message;
                return false;
            }
        }

        public void Stop()
        {
            _clip = null; _clipIndex = 0;
            _live = false; _liveMetadata = null;
            _host.Stop();
            Transport.Stop();
            CloseReader();
        }

        private void CloseReader()
        {
            IsActive = false;
            _reader?.Dispose();
            _reader = null;
            _pending = null;
            _frame = 0;
            _started = false;
        }

        internal void FailVerification(string error)
        {
            LastResult = ReplayOpenResult.StateMismatch;
            LastError = error;
            _pending = null;
            Console.WriteLine("[replay] " + error);
        }
    }
}
