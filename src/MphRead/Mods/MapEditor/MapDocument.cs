using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MphRead.Mods.MapGen;

namespace MphRead.Mods.MapEditor
{
    public sealed partial class MapDocument
    {
        public MapProject Project { get; private set; }
        public string? FilePath { get; private set; }
        public HashSet<Guid> Selection { get; } = new();
        public Guid ActiveObjectId { get; set; }
        public MapCommandHistory History { get; } = new();
        public MapValidationResult Diagnostics { get; set; } = new();
        public DateTime LastEditUtc { get; private set; } = DateTime.UtcNow;
        public DocumentStateId CurrentStateId => History.CurrentStateId;
        public DocumentStateId? SavedStateId { get; private set; }
        public bool IsDirty => SavedStateId != CurrentStateId;
        public event Action<MapDocumentChange>? Invalidated;
        public event Action? Changed;
        private readonly string _recoveryKey;

        public MapDocument(MapProject project, string? path = null)
        {
            Project = new(MapProjectSerializer.Clone(project.Definition));
            FilePath = path;
            // Legacy object IDs exist in the editor snapshot only until Save.
            foreach (var item in MapObjects.All(Project.Definition)) if (item.Id == Guid.Empty) item.SetId(Guid.NewGuid());
            SavedStateId = path == null ? null : History.CurrentStateId;
            if (path != null) History.MarkSaved();
            History.Changed += change =>
            {
                Selection.RemoveWhere(id => MapObjects.Find(Project.Definition, id) == null);
                LastEditUtc = DateTime.UtcNow;
                Invalidated?.Invoke(change); Changed?.Invoke();
            };
            _recoveryKey = MapBuildFingerprint.HashText(path == null ? Guid.NewGuid().ToString() : Path.GetFullPath(path));
        }

        public void SelectionChanged()
        {
            if (!Selection.Contains(ActiveObjectId)) ActiveObjectId = Selection.FirstOrDefault();
            Invalidated?.Invoke(new(MapChangeDomain.Selection));
        }
        public void OverlayChanged() => Invalidated?.Invoke(new(MapChangeDomain.Overlay));

        private DocumentStateId? _snapshotState;
        private MapBuildSnapshot? _snapshot;
        public MapBuildSnapshot CaptureBuildSnapshot()
        {
            if (_snapshot == null || _snapshotState != CurrentStateId)
            { _snapshot = MapBuildSnapshot.Capture(Project); _snapshotState = CurrentStateId; }
            return _snapshot;
        }
        public MapProject Snapshot() => new(CaptureBuildSnapshot().CreateDefinition());
        private readonly HashSet<string> _recoveryAssets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _generatedAssets = new(StringComparer.Ordinal);
        public void RegisterGeneratedAsset(string relative, string root)
            => _generatedAssets[relative] = Path.GetFullPath(root);
        public int CleanupGeneratedAssets()
        {
            var retained = Project.Definition.Assets.Select(a => a.Path).Concat(History.RetainedAssets).Concat(_recoveryAssets).ToHashSet(StringComparer.Ordinal);
            int removed = 0;
            foreach (var entry in _generatedAssets.ToArray())
            {
                if (retained.Contains(entry.Key)) continue;
                string root = entry.Value + Path.DirectorySeparatorChar;
                string path = Path.GetFullPath(Path.Combine(root, entry.Key));
                if (!path.StartsWith(root, StringComparison.Ordinal) || File.GetAttributes(Path.GetDirectoryName(path)!).HasFlag(FileAttributes.ReparsePoint)) continue;
                if (File.Exists(path)) { File.Delete(path); removed++; }
                _generatedAssets.Remove(entry.Key);
            }
            return removed;
        }
        public MapAutosavePayload CaptureAutosave(string directory)
        {
            foreach (var asset in Project.Definition.Assets) _recoveryAssets.Add(asset.Path);
            return new(CaptureBuildSnapshot(), RecoveryPath(directory), FilePath,
                Project.Definition.BaseDirectory, Project.Definition.BundlePath, CurrentStateId);
        }

        public void Edit(string label, Action<MapDefinition> edit, MapChangeDomain domains = MapChangeDomain.All)
        {
            MapDefinition before = Project.ToDefinition(), after = Project.ToDefinition();
            edit(after);
            if (before.Serialize() == after.Serialize()) return;
            History.Execute(new SnapshotCommand(this, label, before, after, domains));
        }

