using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenTK.Mathematics;

namespace MphRead.Mods.Multiplayer
{
    public static class ResourceAudit
    {
        // Asset-backed metadata checks. Distances are straight lines, not path
        // lengths: reachability and dominant routes still require playtesting.
        public static int Run()
        {
            var scenarios = new (string Label, GameMode Mode, int Players)[]
            {
                ("FFA2", GameMode.Battle, 2), ("FFA3", GameMode.Battle, 3),
                ("FFA4", GameMode.Battle, 4), ("FFA8", GameMode.Battle, 8),
                ("1v1", GameMode.BattleTeams, 2), ("2v2", GameMode.BattleTeams, 4),
                ("3v3", GameMode.BattleTeams, 6), ("4v4", GameMode.BattleTeams, 8),
                ("4v2", GameMode.BattleTeams, 6), ("2v2v2v2", GameMode.BattleTeams, 8),
                ("BountyFFA4", GameMode.Bounty, 4), ("BountyTeams8", GameMode.BountyTeams, 8),
                ("NodesFFA8", GameMode.Nodes, 8), ("NodesTeams8", GameMode.NodesTeams, 8),
                ("DefenderFFA8", GameMode.Defender, 8), ("DefenderTeams8", GameMode.DefenderTeams, 8),
                ("Capture2v2", GameMode.Capture, 4), ("Capture4v4", GameMode.Capture, 8)
            };
            int checkedCount = 0, missingLayers = 0, missingObjectives = 0, failures = 0;
            Console.WriteLine("room\tlayout\tlayer\tprofile\tspawns\tbaseHealth\thealth\thealthUnits\tmaxSpawnDistance\tmaxObjectiveDistance\trespawnSeconds\tobjectives\tfingerprint");
            foreach (string key in ThumbnailGenerator.MultiplayerRooms())
            {
                RoomMetadata room = Metadata.RoomMetadata[key];
                if (room.FirstHunt || room.EntityPath == null) continue;
                foreach (var scenario in scenarios)
                {
                    try
                    {
                        MatchWorldProfile profile = MatchWorldProfile.Resolve(scenario.Players);
                        int layer = Metadata.GetMultiplayerEntityLayer(scenario.Mode, profile.EntityLayerPlayers);
                        IReadOnlyList<Entity> original = Read.GetEntities(room.EntityPath, layer, false, allowHook: true);
                        IReadOnlyList<Entity> actual = MapResourceRules.Resolve(room, profile.Resources, original);
                        var spawns = actual.OfType<Entity<PlayerSpawnEntityData>>().Where(e => e.Data.Active != 0).ToArray();
                        if (spawns.Length == 0) missingLayers++;
                        Entity<ItemSpawnEntityData>[] health = Health(actual);
                        int baseHealth = Health(original).Length;
                        var objectives = actual.Where(e => e.Type is EntityType.FlagBase or EntityType.OctolithFlag
                            or EntityType.NodeDefense).ToArray();
                        if (scenario.Mode is not (GameMode.Battle or GameMode.BattleTeams) && objectives.Length == 0)
                            missingObjectives++;
                        int units = 0;
                        var intervals = new SortedSet<int>();
                        foreach (var item in health)
                        {
                            var data = MapResourceRules.ResolveData(room, profile.Resources, item.Data);
                            units += data.ItemType == ItemType.HealthSmall ? 60 : data.ItemType == ItemType.HealthMedium ? 30 : 100;
                            intervals.Add(data.SpawnInterval / 30);
                        }
                        string fingerprint = Fingerprint(room, profile.Resources, health);
                        string repeated = Fingerprint(room, profile.Resources,
                            Health(MapResourceRules.Resolve(room, profile.Resources, original)));
                        if (fingerprint != repeated || actual.Select(e => e.EntityId).Distinct().Count() != actual.Count)
                            throw new ProgramException("Nondeterministic or duplicate entity IDs.");
                        foreach (var item in health.Where(e => !original.Contains(e)))
                            if (health.Any(other => other != item && (item.Position - other.Position).LengthSquared < 1))
                                throw new ProgramException("Supplemental health overlaps another pickup.");
                        Console.WriteLine(String.Join('\t', key, scenario.Label, layer, profile.Resources, spawns.Length,
                            baseHealth, health.Length, units, Distance(spawns.Select(e => e.Position), health),
                            Distance(objectives.Select(e => e.Position), health), String.Join('/', intervals), objectives.Length, fingerprint));
                        checkedCount++;
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        Console.Error.WriteLine($"RESOURCEFAIL {key} {scenario.Label}: {ex.Message}");
                    }
                }
            }
            Console.Error.WriteLine($"Resource audit: {checkedCount} layouts checked, {failures} failures, "
                + $"{missingLayers} layouts have no native player spawns; {missingObjectives} objective layouts lack native objectives. "
                + "Distances are geometric; routes, reachability and competitive balance require gameplay acceptance.");
            return failures == 0 ? 0 : 1;
        }

        private static Entity<ItemSpawnEntityData>[] Health(IReadOnlyList<Entity> entities) => entities
            .OfType<Entity<ItemSpawnEntityData>>()
            .Where(e => e.Data.Enabled != 0 && MapResourceRules.IsHealth(e.Data.ItemType)).ToArray();

        private static string Distance(IEnumerable<Vector3> from, Entity<ItemSpawnEntityData>[] health)
        {
            if (health.Length == 0) return "n/a";
            float max = 0;
            bool any = false;
            foreach (Vector3 position in from)
            {
                any = true;
                float nearest = Single.MaxValue;
                foreach (var item in health) nearest = Math.Min(nearest, (item.Position - position).Length);
                max = Math.Max(max, nearest);
            }
            return any ? max.ToString("F2", CultureInfo.InvariantCulture) : "n/a";
        }

        private static string Fingerprint(RoomMetadata room, ResourceSpawnProfile profile,
            Entity<ItemSpawnEntityData>[] health)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> bytes = stackalloc byte[72];
            foreach (var item in health)
            {
                var data = MapResourceRules.ResolveData(room, profile, item.Data);
                MemoryMarshal.Write(bytes, in data);
                hash.AppendData(bytes);
            }
            return Convert.ToHexString(hash.GetHashAndReset())[..16];
        }
    }
}
