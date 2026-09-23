using System;
using System.Collections.Generic;
using MphRead.Entities;

namespace MphRead
{
    /// <summary>Players and allocation cursors belonging to one simulation world.</summary>
    public sealed class ScenePlayerRegistry
    {
        internal PlayerEntity[] Values { get; } = new PlayerEntity[PlayerEntity.SlotCapacity];
        public IReadOnlyList<PlayerEntity> Items => Values;
        public int MainPlayerIndex { get; set; }
        public int PlayerCount { get; set; }
        public int MaxPlayers { get; set; } = 4;
        public int PlayersCreated { get; set; }
        public PlayerEntity Main => Values[MainPlayerIndex];
        internal void Construct(Scene scene)
        {
            for (int i = 0; i < Values.Length; i++) Values[i] ??= new PlayerEntity(i, scene);
        }
        public void Reset()
        {
            Array.Clear(Values);
            PlayerCount = PlayersCreated = MainPlayerIndex = 0;
        }
        public PlayerEntity? Create(Hunter hunter, int recolor)
        {
            if (PlayersCreated >= MaxPlayers) return null;
            PlayerEntity player = Values[PlayersCreated++];
            player.Hunter = hunter;
            player.Recolor = recolor;
            player.LoadFlags |= LoadFlags.SlotActive;
            player.LoadFlags &= ~LoadFlags.Spawned;
            player.CreateHalfturret();
            return player;
        }
    }
}
