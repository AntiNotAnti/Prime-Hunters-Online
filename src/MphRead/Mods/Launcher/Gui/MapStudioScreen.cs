using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MphRead.Mods.MapEditor;
using MphRead.Mods.MapGen;

namespace MphRead.Mods.Launcher.Gui
{
    internal sealed class MapStudioScreen : UserControl, IDisposable
    {
        public event EventHandler? Closed;
        public event EventHandler<MapDefinition>? PlayRequested;
        private readonly Grid _root = new() { RowDefinitions=new("Auto,Auto,*,100,Auto"), Margin=new Thickness(16) };
        private readonly Panel _viewportHost = new();
        private readonly List<Control> _editingControls = new();
        private readonly List<Bitmap> _images=new();
        private readonly StackPanel _inspector = new() { Spacing=6, Margin=new Thickness(10) };
        private readonly ListBox _hierarchy = new() { SelectionMode=SelectionMode.Multiple };
        private readonly ListBox _problems = new();
        private readonly TextBox _path = new() { PlaceholderText="Project filename (.json)" };
        private readonly TextBlock _status = new() { Foreground=GuiTheme.TextDimBrush, TextWrapping=TextWrapping.Wrap };
        private readonly TextBox _search = new() { PlaceholderText="Search objects" };
        private readonly PrimeOverlayHost? _overlays;
        private Control? _sheet;
        private readonly Border _modal = new() { Background=GuiTheme.ScrimBrush, IsVisible=false };
        private readonly DispatcherTimer _idle = new() { Interval=TimeSpan.FromSeconds(2) };
        private readonly MapCatalog _catalog = new(CustomRooms.MapDirectory);
        private MapDocument? _document;
        private MapViewport? _viewport;
        private CancellationTokenSource? _work;
        private bool _refreshing;
        private DateTime _autosaved = DateTime.MinValue;
        private DateTime _checked=DateTime.MinValue;
        private bool _checking;
        private MapBuildResult? _lastBuild;
        private TextBlock? _diagnostics;
        private readonly string _previewName="STUDIO "+Guid.NewGuid().ToString("N");

        internal static int Capture(string directory)
        {
            if(!GuiLauncher.EnsureSetup())return 1;
            Directory.CreateDirectory(directory);
            Dispatcher.UIThread.Invoke(()=>
            {
                var screen=new MapStudioScreen();screen.Load(MapTemplates.Create("Studio example",true));
                UiCapture.Capture(screen,Path.Combine(directory,"map-studio.png"),new Size(1440,900));
                var small=new MapStudioScreen();small.Load(MapTemplates.Create("Studio example",false));
                UiCapture.Capture(small,Path.Combine(directory,"map-studio-small.png"),new Size(960,600));
            });
            return 0;
        }

