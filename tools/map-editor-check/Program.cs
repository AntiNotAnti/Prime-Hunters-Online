using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using MphRead.Mods.MapEditor;
using MphRead.Mods.MapGen;

int checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
var definition = new MapDefinition { Name = "CHECK_ARENA", FormatVersion = 2, MapId = Guid.NewGuid() };
var box = new MapBox(); var spawn = new MapSpawn { Id = Guid.NewGuid() };
definition.Geometry.Add(box); definition.Spawns.Add(spawn); definition.Materials.Add(new());
var doc = new MapDocument(new MapProject(definition));
Check(doc.IsDirty, "new document dirty");
string root = Path.Combine(Path.GetTempPath(), "prime-history-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    doc.Save(Path.Combine(root, "map.json"));
    var saved = doc.CurrentStateId;
    Check(!doc.IsDirty, "save clean");
    var stable = doc.Project.Definition;
    doc.TransformSelection(new[] { box.Id }, "Move", new(1, 2, 3), 0, 1, false);
    Check(ReferenceEquals(stable, doc.Project.Definition), "transform preserves document graph");
    Check(doc.Project.Definition.Geometry[0].Transform.Position.SequenceEqual(new float[] { 1, 2, 3 }), "transform applied");
    Check(doc.IsDirty && doc.History.ApproximateBytes < 1024, "compact transform history");
    doc.History.Undo(); Check(doc.CurrentStateId == saved && !doc.IsDirty, "undo to saved identity");
    doc.History.Redo(); Check(doc.IsDirty, "redo dirty");
    var branch = doc.CurrentStateId;
    doc.History.Undo(); doc.TransformSelection(new[] { box.Id }, "Move", new(9, 0, 0), 0, 1, false);
    Check(doc.CurrentStateId != branch && !doc.History.CanRedo, "branch gets unique identity");
    int count = doc.History.CommandCount;
    object transaction = new();
    for (int i = 0; i < 100; i++) doc.TransformSelection(new[] { box.Id }, "Move", new(1, 0, 0), 0, 1, false, transaction);
    Check(doc.History.CommandCount == count + 1 && doc.History.ApproximateBytes < 2048, "100 updates coalesce");
    doc.History.Undo(); Check(doc.Project.Definition.Geometry[0].Transform.Position[0] == 9, "coalesced undo");
    doc.History.Redo(); Check(doc.Project.Definition.Geometry[0].Transform.Position[0] == 109, "coalesced redo");
    doc.Save(Path.Combine(root, "map.json"));
    doc.TransformSelection(new[] { box.Id }, "Move", new(1, 0, 0), 0, 1, false, transaction);
    doc.History.Undo(); Check(!doc.IsDirty, "coalescing cannot cross save point");
    var newId = Guid.NewGuid();
    doc.EditObjects("Create", Array.Empty<Guid>(), d => d.Geometry.Add(new MapWedge { Id = newId }));
    Check(doc.Project.Definition.Geometry.Count == 2, "create");
    doc.History.Undo(); Check(doc.Project.Definition.Geometry.Count == 1, "undo create");
    doc.History.Redo(); Check(doc.Project.Definition.Geometry[1] is MapWedge, "redo polymorphic create");
    doc.EditObjects("Delete", new[] { box.Id }, d => MapObjects.Delete(d, new System.Collections.Generic.HashSet<Guid> { box.Id }));
    Check(doc.Project.Definition.Geometry.Count == 1, "delete");
    doc.History.Undo(); Check(doc.Project.Definition.Geometry[0].Id == box.Id, "undo preserves order");
    doc.EditObjects("Duplicate", new[] { box.Id }, d => MapObjects.Duplicate(d, new System.Collections.Generic.HashSet<Guid> { box.Id }));
    Check(doc.Project.Definition.Geometry.Count == 3 && doc.Project.Definition.Geometry[2].Id != box.Id, "duplicate identity");
    doc.History.Undo(); Check(doc.Project.Definition.Geometry.Count == 2, "undo duplicate");
    doc.EditObjects("Entity", new[] { spawn.Id }, d => d.Spawns[0].Yaw = 90);
    Check(doc.Project.Definition.Spawns[0].Yaw == 90, "entity properties");
    doc.History.Undo(); Check(doc.Project.Definition.Spawns[0].Yaw == 0, "undo entity properties");
    doc.EditMaterial(0, m => m.TexScale = 42);
    doc.History.Undo(); Check(doc.Project.Definition.Materials[0].TexScale != 42, "undo material");
    doc.History.Redo(); Check(doc.Project.Definition.Materials[0].TexScale == 42, "redo material");
    var state = doc.CurrentStateId;
    try { doc.EditObjects("Fail", new[] { box.Id }, d => { d.Geometry[0].Label = "bad"; throw new InvalidOperationException(); }); }
    catch (InvalidOperationException) { }
    Check(doc.CurrentStateId == state && doc.Project.Definition.Geometry[0].Label != "bad", "failed edit atomic");
    doc.TransformSelection(new[] { box.Id }, "Move", Vector3.Zero, 0, 1, false);
    Check(doc.CurrentStateId == state, "no-op has no state");
    doc.EditObjects("Lock", new[] { box.Id }, d => d.Geometry[0].Locked = true);
    state = doc.CurrentStateId;
    doc.TransformSelection(new[] { box.Id }, "Move", Vector3.One, 0, 1, false);
    Check(doc.CurrentStateId == state, "locked transform no-op");
    doc.History.Undo(); Check(!doc.Project.Definition.Geometry[0].Locked, "undo lock");
    var history = new MapCommandHistory(2, 1000);
    int value = 0;
    for (int i = 0; i < 10; i++) history.Execute(new Counter(() => value++, () => value--, 100));
    Check(history.CommandCount == 2 && history.ApproximateBytes == 200, "count bound");
    history.Undo(); history.Undo(); Check(value == 8 && !history.CanUndo, "pruned undo floor");
    history.Execute(new Counter(() => value++, () => value--, 1001));
    Check(history.CommandCount == 0 && history.ApproximateBytes == 0, "oversized history bound and branch pruning");
    var cache = new MapViewportCache();
    cache.Invalidate(doc.Project.Definition, new(MapChangeDomain.All));
    doc.Invalidated += change => cache.Invalidate(doc.Project.Definition, change);
    int geometryCount = cache.GeometryRebuildCount, entityCount = cache.EntityRebuildCount;
    int selectionCount = cache.SelectionRebuildCount;
    doc.Selection.Add(box.Id); doc.SelectionChanged();
    Check(cache.GeometryRebuildCount == geometryCount && cache.EntityRebuildCount == entityCount
        && cache.SelectionRebuildCount == selectionCount + 1, "selection invalidates only selection");
    var native = cache.NativeFaces;
    doc.TransformSelection(new[] { spawn.Id }, "Move", Vector3.One, 0, 1, false);
    Check(cache.GeometryRebuildCount == geometryCount && ReferenceEquals(native, cache.NativeFaces), "entity move preserves mesh cache");
    Check(cache.EntityRebuildCount == entityCount + 1, "entity move invalidates entity cache");
    int rebuilt = cache.GeometryObjectsRebuilt;
    doc.TransformSelection(new[] { box.Id }, "Move", Vector3.One, 0, 1, false);
    Check(cache.GeometryObjectsRebuilt == rebuilt + 1, "single object geometry rebuild");
    doc.OverlayChanged();
    Check(cache.GeometryObjectsRebuilt == rebuilt + 1, "overlay leaves geometry cached");
    doc.Save(Path.Combine(root, "map.json"));
    Check(cache.GeometryObjectsRebuilt == rebuilt + 1, "save leaves geometry cached");
    var layout = new MapViewportLayout(800, 600, 1.5);
    Check(layout.PixelWidth == 1200 && layout.PixelHeight == 900 && layout.Normalize(400, 300) == (0d, 0d), "DPI layout contract");
    Check(new MapViewportLayout(0, 0).PixelWidth == 0, "empty viewport safe");
    var original = MapBuildSnapshot.Capture(doc.Project);
    float snapshotX = original.CreateDefinition().Geometry[0].Transform.Position[0];
    doc.Project.Definition.Geometry[0].Transform.Position[0] += 10;
    Check(original.CreateDefinition().Geometry[0].Transform.Position[0] == snapshotX, "build snapshot detached from editor");
    var copied = original.CreateDefinition(); copied.Geometry[0].Transform.Position[0] += 20;
    Check(original.CreateDefinition().Geometry[0].Transform.Position[0] == snapshotX, "each worker has its own graph");
    var logical = original.CreateDefinition();
    string identity = MapBuildFingerprint.Create(logical).ContentKey;
    logical.SourcePath = Path.Combine(root, "absent.json");
    Check(MapBuildFingerprint.Create(logical).ContentKey == identity, "fingerprint uses in-memory recipe");
    logical.Geometry[0].Transform.Position[0]++;
    Check(MapBuildFingerprint.Create(logical).ContentKey != identity, "unsaved edit changes fingerprint");
    string cacheRoot = Path.Combine(root, "cache");
    int builds = 0;
    using var release = new ManualResetEventSlim();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    MapValidationResult FakeBuild(MapDefinition map, string directory)
    {
        Interlocked.Increment(ref builds); entered.TrySetResult();
        if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test build release");
        foreach (var file in MapOutputSet.Create(map,directory,directory,directory).Files) File.WriteAllText(file,map.Name);
        return new MapValidationResult();
    }
    var scheduler = new MapBuildScheduler(cacheRoot, build: FakeBuild);
    using var cancel = new CancellationTokenSource();
    var first = scheduler.BuildAsync(original, cancel.Token);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var second = scheduler.BuildAsync(original);
    var timeout = DateTime.UtcNow.AddSeconds(10);
    while (scheduler.SharedRequests == 0 && DateTime.UtcNow < timeout) await Task.Delay(1);
    Check(scheduler.SharedRequests == 1, "identical concurrent requests share build");
    cancel.Cancel();
    try { await first; throw new Exception("cancellation did not propagate"); }
    catch (OperationCanceledException) { checks++; }
    release.Set();
    var built = await second;
    Check(built.Succeeded && builds == 1, "one caller cancellation preserves shared work");
    var hit = await scheduler.BuildAsync(original);
    Check(hit.CacheHit && builds == 1, "persistent cache hit skips compilation");
    File.WriteAllText(hit.Outputs!.Model, "corruption");
    var repaired = await scheduler.BuildAsync(original);
    Check(repaired.Succeeded && !repaired.CacheHit && builds == 2, "corrupt output is rebuilt");
    MapBuildScheduler.Install(repaired,original.CreateDefinition(),Path.Combine(root,"runtime"),Path.Combine(root,"entities"),Path.Combine(root,"nodes"));
    Check(MapBuildManifest.IsCurrent(original.CreateDefinition(),MapOutputSet.Create(original.CreateDefinition(),Path.Combine(root,"runtime"),Path.Combine(root,"entities"),Path.Combine(root,"nodes"))), "installed manifest matches outputs");
    int active=0, maximum=0;
    using var parallelRelease = new ManualResetEventSlim();
    var twoEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var parallel = new MapBuildScheduler(Path.Combine(root,"parallel"),build:(map,directory)=>
    {
        int current=Interlocked.Increment(ref active);
        int seen; do { seen=Volatile.Read(ref maximum); } while(current>seen && Interlocked.CompareExchange(ref maximum,current,seen)!=seen);
        if(current==2)twoEntered.TrySetResult();
        try
        {
            if(!parallelRelease.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException();
            foreach(var file in MapOutputSet.Create(map,directory,directory,directory).Files)File.WriteAllText(file,map.Name);
            return new MapValidationResult();
        }
        finally{Interlocked.Decrement(ref active);}
    });
    var requests=Enumerable.Range(0,6).Select(n=>{var map=original.CreateDefinition();map.Name="CHECK_"+n;return parallel.BuildAsync(MapBuildSnapshot.Capture(map));}).ToArray();
    await twoEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    parallelRelease.Set();
    var results=await Task.WhenAll(requests);
    Check(maximum==2 && results.All(r=>r.Succeeded),"compiler concurrency is bounded at two");
    var failureScheduler=new MapBuildScheduler(Path.Combine(root,"failures"),build:(_,_)=>throw new IOException("fixture compiler failure"));
    var failure=await failureScheduler.BuildAsync(original);
    Check(!failure.Succeeded && failure.Diagnostics.Any(d=>d.Message.Contains("fixture compiler failure")),"compiler exception is structured");
    Check(failureScheduler.PendingCount==0,"failed jobs leave no retained flight");
    // Exercise the real compiler/packer with a synthetic texture, without game assets.
    string texturePath=Path.Combine(root,"test.tex");
    using(var texture=new BinaryWriter(File.Create(texturePath)))
    {
        texture.Write(System.Text.Encoding.ASCII.GetBytes("FPTX"));texture.Write((ushort)1);texture.Write((ushort)1);
        texture.Write((ushort)0);texture.Write((ushort)8);texture.Write((ushort)8);texture.Write((ushort)1);texture.Write((ushort)0);
        texture.Write((ushort)32767);texture.Write(new byte[64]);
    }
    var realDefinition=new MapDefinition{Name="REAL_BUILD_CHECK",BaseDirectory=root};
    realDefinition.Materials.Add(new(){Texture="test.tex"});realDefinition.Assets.Add(new(){Path="test.tex"});
    realDefinition.Geometry.Add(new MapBox{Transform=new(){Position=new[]{0f,-1,0},Scale=new[]{8f,1,8}}});
    realDefinition.Spawns.Add(new(){Position=new[]{0f,2,0}});
    var realSnapshot=MapBuildSnapshot.Capture(realDefinition);
    var realScheduler=new MapBuildScheduler(Path.Combine(root,"real-cache"));
    var analysis=await realScheduler.AnalyzeAsync(realSnapshot);
    var navigation=await realScheduler.AnalyzeAsync(realSnapshot,navigation:true);
    Check(analysis.Succeeded && analysis.Faces.Length>0 && navigation.CreateNavigation()!=null,
        "validation and navigation share scheduler without game files");
    var graph=navigation.CreateNavigation()!;
    byte originalNode=graph.Bytes[0];graph.Bytes[0]^=255;
    Check(navigation.CreateNavigation()!.Bytes[0]==originalNode,"navigation views are detached between consumers");
    var realBuild=await realScheduler.BuildAsync(realSnapshot);
    Check(realBuild.Succeeded,"real native compile/pack: "+string.Join(";",realBuild.Diagnostics.Select(d=>d.Message)));
    Check(realBuild.Outputs!.Files.All(f=>new FileInfo(f).Length>0),"real build produces all five binaries");
    Check((await realScheduler.BuildAsync(realSnapshot)).CacheHit,"real build cache hit");
    string scheduledPackage=await realScheduler.PackageAsync(realSnapshot,Path.Combine(root,"scheduled.ppmap"));
    Check(File.Exists(scheduledPackage) && realScheduler.CompilationCount==1,
        "validation, navigation, runtime and packaging reuse one compilation");
    string repeatedPackage=MapPackageBuilder.Build(realDefinition,Path.Combine(root,"repeated.ppmap"));
    Check(File.ReadAllBytes(scheduledPackage).SequenceEqual(File.ReadAllBytes(repeatedPackage)),
        "scheduled and synchronous package entry points produce identical bytes");
    Check(MapDependencyAnalyzer.PackageAssets(realDefinition).SequenceEqual(new[]{"test.tex"}),
        "packaging and cache use one asset dependency set");
    var borrowed=MapProjectSerializer.Clone(realDefinition);borrowed.Materials[0].Texture=null;
    borrowed.Assets.Clear();borrowed.Name="BORROWED_VALIDATION";
    Check((await realScheduler.AnalyzeAsync(MapBuildSnapshot.Capture(borrowed))).Succeeded,
        "validation does not require cartridge textures used only by packing");
    for(int i=0;i<12;i++)
    {
        var retained=MapProjectSerializer.Clone(realDefinition);retained.Name="RETAINED_"+i;
        await realScheduler.AnalyzeAsync(MapBuildSnapshot.Capture(retained));
    }
    Check(realScheduler.CompiledCacheCount<=8 && realScheduler.CompiledCacheBytes<=128L*1024*1024,
        "shared compiled geometry cache is bounded");
    using var mixedRelease=new ManualResetEventSlim();
    var mixedEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var mixed=new MapBuildScheduler(Path.Combine(root,"mixed"),concurrency:1,maximumPending:2,build:(map,directory)=>
    {
        mixedEntered.TrySetResult();
        if(!mixedRelease.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException();
        foreach(var file in MapOutputSet.Create(map,directory,directory,directory).Files)File.WriteAllText(file,map.Name);
        return new();
    });
    var mixedBuild=mixed.BuildAsync(realSnapshot);
    await mixedEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    using var analysisCancel=new CancellationTokenSource();
    var cancelledAnalysis=mixed.AnalyzeAsync(realSnapshot,cancellation:analysisCancel.Token);
    timeout=DateTime.UtcNow.AddSeconds(10);
    while(mixed.PendingCount<2&&DateTime.UtcNow<timeout)await Task.Delay(1);
    var sharedAnalysis=mixed.AnalyzeAsync(realSnapshot);
    timeout=DateTime.UtcNow.AddSeconds(10);
    while(mixed.SharedRequests==0&&DateTime.UtcNow<timeout)await Task.Delay(1);
    var fullQueue=await mixed.AnalyzeAsync(realSnapshot,navigation:true);
    Check(!fullQueue.Succeeded&&fullQueue.Diagnostics.Any(d=>d.Message.Contains("queue is full")),
        "analysis and runtime jobs share the same queue capacity");
    analysisCancel.Cancel();
    try{await cancelledAnalysis;throw new Exception("analysis cancellation did not propagate");}
    catch(OperationCanceledException){checks++;}
    mixedRelease.Set();
    Check((await mixedBuild).Succeeded&&(await sharedAnalysis).Succeeded&&mixed.CompilationCount==1&&mixed.PendingCount==0,
        "cancelling an analysis waiter preserves shared queued work");
    // Independent scheduler owners share the same persistent content cache.
    using var otherRelease=new ManualResetEventSlim();
    var otherEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int otherBuilds=0;
    MapValidationResult OtherBuild(MapDefinition map,string directory)
    {
        Interlocked.Increment(ref otherBuilds);otherEntered.TrySetResult();
        if(!otherRelease.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException();
        foreach(var file in MapOutputSet.Create(map,directory,directory,directory).Files)File.WriteAllText(file,map.Name);
        return new();
    }
    var ownerA=new MapBuildScheduler(Path.Combine(root,"shared-owner"),build:OtherBuild);
    var ownerB=new MapBuildScheduler(Path.Combine(root,"shared-owner"),build:OtherBuild);
    var ownerFirst=ownerA.BuildAsync(realSnapshot);
    await otherEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var ownerSecond=ownerB.BuildAsync(realSnapshot);
    timeout=DateTime.UtcNow.AddSeconds(10);
    while(ownerB.PendingCount==0&&DateTime.UtcNow<timeout)await Task.Delay(1);
    await Task.Delay(60);
    otherRelease.Set();
    var ownerResults=await Task.WhenAll(ownerFirst,ownerSecond);
    Check(otherBuilds==1&&ownerResults.All(r=>r.Succeeded)&&ownerResults.Any(r=>r.CacheHit),
        "independent schedulers wait for publication instead of failing or compiling twice");
    string beforeAssetChange=MapBuildFingerprint.Create(realDefinition).ContentKey;
    using(var texture=File.Open(texturePath,FileMode.Open,FileAccess.Write)){texture.Position=18;texture.WriteByte(0);}
    Check(MapBuildFingerprint.Create(realDefinition).ContentKey!=beforeAssetChange,"asset content changes fingerprint");
    string package=MapPackageBuilder.Build(realDefinition,Path.Combine(root,"real.ppmap"));
    var importedPackage=MapDefinition.Load(package);
    Check(importedPackage.FormatVersion==2 && importedPackage.MapId!=Guid.Empty,"legacy project packages with stable upgraded identity");
    var packageResult=await realScheduler.BuildAsync(MapBuildSnapshot.Capture(importedPackage));
    Check(packageResult.Succeeded,"existing ppmap package compiles through scheduler");
    var changedScheduler=new MapBuildScheduler(Path.Combine(root,"changed-cache"),build:(map,directory)=>
    {
        File.AppendAllText(texturePath,"changed while building");
        foreach(var file in MapOutputSet.Create(map,directory,directory,directory).Files)File.WriteAllText(file,map.Name);
        return new MapValidationResult();
    });
    var changedResult=await changedScheduler.BuildAsync(realSnapshot);
    Check(!changedResult.Succeeded && changedResult.Diagnostics.Any(d=>d.Message.Contains("dependencies changed")),"changing dependency cannot poison cache");
    Console.WriteLine($"Map editor: {checks} checks passed.");
    if (args.Contains("--benchmark")) Benchmarks.Run();
}
finally { Directory.Delete(root, true); }
sealed class Counter(Action execute, Action undo, long bytes) : IMapEditCommand
{
    public string Label => "Counter";
    public long ApproximateBytes => bytes;
    public MapDocumentChange Change { get; } = new(MapChangeDomain.Metadata);
    public void Execute() => execute();
    public void Undo() => undo();
}
