namespace MphRead.Mods.Network
{
    internal sealed class ReplaySceneServices : ISceneServices
    {
        public ReplayPlaybackSession Session { get; }
        public ReplayReplicaState State { get; }
        public bool IsReplica => true;
        public bool MayEndOnScore => false;
        public bool ShouldLeaveAfterMatch => false;
        public bool SuppressDamage => true;
        public bool AllowsPresentationSideEffects => false;
        public ReplaySceneServices(ReplayPlaybackSession session, ReplayReplicaState state)
        { Session = session; State = state; }
    }
}