        public MapStudioScreen(PrimeOverlayHost? overlays = null, bool preview = false)
        {
            _overlays = overlays;
            Background=GuiTheme.InkBrush;Focusable=true;
            var toolbar=new WrapPanel { Orientation=Orientation.Horizontal };
            AddButton(toolbar,"Back",Close);AddButton(toolbar,"Library",ShowLibrary);AddButton(toolbar,"New",NewMap);
            AddButton(toolbar,"Open",()=>Browse("Open project",false,p=>Open(p),".json",".ppmap"));
            AddButton(toolbar,"Import Q3",Import);AddButton(toolbar,"Save",Save);AddButton(toolbar,"Save as",()=>Browse("Save project",true,p=>SaveTo(p),".json"));
            AddButton(toolbar,"Undo",()=>_document?.History.Undo());AddButton(toolbar,"Redo",()=>_document?.History.Redo());
            AddButton(toolbar,"Validate",()=>_=Validate());AddButton(toolbar,"Build",()=>_=Build(false));AddButton(toolbar,"Build .ppmap",()=>_=Build(true));
            AddButton(toolbar,"Play from here",()=>_=Play());AddButton(toolbar,"Run map test",()=>_=Audit());
            _editingControls.AddRange(toolbar.Children);
            AddButton(toolbar,"Cancel job",()=>_work?.Cancel());
            _root.Children.Add(toolbar);
            Grid.SetRow(_path,1);_root.Children.Add(_path);
            var body=new Grid { ColumnDefinitions=new("200,*,245"), Margin=new Thickness(0,8) };
            var tree=new DockPanel();DockPanel.SetDock(_search,Dock.Top);tree.Children.Add(_search);tree.Children.Add(_hierarchy);body.Children.Add(tree);
            var center=new Grid { RowDefinitions=new("Auto,*") };
            var tools=new WrapPanel();
            void Choice(string[] choices,Action<string> choose)
            {
                var box=new ComboBox {ItemsSource=choices,SelectedIndex=0,Margin=new Thickness(2),MinWidth=85};
                box.SelectionChanged+=(_,_)=>{if(box.SelectedItem is string text)choose(text);};tools.Children.Add(box);
            }
            Choice(new[]{"Move","Rotate","Scale"},name=>{if(_viewport!=null)_viewport.Tool=name;});
            Choice(new[]{"Perspective","Top","Front","Side"},name=>_viewport?.SetView(name));
            Choice(new[]{"Add object","Box","Wedge","Prism","Convex","Spawn","Pickup","Jump pad","Navigation link"},name=>{if(name!="Add object")AddObject(name);});
            Choice(new[]{"Overlays","Rendered","Wireframe","Collision","Kill plane","Navigation"},name=>
            {
                if(_viewport==null)return;
                if(name=="Navigation"){_=Navigation();return;}
                _viewport.Wireframe=name=="Wireframe";_viewport.Collision=name=="Collision";_viewport.KillPlane=name=="Kill plane";_viewport.InvalidateVisual();
            });
            Choice(new[]{"Inspector","Environment","Materials","Assets & music","Snapping","Statistics"},name=>{if(name=="Materials")MaterialInspector();else if(name=="Assets & music")AssetInspector();else if(name=="Snapping")SnapInspector();else if(name=="Statistics")Statistics();else Inspect();});
            AddButton(tools,"Frame all",()=>_viewport?.FrameAll());AddButton(tools,"Focus",()=>_viewport?.FrameSelection());
            AddButton(tools,"Duplicate",()=>EditSelection("Duplicate",MapObjects.Duplicate));AddButton(tools,"Delete",()=>EditSelection("Delete",MapObjects.Delete));
            AddButton(tools,"Capture preview",CapturePreview);
            center.Children.Add(tools);Grid.SetRow(_viewportHost,1);center.Children.Add(_viewportHost);Grid.SetColumn(center,1);body.Children.Add(center);
            var inspectorScroll=new ScrollViewer { Content=_inspector };Grid.SetColumn(inspectorScroll,2);body.Children.Add(inspectorScroll);
            Grid.SetRow(body,2);_root.Children.Add(body);
            _editingControls.Add(body);_editingControls.Add(_path);
            Grid.SetRow(_problems,3);_root.Children.Add(_problems);Grid.SetRow(_status,4);_root.Children.Add(_status);
            var layer=new Panel();layer.Children.Add(_root);layer.Children.Add(_modal);Content=layer;
            _search.TextChanged+=(_,_)=>RefreshHierarchy();
            _hierarchy.SelectionChanged+=(_,_)=>
            {
                if(_refreshing||_document==null)return;
                _document.Selection.Clear();foreach(var item in _hierarchy.SelectedItems?.OfType<MapObject>()??Enumerable.Empty<MapObject>())_document.Selection.Add(item.Id);
                _document.SelectionChanged();Inspect();_viewport?.InvalidateVisual();
            };
            _problems.SelectionChanged+=(_,_)=>
            {
                if(_document!=null&&_problems.SelectedItem is ProblemRow {Diagnostic.ObjectId:Guid id})
                {_document.Selection.Clear();_document.Selection.Add(id);_document.SelectionChanged();RefreshHierarchy();Inspect();_viewport?.FrameSelection();}
            };
            _idle.Interval=TimeSpan.FromMilliseconds(250);
            _idle.Tick+=async(_,_)=>
            {
                if (_diagnostics != null && _inspector.Children.Contains(_diagnostics)) RefreshStatistics();
                if(_work==null&&!_checking&&_document!=null&&_document.LastEditUtc>_checked&&DateTime.UtcNow-_document.LastEditUtc>TimeSpan.FromMilliseconds(500))
                {
                    var document=_document;var edited=document.LastEditUtc;var snapshot=document.Snapshot();_checking=true;
                    try{var result=await Task.Run(()=>MapValidator.Validate(snapshot.Definition,false));if(_document==document&&document.LastEditUtc==edited){_checked=edited;_problems.ItemsSource=result.Diagnostics.Select(d=>new ProblemRow(d)).ToArray();}}
                    catch(Exception ex){Failure(ex);}finally{_checking=false;}
                }
                if(_document==null||!_document.IsDirty||_document.LastEditUtc<=_autosaved||DateTime.UtcNow-_document.LastEditUtc<TimeSpan.FromSeconds(3))return;
                try{_document.Autosave(CustomRooms.MapDirectory);_autosaved=DateTime.UtcNow;}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){_status.Text="Autosave failed: "+ex.Message;}
            };
            AttachedToVisualTree+=(_,_)=>{LauncherBackdrop.Set(LauncherBackdropScene.MapEditor);_idle.Start();};
            DetachedFromVisualTree+=(_,_)=>{_idle.Stop();_work?.Cancel();};
            if (preview) Load(MapTemplates.Create("Studio example", true)); else ShowLibrary();
        }
        public void Dispose()
        {
            _idle.Stop(); _work?.Cancel();
            if (_document != null) _document.Changed -= Changed;
            foreach (var bitmap in _images) bitmap.Dispose();
            _images.Clear();
        }
        internal void ShowStatus(string message)=>_status.Text=message;
        private static TextBlock Text(string text)=>new(){Text=text,Foreground=GuiTheme.TextBrush,TextWrapping=TextWrapping.Wrap};
        private static void AddButton(Panel panel,string title,Action action)
        {var button=new PrimeButton(title.ToUpperInvariant(), action) {Margin=new Thickness(2),MinHeight=28,Height=28,MinWidth=60};panel.Children.Add(button);}
        private void Modal(Control control)
        {
            if (_overlays != null)
            {
                if (_sheet != null) _overlays.Close(_sheet);
                _sheet = control;
                _overlays.Show(control, PrimeModalSize.Large, Dismiss);
                return;
            }
            _modal.Child=new Border {Background=GuiTheme.PanelBrush,Padding=new Thickness(20),MaxWidth=800,MaxHeight=620,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Child=control};_modal.IsVisible=true;}
        private void Dismiss(){if (_sheet != null) { _overlays?.Close(_sheet); _sheet=null; } _modal.IsVisible=false;_modal.Child=null;}
        private void Confirm(string message,Action yes)
        {var view=new ConfirmScreen(message);view.Answered+=(_,answer)=>{Dismiss();if(answer)yes();};Modal(view);}
        private void WithUnsaved(Action action)
        {if(_document?.IsDirty==true)Confirm("Discard unsaved changes? A recovery copy will remain available.",()=>{_document.Autosave(CustomRooms.MapDirectory);action();});else action();}
        internal bool IsDirty => _document?.IsDirty == true;
        internal bool SaveRecovery()
        {
            try { if (_document?.IsDirty == true) _document.Autosave(CustomRooms.MapDirectory); return true; }
            catch (Exception ex) { Failure(ex); return false; }
        }
        private void Close()
        {
            if (_overlays != null) Closed?.Invoke(this, EventArgs.Empty);
            else WithUnsaved(() => Closed?.Invoke(this, EventArgs.Empty));
        }
        internal void Load(MapProject project,string? path=null)
        {
            _lastBuild = null;
            if(_document!=null)_document.Changed-=Changed;
            _document=new(project,path);_document.Changed+=Changed;_viewport=new(_document);_viewport.SelectionChanged+=()=>{RefreshHierarchy();Inspect();};
            _viewportHost.Children.Clear();_viewportHost.Children.Add(_viewport);_path.Text=path??Path.Combine(CustomRooms.MapDirectory,project.Definition.Name.ToLowerInvariant()+".json");
            Dismiss();Changed();_viewport.FrameAll();
            if(_document.HasRecovery(CustomRooms.MapDirectory))Recovery();
            if(project.Definition.Import!=null)_=Validate();
        }
        private void Recovery()
        {
            if(_document==null)return;var view=new StackPanel {Spacing=10};view.Children.Add(Text("A newer recovery file exists."));
            AddButton(view,"Restore",()=>{_document.Restore(CustomRooms.MapDirectory);Dismiss();});
            AddButton(view,"Discard",()=>{_document.DiscardRecovery(CustomRooms.MapDirectory);Dismiss();});
            AddButton(view,"Inspect",()=>{_status.Text=File.ReadAllText(_document.RecoveryPath(CustomRooms.MapDirectory));Dismiss();});Modal(view);
        }
        private void Open(string path)
        {try{WithUnsaved(()=>{try{Load(MapProjectSerializer.Load(path),path);}catch(Exception ex){Failure(ex);}});}catch(Exception ex){Failure(ex);}}
        private void Save(){if(_document!=null)SaveTo(_path.Text??"");}
        private void SaveTo(string path)
        {try{_document?.Save(path);_document?.DiscardRecovery(CustomRooms.MapDirectory);_path.Text=path;_status.Text="Saved "+path;}catch(Exception ex){Failure(ex);}}
        private void Changed(){RefreshHierarchy();Inspect();_status.Text=(_document?.IsDirty==true?"Unsaved changes · ":"")+"RMB orbit · MMB pan · WASD fly · F focus · drag selection or axis handles";}
        private void RefreshHierarchy()
        {
            if(_document==null)return;_refreshing=true;
            try{var objects=MapObjects.All(_document.Project.Definition).Where(o=>o.ToString().Contains(_search.Text??"",StringComparison.OrdinalIgnoreCase)).ToArray();_hierarchy.ItemsSource=objects;
                _hierarchy.SelectedItems?.Clear();foreach(var o in objects.Where(o=>_document.Selection.Contains(o.Id)))_hierarchy.SelectedItems?.Add(o);}
            finally{_refreshing=false;}
        }
        private void EditSelection(string label,Action<MapDefinition,ISet<Guid>> edit)
        {if(_document==null)return;var ids=_document.Selection.ToHashSet();_document.EditObjects(label,ids,d=>edit(d,ids));}
        private void NewMap()
        {
            var view=new StackPanel {Spacing=10};view.Children.Add(Text("NEW MAP"));var name=new TextBox {Text="My Arena"};view.Children.Add(name);
            var template=new ComboBox {ItemsSource=new[]{"Blank Arena","Simple Box Arena","Team Arena"},SelectedIndex=0};view.Children.Add(template);
            AddButton(view,"Create",()=>WithUnsaved(()=>{try{Load(MapTemplates.Create(name.Text??"",template.SelectedIndex!=0,template.SelectedIndex==2));}catch(Exception ex){Failure(ex);}}));AddButton(view,"Cancel",Dismiss);Modal(view);
        }
        private void ShowLibrary()
        {
            _inspector.Children.Clear();
            foreach(var bitmap in _images)bitmap.Dispose();_images.Clear();
            Inspect();
            var view=new Grid {RowDefinitions=new("Auto,*,Auto"),MinWidth=650,Height=460};view.Children.Add(Text("MAP LIBRARY"));
            var list=new ListBox();Grid.SetRow(list,1);view.Children.Add(list);
            list.ItemTemplate=new FuncDataTemplate<LibraryRow>((row,_)=>
            {
                var card=new StackPanel {Orientation=Orientation.Horizontal,Spacing=12,Margin=new Thickness(4)};
                if(row?.Entry.Definition is {} definition)
                {
                    try
                    {
                        var preview=definition.Assets.FirstOrDefault(a=>a.Kind=="preview");
                        using var stream=preview!=null?new MemoryStream(MapAssets.Read(definition,preview.Path)):File.Exists(ThumbnailGenerator.PathFor(definition.Name))?File.OpenRead(ThumbnailGenerator.PathFor(definition.Name)):(Stream?)null;
                        if(stream!=null){var bitmap=Bitmap.DecodeToWidth(stream,96);_images.Add(bitmap);card.Children.Add(new Image {Source=bitmap,Width=96,Height=54,Stretch=Stretch.UniformToFill});}
                    }
                    catch(Exception ex)when(ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException){ }
                }
                card.Children.Add(Text(row?.ToString()??""));return card;
            });
            try{list.ItemsSource=_catalog.Refresh(false).Select(e=>new LibraryRow(e)).ToArray();}catch(Exception ex){Failure(ex);}
            var buttons=new WrapPanel();Grid.SetRow(buttons,2);view.Children.Add(buttons);
            AddButton(buttons,"Open",()=>{if(list.SelectedItem is LibraryRow row)Open(row.Entry.Path);});
            AddButton(buttons,"Duplicate",()=>{if(list.SelectedItem is LibraryRow {Entry.Definition:not null} row){var p=MapProjectMigrator.Upgrade(new(row.Entry.Definition));p.Definition.MapId=Guid.NewGuid();p.Definition.Name=NextCopyName(p.Definition.Name);WithUnsaved(()=>Load(p));}});
            AddButton(buttons,"Delete",()=>{if(list.SelectedItem is LibraryRow row)Confirm("Delete "+Path.GetFileName(row.Entry.Path)+"?",()=>{try{File.Delete(row.Entry.Path);ShowLibrary();}catch(Exception ex){Failure(ex);}});});
            AddButton(buttons,"Reveal folder",()=>{try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CustomRooms.MapDirectory){UseShellExecute=true});}catch(Exception ex){Failure(ex);}});
            AddButton(buttons,"Refresh",ShowLibrary);AddButton(buttons,"New",NewMap);AddButton(buttons,"Close",Dismiss);Modal(view);
            AddButton(buttons,"Recover unsaved",RecoverUnsaved);
        }
        private void RecoverUnsaved()
        {
            var panel=new StackPanel {Spacing=8};panel.Children.Add(Text("RECOVERY FILES"));
            var list=new ListBox {MaxHeight=350};string directory=Path.Combine(CustomRooms.MapDirectory,".autosave");
            list.ItemsSource=Directory.Exists(directory)?Directory.EnumerateFiles(directory,"*.json").Where(p=>!p.EndsWith(".context.json",StringComparison.OrdinalIgnoreCase)).Select(p=>new RecoveryRow(p)).ToArray():Array.Empty<RecoveryRow>();panel.Children.Add(list);
            AddButton(panel,"Restore",()=>{if(list.SelectedItem is RecoveryRow row)WithUnsaved(()=>{try{Load(MapDocument.ReadRecovery(row.Path));}catch(Exception ex){Failure(ex);}});});
            AddButton(panel,"Discard",()=>{if(list.SelectedItem is RecoveryRow row)Confirm("Delete this recovery copy?",()=>{try{File.Delete(row.Path);if(File.Exists(row.Path+".context.json"))File.Delete(row.Path+".context.json");RecoverUnsaved();}catch(Exception ex){Failure(ex);}});});
            AddButton(panel,"Back",ShowLibrary);Modal(panel);
        }
        private string NextCopyName(string name)
        {
            string root=name.Trim();
            if(root.Length>34)root=root[..34].TrimEnd();
            var used=_catalog.Refresh(false).Where(e=>e.Definition!=null)
                .Select(e=>e.Definition!.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string candidate=root+" COPY";int suffix=2;
            while(used.Contains(candidate))
            {
                string tail=" COPY "+suffix++;
                string head=root.Length>40-tail.Length?root[..(40-tail.Length)].TrimEnd():root;
                candidate=head+tail;
            }
            return candidate;
        }
        private sealed record RecoveryRow(string Path)
        {public override string ToString(){try{return MapDocument.ReadRecovery(Path).Definition.Name+" · "+File.GetLastWriteTime(Path).ToString("g");}catch{return System.IO.Path.GetFileName(Path)+" · unreadable recovery";}}}
        private sealed record LibraryRow(MapCatalogEntry Entry)
        {
            public override string ToString()
            {
                if(Entry.Definition is not {} d)return Path.GetFileName(Entry.Path)+" · Invalid source";
                string status=Entry.Validation.IsValid?"Ready to validate":"Source problems";
                try{if(Entry.Validation.IsValid&&GameFiles.Ready)status=CustomRooms.NeedsGenerating(d)?"Needs build":"Built";}catch(IOException){status="Needs build";}
                return $"{d.InGameName??d.Name} · {d.Author??""} {d.Version??""}\n{(d.Import==null?"Native":"Q3")} · {status} · {Entry.Validation.Diagnostics.Count} diagnostics";
            }
        }
        private void Browse(string title,bool save,Action<string> selected,params string[] extensions)
        {
            var view=new Grid {RowDefinitions=new("Auto,Auto,*,Auto,Auto"),MinWidth=650,Height=480};view.Children.Add(Text(title));
            var location=new TextBox {Text=Directory.Exists(CustomRooms.MapDirectory)?CustomRooms.MapDirectory:AppContext.BaseDirectory};Grid.SetRow(location,1);view.Children.Add(location);
            var files=new ListBox();Grid.SetRow(files,2);view.Children.Add(files);var filename=new TextBox {Text=save?"map.json":""};Grid.SetRow(filename,3);view.Children.Add(filename);
            void Refresh()
            {try{files.ItemsSource=Directory.EnumerateFileSystemEntries(location.Text??"").Where(p=>Directory.Exists(p)||extensions.Contains(Path.GetExtension(p).ToLowerInvariant())).OrderBy(p=>!Directory.Exists(p)).ThenBy(p=>p).Select(p=>new BrowserRow(p)).ToArray();}catch(Exception ex){_status.Text=ex.Message;}}
            files.DoubleTapped+=(_,_)=>{if(files.SelectedItem is BrowserRow row){if(Directory.Exists(row.Path)){location.Text=row.Path;Refresh();}else filename.Text=Path.GetFileName(row.Path);}};
            files.SelectionChanged+=(_,_)=>{if(files.SelectedItem is BrowserRow row&&!Directory.Exists(row.Path))filename.Text=Path.GetFileName(row.Path);};
            var buttons=new WrapPanel();Grid.SetRow(buttons,4);view.Children.Add(buttons);
            AddButton(buttons,"Up",()=>{location.Text=Path.GetDirectoryName(location.Text)??location.Text;Refresh();});AddButton(buttons,"Go",Refresh);
            AddButton(buttons,save?"Save":"Open",()=>
            {
                string path=Path.Combine(location.Text??"",filename.Text??"");
                if(!extensions.Contains(Path.GetExtension(path).ToLowerInvariant())){_status.Text="Choose a supported file type: "+string.Join(", ",extensions);return;}
                if(save&&File.Exists(path))Confirm("Replace "+Path.GetFileName(path)+"?",()=>{Dismiss();selected(path);});else{Dismiss();selected(path);}
            });AddButton(buttons,"Cancel",Dismiss);Refresh();Modal(view);
        }
        private sealed record BrowserRow(string Path){public override string ToString()=>(Directory.Exists(Path)?"[folder] ":"")+System.IO.Path.GetFileName(Path);}
        private void AddObject(string kind)
        {
            _document?.EditObjects("Create "+kind,Array.Empty<Guid>(),d=>
            {
                switch(kind)
                {
                    case "Box":d.Geometry.Add(new MapBox {Label="Box",Transform=new(){Position=new[]{0f,1,0},Scale=new[]{4f,2,4}}});break;
                    case "Wedge":d.Geometry.Add(new MapWedge {Label="Ramp",Transform=new(){Position=new[]{0f,1,0},Scale=new[]{4f,2,6}}});break;
                    case "Prism":d.Geometry.Add(new MapPrism {Label="Prism",Transform=new(){Position=new[]{0f,1,0},Scale=new[]{3f,2,3}}});break;
                    case "Convex":d.Geometry.Add(new MapConvexBrush {Label="Convex brush",Vertices=new(){new[]{-1f,0,-1},new[]{1f,0,-1},new[]{0f,2,0},new[]{0f,0,1}},Faces=new(){new[]{0,1,2},new[]{0,1,3},new[]{0,2,3},new[]{1,2,3}}});break;
                    case "Spawn":d.Spawns.Add(new(){Id=Guid.NewGuid(),Position=new[]{0f,.1f,0}});break;
                    case "Pickup":d.Items.Add(new(){Id=Guid.NewGuid(),Type="HealthMedium",Position=new[]{0f,.1f,0}});break;
                    case "Jump pad":d.JumpPads.Add(new(){Id=Guid.NewGuid(),Position=new[]{0f,.1f,0},Target=new[]{8f,2,0}});break;
                    case "Navigation link":d.NavigationLinks.Add(new(){From=new[]{0f,.1f,0},To=new[]{6f,.1f,0}});break;
                }
            });
        }
        private void Inspect()
        {
            _inspector.Children.Clear();if(_document==null)return;
            var selected=MapObjects.All(_document.Project.Definition).FirstOrDefault(o=>_document.Selection.Contains(o.Id));
            if(selected==null){EnvironmentInspector();return;}
            _inspector.Children.Add(Text(selected.Kind));Guid id=selected.Id;
            var edits=new List<Action<object>>();
            void Field(string label,object? value,Action<object,string> apply)
            { _inspector.Children.Add(Text(label));var input=new TextBox {Text=Convert.ToString(value,CultureInfo.InvariantCulture)};_inspector.Children.Add(input);edits.Add(o=>apply(o,input.Text??"")); }
            void Vec(string label,float[] values,Action<object,float[]> apply)
            {Field(label,string.Join(", ",values.Select(v=>v.ToString(CultureInfo.InvariantCulture))),(o,text)=>apply(o,ParseVector(text,values.Length)));}
            void Material(int selectedIndex,Action<object,int> apply)
            {
                _inspector.Children.Add(Text("Material"));var choice=new ComboBox {ItemsSource=_document.Project.Definition.Materials.Select((m,i)=>$"{i} · {m.Name}").ToArray(),SelectedIndex=selectedIndex};_inspector.Children.Add(choice);edits.Add(o=>apply(o,choice.SelectedIndex));
            }
            if(selected.Value is not MapBrush)Vec("Position (X, Y, Z)",selected.Position,(o,v)=>{var current=MapObjects.All(_document.Project.Definition).First(x=>x.Id==id).Position;new MapObject(id,"","",o,_=>{}).Move(v.Zip(current,(a,b)=>a-b).ToArray());});
            if(selected.Value is MapEntityDefinition entity)Field("Label",entity.Label,(o,value)=>((MapEntityDefinition)o).Label=value);
            switch(selected.Value)
            {
                case MapGeometry g:
                    Field("Label",g.Label,(o,s)=>((MapGeometry)o).Label=s);
                    Field("Layer",g.Layer,(o,s)=>((MapGeometry)o).Layer=s);
                    Vec("Size",g.Transform.Scale,(o,v)=>((MapGeometry)o).Transform.Scale=v);
                    var rotation=new OpenTK.Mathematics.Quaternion(g.Transform.Rotation[0],g.Transform.Rotation[1],g.Transform.Rotation[2],g.Transform.Rotation[3]).ToEulerAngles()* (180/MathF.PI);
                    Vec("Rotation X, Y, Z (degrees)",new[]{rotation.X,rotation.Y,rotation.Z},(o,v)=>{var q=OpenTK.Mathematics.Quaternion.FromEulerAngles(new OpenTK.Mathematics.Vector3(v[0],v[1],v[2])*(MathF.PI/180));((MapGeometry)o).Transform.Rotation=new[]{q.X,q.Y,q.Z,q.W};});
                    Material(g.Material,(o,index)=>((MapGeometry)o).Material=index);
                    Field("Shade",g.Shade,(o,s)=>((MapGeometry)o).Shade=Number(s));
                    Field("Terrain",g.Terrain,(o,s)=>((MapGeometry)o).Terrain=s);
                    Vec("UV scale",g.Uv.Scale,(o,v)=>((MapGeometry)o).Uv.Scale=v);Vec("UV offset",g.Uv.Offset,(o,v)=>((MapGeometry)o).Uv.Offset=v);
                    Field("UV rotation",g.Uv.Rotation,(o,s)=>((MapGeometry)o).Uv.Rotation=Number(s));
                    foreach(var pair in new[]{("Collision",g.Solid),("Damaging",g.Damaging),("Hidden",g.Hidden),("Locked",g.Locked)})
                    {var check=new CheckBox {Content=pair.Item1,IsChecked=pair.Item2};_inspector.Children.Add(check);edits.Add(o=>{var geometry=(MapGeometry)o;switch(pair.Item1){case "Collision":geometry.Solid=check.IsChecked==true;break;case "Damaging":geometry.Damaging=check.IsChecked==true;break;case "Hidden":geometry.Hidden=check.IsChecked==true;break;case "Locked":geometry.Locked=check.IsChecked==true;break;}});}
                    if(g is MapPrism prism)Field("Sides",prism.Sides,(o,s)=>((MapPrism)o).Sides=int.Parse(s,CultureInfo.InvariantCulture));
                    break;
                case MapSpawn s:Field("Yaw",s.Yaw,(o,v)=>((MapSpawn)o).Yaw=Number(v));Field("Team (-1 = neutral)",s.Team,(o,v)=>((MapSpawn)o).Team=int.Parse(v,CultureInfo.InvariantCulture));break;
                case MapNavigationLink link:
                    Vec("Destination",link.To,(o,v)=>((MapNavigationLink)o).To=v);
                    Field("Traversal type",link.Kind,(o,v)=>((MapNavigationLink)o).Kind=Enum.Parse<MapNavigationLinkKind>(v,true));
                    var both=new CheckBox {Content="Bidirectional",IsChecked=link.Bidirectional};_inspector.Children.Add(both);edits.Add(o=>((MapNavigationLink)o).Bidirectional=both.IsChecked==true);break;
                case MapItem i:
                    var itemType=new ComboBox {ItemsSource=MapBuilder.MultiplayerItems.Select(t=>t.ToString()).Order().ToArray(),SelectedItem=i.Type};_inspector.Children.Add(itemType);edits.Add(o=>((MapItem)o).Type=itemType.SelectedItem as string??i.Type);
                    Field("Respawn frames",i.SpawnInterval,(o,v)=>((MapItem)o).SpawnInterval=ushort.Parse(v,CultureInfo.InvariantCulture));
                    var hasBase=new CheckBox {Content="Has base",IsChecked=i.HasBase};_inspector.Children.Add(hasBase);edits.Add(o=>((MapItem)o).HasBase=hasBase.IsChecked==true);break;
                case MapJumpPad p:
                    Vec("Trigger size",p.Size,(o,v)=>((MapJumpPad)o).Size=v);
                    var launchMode=new ComboBox {ItemsSource=new[]{"Target","Vector and speed"},SelectedIndex=p.Vector==null?0:1};_inspector.Children.Add(launchMode);
                    Vec("Target",p.Target??new[]{0f,4,0},(o,v)=>((MapJumpPad)o).Target=launchMode.SelectedIndex==0?v:null);
                    Vec("Direction",p.Vector??new[]{0f,1,0},(o,v)=>((MapJumpPad)o).Vector=launchMode.SelectedIndex==1?v:null);
                    Field("Speed",p.Speed,(o,v)=>((MapJumpPad)o).Speed=Number(v));
                    Field("Control lock",p.ControlLockTime,(o,v)=>((MapJumpPad)o).ControlLockTime=ushort.Parse(v,CultureInfo.InvariantCulture));
                    Field("Cooldown",p.CooldownTime,(o,v)=>((MapJumpPad)o).CooldownTime=ushort.Parse(v,CultureInfo.InvariantCulture));break;
                case MapBrush b:Vec("Minimum",b.Min,(o,v)=>((MapBrush)o).Min=v);Vec("Maximum",b.Max,(o,v)=>((MapBrush)o).Max=v);Material(b.Material,(o,index)=>((MapBrush)o).Material=index);break;
            }
            AddButton(_inspector,"Apply",()=>{try{_document.EditObjects("Edit properties",new[]{id},d=>{var target=MapObjects.All(d).First(o=>o.Id==id).Value;foreach(var edit in edits)edit(target);});}catch(Exception ex){Failure(ex);}});
        }
        private void Statistics()
        {
            _inspector.Children.Clear();
            _diagnostics = Text(""); _diagnostics.TextWrapping = TextWrapping.Wrap;
            _inspector.Children.Add(_diagnostics); RefreshStatistics();
        }
        private void RefreshStatistics()
        {
            if (_diagnostics == null || _document == null || _viewport == null) return;
            var cache = _viewport.Cache; var jobs = MapBuildScheduler.Shared;
            _diagnostics.Text = $"Viewport rebuilds\nGeometry: {cache.GeometryRebuildCount} ({cache.GeometryObjectsRebuilt} objects)\n"
                + $"Imported: {cache.ImportedRebuildCount}\nSelection: {cache.SelectionRebuildCount}\nEntities: {cache.EntityRebuildCount}\n"
                + $"Collision: {cache.CollisionRebuildCount}\nNavigation invalidations: {cache.NavigationInvalidationCount}\n\n"
                + $"History: {_document.History.CommandCount} commands / {_document.History.ApproximateBytes / 1024d:0.0} KiB\n\n"
                + $"Build queue: {jobs.PendingCount}\nShared requests: {jobs.SharedRequests}\nCompilations: {jobs.CompilationCount}\n"
                + $"Compiler cache: {jobs.CompiledCacheCount} entries / {jobs.CompiledCacheBytes / 1048576d:0.0} MiB\n\n"
                + (_lastBuild == null ? "Build this map to measure its runtime cache."
                    : $"Last runtime build: {_lastBuild.Milliseconds:0.0} ms / {(_lastBuild.CacheHit ? "cache hit" : "cache miss")}\n{_lastBuild.Fingerprint}");
        }
        private static float Number(string value){float number=float.Parse(value,CultureInfo.InvariantCulture);if(!float.IsFinite(number))throw new FormatException("Enter a finite number.");return number;}
        private static float[] ParseVector(string value,int count)
        {var result=value.Split(',',StringSplitOptions.TrimEntries).Select(Number).ToArray();if(result.Length!=count)throw new FormatException($"Enter {count} comma-separated numbers.");return result;}
        private void EnvironmentInspector()
        {
            _inspector.Children.Clear();if(_document==null)return;var d=_document.Project.Definition;_inspector.Children.Add(Text("PROJECT & ENVIRONMENT"));var edits=new List<Action<MapDefinition>>();
            void Field(string label,string value,Action<MapDefinition,string> apply){_inspector.Children.Add(Text(label));var input=new TextBox{Text=value};_inspector.Children.Add(input);edits.Add(map=>apply(map,input.Text??""));}
            Field("Runtime name",d.Name,(m,s)=>{MapValidator.RequireRuntimeName(s);m.Name=s;});Field("Display name",d.InGameName??d.Name,(m,s)=>m.InGameName=s);Field("Author",d.Author??"",(m,s)=>m.Author=s);Field("Version",d.Version??"",(m,s)=>m.Version=s);
            Field("Kill height",d.KillHeight.ToString(CultureInfo.InvariantCulture),(m,s)=>m.KillHeight=Number(s));Field("Far clip",d.FarClip.ToString(CultureInfo.InvariantCulture),(m,s)=>m.FarClip=Number(s));
            Field("Supported modes (comma separated)",string.Join(",",d.Capabilities?.SupportedModes??new(){"Battle","Survival"}),(m,s)=>{m.Capabilities??=new();m.Capabilities.SupportedModes=s.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).ToList();});
            Field("Light 1 color (0–31)",string.Join(",",d.Light1Color),(m,s)=>m.Light1Color=ParseVector(s,3).Select(v=>(int)v).ToArray());
            Field("Light 1 direction",string.Join(",",d.Light1Vector),(m,s)=>m.Light1Vector=ParseVector(s,3));
            Field("Light 2 color (0–31)",string.Join(",",d.Light2Color),(m,s)=>m.Light2Color=ParseVector(s,3).Select(v=>(int)v).ToArray());
            Field("Fog color (0–31)",string.Join(",",d.FogColor),(m,s)=>m.FogColor=ParseVector(s,3).Select(v=>(int)v).ToArray());
            if(d.Import is {} import)
            {
                _inspector.Children.Add(Text("IMPORTED ARCHITECTURE (read-only)"));
                Field("Q3 units per world unit",import.UnitsPerUnit.ToString(CultureInfo.InvariantCulture),(m,s)=>m.Import!.UnitsPerUnit=Number(s));
                Field("Patch detail (1–8)",import.PatchLevel.ToString(),(m,s)=>m.Import!.PatchLevel=int.Parse(s,CultureInfo.InvariantCulture));
                var spawns=new CheckBox {Content="Use imported spawns",IsChecked=import.KeepSpawns};_inspector.Children.Add(spawns);edits.Add(m=>m.Import!.KeepSpawns=spawns.IsChecked==true);
            }
            var fog=new CheckBox {Content="Fog enabled",IsChecked=d.FogEnabled};_inspector.Children.Add(fog);edits.Add(m=>m.FogEnabled=fog.IsChecked==true);
            AddButton(_inspector,"Apply",()=>{try{_document.Edit("Environment",map=>{foreach(var edit in edits)edit(map);},MapChangeDomain.Environment | MapChangeDomain.Metadata | (d.Import != null ? MapChangeDomain.Import : MapChangeDomain.None));}catch(Exception ex){Failure(ex);}});
            AddButton(_inspector,"Upgrade project",()=>_document.Upgrade());
            AddButton(_inspector,"Use camera as preview",()=>{if(_viewport!=null){var p=_viewport.CameraPosition;var t=_viewport.CameraTarget;_document.Edit("Preview camera",m=>m.Preview=new(){Position=new[]{p.X,p.Y,p.Z},Target=new[]{t.X,t.Y,t.Z}});}});
        }
        private void MaterialInspector()
        {
            _inspector.Children.Clear();if(_document==null)return;_inspector.Children.Add(Text("MATERIALS"));
            for(int i=0;i<_document.Project.Definition.Materials.Count;i++)
            {
                int index=i;var m=_document.Project.Definition.Materials[i];_inspector.Children.Add(Text($"{i} · {m.Name}"));
                try{if(m.Texture!=null||GameFiles.Ready){var preview=MapMaterialPreview.Create(_document.Project.Definition,m);_images.Add(preview.Bitmap);_inspector.Children.Add(new Image {Source=preview.Bitmap,Width=64,Height=64,HorizontalAlignment=HorizontalAlignment.Left});_inspector.Children.Add(Text(preview.Details));}}
                catch(Exception ex)when(ex is IOException or InvalidDataException or ProgramException or ArgumentException or InvalidOperationException){_inspector.Children.Add(Text("Preview unavailable: "+ex.Message));}
                var source=new TextBox{Text=m.SourceMaterial.ToString()};var scale=new TextBox{Text=m.TexScale.ToString(CultureInfo.InvariantCulture)};_inspector.Children.Add(Text("Source material / texels per unit"));_inspector.Children.Add(source);_inspector.Children.Add(scale);
                AddButton(_inspector,"Apply material",()=>{try{_document.EditMaterial(index,m=>{m.SourceMaterial=int.Parse(source.Text??"",CultureInfo.InvariantCulture);m.TexScale=Number(scale.Text??"");});}catch(Exception ex){Failure(ex);}});
                if(m.Texture==null&&GameFiles.Ready)
                {
                    try
                    {
                        var materials=Read.GetRoomModelInstance(_document.Project.Definition.TextureSource).Model.Materials;
                        var choices=new ComboBox {ItemsSource=materials.Select((material,n)=>$"{n} · {material.Name}").ToArray(),SelectedIndex=m.SourceMaterial};_inspector.Children.Add(choices);
                        choices.SelectionChanged+=(_,_)=>{if(choices.SelectedIndex>=0)source.Text=choices.SelectedIndex.ToString(CultureInfo.InvariantCulture);};
                    }
                    catch(Exception ex){_inspector.Children.Add(Text("Source materials unavailable: "+ex.Message));}
                }
            }
            AddButton(_inspector,"Add material",()=>_document.Edit("Add material",d=>d.Materials.Add(new(){Id=Guid.NewGuid(),Name="Material "+d.Materials.Count})));
        }
        private sealed record ProblemRow(MapDiagnostic Diagnostic){public override string ToString()=>$"{Diagnostic.Severity} · {Diagnostic.Code} · {Diagnostic.Message}";}
        private string StoreAsset(string kind,string extension,byte[] bytes)
        {
            if(_document==null)throw new InvalidOperationException("Open a project first.");
            if(bytes.Length>32*1024*1024)throw new IOException("Assets must be no larger than 32 MiB.");
            string root=_document.Project.Definition.BaseDirectory??CustomRooms.MapDirectory;
            string relative=kind+"/"+Guid.NewGuid().ToString("N")+extension;
            AtomicFile.Write(Path.Combine(root,relative),bytes);
            _document.Edit("Add "+kind,d=>{d.BaseDirectory=root;d.Assets.Add(new(){Path=relative,Kind=kind=="audio"?"audio":kind=="preview"?"preview":"texture"});});
            return relative;
        }
        private void AssetInspector()
        {
            _inspector.Children.Clear();if(_document==null)return;_inspector.Children.Add(Text("ASSETS & MUSIC"));
            foreach(var asset in _document.Project.Definition.Assets)_inspector.Children.Add(Text(asset.Kind+" · "+Path.GetFileName(asset.Path)));
            AddButton(_inspector,"Import texture",()=>Browse("Choose a texture image",false,path=>_=Job("Baking texture",async token=>
            {
                try
                {
                    if(new FileInfo(path).Length>16*1024*1024)throw new IOException("Texture image exceeds 16 MiB.");
                    byte[] baked=await Task.Run(()=>MapTextureBake.BakeImage(File.ReadAllBytes(path)));
                    token.ThrowIfCancellationRequested();
                    string asset=StoreAsset("textures",".tex",baked);
                    _document.Edit("Add custom material",d=>d.Materials.Add(new(){Id=Guid.NewGuid(),Name=Path.GetFileNameWithoutExtension(path),Texture=asset,TexScale=16}));
                    MaterialInspector();
                }
                catch(Exception ex){Failure(ex);}
            }),".png",".jpg",".jpeg"));
            AddButton(_inspector,"Choose custom music",()=>Browse("Choose map music",false,path=>
            {
                try
                {
                    if(new FileInfo(path).Length>32*1024*1024)throw new IOException("Music exceeds 32 MiB.");
                    string asset=StoreAsset("audio",Path.GetExtension(path).ToLowerInvariant(),File.ReadAllBytes(path));
                    _document.Edit("Map music",d=>d.Audio=new(){Music=asset});AssetInspector();
                }
                catch(Exception ex){Failure(ex);}
            },".wav",".ogg",".mp3"));
            var gameMusic=new ComboBox {ItemsSource=Enum.GetNames<MusicId>(),SelectedItem=_document.Project.Definition.Audio?.GameMusic};_inspector.Children.Add(Text("Existing game music"));_inspector.Children.Add(gameMusic);
            AddButton(_inspector,"Use game music",()=>{if(gameMusic.SelectedItem is string music)_document.Edit("Game music",d=>d.Audio=new(){GameMusic=music});});
            var volume=new TextBox {Text=(_document.Project.Definition.Audio?.Volume??.8f).ToString(CultureInfo.InvariantCulture)};
            var loop=new CheckBox {Content="Loop music",IsChecked=_document.Project.Definition.Audio?.Loop??true};_inspector.Children.Add(Text("Music volume (0–1)"));_inspector.Children.Add(volume);_inspector.Children.Add(loop);
            AddButton(_inspector,"Apply audio",()=>{try{_document.Edit("Audio settings",d=>{d.Audio??=new();d.Audio.Volume=Number(volume.Text??"");d.Audio.Loop=loop.IsChecked==true;});}catch(Exception ex){Failure(ex);}});
            AddButton(_inspector,"Use default audio",()=>_document.Edit("Default audio",d=>d.Audio=null));
        }
        private void CapturePreview()
        {
            if(_viewport==null||_document==null||_viewport.Bounds.Width<1||_viewport.Bounds.Height<1)return;
            try
            {
                using var bitmap=new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)_viewport.Bounds.Width,(int)_viewport.Bounds.Height),new Avalonia.Vector(96,96));
                bitmap.Render(_viewport);using var stream=new MemoryStream();bitmap.Save(stream);
                byte[] preview = stream.ToArray();
