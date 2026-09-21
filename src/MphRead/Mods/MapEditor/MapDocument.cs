using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MphRead.Mods.MapGen;

namespace MphRead.Mods.MapEditor
{
    public interface IMapEditCommand { string Label { get; } void Execute(); void Undo(); }

    public sealed class MapCommandHistory
    {
        private readonly List<IMapEditCommand> _undo = new(), _redo = new();
        public bool CanUndo => _undo.Count != 0;
        public bool CanRedo => _redo.Count != 0;
        public void Execute(IMapEditCommand command)
        {
            command.Execute(); _undo.Add(command); _redo.Clear();
            if (_undo.Count > 50) _undo.RemoveAt(0);
        }
        public void Undo()
        {
            if (!CanUndo) return;
            var command = _undo[^1]; command.Undo(); _undo.RemoveAt(_undo.Count-1); _redo.Add(command);
        }
        public void Redo()
        {
            if (!CanRedo) return;
            var command = _redo[^1]; command.Execute(); _redo.RemoveAt(_redo.Count-1); _undo.Add(command);
        }
    }

    public sealed class MapDocument
    {
        public MapProject Project { get; private set; }
        public string? FilePath { get; private set; }
        public HashSet<Guid> Selection { get; } = new();
        public MapCommandHistory History { get; } = new();
        public MapValidationResult Diagnostics { get; set; } = new();
        public DateTime LastEditUtc { get; private set; } = DateTime.UtcNow;
        public bool IsDirty => Project.Definition.Serialize() != _saved;
        public event Action? Changed;
        private string _saved;
        private readonly string _recoveryKey;

        public MapDocument(MapProject project, string? path = null)
        {
            Project = new(MapProjectSerializer.Clone(project.Definition));
            FilePath = path;
            // Legacy object IDs exist in the editor snapshot only until Save.
            foreach (var item in MapObjects.All(Project.Definition)) if (item.Id == Guid.Empty) item.SetId(Guid.NewGuid());
            _saved = path == null ? "" : Project.Definition.Serialize();
            _recoveryKey = MapBuildFingerprint.HashText(path == null ? Guid.NewGuid().ToString() : Path.GetFullPath(path));
        }

        public MapProject Snapshot() => new(Project.ToDefinition());
        public void Edit(string label, Action<MapDefinition> edit)
        {
            MapDefinition before = Project.ToDefinition(), after = Project.ToDefinition();
            edit(after);
            if (before.Serialize() == after.Serialize()) return;
            History.Execute(new SnapshotCommand(this, label, before, after));
        }

        private sealed class SnapshotCommand : IMapEditCommand
        {
            private readonly MapDocument _document;
            private readonly MapDefinition _before, _after;
            public string Label { get; }
            public SnapshotCommand(MapDocument document, string label, MapDefinition before, MapDefinition after)
            { _document=document; Label=label; _before=before; _after=after; }
            public void Execute() => _document.Replace(_after);
            public void Undo() => _document.Replace(_before);
        }
        private void Replace(MapDefinition definition)
        {
            Project = new(MapProjectSerializer.Clone(definition));
            Selection.IntersectWith(MapObjects.All(Project.Definition).Select(o => o.Id));
            LastEditUtc = DateTime.UtcNow; Changed?.Invoke();
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
            Project = new(definition); _saved = definition.Serialize(); Changed?.Invoke();
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
            var copy = MapProjectSerializer.Clone(d);
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
