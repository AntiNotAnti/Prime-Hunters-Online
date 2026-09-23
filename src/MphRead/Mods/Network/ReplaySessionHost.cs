using System;

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
        void Inject(ReadOnlySpan<byte> packet, uint frame);
        void Advance(double seconds);
        void RestoreClock(uint frame);
        void ResetDiagnostics();
        void SeekTo(uint frame);
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
        public void Inject(ReadOnlySpan<byte> packet, uint frame) => State.Accept(packet, frame);
        public void Advance(double seconds) { }
        public void RestoreClock(uint frame) => State.Rewind();
        public void ResetDiagnostics() { }
        public void SeekTo(uint frame) { }
    }
}