#if MPHREAD_SHELL
                preview = _viewport.CaptureGpuPreview(preview);
#endif
                _document.Edit("Replace preview",d=>d.Assets.RemoveAll(a=>a.Kind=="preview"));
                StoreAsset("preview",".png",preview);_status.Text="Preview captured.";
            }
            catch(Exception ex){Failure(ex);}
        }
        private void Problems(MapValidationResult result)
        {_problems.ItemsSource=result.Diagnostics.Select(d=>new ProblemRow(d)).ToArray();_status.Text=(result.IsValid?"Validation passed. ":"Build blocked. ")+string.Join(" · ",result.Budgets.Select(b=>$"{b.Name}: {b.Used:N0}"+(b.Limit!=null?$" / {b.Limit:N0}":"")));}
        private async Task Work(string label,Func<MapProject,CancellationToken,Task> action)
        {
            if(_document==null||_work!=null)return;var snapshot=_document.Snapshot();await Job(label,token=>action(snapshot,token));
        }
        private async Task Job(string label,Func<CancellationToken,Task> action)
        {
            if(_work!=null)return;_work=new();_status.Text=label+"…";
            SetBusy(true);
            try{await action(_work.Token);}catch(OperationCanceledException){_status.Text="Cancelled.";}catch(Exception ex){Failure(ex);}finally{_work.Dispose();_work=null;SetBusy(false);}
        }
        private void SetBusy(bool busy){foreach(var control in _editingControls)control.IsEnabled=!busy;}
        private void SnapInspector()
        {
            _inspector.Children.Clear();if(_viewport==null)return;
            var grid=new TextBox {Text=_viewport.Snap.ToString(CultureInfo.InvariantCulture)};
            var angle=new TextBox {Text=_viewport.AngleSnap.ToString(CultureInfo.InvariantCulture)};
            var scale=new TextBox {Text=_viewport.ScaleSnap.ToString(CultureInfo.InvariantCulture)};
            var local=new CheckBox {Content="Local transform axes",IsChecked=_viewport.LocalAxes};
            _inspector.Children.Add(Text("Grid spacing (0 disables snapping)"));_inspector.Children.Add(grid);
            _inspector.Children.Add(Text("Rotation step (degrees)"));_inspector.Children.Add(angle);
            _inspector.Children.Add(Text("Scale step"));_inspector.Children.Add(scale);_inspector.Children.Add(local);
            AddButton(_inspector,"Apply",()=>{try{float g=Number(grid.Text??""),a=Number(angle.Text??""),s=Number(scale.Text??"");if(g<0||g>100||a<1||a>180||s<=0||s>10)throw new FormatException("Use grid spacing 0–100, rotation step 1–180 and scale step above 0 through 10.");_viewport.Snap=g;_viewport.AngleSnap=a;_viewport.ScaleSnap=s;_viewport.LocalAxes=local.IsChecked==true;}catch(Exception ex){Failure(ex);}});
        }
        private Task Validate()=>Work("Validating",async(p,token)=>
        {
            var result=await MapBuildScheduler.Shared.AnalyzeAsync(MapBuildSnapshot.Capture(p),cancellation:token);
            token.ThrowIfCancellationRequested();Problems(result.Validation());
            if(result.Succeeded&&p.Definition.Import!=null)_viewport?.SetImported(result);
        });
        private Task Navigation()=>Work("Generating navigation",async(p,token)=>
        {
            var result=await MapBuildScheduler.Shared.AnalyzeAsync(MapBuildSnapshot.Capture(p),navigation:true,cancellation:token);
            token.ThrowIfCancellationRequested();Problems(result.Validation());if(!result.Succeeded)return;
            var graph=result.CreateNavigation();if(graph==null)return;
            if(_viewport!=null){_viewport.Navigation=graph;_viewport.InvalidateVisual();}
            int components=graph.Components.Distinct().Count();_status.Text=$"{graph.Positions.Length} navigation nodes · {graph.Edges} edges · {components} connected regions";
        });
        private Task Build(bool package)
        {
            if(package)CapturePreview();
            return Work(package?"Building package":"Building map",async(p,token)=>
        {
            if(package)
            {
                string output=Path.ChangeExtension(_path.Text??Path.Combine(CustomRooms.MapDirectory,p.Definition.Name),".ppmap");
                string path=await MapBuildScheduler.Shared.PackageAsync(MapBuildSnapshot.Capture(p),output,token);
                token.ThrowIfCancellationRequested();_status.Text="Package built: "+path;
            }
            else
            {
                if(!GameFiles.Ready)throw new IOException("Set up game files in Settings before building runtime files.");GameFiles.ApplyPaths();
                var built = await MapBuildScheduler.Shared.BuildAsync(MapBuildSnapshot.Capture(p), token);
                _lastBuild = built;
                token.ThrowIfCancellationRequested(); Problems(built.Validation()); if (!built.Succeeded) return;
                await Task.Run(()=>{token.ThrowIfCancellationRequested();MapBuildScheduler.Install(built,p.Definition,CustomRooms.ArchiveDirectory(p.Definition),CustomRooms.EntityDirectory(),CustomRooms.NodeDirectory());},token);
                Metadata.RegisterDownloadedMap(p.Definition);_status.Text=$"Runtime map ready · {(built.CacheHit ? "cache hit" : "compiled")} · {built.Milliseconds:0} ms";
            }
        });
        }
        private Task Play()=>Work("Preparing playtest",async(p,token)=>
        {
            if(!GameFiles.Ready)throw new IOException("Set up game files in Settings before playtesting.");GameFiles.ApplyPaths();
            p.Definition.Name=_previewName;p.Definition.SourcePath=null;
            p.Definition.Capabilities=null;
            if(p.Definition.Import!=null)p.Definition.Import.KeepSpawns=false;
            if(_viewport!=null){var pos=_viewport.CameraPosition;p.Definition.Spawns.Clear();p.Definition.Spawns.Add(new(){Position=new[]{pos.X,pos.Y,pos.Z}});}
            var result=await MapBuildScheduler.Shared.BuildAsync(MapBuildSnapshot.Capture(p),token);
            token.ThrowIfCancellationRequested();Problems(result.Validation());if(!result.Succeeded)return;
            await Task.Run(()=>{token.ThrowIfCancellationRequested();MapBuildScheduler.Install(result,p.Definition,CustomRooms.ArchiveDirectory(p.Definition),CustomRooms.EntityDirectory(),CustomRooms.NodeDirectory());},token);
            PlayRequested?.Invoke(this,p.Definition);
        });
        private Task Audit()=>Work("Running map audit",async(p,token)=>
        {
            if(!GameFiles.Ready)throw new IOException("Set up game files before running a map audit.");
            var result=await MapAuditRunner.Run(p,token);_status.Text=result.Passed?"Map audit passed.":"Map audit failed.";
            _problems.ItemsSource=result.Lines;
        });
        private void Import()=>Browse("Choose a Quake 3 source",false,source=>
        {
            var view=new StackPanel {Spacing=8};view.Children.Add(Text("IMPORT QUAKE 3"));var maps=new ComboBox();
            try{maps.ItemsSource=Q3Bsp.ListMaps(source);maps.SelectedIndex=0;}catch(Exception ex){Failure(ex);return;}view.Children.Add(maps);
            var name=new TextBox {Text=Path.GetFileNameWithoutExtension(source)};view.Children.Add(Text("Runtime name"));view.Children.Add(name);
            var scale=new TextBox {PlaceholderText="Scale (blank = automatic)"};view.Children.Add(scale);var clip=new CheckBox {Content="Keep player clips",IsChecked=true};view.Children.Add(clip);
            AddButton(view,"Import",()=>
            {
                string room=name.Text??"";string? map=maps.SelectedItem as string;string size=scale.Text??"";bool keep=clip.IsChecked==true;
                WithUnsaved(()=>_=Job("Importing Quake 3 map",async token=>
                {
                    try
                    {
                        MapValidator.RequireRuntimeName(room);string directory=Path.Combine(CustomRooms.MapDirectory,room.ToLowerInvariant());
                        if(Directory.Exists(directory))throw new IOException("A map folder already has this name. Choose a new name.");
                        Dismiss();_status.Text="Importing Quake 3 map…";
                        int status=await Task.Run(()=>Q3Convert.Run(source,map,room,directory,!keep,false,string.IsNullOrWhiteSpace(size)?null:Number(size),64));
                        token.ThrowIfCancellationRequested();
                        if(status!=0)throw new IOException("Import failed; inspect the build log.");
                        string path=Directory.EnumerateFiles(directory,"*.json").Single();Load(MapProjectMigrator.Upgrade(MapProjectSerializer.Load(path)),path);
                    }
                    catch(Exception ex){Failure(ex);}
                }));
            });AddButton(view,"Cancel",Dismiss);Modal(view);
        },".pk3",".bsp");
        private void Failure(Exception ex)
        {_status.Text=ex.Message;if(ex is not(IOException or InvalidDataException or ProgramException or FormatException or ArgumentException))DebugLog.Exception("mapeditor",ex);}
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if(_work!=null){if(e.Key==Key.Escape)_work.Cancel();e.Handled=true;return;}
            if(e.Key==Key.Escape){if(_modal.IsVisible)Dismiss();else Close();e.Handled=true;}
            else if(e.KeyModifiers.HasFlag(KeyModifiers.Control)&&e.Key==Key.S){Save();e.Handled=true;}
            else base.OnKeyDown(e);
        }
    }
}
