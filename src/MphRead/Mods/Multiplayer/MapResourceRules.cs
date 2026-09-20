using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MphRead.Mods.Multiplayer
{
    // Only references to locations already authored in each room. The player's
    // own assets supply the positions, node names and pickup definitions.
    public static class MapResourceRules
    {
        private static readonly IReadOnlyDictionary<string, short[]> _highHealth =
            new Dictionary<string, short[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["MP1 SANCTORUS"] = [7, 11, 46, 47, 4, 5],
                ["MP2 HARVESTER"] = [27, 29, 30, 38, 57, 58, 63, 64, 60, 59, 2, 6],
                ["MP3 PROVING GROUND"] = [10, 11, 12, 26, 0],
                ["MP4 HIGHGROUND - EXPANDED"] = [1, 2, 16, 28, 32, 54, 73, 74, 75],
                ["MP4 HIGHGROUND"] = [1, 2, 16, 27, 28, 46],
                ["MP5 FUEL SLUICE"] = [16, 17, 22, 27, 31, 3, 4],
                ["MP6 HEADSHOT"] = [28, 30, 32, 34, 40, 49, 54, 55, 69, 70, 71, 72, 12, 4, 5, 13],
                ["MP7 PROCESSOR CORE"] = [6, 9, 14, 15, 19, 20, 21],
                ["MP8 FIRE CONTROL"] = [26, 63, 7, 17, 18, 35],
                ["MP9 CRYOCHASM"] = [0, 3, 23, 37, 36, 15, 26],
                ["MP10 OVERLOAD"] = [1, 4, 10, 6, 19],
                ["MP11 BREAKTHROUGH"] = [2, 3, 8, 19],
                ["MP12 SIC TRANSIT"] = [8, 15, 43, 10],
                ["MP13 ACCELERATOR"] = [6, 7, 8, 15, 16, 17, 23, 45],
                ["MP14 OUTER REACH"] = [35, 36, 19, 18],
                ["CTF1 FAULT LINE - EXPANDED"] = [9, 27, 34, 18, 28, 32, 54, 56, 75, 76],
                ["CTF1_FAULT LINE"] = [9, 43, 44, 25, 49, 40],
                ["AD1 TRANSFER LOCK BT"] = [5, 11, 12, 26, 37, 51, 52, 53, 57, 58],
                ["AD1 TRANSFER LOCK DM"] = [9, 33, 34, 41, 44, 45],
                ["AD2 MAGMA VENTS"] = [31, 32, 33, 35, 34, 17, 58, 70, 71],
                ["AD2 ALINOS PERCH"] = [22, 25, 27, 42, 48, 49, 18, 14, 2],
                ["UNIT1 ALINOS LANDFALL"] = [26, 29, 30, 19, 34, 37],
                ["UNIT2 LANDING BAY"] = [12, 13, 16, 15, 21, 22, 24, 25, 26, 29],
                ["UNIT 3 VESPER STARPORT"] = [2, 19, 23, 40, 41, 42, 43, 44, 60, 61, 62, 63],
                ["UNIT 4 ARCTERRA BASE"] = [53, 55, 56, 58],
                ["Gorea Prison"] = [13, 20, 21],
                ["E3 FIRST HUNT"] = [15, 16, 10, 20, 50]
            };

        public static bool IsHealth(ItemType type) => type is ItemType.HealthSmall
            or ItemType.HealthMedium or ItemType.HealthBig;

        public static IReadOnlyList<Entity> Resolve(RoomMetadata room, ResourceSpawnProfile profile,
            IReadOnlyList<Entity> original)
        {
            if (!room.Multiplayer || room.FirstHunt || room.EntityPath == null
                || !_highHealth.TryGetValue(room.Name, out short[]? ids))
                return original;
            if (profile != ResourceSpawnProfile.High)
            {
                // Transfer Lock's bounty variant has no Battle health in any
                // population layer. Use its authored bounty health routes.
                if (!room.Name.Equals("AD1 TRANSFER LOCK BT", StringComparison.OrdinalIgnoreCase)) return original;
                ids = profile == ResourceSpawnProfile.Low ? [5, 26, 37] : [5, 11, 12, 26, 37, 57, 58];
            }

            var result = new List<Entity>(original);
            var used = new HashSet<short>();
            foreach (Entity entity in original) used.Add(entity.EntityId);
            IReadOnlyList<Entity> all = Read.GetEntities(room.EntityPath, -1, false, allowHook: true);
            // Source-file order is stable across server, clients and reconnects.
            foreach (Entity entity in all)
            {
                if (Array.IndexOf(ids, entity.EntityId) < 0 || used.Contains(entity.EntityId)
                    || entity is not Entity<ItemSpawnEntityData> item || !IsHealth(item.Data.ItemType)
                    || item.Data.Enabled == 0 || item.Data.ParentId is not (-1 or 65535)) continue;
                bool overlaps = false;
                foreach (Entity existing in result)
                {
                    if (existing is Entity<ItemSpawnEntityData> other && other.Data.Enabled != 0
                        && IsHealth(other.Data.ItemType) && (existing.Position - entity.Position).LengthSquared < 1)
                    { overlaps = true; break; }
                }
                if (overlaps) continue;
                result.Add(entity);
                used.Add(entity.EntityId);
            }
            return result;
        }

        public static ItemSpawnEntityData ResolveData(RoomMetadata room, ResourceSpawnProfile profile,
            ItemSpawnEntityData data)
        {
            if (profile != ResourceSpawnProfile.High || !room.Multiplayer || room.FirstHunt
                || !_highHealth.ContainsKey(room.Name) || !IsHealth(data.ItemType)) return data;
            // High population keeps the original routes but caps health downtime
            // at ten seconds (the source format stores thirty ticks per second).
            if (data.SpawnInterval <= 300) return data;
            Span<byte> bytes = stackalloc byte[72];
            MemoryMarshal.Write(bytes, in data);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[54..], 300);
            return Read.ReadStruct<ItemSpawnEntityData>(bytes);
        }
    }
}
