namespace MphRead.Entities
{
    public partial class PlayerEntity
    {
        internal void ModBootstrapSpawn()
        {
            if (!Mods.Network.NetSession.IsAuthority || !LoadFlags.TestFlag(LoadFlags.Active)
                || Mods.Network.NetPlayerLifecycle.Get(SlotIndex) != 0) return;
            var point = GetRespawnPoint();
            if (point != null) Spawn(ForcedSpawnPos ?? point.Position, point.FacingVector,
                point.UpVector, point.NodeRef, respawn: true);
        }

        /// <summary>
        /// Prepare a hunter that was not part of the initial active roster.
        /// This is used by late joins and in-match hunter changes.
        /// </summary>
        internal void ModPrepareHunterResources(Hunter hunter)
        {
            SceneSetup.LoadHunterResources(hunter, _scene);
        }
    }
}
