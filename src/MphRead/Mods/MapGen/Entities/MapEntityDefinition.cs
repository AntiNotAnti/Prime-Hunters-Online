using System;

namespace MphRead.Mods.MapGen
{
    // Legacy lists retain their JSON shape and runtime compiler. Shared identity
    // and position give editor commands one typed authoring contract.
    public abstract class MapEntityDefinition
    {
        public Guid Id { get; set; }
        public string? Label { get; set; }
        public float[] Position { get; set; } = new float[3];
    }
}
