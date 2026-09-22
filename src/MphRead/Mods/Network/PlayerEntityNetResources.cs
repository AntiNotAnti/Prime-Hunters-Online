namespace MphRead.Entities
{
    public partial class PlayerEntity
    {
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
