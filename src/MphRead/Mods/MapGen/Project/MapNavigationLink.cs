using System;

namespace MphRead.Mods.MapGen
{
    public enum MapNavigationLinkKind { Jump, Drop, JumpPad, Teleporter, Platform, Manual }
    public sealed class MapNavigationLink
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public MapNavigationLinkKind Kind { get; set; } = MapNavigationLinkKind.Manual;
        public float[] From { get; set; } = new float[3];
        public float[] To { get; set; } = new float[3];
        public bool Bidirectional { get; set; }
    }
}