        private sealed class SnapshotCommand : IMapEditCommand
        {
            private readonly MapDocument _document;
            private readonly MapDefinition _before, _after;
            public IEnumerable<string> RetainedAssets => _before.Assets.Concat(_after.Assets).Select(a => a.Path);
            public string Label { get; }
            public long ApproximateBytes { get; }
            public MapDocumentChange Change { get; }
            public SnapshotCommand(MapDocument document, string label, MapDefinition before, MapDefinition after,
                MapChangeDomain domains = MapChangeDomain.All)
            {
                _document=document; Label=label; _before=before; _after=after;
                ApproximateBytes = 512 + 4L * (before.Serialize().Length + after.Serialize().Length);
                Change = new(domains);
            }
            public void Execute() => _document.Replace(_after);
            public void Undo() => _document.Replace(_before);
        }
        private void Replace(MapDefinition definition)
        {
            var replacement = MapProjectSerializer.Clone(definition);
            if (FilePath != null)
            {
                replacement.SourcePath = FilePath; replacement.BaseDirectory = Path.GetDirectoryName(FilePath);
                replacement.BundlePath = null;
                if (replacement.Import != null) { replacement.Import.BaseDirectory = replacement.BaseDirectory; replacement.Import.BundlePath = null; }
                if (replacement.Collision != null) { replacement.Collision.BaseDirectory = replacement.BaseDirectory; replacement.Collision.BundlePath = null; }
            }
            Project = new(replacement);
            Selection.RemoveWhere(id => MapObjects.Find(Project.Definition, id) == null);

        }

        public void Upgrade() => Edit("Upgrade project", d =>
        {
            d.FormatVersion = 2; if (d.MapId == Guid.Empty) d.MapId = Guid.NewGuid();
        });
        public void Save(string path)
        {
            if (MapBundle.Is(path)) throw new IOException("Choose a .json project filename.");
            var definition = Project.ToDefinition();
            MapProjectExport.Save(definition, path);
            FilePath = Path.GetFullPath(path);
            definition.BaseDirectory = Path.GetDirectoryName(FilePath); definition.SourcePath = FilePath;
            definition.BundlePath = null;
            if (definition.Import != null) { definition.Import.BaseDirectory=definition.BaseDirectory; definition.Import.BundlePath=null; }
            if (definition.Collision != null) { definition.Collision.BaseDirectory=definition.BaseDirectory; definition.Collision.BundlePath=null; }
            _snapshot = null; Project = new(definition); SavedStateId = CurrentStateId; History.MarkSaved();
            Invalidated?.Invoke(new(MapChangeDomain.Metadata)); Changed?.Invoke();
        }
        public string RecoveryPath(string directory) => Path.Combine(directory, ".autosave", _recoveryKey + ".json");
        public void Autosave(string directory)
        {
            if (IsDirty)
            {
                string path=RecoveryPath(directory);
                AtomicFile.Write(path+".context.json",JsonSerializer.SerializeToUtf8Bytes(new RecoveryContext(FilePath,Project.Definition.BaseDirectory,Project.Definition.BundlePath)));
                Project.Definition.Save(path);
            }
        }
        private sealed record RecoveryContext(string? FilePath,string? BaseDirectory,string? BundlePath);
        public static MapProject ReadRecovery(string path)
        {
            var definition=MapDefinition.Load(path);
            if(File.Exists(path+".context.json"))
            {
                var context=JsonSerializer.Deserialize<RecoveryContext>(File.ReadAllText(path+".context.json"));
                definition.BaseDirectory=context?.BaseDirectory;definition.SourcePath=context?.FilePath;definition.BundlePath=context?.BundlePath;
                if(definition.Import!=null){definition.Import.BaseDirectory=definition.BaseDirectory;definition.Import.BundlePath=definition.BundlePath;}
                if(definition.Collision!=null){definition.Collision.BaseDirectory=definition.BaseDirectory;definition.Collision.BundlePath=definition.BundlePath;}
            }
            return new(definition);
        }
        public bool HasRecovery(string directory)
        {
            string recovery = RecoveryPath(directory);
            return File.Exists(recovery) && (FilePath == null || !File.Exists(FilePath)
                || File.GetLastWriteTimeUtc(recovery) > File.GetLastWriteTimeUtc(FilePath));
        }
        public void Restore(string directory)
        {
            var recovery = ReadRecovery(RecoveryPath(directory)).Definition;
            // Relative imports are relative to the real document, not .autosave.
            recovery.BaseDirectory = Project.Definition.BaseDirectory;
            recovery.SourcePath = Project.Definition.SourcePath;
            recovery.BundlePath = Project.Definition.BundlePath;
            if (recovery.Import != null) { recovery.Import.BaseDirectory=recovery.BaseDirectory; recovery.Import.BundlePath=recovery.BundlePath; }
            if (recovery.Collision != null) { recovery.Collision.BaseDirectory=recovery.BaseDirectory; recovery.Collision.BundlePath=recovery.BundlePath; }
            History.Execute(new SnapshotCommand(this, "Restore recovery", Project.ToDefinition(), recovery));
        }
        public void DiscardRecovery(string directory)
        { string path = RecoveryPath(directory); if (File.Exists(path)) File.Delete(path);if(File.Exists(path+".context.json"))File.Delete(path+".context.json"); }
    }

