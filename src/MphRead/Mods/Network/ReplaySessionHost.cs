using System;
using MphRead.Entities;

namespace MphRead.Mods.Network
{
    /// <summary>Explicit boundary between recorded facts and foreground compatibility services.</summary>
    internal interface IReplaySessionHost
    {
        bool IsPassive { get; }
        MatchStatePacket? Match { get; }
        void Prepare(string path, bool pathChanged);
        void Start();
        void Stop();
        void Rewind();
        void Inject(byte[] packet, uint frame);
        void Advance(double seconds);
        void RestoreClock(uint frame);
        void ResetDiagnostics();
        void SeekTo(uint frame);
        bool CanTakeControl(int slot) => false;
        bool TakeControl(int slot) => false;
        void Detached() { }
    }

    /// <summary>The sole adapter allowed to use the legacy foreground packet pipeline.</summary>
    internal sealed class TheatreReplaySessionHost : IReplaySessionHost
    {
        public bool IsPassive => false;
        public MatchStatePacket? Match => NetSession.ServerMatch;
        public void Prepare(string path, bool pathChanged)
        {
            Rng.SetRng1(Rng.Rng1StartValue);
            Rng.SetRng2(Rng.Rng2StartValue);
            SpinningEntityBase.ResetReplayRotation();
            if (pathChanged) Replay.ReplayCamera.ClearBookmarks();
            Replay.ReplayCheckpointManager.NoteReplay(path);
        }
        public void Start() => NetSession.StartPlayback();
        public void Stop()
        {
            ReplayVerification.Reset();
            if (Replay.ReplayVideoExporter.Active) Replay.ReplayVideoExporter.Cancel();
            Replay.ReplayStudio.ResetCache();
            Replay.ReplayCheckpointManager.NoteReplay(null);
            Replay.ReplayNetworkDiagnostics.Reset();
            Replay.ReplayHud.Reset();
            Replay.ReplayCamera.Reset();
        }
        public void Rewind() => NetSession.RewindPlayback();
        public void Inject(byte[] packet, uint frame)
        {
            Replay.ReplayNetworkDiagnostics.OnPacket(frame, packet);
            NetSession.InjectPlaybackPacket(packet, packet.Length,
                ReplayPlaybackSession.PlaybackArrivalTicks(frame));
        }
        public void Advance(double seconds) => NetSession.Update(seconds);
        public void RestoreClock(uint frame) => NetSession.PreparePlaybackCheckpoint(frame);
        public void ResetDiagnostics() => Replay.ReplayNetworkDiagnostics.Reset();
        public void SeekTo(uint frame) => ReplayVerification.SeekTo(frame);
        public bool CanTakeControl(int slot) => SpectatorMode.IsSpectating
            && (uint)slot < PlayerEntity.Players.Count
            && PlayerEntity.Players[slot].LoadFlags.TestFlag(LoadFlags.Active)
            && PlayerEntity.Players[slot].LoadFlags.TestFlag(LoadFlags.Spawned);
        public bool TakeControl(int slot)
        {
            NetSession.DetachPlaybackForLab(slot);
            return SpectatorMode.TakeReplayControl(slot);
        }
        public void Detached() => Stop();
    }

    /// <summary>No transport, connection, lobby, presentation singleton or live lifecycle access.</summary>
    internal sealed class PassiveReplaySessionHost : IReplaySessionHost
    {
        public ReplayReplicaState State { get; } = new();
        public bool IsPassive => true;
        public MatchStatePacket? Match => State.Match;
        public void Prepare(string path, bool pathChanged) { }
        public void Start() => State.Reset();
        public void Stop() => State.Reset();
        public void Rewind() => State.Rewind();
        public void Inject(byte[] packet, uint frame) => State.Accept(packet, frame);
        public void Advance(double seconds) { }
        public void RestoreClock(uint frame) => State.Rewind();
        public void ResetDiagnostics() { }
        public void SeekTo(uint frame) { }
    }
}
