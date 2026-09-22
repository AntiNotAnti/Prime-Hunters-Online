using System;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRead.Mods.Input;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    /// <summary>A scene and reader with a common lifetime, separate from the foreground host.</summary>
    internal sealed class PassiveReplayScene : IDisposable
    {
        public ReplayPlaybackSession Session { get; }
        public ReplayReplicaState State { get; }
        public Scene Scene { get; }
        internal Replay.ReplayWorldCheckpoint.Bindings CheckpointBindings { get; private set; } = null!;
        internal ReplayReplicaCheckpoint InitialState { get; }
        internal ulong MapHash { get; }
        internal bool HasStepped { get; private set; }
        private bool _disposed;
        public PassiveReplayScene(string path, Vector2i size) : this(Open(path), size) { }
        public PassiveReplayScene(ReplayTimelineClip clip, Vector2i size) : this(Open(clip), size)
        {
            try
            {
                Checkpoint(clip).Restore(this);
                Session.Transport.ContinueSeek(clip.StartRecordingFrame, resume: true);
                Session.Transport.AfterFrame();
            }
            catch { Dispose(); throw; }
        }
        internal static Replay.ReplayWorldCheckpoint Checkpoint(ReplayTimelineClip clip)
        {
            if (clip.RestorePoint.Kind != ReplayRestoreKind.ReplicaCheckpoint)
                throw new InvalidDataException("A network baseline cannot restore a historical world.");
            var record = clip.RestorePoint.Records.SingleOrDefault(r => r.Kind == ReplayFactKind.World)
                ?? throw new InvalidDataException("The clip has no historical world checkpoint.");
            var checkpoint = Replay.ReplayWorldCheckpoint.FromBytes(record.Payload);
            if (checkpoint.Frame != clip.RestorePoint.RecordingFrame) throw new InvalidDataException("Checkpoint frame differs from the clip index.");
            return checkpoint;
        }
        private static ReplayPlaybackSession Open(string path)
        {
            var session = new ReplayPlaybackSession(new PassiveReplaySessionHost());
            if (session.Join(path)) return session;
            session.Dispose(); throw new InvalidDataException(session.LastError);
        }
        private static ReplayPlaybackSession Open(ReplayTimelineClip clip)
        {
            var checkpoint = Checkpoint(clip);
            var session = new ReplayPlaybackSession(new PassiveReplaySessionHost());
            try { session.Join(clip, checkpoint.ConstructionState()); return session; }
            catch { session.Dispose(); throw; }
        }
        private PassiveReplayScene(ReplayPlaybackSession session, Vector2i size)
        {
            Session = session;
            State = ((PassiveReplaySessionHost)session.Host).State;
            MatchStatePacket match = State.Match ?? throw new InvalidDataException("Replay has no room.");
            InitialState = State.CaptureCheckpoint();
            MapHash = Session.Metadata?.MapHash is > 0 ? Session.Metadata.MapHash : ReplayMapIdentity.Compute(match.RoomKey);
            Scene = new Scene(size, SyntheticInput.CreateKeyboard(), SyntheticInput.CreateMouse(), _ => { }, () => { },
                new ReplaySceneServices(Session, State));
            try
            {
                Scene.GameState.Mode = (GameMode)match.Mode;
                if (Scene.GameState.SinglePlayer) throw new InvalidDataException("Passive reconstruction requires a recorded multiplayer world.");
                ((ReplaySceneServices)Scene.Services).ApplyRules(Scene, 0);
                for (int slot = 0; slot < PlayerEntity.SlotCapacity; slot++)
                {
                    var occupant = State.Occupant(slot);
                    Scene.AddPlayer(occupant.Generation == 0 ? Hunter.Samus : occupant.Hunter, occupant.Color);
                    Scene.Players.Items[slot].IsBot = false;
                }
                Scene.AddRoom(match.RoomKey, (GameMode)match.Mode,
                    playerCount: Scene.Services.NetworkWorldProfile?.EntityLayerPlayers ?? match.PlayerCount);
                ((ReplaySceneServices)Scene.Services).ApplyRules(Scene, 0);
                Scene.OnLoad();
                CheckpointBindings = new(Scene);
            }
            catch { Dispose(); throw; }
        }
        public bool Step()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Session.AtEnd) return false;
            Session.PumpFrame();
            if (Session.LastResult != ReplayOpenResult.Success) throw new InvalidDataException(Session.LastError);
            Scene.StepReplica();
            HasStepped = true;
            Session.Transport.AfterFrame();
            return true;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Scene.DoCleanup();
            Scene.UnloadGl();
            Session.Dispose();
        }
    }
}
