using System;
using System.Collections.Generic;
using MphRead.Editor;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.MapGen
{
    /// <summary>
    /// Builds a map from its hand-written description: boxes in, geometry and
    /// collision out.
    ///
    /// Both come from the same brush list on purpose. The oldest bug in level
    /// editing is a wall you can see and walk through, or one you can walk
    /// into and not see; deriving both from one description makes that
    /// particular mistake impossible to express.
    /// </summary>
    public static class MapBuilder
    {
        /// <summary>
        /// What a custom map may put on the floor: what the cartridge's own
        /// multiplayer rooms hold, and nothing else.
        ///
        /// The ones left out are not a matter of taste. An energy tank, a
        /// missile expansion, a UA expansion and an artifact are the story's
        /// pickups: they raise a hunter's *capacity* permanently instead of
        /// topping it up until the end of the match, so one of them in a
        /// deathmatch is a player who is simply better than the others for as
        /// long as the session lasts. Double Damage, Cloak and Deathalt are
        /// the powerups a multiplayer room does have -- they run out.
        /// </summary>
        public static readonly HashSet<ItemType> MultiplayerItems = new HashSet<ItemType>()
        {
            ItemType.HealthSmall, ItemType.HealthMedium, ItemType.HealthBig,
            ItemType.UASmall, ItemType.UABig,
            ItemType.MissileSmall, ItemType.MissileBig,
            ItemType.DoubleDamage, ItemType.Cloak, ItemType.Deathalt,
            ItemType.VoltDriver, ItemType.Battlehammer, ItemType.Imperialist,
            ItemType.Judicator, ItemType.Magmaul, ItemType.ShockCoil,
            ItemType.OmegaCannon, ItemType.AffinityWeapon
        };

        // brighter on top, darker underneath: with no lightmaps and no
        // per-vertex lighting to import, this is what keeps a box from
        // reading as a flat silhouette
        private static readonly float[] _faceShades = new[] { 1f, 0.55f, 0.82f, 0.82f, 0.74f, 0.74f };

        public static BuiltMap Build(MapDefinition def)
        {
            var map = new BuiltMap(def);
            foreach (MapBrush brush in def.Brushes)
            {
                AddBrush(map, def, brush);
            }
            AddEntities(map, def);
            return map;
        }

        private static void AddBrush(BuiltMap map, MapDefinition def, MapBrush brush)
        {
            float x0 = MathF.Min(brush.Min[0], brush.Max[0]);
            float y0 = MathF.Min(brush.Min[1], brush.Max[1]);
            float z0 = MathF.Min(brush.Min[2], brush.Max[2]);
            float x1 = MathF.Max(brush.Min[0], brush.Max[0]);
            float y1 = MathF.Max(brush.Min[1], brush.Max[1]);
            float z1 = MathF.Max(brush.Min[2], brush.Max[2]);
            // wound counter-clockwise seen from outside, so back-face culling keeps them
            var sides = new (Vector3[] points, Vector3 normal)[]
            {
                (new[] { new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0) }, Vector3.UnitY),
                (new[] { new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), new Vector3(x0, y0, z1) }, -Vector3.UnitY),
                (new[] { new Vector3(x1, y0, z1), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1) }, Vector3.UnitX),
                (new[] { new Vector3(x0, y0, z0), new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x0, y1, z0) }, -Vector3.UnitX),
                (new[] { new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1) }, Vector3.UnitZ),
                (new[] { new Vector3(x1, y0, z0), new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0) }, -Vector3.UnitZ)
            };
            float texScale = brush.Material >= 0 && brush.Material < def.Materials.Count
                ? def.Materials[brush.Material].TexScale
                : 16f;
            var origin = new Vector3(x0, y0, z0);
            Terrain terrain = Terrain.Metal;
            if (brush.Terrain != null && Enum.TryParse(brush.Terrain, ignoreCase: true, out Terrain parsed))
            {
                terrain = parsed;
            }
            for (int i = 0; i < sides.Length; i++)
            {
                (Vector3[] points, Vector3 normal) = sides[i];
                var texcoords = new Vector2[points.Length];
                for (int j = 0; j < points.Length; j++)
                {
                    texcoords[j] = Project(points[j], normal, origin, texScale);
                }
                var face = new BuiltFace(points, texcoords, normal, brush.Material,
                    _faceShades[i] * brush.Shade)
                {
                    Damaging = brush.Damaging,
                    Terrain = terrain
                };
                map.Faces.Add(face);
                if (brush.Solid)
                {
                    map.Solid.Add(face);
                }
            }
        }

        /// <summary>
        /// World-aligned texture coordinates, measured from the brush's own
        /// corner so the texel numbers stay small: they are 1.11.4 fixed
        /// point, which runs out at 2047 texels.
        /// </summary>
        private static Vector2 Project(Vector3 point, Vector3 normal, Vector3 origin, float texScale)
        {
            float ax = MathF.Abs(normal.X);
            float ay = MathF.Abs(normal.Y);
            float az = MathF.Abs(normal.Z);
            if (ay > ax && ay >= az)
            {
                return new Vector2((point.X - origin.X) * texScale, (point.Z - origin.Z) * texScale);
            }
            if (ax >= az)
            {
                return new Vector2((point.Z - origin.Z) * texScale, (origin.Y - point.Y) * texScale);
            }
            return new Vector2((point.X - origin.X) * texScale, (origin.Y - point.Y) * texScale);
        }

        public static void AddEntities(BuiltMap map, MapDefinition def)
        {
            short id = (short)map.Entities.Count;
            foreach (MapSpawn spawn in def.Spawns)
            {
                float yaw = MathHelper.DegreesToRadians(spawn.Yaw);
                map.Entities.Add(new PlayerSpawnEntityEditor()
                {
                    Id = id++,
                    LayerMask = 0xFFFF,
                    Position = ToVector(spawn.Position),
                    Up = Vector3.UnitY,
                    Facing = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw)).Normalized(),
                    NodeName = "rmMain",
                    Active = true,
                    Availability = 0,
                    TeamIndex = -1
                });
            }
            foreach (MapJumpPad pad in def.JumpPads)
            {
                (Vector3 beam, float speed) = SolveJumpPad(pad);
                map.Entities.Add(new JumpPadEntityEditor()
                {
                    Id = id++,
                    LayerMask = 0xFFFF,
                    Position = ToVector(pad.Position),
                    Up = Vector3.UnitY,
                    Facing = Vector3.UnitZ,
                    NodeName = "rmMain",
                    ParentId = -1,
                    Volume = MakeBox(pad.Size),
                    BeamVector = beam,
                    Speed = speed,
                    ControlLockTime = pad.ControlLockTime,
                    CooldownTime = pad.CooldownTime,
                    Active = true,
                    ModelId = pad.ModelId,
                    BeamType = 0,
                    // without the player bits nothing ever triggers it, and
                    // without IncludeBots the bots walk over it
                    TriggerFlags = TriggerFlags.PlayerBiped | TriggerFlags.PlayerAlt | TriggerFlags.IncludeBots
                });
            }
            foreach (MapItem item in def.Items)
            {
                if (!Enum.TryParse(item.Type, ignoreCase: true, out ItemType itemType))
                {
                    throw new ProgramException($"Unknown item type {item.Type}.");
                }
                if (!MultiplayerItems.Contains(itemType))
                {
                    throw new ProgramException($"{item.Type} does not belong in a multiplayer map. "
                        + "It is one of the story's permanent upgrades -- an energy tank, a missile or "
                        + "UA expansion, an artifact -- which raise a hunter's capacity for the rest of "
                        + $"the game rather than topping it up for the rest of the match. Use one of: "
                        + $"{String.Join(", ", MultiplayerItems)}.");
                }
                map.Entities.Add(new ItemSpawnEntityEditor()
                {
                    Id = id++,
                    LayerMask = 0xFFFF,
                    Position = ToVector(item.Position),
                    Up = Vector3.UnitY,
                    Facing = Vector3.UnitZ,
                    NodeName = "rmMain",
                    ParentId = -1,
                    ItemType = itemType,
                    Enabled = true,
                    HasBase = item.HasBase,
                    AlwaysActive = true,
                    MaxSpawnCount = 0,
                    SpawnInterval = item.SpawnInterval,
                    SpawnDelay = 0,
                    NotifyEntityId = -1,
                    CollectedMessage = Message.None
                });
            }
            if (map.Entities.Count == 0)
            {
                throw new ProgramException("A map needs at least one entity.");
            }
        }

        /// <summary>
        /// Works out a jump pad's launch velocity. Given a target, solve the
        /// ballistic arc under the biped gravity the hunters actually use;
        /// given an explicit vector and speed, take them as written.
        /// </summary>
        public static (Vector3, float) SolveJumpPad(MapJumpPad pad)
        {
            if (pad.Vector != null)
            {
                return (ToVector(pad.Vector).Normalized(), pad.Speed);
            }
            if (pad.Target == null)
            {
                throw new ProgramException("A jump pad needs either a target or a vector and speed.");
            }
            Vector3 from = ToVector(pad.Position);
            Vector3 to = ToVector(pad.Target);
            Vector3 delta = to - from;
            float horizontal = new Vector3(delta.X, 0, delta.Z).Length;
            // Samus's biped gravity, in units per frame squared, at the 30 fps
            // the game's own values were written for
            float gravity = 77 / 4096f;
            // clear the target by a little, and never by less than a jump
            float rise = MathF.Max(delta.Y, 0) + MathF.Max(2f, horizontal * 0.22f);
            float up = MathF.Sqrt(2 * gravity * rise);
            float fall = MathF.Sqrt(2 * gravity * MathF.Max(rise - delta.Y, 0.01f));
            float frames = (up + fall) / gravity;
            var velocity = new Vector3(delta.X / frames, up, delta.Z / frames);
            float speed = velocity.Length;
            return (velocity / speed, speed);
        }

        private static CollisionVolume MakeBox(float[] size)
        {
            return new CollisionVolume()
            {
                Type = VolumeType.Box,
                BoxVector1 = Vector3.UnitX,
                BoxVector2 = Vector3.UnitY,
                BoxVector3 = Vector3.UnitZ,
                // the volume is entity-local and its position is the corner,
                // so centre it on the pad in X and Z and start it at its feet
                BoxPosition = new Vector3(-size[0] / 2, 0, -size[2] / 2),
                BoxDot1 = size[0],
                BoxDot2 = size[1],
                BoxDot3 = size[2]
            };
        }

        public static Vector3 ToVector(float[] values)
        {
            return new Vector3(values[0], values[1], values[2]);
        }
    }
}
