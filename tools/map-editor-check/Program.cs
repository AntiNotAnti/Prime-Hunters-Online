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

    // QoL commands keep object operations transactional and undoable.
    doc.Selection.Clear(); doc.Selection.Add(box.Id); doc.SelectionChanged();
    doc.CopySelection(); int clipboardCount=doc.Project.Definition.Geometry.Count;
    Check(doc.PasteClipboard() && doc.Project.Definition.Geometry.Count==clipboardCount+1, "clipboard paste duplicates selection");
    Check(doc.Selection.Count==1 && !doc.Selection.Contains(box.Id), "pasted objects become selection");
    doc.History.Undo(); Check(doc.Project.Definition.Geometry.Count==clipboardCount, "clipboard paste undo");
    doc.Selection.Clear(); doc.Selection.Add(box.Id); doc.HideSelection();
    Check(doc.Project.Definition.Geometry.First(g=>g.Id==box.Id).Hidden, "hide selection");
    doc.History.Undo(); Check(!doc.Project.Definition.Geometry.First(g=>g.Id==box.Id).Hidden, "hide selection undo");
    doc.SetLayerState("Architecture",locked:true);
    Check(doc.Project.Definition.Geometry.Where(g=>g.Layer=="Architecture").All(g=>g.Locked), "layer lock");
    doc.History.Undo();

    var hybridCheck=new MapDefinition{Name="HYBRID_CHECK",Import=new(){Source="missing.pk3",KeepSpawns=true}};
    hybridCheck.Materials.Add(new()); hybridCheck.Geometry.Add(new MapBox());
    var hybridValidation=MapValidator.Validate(hybridCheck,checkSources:false);
    Check(!hybridValidation.Diagnostics.Any(d=>d.Message.Contains("additional primitives",StringComparison.OrdinalIgnoreCase)),
        "imported projects allow authored hybrid primitives");
    Check(GeometryCompiler.Compile(hybridCheck.Geometry[0],16,7).All(face=>face.Material==7),
        "hybrid geometry material offset");

    string pk3Root=Path.Combine(root,"pk3-discovery");Directory.CreateDirectory(pk3Root);
    string sourcePk3=Path.Combine(pk3Root,"level.pk3"), dependencyPk3=Path.Combine(pk3Root,"pak-textures.pk3");
    File.WriteAllBytes(sourcePk3,Array.Empty<byte>());File.WriteAllBytes(dependencyPk3,Array.Empty<byte>());
    var archives=MapTextureBake.DiscoverArchives(sourcePk3);
    Check(Path.GetFullPath(archives[0])==Path.GetFullPath(sourcePk3)
        && archives.Contains(Path.GetFullPath(dependencyPk3),StringComparer.OrdinalIgnoreCase),
        "PK3 discovery keeps selected source first and finds siblings");
    string failedImport=Path.Combine(root,"failed-import");
    var failedResult=Q3ImportService.Import(new("missing.pk3",null,"FAILED_IMPORT",failedImport));
    Check(!failedResult.Succeeded&&!Directory.Exists(failedImport),"failed import leaves no destination folder");

    var prefabSource=new MapDefinition{Name="PREFAB_SOURCE"};prefabSource.Materials.Add(new(){Name="base"});
    var prefabBox=new MapBox{Label="Prefab box"};prefabSource.Geometry.Add(prefabBox);
    var prefabSpawn=new MapSpawn{Id=Guid.NewGuid(),Label="Prefab spawn"};prefabSource.Spawns.Add(prefabSpawn);
    string prefabPath=Path.Combine(root,"prefabs","fixture.json");
    MapPrefabService.Save(prefabSource,new System.Collections.Generic.HashSet<Guid>{prefabBox.Id,prefabSpawn.Id},prefabPath);
    var prefabDestination=new MapDefinition{Name="PREFAB_DEST"};string prefabDestRoot=Path.Combine(root,"prefab-dest");Directory.CreateDirectory(prefabDestRoot);
    var inserted=MapPrefabService.Insert(prefabDestination,prefabPath,prefabDestRoot);
    Check(inserted.ObjectIds.Count==2&&prefabDestination.Geometry.Count==1&&prefabDestination.Spawns.Count==1,
        "prefab selection roundtrip");
    Check(prefabDestination.Materials.Count==1&&prefabDestination.Geometry[0].Material==0,
        "prefab material remap");

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
    var meshIdentities = cache.Meshes.ToDictionary(m => m.ObjectId);
    doc.SelectionChanged();
    Check(cache.Meshes.All(m => ReferenceEquals(m,meshIdentities[m.ObjectId])), "selection preserves GPU mesh identities");
    var pickMap = new MapDefinition();
    var nearBrush = new MapBox { Transform = new() { Position = new[] { 0f, 0, 2f }, Scale = new[] { 2f, 1, .5f } } };
    var farBrush = new MapBox { Transform = new() { Position = new[] { 0f, 0, -5f } } };
    pickMap.Geometry.Add(farBrush);pickMap.Geometry.Add(nearBrush);
    var pickCache = new MapViewportCache();pickCache.Invalidate(pickMap,new(MapChangeDomain.All));
    foreach (double dpi in new[] { 1d, 1.5, 2 })
    foreach (bool perspective in new[] { true, false })
    {
        var pickLayout = new MapViewportLayout(800,450,dpi,120,80);
        var camera = new MapViewportCamera(new(0,0,10),Vector3.Zero,perspective);
        var frame = new MapRenderFrame(pickLayout,camera,pickCache.Meshes,new System.Collections.Generic.HashSet<Guid>(),
            new System.Collections.Generic.Dictionary<Guid,Matrix4x4>(),false,false);
        Check(MapViewportPicking.Pick(frame,400,225)==nearBrush.Id,"nearest world-space picking at DPI "+dpi+" perspective "+perspective);
        var point = new Vector3(1, .5f, 0);
        var screen = camera.Project(pickLayout,point)!.Value;
        var ray = camera.Ray(pickLayout,screen.X,screen.Y);
        Check(Vector3.Cross(Vector3.Normalize(point-ray.Origin),ray.Direction).Length()<.00001f,
            "projection and pointer ray agree");
        var view = Matrix4x4.CreateLookAt(camera.Position,camera.Target,camera.Basis().Up);
        var clip = Vector4.Transform(new Vector4(point,1),view*camera.Projection(pickLayout));
        Check(Math.Abs((clip.X/clip.W+1)*400-screen.X)<.001&&Math.Abs((1-clip.Y/clip.W)*225-screen.Y)<.001,
            "renderer projection matches overlay coordinates");
    }
    // Multi-object tools publish one history command and preserve local transforms.
    var layoutDefinition = new MapDefinition();
    layoutDefinition.Geometry.Add(new MapBox { Transform = new() { Position = new[] { 0f, 1, 0 } } });
    layoutDefinition.Geometry.Add(new MapBox { Transform = new() { Position = new[] { 3f, 2, 0 } } });
    layoutDefinition.Geometry.Add(new MapBox { Transform = new() { Position = new[] { 10f, 4, 0 } } });
    var layoutDocument = new MapDocument(new(layoutDefinition));
    var layoutIds = layoutDocument.Project.Definition.Geometry.Select(g => g.Id).ToHashSet();
    layoutDocument.EditObjects("Align", layoutIds, d => MapLayoutCommands.Align(d, layoutIds, 1, -1));
    Check(layoutDocument.Project.Definition.Geometry.All(g => g.Transform.Position[1] == 1), "align bounds");
    layoutDocument.History.Undo();
    Check(layoutDocument.Project.Definition.Geometry[2].Transform.Position[1] == 4, "align undo restores all objects");
    layoutDocument.EditObjects("Distribute", layoutIds, d => MapLayoutCommands.Distribute(d, layoutIds, 0));
    Check(layoutDocument.Project.Definition.Geometry[1].Transform.Position[0] == 5, "distribute centers");
    layoutDocument.EditObjects("Array", layoutIds, d => MapLayoutCommands.Array(d, layoutIds, 4, new(0,0,3)));
    Check(layoutDocument.Project.Definition.Geometry.Count == 15 && layoutDocument.Project.Definition.Geometry.Select(g=>g.Id).Distinct().Count() == 15, "array identities");
    layoutDocument.History.Undo(); Check(layoutDocument.Project.Definition.Geometry.Count == 3, "array undoes as one transaction");
    layoutDocument.TransformSelection(layoutIds, "Scale", Vector3.Zero, 0, 2, true, scaleAxes: Vector3.UnitX, pivot: Vector3.Zero);
    Check(layoutDocument.Project.Definition.Geometry[2].Transform.Position[0] == 20 && layoutDocument.Project.Definition.Geometry[0].Transform.Scale.SequenceEqual(new[]{2f,1,1}), "axis scale and world pivot");
    layoutDocument.History.Undo();
    layoutDocument.TransformSelection(layoutIds, "Rotate", Vector3.Zero, 90, 1, false, rotationAxis: Vector3.UnitZ, pivot: Vector3.Zero);
    Check(Math.Abs(layoutDocument.Project.Definition.Geometry[0].Transform.Position[0] + 1) < .001, "Z rotation around world pivot");
    layoutDocument.History.Undo();
    var graphCheck = new MapNodePacker.NavigationGraph(System.Array.Empty<byte>(),
        new[]{OpenTK.Mathematics.Vector3.Zero,OpenTK.Mathematics.Vector3.UnitX,new OpenTK.Mathematics.Vector3(2,0,0),new OpenTK.Mathematics.Vector3(5,0,0)},
        new[]{new[]{1},new[]{0,2},new[]{1},System.Array.Empty<int>()},new[]{0,0,0,1},2);
    Check(MapNavigationInspection.Find(graphCheck,0,2).SequenceEqual(new[]{0,1,2}), "navigation path");
    Check(MapNavigationInspection.Find(graphCheck,0,3).Length == 0, "disconnected navigation path");
    var floorId = Guid.NewGuid();
    var floorFaces = new[] { new MapViewportFace(floorId, new[] { new Vector3(-20,0,-20), new Vector3(-20,0,20), new Vector3(20,0,20), new Vector3(20,0,-20) }, 1, 0, true) };
    Check(MapLayoutCommands.FloorBelow(new(0,5,0), floorFaces) == 0 && MapLayoutCommands.FloorBelow(new(40,5,0), floorFaces) == null, "floor snap intersects the actual surface");
    var floorIndex=new MapFaceSpatialIndex(floorFaces);
    Check(floorIndex.Column(new(0,5,0)).Count==1&&floorIndex.Column(new(40,5,0)).Count==0,
        "spatial floor query prunes distant imported faces");
    var chunkFaces=Enumerable.Range(0,128).Select(i=>new MapViewportFace(Guid.Empty,
        new[]{new Vector3(i*8,0,0),new Vector3(i*8+1,0,0),new Vector3(i*8,1,0)},1,0,true)).ToArray();
    var chunks=MapViewportChunker.Create(chunkFaces,chunkFaces);
    Check(chunks.Count>1&&chunks.Sum(x=>x.Faces.Count)==chunkFaces.Length,"large imported geometry partitions into stable viewport chunks");
    Check(MapCollisionHeatmap.Build(chunkFaces).Count>1,"collision heatmap aggregates large-map cost spatially");
    var partitionDefinition=new MapDefinition{Name="PARTITION_CHECK",ScaleFactor=8};partitionDefinition.Materials.Add(new());
    var partitionMap=new BuiltMap(partitionDefinition);
    for(int i=0;i<9000;i++)
    {
        float x=(i%300)*2,z=(i/300)*2;
        partitionMap.Faces.Add(new BuiltFace(
            new[]{new OpenTK.Mathematics.Vector3(x,0,z),new OpenTK.Mathematics.Vector3(x+1,0,z),new OpenTK.Mathematics.Vector3(x,0,z+1)},
            new[]{OpenTK.Mathematics.Vector2.Zero,OpenTK.Mathematics.Vector2.Zero,OpenTK.Mathematics.Vector2.Zero},
            OpenTK.Mathematics.Vector3.UnitY,0,1));
    }
    partitionMap.Solid.Add(partitionMap.Faces[0]);
    var partitionValidation=new MapValidationResult();MapBudgetValidator.Analyze(partitionMap,partitionValidation);
    Check(partitionValidation.Budgets.Single(b=>b.Name=="Render partitions").Used>1,
        "large runtime geometry is spatially partitioned before packing");

    var portalFaces=new System.Collections.Generic.List<BuiltFace>();
    for(int i=0;i<300;i++)
    {
        float x=i<150?1:9;
        float z=(i%10)*.05f;
        portalFaces.Add(new BuiltFace(
            new[]{new OpenTK.Mathematics.Vector3(x,0,z),new OpenTK.Mathematics.Vector3(x+.2f,0,z),new OpenTK.Mathematics.Vector3(x,1,z+.2f)},
            new[]{OpenTK.Mathematics.Vector2.Zero,OpenTK.Mathematics.Vector2.Zero,OpenTK.Mathematics.Vector2.Zero},
            OpenTK.Mathematics.Vector3.UnitY,0,1));
    }
    var portalSettings=new MapPartitionSettings{Enabled=true,CellSize=8,FaceThreshold=256,PortalCulling=true};
    var portalPlan=MapRuntimePartitioner.Create(portalFaces,portalSettings);
    Check(portalPlan.Parts.Count==2&&portalPlan.PortalCullingApplied&&portalPlan.Portals.Count==1,
        "connected spatial parts generate one safe runtime portal");
    var partitionSpawn=new MphRead.Editor.PlayerSpawnEntityEditor{Position=new OpenTK.Mathematics.Vector3(9,1,0)};
    MapRuntimePartitioner.AssignEntityNodes(new MphRead.Editor.EntityEditorBase[]{partitionSpawn},portalPlan);
    Check(partitionSpawn.NodeName==portalPlan.Parts[1].RoomNodeName,
        "runtime entity is assigned to containing generated room part");
    var collisionEditor=new MphRead.Utility.CollisionDataEditor
    {
        Plane=new OpenTK.Mathematics.Vector4(OpenTK.Mathematics.Vector3.UnitY,0),
        LayerMask=5
    };
    collisionEditor.Points.AddRange(new[]{new OpenTK.Mathematics.Vector3(0,0,0),new OpenTK.Mathematics.Vector3(0,0,1),new OpenTK.Mathematics.Vector3(1,0,0)});
    byte[] portalCollision=MapCollisionPacker.Pack(new[]{collisionEditor},portalPlan.Portals);
    var portalHeader=MphRead.Read.ReadStruct<MphRead.Formats.Collision.CollisionHeader>(portalCollision);
    Check(portalHeader.PortalCount==1,"optimized collision packer publishes generated room portal");
    var partitionRoundtrip=new MapDefinition{Name="PARTITION_SERIALIZE",FormatVersion=2,MapId=Guid.NewGuid(),
        Partitioning=portalSettings};
    partitionRoundtrip.Materials.Add(new(){Id=Guid.NewGuid()});
    partitionRoundtrip.Geometry.Add(new MapBox());
    partitionRoundtrip.Spawns.Add(new(){Id=Guid.NewGuid(),Position=new[]{0f,2,0}});
    Check(MapProjectSerializer.Clone(partitionRoundtrip).Partitioning?.PortalCulling==true,
        "partition policy survives project serialization");
    layoutDocument.EditObjects("Floor", layoutIds, d => MapLayoutCommands.SnapToFloor(d, layoutIds, floorFaces));
    Check(layoutDocument.Project.Definition.Geometry.All(g => Math.Abs(g.Transform.Position[1] - .5f) < .001), "floor snap lands selected bounds");
    layoutDocument.History.Undo();

    // General mesh authoring: convert, edit faces/vertices and preserve winding/materials.
    var sourceBox=new MapBox{Material=0,Transform=new(){Position=new[]{2f,1,3},Scale=new[]{4f,2,6}}};
    var editable=MapMeshEditing.Convert(sourceBox,16);
    Check(editable.Vertices.Count==8&&editable.Faces.Count==6&&editable.Transform.Position.SequenceEqual(new[]{0f,0f,0f}),
        "primitive converts to world-space editable mesh");
    Check(editable.FaceTexcoords.Count==editable.Faces.Count&&editable.FaceTexcoords.All(uv=>uv!=null),
        "mesh conversion preserves explicit per-face UVs");
    int originalFaces=editable.Faces.Count,originalVertices=editable.Vertices.Count;
    MapMeshEditing.ExtrudeFace(editable,0,1);
    Check(editable.Faces.Count==originalFaces+4&&editable.Vertices.Count==originalVertices+4,
        "mesh face extrusion adds cap and side walls");
    MapMeshEditing.InsetFace(editable,0,.25f);
    Check(editable.Faces.Count==originalFaces+8,"mesh face inset adds border faces");
    int beforeSubdivide=editable.Faces.Count;
    MapMeshEditing.SubdivideFace(editable,0);
    Check(editable.Faces.Count>beforeSubdivide,"mesh face subdivision creates fan");
    int beforeWeld=editable.Vertices.Count;
    editable.Vertices.Add((float[])editable.Vertices[0].Clone());
    editable.Faces.Add(new[]{0,1,editable.Vertices.Count-1});editable.FaceMaterials.Add(0);
    Check(MapMeshEditing.Weld(editable,.0001f)>=1&&editable.Vertices.Count<=beforeWeld,
        "mesh vertex weld removes duplicate vertices");
    var explicitUvMesh=new MapMesh
    {
        Vertices=new(){new[]{0f,0,0},new[]{1f,0,0},new[]{0f,1,0}},
        Faces=new(){new[]{0,1,2}},
        FaceMaterials=new(){0},
        FaceTexcoords=new(){new[]{new[]{3f,4f},new[]{7f,4f},new[]{3f,9f}}},
        Solid=false
    };
    var explicitUvFace=GeometryCompiler.Compile(explicitUvMesh,16).Single();
    Check(explicitUvFace.Texcoords[0].X==3&&explicitUvFace.Texcoords[1].X==7&&explicitUvFace.Texcoords[2].Y==9,
        "explicit mesh UVs compile without reprojection");
    MapMeshEditing.FlipFace(explicitUvMesh,0);
    Check(explicitUvMesh.FaceTexcoords[0]![0][1]==9,
        "face flip keeps explicit UV winding aligned");

    var meshDefinition=new MapDefinition{Name="MESH_CHECK",FormatVersion=2,MapId=Guid.NewGuid()};
    meshDefinition.Materials.Add(new(){Id=Guid.NewGuid()});meshDefinition.Geometry.Add(editable);
    meshDefinition.Spawns.Add(new(){Id=Guid.NewGuid(),Position=new[]{0f,5,0}});
    Check(MapValidator.Validate(meshDefinition,checkSources:false).Diagnostics.All(d=>d.Code!="FP-MAP-013"),
        "editable mesh passes geometry validation");

    var csgA=new MapBox{Transform=new(){Position=new[]{0f,0,0},Scale=new[]{4f,4,4}}};
    var csgB=new MapBox{Transform=new(){Position=new[]{1f,0,0},Scale=new[]{2f,2,2}}};
    Check(MapBoxCsg.Intersect(csgA,csgB)!=null&&MapBoxCsg.Subtract(csgA,csgB).Count>0,
        "axis-aligned box CSG intersection and subtraction");

    var nativeDefinition=new MapDefinition{Name="NATIVE_REMIX_CHECK",FormatVersion=2,MapId=Guid.NewGuid(),
        NativeRoom=new(){Room="MP3 PROVING GROUND",UseNativeArchitecture=false},TextureSource="MP3 PROVING GROUND"};
    nativeDefinition.Materials.Add(new(){Id=Guid.NewGuid(),SourceMaterial=0});
    nativeDefinition.Spawns.Add(new(){Id=Guid.NewGuid(),Position=new[]{0f,1,0}});
    var nativeRoundtrip=MapProjectSerializer.Clone(nativeDefinition);
    Check(nativeRoundtrip.NativeRoom?.Room=="MP3 PROVING GROUND"&&nativeRoundtrip.NativeRoom.UseNativeCollision
        &&!nativeRoundtrip.NativeRoom.UseNativeArchitecture,
        "native room remix source and detached-architecture policy survive project serialization");
    Check(MapValidator.Validate(nativeRoundtrip,checkSources:false).Diagnostics.All(d=>d.Code!="FP-MAP-005"),
        "native room remix source validates without reading cartridge bytes");

    foreach (string tool in new[] { "Move", "Rotate", "Scale" })
    {
        var id = layoutIds.First(); var item = MapObjects.Find(layoutDocument.Project.Definition, id)!;
        var beforeFaces = GeometryCompiler.Compile((MapGeometry)item.Value, 1).SelectMany(f => f.Points).ToArray();
        var matrix = MapTransformPreview.Matrix(item, tool, new(2,3,4), 35, 1.7f, true, Vector3.UnitZ, Vector3.UnitX, Vector3.Zero);
        layoutDocument.TransformSelection(new[]{id},tool,new(2,3,4),35,1.7f,true,rotationAxis:Vector3.UnitZ,scaleAxes:Vector3.UnitX,pivot:Vector3.Zero);
        var afterFaces = GeometryCompiler.Compile((MapGeometry)MapObjects.Find(layoutDocument.Project.Definition,id)!.Value,1).SelectMany(f=>f.Points).ToArray();
        Check(beforeFaces.Select((point,i) => Vector3.Distance(Vector3.Transform(new(point.X,point.Y,point.Z),matrix),new(afterFaces[i].X,afterFaces[i].Y,afterFaces[i].Z))).All(distance => distance < .001),
            tool + " preview matches committed geometry");
        layoutDocument.History.Undo();
    }
    string previewOld="preview/old.png", previewNew="preview/new.png";
    Directory.CreateDirectory(Path.Combine(root,"preview")); File.WriteAllText(Path.Combine(root,previewOld),"old"); File.WriteAllText(Path.Combine(root,previewNew),"new");
    layoutDocument.RegisterGeneratedAsset(previewOld,root);layoutDocument.RegisterGeneratedAsset(previewNew,root);
    layoutDocument.Edit("Preview",d=>d.Assets.Add(new(){Path=previewOld,Kind="preview"}));
    int previewHistory=layoutDocument.History.CommandCount;
    layoutDocument.Edit("Replace preview",d=>{d.Assets.RemoveAll(a=>a.Kind=="preview");d.Assets.Add(new(){Path=previewNew,Kind="preview"});});
    Check(layoutDocument.History.CommandCount==previewHistory+1,"preview replacement is one command");
    layoutDocument.History.Undo(); Check(layoutDocument.Project.Definition.Assets.Single().Path==previewOld,"preview undo");
    Check(layoutDocument.CleanupGeneratedAssets()==0 && File.Exists(Path.Combine(root,previewNew)),"cleanup preserves undo and redo files");
    layoutDocument.History.Redo(); Check(layoutDocument.Project.Definition.Assets.Single().Path==previewNew,"preview redo");
    string orphan="preview/orphan.png";File.WriteAllText(Path.Combine(root,orphan),"orphan");layoutDocument.RegisterGeneratedAsset(orphan,root);
    Check(layoutDocument.CleanupGeneratedAssets()==1&&!File.Exists(Path.Combine(root,orphan)),"cleanup deletes only registered orphan");
    var original = MapBuildSnapshot.Capture(doc.Project);
    Check(original.CreateDefinition().Serialize() == doc.Project.Definition.Serialize(), "snapshot preserves all serialized DTO values");
    Check(ReferenceEquals(doc.CaptureBuildSnapshot(), doc.CaptureBuildSnapshot()), "unchanged snapshot reused");
    var autosaveState = doc.CurrentStateId;
    using (var autosave = new MapAutosaveService())
    {
        var payload = doc.CaptureAutosave(root);
        Check(autosave.Queue(payload), "autosave accepts detached payload");
        await autosave.Completion;
        Check(autosave.Result?.Error == null && File.Exists(payload.Path), "autosave commits on worker");
        Check(doc.CurrentStateId == autosaveState, "autosave never mutates document");
        Check(MapDocument.ReadRecovery(payload.Path).Definition.Serialize() == doc.Project.Definition.Serialize(), "recovery roundtrip");
        for (int i = 0; i < 12; i++)
        {
            doc.TransformSelection(new[] { box.Id }, "Move", new(1, 0, 0), 0, 1, false);
            autosave.Queue(doc.CaptureAutosave(root));
        }
        await autosave.Completion;
        Check(autosave.Result?.State == doc.CurrentStateId, "latest autosave wins");
        autosave.Dispose();
        Check(!autosave.Queue(payload), "closed autosave rejects future work");
    }
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel(); bool stopped = false;
        try { MapTextureBake.BakeImage(Array.Empty<byte>(), cancelled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped, "cancelled texture bake never decodes");
        var grayTga = new byte[18 + 32 * 32];
        grayTga[2] = 3; // uncompressed grayscale
        grayTga[12] = 32; grayTga[14] = 32; grayTga[16] = 8; grayTga[17] = 0x20;
        for (int i = 18; i < grayTga.Length; i++) grayTga[i] = (byte)(i & 255);
        Check(MapTextureBake.BakeImage(grayTga).Length > 0, "grayscale Q3 texture bake");
        var collisionTriangle=new BuiltFace(
            new[]{new OpenTK.Mathematics.Vector3(0,0,0),new OpenTK.Mathematics.Vector3(1,0,0),new OpenTK.Mathematics.Vector3(0,0,1)},
            new[]{OpenTK.Mathematics.Vector2.Zero,OpenTK.Mathematics.Vector2.Zero,OpenTK.Mathematics.Vector2.Zero},
            OpenTK.Mathematics.Vector3.UnitY,0,1);
        Check(MapBudgetValidator.CollisionFits(Enumerable.Repeat(collisionTriangle,10000)),
            "collision fit accepts representable point-index load");
        Check(!MapBudgetValidator.CollisionFits(Enumerable.Repeat(collisionTriangle,17000)),
            "collision fit rejects 16-bit point-index overflow");
        stopped = false;
        try { Q3Bsp.Load("missing.bsp", null, cancelled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped, "cancelled BSP load never reads");
    }
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
    using (var lastWaiter = new CancellationTokenSource())
    using (var orphanRelease = new ManualResetEventSlim())
    {
        var orphanEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var orphanScheduler = new MapBuildScheduler(Path.Combine(root, "cancelled-build"), build: (map, directory) =>
        {
            orphanEntered.TrySetResult();
            if (!orphanRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            foreach (var file in MapOutputSet.Create(map, directory, directory, directory).Files) File.WriteAllText(file, map.Name);
            return new();
        });
        var abandoned = orphanScheduler.BuildAsync(original, lastWaiter.Token);
        await orphanEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); lastWaiter.Cancel();
        try { await abandoned; throw new Exception("last waiter did not cancel"); } catch (OperationCanceledException) { checks++; }
        orphanRelease.Set();
        var cancelledDeadline = DateTime.UtcNow.AddSeconds(10);
        while (orphanScheduler.PendingCount != 0 && DateTime.UtcNow < cancelledDeadline) await Task.Delay(1);
        Check(orphanScheduler.PendingCount == 0 && !Directory.EnumerateFiles(Path.Combine(root, "cancelled-build"), "cache.json", SearchOption.AllDirectories).Any(),
            "last cancelled waiter prevents cache publication and releases queue");
    }
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
    // Editor analysis keeps compiled geometry visible when a runtime
    // post-compile budget is exceeded, while runtime publication stays blocked.
    var oversizedDefinition=new MapDefinition{Name="OVERSIZED_PREVIEW_CHECK",ScaleFactor=7};
    oversizedDefinition.Materials.Add(new(){TexScale=1});
    oversizedDefinition.Geometry.Add(new MapBox{Transform=new(){Position=new[]{0f,0,0},Scale=new[]{1024f,1,1024f}}});
    oversizedDefinition.Spawns.Add(new(){Position=new[]{0f,2,0}});
    var oversizedCompilation=MapCompiler.Compile(oversizedDefinition);
    Check(oversizedCompilation.Map!=null&&!oversizedCompilation.Validation.IsValid
        && oversizedCompilation.Validation.Diagnostics.Any(d=>d.Message.Contains("Collision references")),
        "oversized map retains editor geometry with runtime budget errors");

    var oversizedScheduler=new MapBuildScheduler(Path.Combine(root,"oversized-cache"));
    var oversizedAnalysis=await oversizedScheduler.AnalyzeAsync(MapBuildSnapshot.Capture(oversizedDefinition));
    Check(!oversizedAnalysis.Succeeded&&oversizedAnalysis.Faces.Length>0,
        "oversized analysis exposes preview faces");
    var oversizedBuild=await oversizedScheduler.BuildAsync(MapBuildSnapshot.Capture(oversizedDefinition));
    Check(!oversizedBuild.Succeeded&&oversizedBuild.Outputs==null,
        "oversized runtime build remains blocked");

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
    if (args.Contains("--benchmark"))
    foreach (int objects in new[] { 1000, 5000, 10000 })
    {
        var big = new MapDefinition(); for(int i=0;i<objects;i++)big.Geometry.Add(new MapBox());
        var watch=System.Diagnostics.Stopwatch.StartNew(); var legacy=MapProjectSerializer.Clone(big); watch.Stop();double oldMs=watch.Elapsed.TotalMilliseconds;
        watch.Restart();var snapshot=MapBuildSnapshot.Capture(big);watch.Stop();
        Console.WriteLine($"Snapshot {objects}: JSON clone {oldMs:0.00} ms; detached capture {watch.Elapsed.TotalMilliseconds:0.00} ms");
    }
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