    public sealed record MapObject(Guid Id, string Kind, string Label, object Value, Action<Guid> SetId)
    {
        public override string ToString() => Kind + " · " + Label;
        public float[] Position => Value switch
        {
            MapGeometry g => g.Transform.Position,
            MapBrush b => new[] {(b.Min[0]+b.Max[0])/2, (b.Min[1]+b.Max[1])/2, (b.Min[2]+b.Max[2])/2},
            MapSpawn s => s.Position, MapItem i => i.Position, MapJumpPad p => p.Position, MapNavigationLink link=>link.From, _ => new float[3]
        };
        public void Move(float[] delta)
        {
            if (Value is MapGeometry { Locked: true }) return;
            if (Value is MapBrush b) { for(int i=0;i<3;i++){ b.Min[i]+=delta[i]; b.Max[i]+=delta[i]; } return; }
            if(Value is MapNavigationLink link)for(int i=0;i<3;i++)link.To[i]+=delta[i];
            float[] position=Position; for(int i=0;i<3;i++) position[i]+=delta[i];
        }
    }

    public static class MapObjects
    {
        public static MapObject? Find(MapDefinition d, Guid id)
        {
            foreach (var g in d.Geometry) if (g.Id == id) return new(g.Id,"Geometry",g.Label,g,value=>g.Id=value);
            foreach (var b in d.Brushes) if (b.Id == id) return new(b.Id,"Box",b.Label??"Legacy box",b,value=>b.Id=value);
            foreach (var s in d.Spawns) if (s.Id == id) return new(s.Id,"Spawn",s.Label??"Player spawn",s,value=>s.Id=value);
            foreach (var item in d.Items) if (item.Id == id) return new(item.Id,"Pickup",item.Label??item.Type,item,value=>item.Id=value);
            foreach (var p in d.JumpPads) if (p.Id == id) return new(p.Id,"Jump pad",p.Label??"Jump pad",p,value=>p.Id=value);
            foreach (var n in d.NavigationLinks) if (n.Id == id) return new(n.Id,"Navigation",n.Kind.ToString(),n,value=>n.Id=value);
            return null;
        }

        public static IEnumerable<MapObject> All(MapDefinition d)
        {
            foreach(var g in d.Geometry) yield return new(g.Id,"Geometry",g.Label,g,id=>g.Id=id);
            foreach(var b in d.Brushes) yield return new(b.Id,"Box",b.Label??"Legacy box",b,id=>b.Id=id);
            foreach(var s in d.Spawns) yield return new(s.Id,"Spawn",s.Label??"Player spawn",s,id=>s.Id=id);
            foreach(var i in d.Items) yield return new(i.Id,"Pickup",i.Label??i.Type,i,id=>i.Id=id);
            foreach(var p in d.JumpPads) yield return new(p.Id,"Jump pad",p.Label??"Jump pad",p,id=>p.Id=id);
            foreach(var link in d.NavigationLinks)yield return new(link.Id,"Navigation",link.Kind.ToString(),link,id=>link.Id=id);
        }
        public static void Delete(MapDefinition d, ISet<Guid> ids)
        {
            d.Geometry.RemoveAll(g=>ids.Contains(g.Id)&&!g.Locked); d.Brushes.RemoveAll(g=>ids.Contains(g.Id));
            d.Spawns.RemoveAll(g=>ids.Contains(g.Id)); d.Items.RemoveAll(g=>ids.Contains(g.Id)); d.JumpPads.RemoveAll(g=>ids.Contains(g.Id));
            d.NavigationLinks.RemoveAll(g=>ids.Contains(g.Id));
        }
        public static void Duplicate(MapDefinition d, ISet<Guid> ids)
        {
            var copy = new MapDefinition();
            foreach (var item in All(d).Where(o => ids.Contains(o.Id)).ToArray())
            {
                object value = MapSnapshotCopy.Copy(item.Value);
                switch (value)
                {
                    case MapGeometry g: copy.Geometry.Add(g); break;
                    case MapBrush b: copy.Brushes.Add(b); break;
                    case MapSpawn spawn: copy.Spawns.Add(spawn); break;
                    case MapItem pickup: copy.Items.Add(pickup); break;
                    case MapJumpPad pad: copy.JumpPads.Add(pad); break;
                    case MapNavigationLink link: copy.NavigationLinks.Add(link); break;
                }
            }
            foreach(var o in All(copy).Where(o=>ids.Contains(o.Id)).ToArray())
            {
                o.SetId(Guid.NewGuid()); o.Move(new[] {1f,0,1});
                switch(o.Value)
                {
                    case MapGeometry g: d.Geometry.Add(g); break;
                    case MapBrush b: d.Brushes.Add(b); break;
                    case MapSpawn s: d.Spawns.Add(s); break;
                    case MapItem i: d.Items.Add(i); break;
                    case MapJumpPad p: d.JumpPads.Add(p); break;
                    case MapNavigationLink link:d.NavigationLinks.Add(link);break;
                }
            }
        }
    }
}
