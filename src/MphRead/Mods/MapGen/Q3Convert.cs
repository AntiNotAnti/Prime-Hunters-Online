using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MphRead.Mods.MapGen
{
    /// <summary>
    /// One command from a .pk3 to a playable room.
    ///
    /// Everything the importer needs was already here, but scattered across a
    /// Python script, a hand-written JSON file and a handful of numbers you had
    /// to arrive at by running the generator until it stopped complaining.
    /// This picks those numbers from the level itself and writes the map file,
    /// so converting somebody else's level is a command rather than an
    /// afternoon.
    ///
    /// What it deliberately does not do is *place* weapons and powerups. Where
    /// those go is a judgement about how the map plays -- which routes meet,
    /// what is worth contesting -- and a generator that scattered them evenly
    /// would produce a map that is worse than one with none at all.
    ///
    /// It does write down the ones the level's own author placed, which is a
    /// different thing: those were being imported from the .bsp on every
    /// generation anyway, invisibly, so an author who wanted one moved or gone
    /// had nowhere to say so. Listing them under `items` and turning
    /// `keepItems` off makes the recipe the only answer to what the map holds
    /// -- which is the point of there being a recipe. `-noitems` writes none
    /// and still turns it off, for a level whose pickups are nothing you want.
    /// </summary>
    public static class Q3Convert
    {
        /// <summary>
        /// How wide the biggest converted level should end up, in MPH units.
        ///
        /// Scale is not free: matching the level's architecture exactly means
        /// dividing by 35 (a 56-unit Quake player against Samus's 1.6), and
        /// that is what the importer's own note recommends. But a level built
        /// for a player who covers 216 units in a jump, converted for one who
        /// covers 7.7, is a correct model of a map nobody can get across --
        /// the cartridge's own rooms are 40 to 80 units wide, and a faithful
        /// de_dust2 comes out at 300. So the size is chosen from what a match
        /// wants and 35 is the floor, not the answer.
        ///
        /// 130 was arrived at by measuring something with a known size rather
        /// than by eye. df_dust2's crates are 128 and 192 Quake units, which
        /// are de_dust2's 64 and 96 doubled: the level is built at twice the
        /// scale of the map it copies, so its own player is no guide at all.
        /// At this target its small crate comes out 1.56 units against Samus's
        /// 1.6 -- waist-high, which is what a crate is -- and the map is 109 x
        /// 130 units, bigger than any room on the cartridge and not by much.
        /// </summary>
        public const float TargetExtent = 130f;

        public static int Run(string source, string? mapName, string? roomName, string? outputDir,
            bool dropClip, bool dropItems, float? forcedScale, int textureSize)
        {
            if (!File.Exists(source))
            {
                Console.WriteLine($"No such file: {source}");
                return 1;
            }
            Q3Bsp bsp;
            try
            {
                bsp = Q3Bsp.Load(source, mapName);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return 1;
            }
            mapName ??= Q3Bsp.ListMaps(source).FirstOrDefault();
            string room = (roomName ?? mapName ?? "CUSTOM").ToUpperInvariant();
            string prefix = room.ToLowerInvariant();
            string directory = outputDir ?? Path.Combine(CustomRooms.MapDirectory, prefix);
            Directory.CreateDirectory(directory);

            Bounds(bsp, out float[] min, out float[] max, sky: false);
            if (min[0] > max[0])
            {
                Console.WriteLine($"{mapName} has no drawn surfaces.");
                return 1;
            }
            float widest = Math.Max(max[0] - min[0], Math.Max(max[1] - min[1], max[2] - min[2]));
            float unit = forcedScale ?? AutoScale(widest);
            // The sky shell sits outside the architecture, and with it drawn
            // its corners are the furthest vertices in the file. The size of
            // the map is decided by the part people walk on; what has to fit
            // in a 16-bit vertex is everything.
            Bounds(bsp, out float[] reachMin, out float[] reachMax, sky: true);

            // The level, beside the map file. import.source takes a bare name
            // and is looked for there first, which is what lets one map file
            // work on a desktop and a phone.
            string levelName = Path.GetFileName(source);
            string beside = Path.Combine(directory, levelName);
            if (Path.GetFullPath(beside) != Path.GetFullPath(source))
            {
                File.Copy(source, beside, overwrite: true);
            }

            string texturePath = Path.Combine(directory, $"{prefix}.tex");
            MapTextureBake.Result baked = MapTextureBake.Bake(bsp, new[] { source }, texturePath, textureSize);
            Console.WriteLine($"  {baked.Baked} textures at {textureSize}x{textureSize}"
                + $" -> {baked.Bytes:N0} B  {Path.GetFileName(texturePath)}");
            if (baked.Missing.Count > 0)
            {
                Console.WriteLine($"  no image for {baked.Missing.Count}:"
                    + $" {String.Join(", ", baked.Missing.Take(6))}"
                    + (baked.Missing.Count > 6 ? " ..." : ""));
                Console.WriteLine("  those surfaces are dropped rather than painted with somebody else's"
                    + " texture; pass another .pk3 in the same folder if it has them");
            }

            var definition = new MapDefinition()
            {
                Name = room,
                InGameName = roomName ?? mapName ?? room,
                ScaleFactor = ScaleFactor(reachMin, reachMax, unit),
                KillHeight = MathF.Round(min[2] / unit) - 5,
                FarClip = MathF.Round(Math.Min(400, widest / unit * 1.2f)),
                Import = new MapImport()
                {
                    Source = levelName,
                    MapName = mapName,
                    UnitsPerUnit = unit,
                    Textures = Path.GetFileName(texturePath),
                    KeepSky = true,
                    KeepClip = !dropClip,
                    KeepSpawns = true
                }
            };
            int clipBrushes = bsp.Brushes.Count(b =>
                (bsp.Textures[b.Texture].Contents & Q3Bsp.ContentsSolid) == 0
                && (bsp.Textures[b.Texture].Contents & Q3Bsp.ContentsPlayerClip) != 0);
            AddSpawns(definition, bsp, unit);
            AddItems(definition, bsp, unit, dropItems);

            string path = Path.Combine(directory, $"{prefix}.json");
            definition.Save(path);
            Console.WriteLine($"  {definition.Spawns.Count} spawn points, {unit:0.#} Quake units per unit"
                + $" -> {(max[0] - min[0]) / unit:0} x {(max[2] - min[2]) / unit:0} x {(max[1] - min[1]) / unit:0} units");
            Console.WriteLine($"  wrote {path}");
            if (definition.Spawns.Count < 4)
            {
                Console.WriteLine($"  only {definition.Spawns.Count} places to appear: this level was not"
                    + " built for a deathmatch. Add spawns to the map file before playing it with a full house.");
            }
            if (clipBrushes > 0 && !dropClip)
            {
                Console.WriteLine($"  {clipBrushes} player-clip brushes kept. They are the level's invisible"
                    + " walls; on a race map they fence the route. -noclip converts without them.");
            }
            List<Q3Import.Q3Pickup> pickups = Q3Import.Pickups(bsp, unit).ToList();
            if (dropItems)
            {
                Console.WriteLine($"  -noitems: the level's {pickups.Count} pickups were left out, and"
                    + " \"keepItems\" turned off so they stay out. Add your own under \"items\", from:");
            }
            else if (definition.Items.Count > 0)
            {
                Console.WriteLine($"  {definition.Items.Count} of the level's own pickups written under"
                    + " \"items\", and \"keepItems\" turned off so the recipe is the only place they"
                    + " live. Move them, drop them, or change what they are, from:");
            }
            else
            {
                Console.WriteLine("  no pickups: this level holds none this game has an answer for."
                    + " Where weapons and powerups go decides how the map plays, so none were"
                    + " invented. Add them under \"items\", from:");
            }
            Console.WriteLine($"  {String.Join(", ", MapBuilder.MultiplayerItems)}");
            int scripted = dropItems ? 0 : pickups.Count(p => p.TargetName != null);
            if (scripted > 0)
            {
                Console.WriteLine($"  {scripted} of them are handed out by the level's own scripts"
                    + " rather than walked over, and are usually stood in a closet nobody can reach."
                    + $" FruityPrime -mapitems \"{room}\" says which.");
            }
            Console.WriteLine($"  then: FruityPrime -mapgen \"{room}\"");
            return 0;
        }

        /// <summary>
        /// The scale a conversion picks for a level of this size, in Quake
        /// units per world unit. See <see cref="TargetExtent"/>; 35 is the
        /// floor because below it the level's own architecture stops fitting
        /// its own player.
        /// </summary>
        public static float AutoScale(float widestExtent)
        {
            return MathF.Round(Math.Max(35f, widestExtent / TargetExtent));
        }

        /// <summary>The widest the level's drawn geometry gets, in Quake units.</summary>
        public static float WidestExtent(Q3Bsp bsp)
        {
            Bounds(bsp, out float[] min, out float[] max, sky: false);
            if (min[0] > max[0])
            {
                return 0;
            }
            return Math.Max(max[0] - min[0], Math.Max(max[1] - min[1], max[2] - min[2]));
        }

        /// <summary>
        /// The level's own pickups, written into the recipe rather than left
        /// to be read out of the .bsp on every generation.
        ///
        /// Nothing is invented here -- this transcribes what the level's
        /// author already decided, and the reason to do it is that the recipe
        /// is the file an author can edit and the .bsp is not.
        /// </summary>
        private static void AddItems(MapDefinition definition, Q3Bsp bsp, float unit, bool dropItems)
        {
            // Either way, the recipe is now the only source. With the level's
            // pickups listed here, importing them as well would double every
            // one of them.
            definition.Import!.KeepItems = false;
            if (dropItems)
            {
                return;
            }
            foreach (Q3Import.Q3Pickup pickup in Q3Import.Pickups(bsp, unit))
            {
                definition.Items.Add(new MapItem()
                {
                    Position = new[]
                    {
                        Round(pickup.Position.X), Round(pickup.Position.Y), Round(pickup.Position.Z)
                    },
                    Type = pickup.Type.ToString()
                });
            }
        }

        /// <summary>The extent of what is drawn, optionally counting the sky shell.</summary>
        private static void Bounds(Q3Bsp bsp, out float[] min, out float[] max, bool sky)
        {
            min = new[] { Single.MaxValue, Single.MaxValue, Single.MaxValue };
            max = new[] { Single.MinValue, Single.MinValue, Single.MinValue };
            foreach (Q3Face face in bsp.Faces)
            {
                if (face.Type != 1 && face.Type != 2 && face.Type != 3)
                {
                    continue;
                }
                Q3Texture texture = bsp.Textures[face.Texture];
                if ((texture.Flags & (Q3Bsp.SurfaceNoDraw | Q3Bsp.SurfaceHint | Q3Bsp.SurfaceSkip)) != 0
                    || (!sky && (texture.Flags & Q3Bsp.SurfaceSky) != 0))
                {
                    continue;
                }
                for (int i = face.Vertex; i < face.Vertex + face.VertexCount; i++)
                {
                    float[] position = bsp.Vertices[i].Position;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        min[axis] = Math.Min(min[axis], position[axis]);
                        max[axis] = Math.Max(max[axis], position[axis]);
                    }
                }
            }
        }

        /// <summary>
        /// Vertices are 16-bit fixed point: model space is +/-8 units times
        /// 2^this, so the smallest power that still reaches the far corner is
        /// the one that keeps the most precision.
        /// </summary>
        private static int ScaleFactor(float[] min, float[] max, float unit)
        {
            float reach = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                reach = Math.Max(reach, Math.Max(Math.Abs(min[axis]), Math.Abs(max[axis])) / unit);
            }
            int factor = 0;
            while (8 * MathF.Pow(2, factor) < reach && factor < 10)
            {
                factor++;
            }
            return factor;
        }

        /// <summary>
        /// Where players appear. The level's own starts if it has them; a race
        /// map has one, on a ledge sealed off from the course, so its
        /// checkpoints stand in -- they are strung along the route by
        /// construction, which is exactly the spread a deathmatch wants.
        /// </summary>
        private static void AddSpawns(MapDefinition definition, Q3Bsp bsp, float unit)
        {
            var starts = new List<float[]>();
            var fallbacks = new List<float[]>();
            foreach (Dictionary<string, string> entity in bsp.Entities)
            {
                if (!entity.TryGetValue("classname", out string? classname)
                    || !entity.TryGetValue("origin", out string? origin))
                {
                    continue;
                }
                float[] position = ParseVector(origin);
                if (classname.StartsWith("info_player_deathmatch", StringComparison.OrdinalIgnoreCase)
                    || classname.Equals("info_player_start", StringComparison.OrdinalIgnoreCase))
                {
                    starts.Add(position);
                }
                else if (classname.StartsWith("target_", StringComparison.OrdinalIgnoreCase)
                    || classname.Equals("info_player_intermission", StringComparison.OrdinalIgnoreCase))
                {
                    fallbacks.Add(position);
                }
            }
            List<float[]> chosen = starts.Count >= 4 ? starts : starts.Concat(fallbacks).ToList();
            // The level's own starts come across through the importer, which
            // reads the same entities; listing them here as well would double
            // them up.
            definition.Import!.KeepSpawns = starts.Count >= 4;
            if (definition.Import.KeepSpawns)
            {
                return;
            }
            float[] centre = new[]
            {
                chosen.Count == 0 ? 0 : chosen.Average(p => p[0]),
                chosen.Count == 0 ? 0 : chosen.Average(p => p[1])
            };
            foreach (float[] position in chosen)
            {
                // Quake sets a start at the player's feet plus a little
                float x = position[0] / unit;
                float y = position[2] / unit - 24 / unit;
                float z = -position[1] / unit;
                float toCentre = MathF.Atan2(centre[0] / unit - x, -centre[1] / unit - z);
                definition.Spawns.Add(new MapSpawn()
                {
                    Position = new[] { Round(x), Round(y), Round(z) },
                    Yaw = Round(toCentre * 180 / MathF.PI)
                });
            }
        }

        private static float Round(float value)
        {
            return MathF.Round(value, 2);
        }

        private static float[] ParseVector(string value)
        {
            string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var result = new float[3];
            for (int i = 0; i < 3 && i < parts.Length; i++)
            {
                Single.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]);
            }
            return result;
        }
    }
}
