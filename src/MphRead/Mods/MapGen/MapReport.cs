using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MphRead.Mods.MapGen
{
    /// <summary>
    /// What a map author needs to know before writing anything: which textures
    /// they can borrow and what those are called, and what the level they are
    /// converting already holds.
    /// </summary>
    public static class MapReport
    {
        /// <summary>
        /// The shaders a level actually draws with, commonest first. This is
        /// the list a conversion maps onto borrowed textures: without it the
        /// whole level comes out in one material.
        /// </summary>
        public static int ListShaders(string source, string? mapName)
        {
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
            var counts = new Dictionary<string, int>();
            foreach (Q3Face face in bsp.Faces)
            {
                if (face.Type != 1 && face.Type != 3)
                {
                    continue;
                }
                Q3Texture texture = bsp.Textures[face.Texture];
                if ((texture.Flags & (Q3Bsp.SurfaceNoDraw | Q3Bsp.SurfaceSky
                    | Q3Bsp.SurfaceHint | Q3Bsp.SurfaceSkip)) != 0)
                {
                    continue;
                }
                counts.TryGetValue(texture.Name, out int count);
                counts[texture.Name] = count + face.MeshVertCount / 3;
            }
            Console.WriteLine($"{mapName ?? source}: {counts.Count} shaders drawn");
            foreach ((string name, int count) in counts.OrderByDescending(p => p.Value))
            {
                Console.WriteLine($"  {count,6} triangles  {name}");
            }
            return 0;
        }

        /// <summary>
        /// The pickups a level holds, as the `items` block a recipe would
        /// carry: what each one is in Quake, what this game puts in its place,
        /// and where that lands in world units.
        ///
        /// It prints and writes nothing. A recipe is allowed comments -- every
        /// one in the repository opens with a "//" line saying where to get
        /// the level -- and serializing one back over itself would throw them
        /// away along with whatever the author had laid out by hand. So the
        /// block is printed for a person to paste, and the file is theirs.
        ///
        /// Takes a custom map's name, in which case the level and the scale
        /// come out of its recipe, or a .bsp or .pk3 directly, in which case
        /// the scale is the one a conversion would pick unless -scale says
        /// otherwise.
        /// </summary>
        public static int ListItems(string target, string? mapName, float? forcedScale)
        {
            MapDefinition? def = CustomRooms.Definitions.FirstOrDefault(
                d => d.Name.Equals(target, StringComparison.OrdinalIgnoreCase) && d.Import != null);
            string source;
            float unit;
            Q3Bsp bsp;
            if (def != null)
            {
                MapImport import = def.Import!;
                source = import.Resolve() ?? import.Source;
                mapName ??= import.MapName;
                unit = forcedScale ?? import.UnitsPerUnit;
            }
            else
            {
                source = target;
                unit = 0;
            }
            try
            {
                bsp = Q3Bsp.Load(source, mapName);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return 1;
            }
            if (unit <= 0)
            {
                unit = forcedScale ?? Q3Convert.AutoScale(Q3Convert.WidestExtent(bsp));
            }
            List<Q3Import.Q3Pickup> pickups = Q3Import.Pickups(bsp, unit).ToList();
            Console.WriteLine($"{def?.Name ?? mapName ?? source}: {pickups.Count} pickups"
                + $" in {mapName ?? Path.GetFileName(source)} at {unit:0.#} Quake units per unit");
            if (def != null)
            {
                Console.WriteLine(def.Import!.KeepItems
                    ? $"  keepItems is on: these are added to the recipe's own {def.Items.Count}"
                        + $", for {def.Items.Count + pickups.Count} in the room."
                    : $"  keepItems is off: none of these reach the room. It has the recipe's"
                        + $" own {def.Items.Count}.");
            }
            if (pickups.Count == 0)
            {
                Console.WriteLine("  Nothing here is a pickup this game has an answer for.");
                return 0;
            }
            Console.WriteLine();
            foreach (IGrouping<string, Q3Import.Q3Pickup> group in pickups
                .GroupBy(p => p.Classname).OrderByDescending(g => g.Count()))
            {
                int scripted = group.Count(p => p.TargetName != null);
                string note = scripted == 0
                    ? ""
                    : scripted == group.Count()
                        ? "  handed out by the level's own scripts, not walked over"
                        : $"  {scripted} handed out by the level's own scripts, not walked over";
                Console.WriteLine($"  {group.Count(),4}  {group.Key,-24} {group.First().Type,-14}{note}".TrimEnd());
            }
            Console.WriteLine();
            Console.WriteLine("  \"items\": [");
            for (int i = 0; i < pickups.Count; i++)
            {
                Q3Import.Q3Pickup pickup = pickups[i];
                string comma = i < pickups.Count - 1 ? "," : "";
                Console.WriteLine("    { \"position\": ["
                    + $" {Round(pickup.Position.X)}, {Round(pickup.Position.Y)}, {Round(pickup.Position.Z)} ],"
                    + $" \"type\": \"{pickup.Type}\" }}{comma}"
                    + (pickup.TargetName == null ? "" : $"   // {pickup.Classname}, given by {pickup.TargetName}")
                    );
            }
            Console.WriteLine("  ]");
            Console.WriteLine();
            Console.WriteLine("  Paste that into the recipe and set \"keepItems\": false under \"import\",");
            Console.WriteLine("  or the room gets one of each from the recipe and one from the level.");
            Console.WriteLine("  A recipe may carry // comments, so those lines can go in as they are.");
            return 0;
        }

        /// <summary>
        /// Two decimals, in the invariant culture -- this is printed to be
        /// pasted into JSON, where a comma for a decimal point is not a
        /// number. Negative zero is a real float and prints as "-0", which is
        /// legal JSON and still looks like a mistake in a file somebody reads.
        /// </summary>
        private static string Round(float value)
        {
            float rounded = MathF.Round(value, 2);
            return (rounded == 0 ? 0 : rounded).ToString("0.##", CultureInfo.InvariantCulture);
        }

        public static int ListMaterials(string room)
        {
            Model model;
            try
            {
                model = Read.GetRoomModelInstance(room).Model;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not load {room}: {ex.Message}");
                return 1;
            }
            Recolor recolor = model.Recolors[0];
            Console.WriteLine($"{room}: {model.Materials.Count} materials, {recolor.Textures.Count} textures");
            for (int i = 0; i < model.Materials.Count; i++)
            {
                Material material = model.Materials[i];
                string size = "no texture";
                string format = "";
                if (material.TextureId >= 0 && material.TextureId < recolor.Textures.Count)
                {
                    Texture texture = recolor.Textures[material.TextureId];
                    size = $"{texture.Width}x{texture.Height}";
                    format = texture.Format.ToString();
                }
                Console.WriteLine($"  {i,3}  {material.Name,-32} tex {material.TextureId,3} pal {material.PaletteId,3}"
                    + $"  {size,-9} {format} {(material.RenderMode == RenderMode.Normal ? "" : material.RenderMode.ToString())}");
            }
            return 0;
        }
    }
}
