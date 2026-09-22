namespace MphRead
{
    /// <summary>Host policy is explicit for each scene, including headless replicas.</summary>
    public interface ISceneServices
    {
        bool IsReplica { get; }
        bool MayEndOnScore { get; }
        bool ShouldLeaveAfterMatch { get; }
        bool SuppressDamage { get; }
        bool AllowsPresentationSideEffects { get; }
    }
    internal sealed class LiveSceneServices : ISceneServices
    {
        internal static LiveSceneServices Instance { get; } = new();
        public bool IsReplica => false;
        public bool MayEndOnScore => true;
        public bool ShouldLeaveAfterMatch => true;
        public bool SuppressDamage => false;
        public bool AllowsPresentationSideEffects => true;
    }
}
