using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Mods.MapGen;

namespace MphRead.Mods.MapEditor;

/// <summary>Reusable selections stored as ordinary map-definition JSON plus local texture assets.</summary>
public static class MapPrefabService
{
    public sealed record InsertResult(IReadOnlyList<Guid> ObjectIds, IReadOnlyList<string> GeneratedAssets);

    public static void Save(MapDefinition source, ISet<Guid> selected, string path)
    {
        if (selected.Count == 0) throw new InvalidOperationException("Select at least one object to save a prefab.");
        string full = Path.GetFullPath(path);
        string root = Path.GetDirectoryName(full) ?? throw new IOException("Prefab path has no directory.");
        Directory.CreateDirectory(root);

        var prefab = MapProjectSerializer.Clone(source);
        prefab.Name = "PREFAB";
        prefab.InGameName = null;
        prefab.Author = null;
        prefab.Version = null;
        prefab.Description = null;
        prefab.Import = null;
        prefab.Collision = null;
        prefab.Preview = null;
        prefab.Audio = null;
        prefab.Capabilities = null;
        prefab.Brushes.RemoveAll(x => !selected.Contains(x.Id));
        prefab.Geometry.RemoveAll(x => !selected.Contains(x.Id));
        prefab.Spawns.RemoveAll(x => !selected.Contains(x.Id));
        prefab.Items.RemoveAll(x => !selected.Contains(x.Id));
        prefab.JumpPads.RemoveAll(x => !selected.Contains(x.Id));
        prefab.NavigationLinks.RemoveAll(x => !selected.Contains(x.Id));

        var used = prefab.Geometry.Select(g => g.Material).Concat(prefab.Brushes.Select(b => b.Material))
            .Distinct().Where(i => i >= 0 && i < source.Materials.Count).OrderBy(i => i).ToArray();
        var remap = used.Select((old, index) => (old, index)).ToDictionary(x => x.old, x => x.index);
        prefab.Materials = used.Select(i => MapSnapshotCopy.Copy(source.Materials[i])).Cast<MapMaterial>().ToList();
        foreach (var geometry in prefab.Geometry) geometry.Material = remap[geometry.Material];
        foreach (var brush in prefab.Brushes) brush.Material = remap[brush.Material];

        prefab.Assets.Clear();
        string assetRoot = Path.Combine(root, Path.GetFileNameWithoutExtension(full) + ".assets");
        foreach (var material in prefab.Materials.Where(m => !String.IsNullOrEmpty(m.Texture)))
        {
            string original = material.Texture!;
            byte[] bytes = MapAssets.Read(source, original);
            Directory.CreateDirectory(assetRoot);
            string relative = Path.Combine(Path.GetFileName(assetRoot), Guid.NewGuid().ToString("N") + ".tex")
                .Replace('\\', '/');
            AtomicFile.Write(Path.Combine(root, relative), bytes);
            material.Texture = relative;
            prefab.Assets.Add(new() { Path = relative, Kind = "texture", Name = material.Name });
        }
        prefab.BaseDirectory = root;
        prefab.SourcePath = full;
        prefab.BundlePath = null;
        prefab.Save(full);
    }

    public static InsertResult Insert(MapDefinition destination, string prefabPath, string destinationRoot,
        float[]? offset = null)
    {
        var prefab = MapDefinition.Load(prefabPath);
        var generated = new List<string>();
        var materialMap = new Dictionary<int, int>();
        for (int i = 0; i < prefab.Materials.Count; i++)
        {
            MapMaterial material = prefab.Materials[i];
            int existing = -1;
            if (material.Texture == null)
                existing = destination.Materials.FindIndex(m => m.Texture == null
                    && m.SourceMaterial == material.SourceMaterial
                    && Math.Abs(m.TexScale - material.TexScale) < .0001f
                    && m.Name.Equals(material.Name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) { materialMap[i] = existing; continue; }

            var copy = MapSnapshotCopy.Copy(material) as MapMaterial
                ?? throw new InvalidDataException("Prefab material could not be copied.");
            copy.Id = Guid.NewGuid();
            if (copy.Texture != null)
            {
                byte[] bytes = MapAssets.Read(prefab, copy.Texture);
                string relative = Path.Combine("prefab-assets", Guid.NewGuid().ToString("N") + ".tex").Replace('\\', '/');
                AtomicFile.Write(Path.Combine(destinationRoot, relative), bytes);
                destination.Assets.Add(new() { Path = relative, Kind = "texture", Name = copy.Name });
                generated.Add(relative);
                copy.Texture = relative;
            }
            materialMap[i] = destination.Materials.Count;
            destination.Materials.Add(copy);
        }

        float[] move = offset ?? new[] { 1f, 0f, 1f };
        var inserted = new List<Guid>();
        void AddObject(object value)
        {
            object copy = MapSnapshotCopy.Copy(value);
            MapObject? mapObject = copy switch
            {
                MapGeometry g => new(g.Id, "Geometry", g.Label, g, id => g.Id = id),
                MapBrush b => new(b.Id, "Box", b.Label ?? "Legacy box", b, id => b.Id = id),
                MapSpawn s => new(s.Id, "Spawn", s.Label ?? "Player spawn", s, id => s.Id = id),
                MapItem item => new(item.Id, "Pickup", item.Label ?? item.Type, item, id => item.Id = id),
                MapJumpPad pad => new(pad.Id, "Jump pad", pad.Label ?? "Jump pad", pad, id => pad.Id = id),
                MapNavigationLink link => new(link.Id, "Navigation", link.Kind.ToString(), link, id => link.Id = id),
                _ => null
            };
            if (mapObject == null) return;
            mapObject.SetId(Guid.NewGuid());
            mapObject.Move(move);
            inserted.Add(mapObject.Id);
            switch (mapObject.Value)
            {
                case MapGeometry g:
                    g.Material = materialMap[g.Material]; destination.Geometry.Add(g); break;
                case MapBrush b:
                    b.Material = materialMap[b.Material]; destination.Brushes.Add(b); break;
                case MapSpawn s: destination.Spawns.Add(s); break;
                case MapItem item: destination.Items.Add(item); break;
                case MapJumpPad pad: destination.JumpPads.Add(pad); break;
                case MapNavigationLink link: destination.NavigationLinks.Add(link); break;
            }
        }

        foreach (var value in prefab.Geometry.Cast<object>().Concat(prefab.Brushes)
            .Concat(prefab.Spawns).Concat(prefab.Items).Concat(prefab.JumpPads).Concat(prefab.NavigationLinks))
            AddObject(value);
        return new(inserted.AsReadOnly(), generated.AsReadOnly());
    }
}
