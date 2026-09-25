using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MphRead.Mods.MapEditor;
using MphRead.Mods.MapGen;
using Vector = System.Numerics.Vector3;

namespace MphRead.Mods.Launcher.Gui
{
    // Game-rendered geometry with Avalonia authoring overlays and a CPU fallback.
    // It never packs collision or regenerates navigation while drawing.
    internal sealed partial class MapViewport : Control, IMapViewport
    {
        public MapDocument Document { get; }
        public Vector CameraPosition { get; private set; } = new(28,24,32);
        public Vector CameraTarget { get; private set; } = Vector.Zero;
        public string View { get; set; } = "Perspective";
        public string Tool { get; set; } = "Move";
        public float Snap { get; set; } = .25f;
        public float AngleSnap { get; set; } = 15;
        public float ScaleSnap {get;set;}=.25f;
        public bool LocalAxes {get;set;}
        public bool Wireframe { get; set; }
        public bool Collision { get; set; }
        public bool KillPlane { get; set; }
        public int[] NavigationPath { get; set; } = Array.Empty<int>();
        public MapNodePacker.NavigationGraph? Navigation { get; set; }
        public event Action? SelectionChanged;
        internal MapViewportCache Cache { get; } = new();
        private IEnumerable<MapViewportFace> Faces => Cache.NativeFaces.Concat(Collision ? Cache.ImportedCollisionFaces : Cache.ImportedFaces);
        private MapValidationResult? _overlayDiagnostics;
        private HashSet<Guid> _warningObjects = new();
        private readonly Dictionary<(Guid Id, string Text), FormattedText> _labels = new();
        private readonly List<(Guid Id, Point[] Points, double Depth)> _pick = new();
        private Point _last, _start;
        private bool _orbit, _pan, _drag, _boxSelect, _boxAdditive;
        private Point _boxCurrent;
        private int _axis = -1;
        public string Axes { get; set; } = "Free";
        public string PivotMode { get; set; } = "Individual";
        public Vector CursorPivot { get; set; }
        private Vector TurnAxis => _axis == 0 || Axes == "X" ? Vector.UnitX : _axis == 2 || Axes == "Z" ? Vector.UnitZ : Vector.UnitY;
        private Vector ScaleAxes => _axis >= 0 ? (_axis == 0 ? Vector.UnitX : _axis == 1 ? Vector.UnitY : Vector.UnitZ)
            : Axes == "Free" ? Vector.One : new(Axes.Contains('X') ? 1 : 0, Axes.Contains('Y') ? 1 : 0, Axes.Contains('Z') ? 1 : 0);
        private Vector? TransformPivot
        {
            get
            {
                if (PivotMode == "Individual") return null;
                if (PivotMode == "World") return Vector.Zero;
                if (PivotMode == "Cursor") return CursorPivot;
                var objects = MapObjects.All(Document.Project.Definition).Where(o => Document.Selection.Contains(o.Id)).ToArray();
                if (objects.Length == 0) return null;
                if (PivotMode == "Active") return MapViewportScene.Vector((objects.FirstOrDefault(o => o.Id == Document.ActiveObjectId) ?? objects[0]).Position);
                return objects.Select(o => MapViewportScene.Vector(o.Position)).Aggregate(Vector.Zero, (a,b) => a+b) / objects.Length;
            }
        }
        private Vector _preview;
        private float _rotation, _scale = 1;

        public MapViewport(MapDocument document)
        {
            Document=document; Focusable=true; ClipToBounds=true;
            Document.Invalidated += InvalidateDocument;
            InvalidateDocument(new(MapChangeDomain.All));
        }
        private void InvalidateDocument(MapDocumentChange change)
        {
            Cache.Invalidate(Document.Project.Definition, change);
            if ((change.Domains & (MapChangeDomain.Geometry | MapChangeDomain.Entity)) != 0) _labels.Clear();
            if ((change.Domains & (MapChangeDomain.Navigation | MapChangeDomain.Import)) != 0) Navigation = null;
            InvalidateVisual();
        }
        public void SetImported(BuiltMap map)
        {
            Cache.SetImported(map); InvalidateVisual();
        }

        public void SetImported(MapAnalysisResult analysis)
        {
            Cache.SetImported(analysis); InvalidateVisual();
        }
        public void FrameAll()
        {
            var points=Faces.SelectMany(f=>f.Points).ToArray();
            if(points.Length==0) return;
            Vector min=points.Aggregate(new Vector(float.MaxValue),Vector.Min), max=points.Aggregate(new Vector(float.MinValue),Vector.Max);
            CameraTarget=(min+max)/2; CameraPosition=CameraTarget+Vector.Normalize(new Vector(1,.8f,1))*Math.Max(8,(max-min).Length()); InvalidateVisual();
        }
        public void FrameSelection()
        {
            var selected=MapObjects.All(Document.Project.Definition).Where(o=>Document.Selection.Contains(o.Id)).ToArray();
            if(selected.Length==0){FrameAll();return;}
            Vector target=selected.Select(o=>MapViewportScene.Vector(o.Position)).Aggregate(Vector.Zero,(a,b)=>a+b)/selected.Length;
            CameraPosition+=target-CameraTarget; CameraTarget=target; InvalidateVisual();
        }
        private MapViewportCamera Camera => new(CameraPosition, CameraTarget, View == "Perspective");
        private MapViewportLayout Layout => new(Bounds.Width, Bounds.Height, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        private (Vector Right,Vector Up,Vector Forward) Basis() => Camera.Basis();
        private (Point Point,double Depth)? Project(Vector point)
        {
            var projected = Camera.Project(Layout, point);
            return projected is { } p ? (new Point(p.X, p.Y), p.Depth) : null;
        }
        private MapObject? ActiveSelection => MapObjects.Find(Document.Project.Definition, Document.ActiveObjectId)
            ?? MapObjects.All(Document.Project.Definition).FirstOrDefault(o => Document.Selection.Contains(o.Id));
        private Matrix4x4 PreviewTransform(MapObject item) => MapTransformPreview.Matrix(item, Tool, _preview,
            _rotation, _scale, LocalAxes, TurnAxis, ScaleAxes, TransformPivot);
        private Vector GizmoAxis(MapObject item, int axis)
        {
            var direction = axis == 0 ? Vector.UnitX : axis == 1 ? Vector.UnitY : Vector.UnitZ;
            if (LocalAxes && item.Value is MapGeometry geometry)
            { var r = geometry.Transform.Rotation; direction = Vector.Transform(direction, new Quaternion(r[0],r[1],r[2],r[3])); }
            return direction;
        }
        internal MapRenderFrame BuildRenderFrame(MapViewportLayout layout)
        {
            var transforms = new Dictionary<Guid, Matrix4x4>();
            if (_drag)
                foreach (var item in MapObjects.All(Document.Project.Definition))
                    if (Document.Selection.Contains(item.Id)) transforms[item.Id] = PreviewTransform(item);
            return new(layout, Camera, Cache.Meshes, Document.Selection.ToHashSet(), transforms, Wireframe, Collision);
        }
#if !MPHREAD_SHELL
        private bool GpuActive => false;
#endif
        private void Line(DrawingContext context,Vector a,Vector b,IBrush color,double width=1)
        { var x=Project(a);var y=Project(b);if(x!=null&&y!=null)context.DrawLine(new Pen(color,width),x.Value.Point,y.Value.Point); }
        private static StreamGeometry Polygon(Point[] points)
        {
            var geometry=new StreamGeometry(); using var path=geometry.Open(); path.BeginFigure(points[0],true);
            foreach(var p in points.Skip(1))path.LineTo(p);path.EndFigure(true);return geometry;
        }
        private void Label(DrawingContext context, Guid id, string text, Vector position)
        {
            var projected = Project(position); if (projected == null) return;
            if (!_labels.TryGetValue((id, text), out var label))
            {
                if (_labels.Count > 1024) _labels.Clear();
                _labels[(id, text)] = label = new FormattedText(text, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, GuiTheme.Face(bold: false), 11, Brushes.White);
            }
            var point = projected.Value.Point + new Avalonia.Vector(9, -8);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(180, 18, 30, 42)), null,
                new Rect(point - new Avalonia.Vector(3, 2), new Size(label.Width + 6, label.Height + 4)));
            context.DrawText(label, point);
        }
        public override void Render(DrawingContext context)
        {
            base.Render(context);
#if MPHREAD_SHELL
            if (GpuActive) context.Custom(new ViewportHole(new Rect(Bounds.Size)));
            else
#endif
                context.FillRectangle(new SolidColorBrush(Color.Parse("#141c25")),new Rect(Bounds.Size));
            var grid=new SolidColorBrush(Color.Parse("#293641"));
            if (!GpuActive)
                for(int n=-64;n<=64;n+=4){Line(context,new(n,0,-64),new(n,0,64),grid);Line(context,new(-64,0,n),new(64,0,n),grid);}
            _pick.Clear();
            if (!GpuActive)
            {
            var projected=new List<(MapViewportFace Face,Point[] Points,double Depth)>();
            var transforms = BuildRenderFrame(Layout).PreviewTransforms;
            foreach(var face in Faces)
            {
                if(Collision&&!face.Solid)continue;
                var transform = transforms.GetValueOrDefault(face.ObjectId, Matrix4x4.Identity);
                var points=face.Points.Select(p=>Project(Vector.Transform(p, transform))).ToArray();
                if(points.Any(p=>p==null))continue;
                projected.Add((face,points.Select(p=>p!.Value.Point).ToArray(),points.Average(p=>p!.Value.Depth)));
            }
            foreach(var item in projected.OrderByDescending(p=>p.Depth))
            {
                bool selected=Document.Selection.Contains(item.Face.ObjectId);
                int shade=(int)Math.Clamp(100*item.Face.Shade,35,200);
                var color=selected?Color.FromRgb(187,140,71):Collision?Color.FromRgb(50,(byte)(shade+30),100):Color.FromRgb((byte)(shade+item.Face.Material%3*15),(byte)(shade+15),(byte)(shade+30));
                context.DrawGeometry(Wireframe?null:new SolidColorBrush(color),new Pen(selected?Brushes.Gold:grid,selected?2:1),Polygon(item.Points));
                if(item.Face.ObjectId!=Guid.Empty)_pick.Add((item.Face.ObjectId,item.Points,item.Depth));
            }
            }
            if (!ReferenceEquals(_overlayDiagnostics, Document.Diagnostics))
            {
                _overlayDiagnostics = Document.Diagnostics;
                _warningObjects = Document.Diagnostics.Diagnostics.Where(d => d.ObjectId.HasValue && d.Severity != MapDiagnosticSeverity.Info)
                    .Select(d => d.ObjectId!.Value).ToHashSet();
                _labels.Clear();
            }
            int spawnIndex = 0;
            foreach(var o in Cache.Entities)
            {
                if (o.Value is MapSpawn) spawnIndex++;
                Vector position = Vector.Transform(MapViewportScene.Vector(o.Position), _drag && Document.Selection.Contains(o.Id) ? PreviewTransform(o) : Matrix4x4.Identity);
                var p=Project(position);if(p==null)continue;
                bool warning = _warningObjects.Contains(o.Id);
                IBrush color = warning ? Brushes.OrangeRed : o.Value is MapSpawn teamSpawn
                    ? teamSpawn.Team == 0 ? Brushes.DeepSkyBlue : teamSpawn.Team == 1 ? Brushes.Orange : Brushes.LimeGreen
                    : o.Value is MapItem ? Brushes.DeepSkyBlue : Brushes.Magenta;
                context.DrawEllipse(color,new Pen(Document.Selection.Contains(o.Id)?Brushes.Gold:Brushes.White,2),p.Value.Point,6,6);
                _pick.Add((o.Id,new[]{p.Value.Point-new Avalonia.Vector(8,8),p.Value.Point+new Avalonia.Vector(8,-8),p.Value.Point+new Avalonia.Vector(8,8),p.Value.Point+new Avalonia.Vector(-8,8)},0));
                if(o.Value is MapSpawn spawn)
                {
                    Label(context, o.Id, $"Spawn {spawnIndex} · {(spawn.Team < 0 ? "Neutral" : "Team " + (char)('A' + spawn.Team))}{(warning ? " · Check" : "")}", position + Vector.UnitY * 2.2f);
                    float angle=spawn.Yaw*MathF.PI/180;
                    Line(context,position,position+new Vector(MathF.Sin(angle),0,MathF.Cos(angle))*2,color,2);
                    Line(context,position,position+Vector.UnitY*1.9f,color);
                }
                if(o.Value is MapNavigationLink link)Line(context,position,MapViewportScene.Vector(link.To),Brushes.Orange,3);
                if(o.Value is MapJumpPad pad && MapValidator.Vector(pad.Position) && ((pad.Target!=null)!=(pad.Vector!=null)))
                {
                    try
                    {
                        var(v,speed)=MapBuilder.SolveJumpPad(pad);Vector velocity=new(v.X*speed,v.Y*speed,v.Z*speed),previous=position;
                        const float gravity = 77 / 4096f;
                        float duration = pad.Target == null ? 90 : (velocity.Y + MathF.Sqrt(MathF.Max(0,
                            velocity.Y * velocity.Y - 2 * gravity * (pad.Target[1] - pad.Position[1])))) / gravity;
                        Vector At(float t) => position + velocity * t - Vector.UnitY * (.5f * gravity * t * t);
                        for(int i=1;i<=60;i++){Vector point=At(duration*i/60);Line(context,previous,point,color);previous=point;}
                        Vector apex = At(Math.Clamp(velocity.Y / gravity, 0, duration));
                        Label(context, o.Id, $"Apex · {duration / 30:0.0}s flight", apex);
                        var landing = Project(previous);
                        if (landing != null)
                        {
                            context.DrawEllipse(null, new Pen(color, 2), landing.Value.Point, 7, 7);
                            var point = landing.Value.Point;
                            _pick.Add((o.Id, new[] { point + new Avalonia.Vector(-9,-9), point + new Avalonia.Vector(9,-9),
                                point + new Avalonia.Vector(9,9), point + new Avalonia.Vector(-9,9) }, 0));
                        }
                        Label(context, o.Id, pad.Target == null ? "Landing estimate" : "Target", previous);
                    }
                    catch(ProgramException){ }
                }
            }
            if(KillPlane)
            {
                float y=Document.Project.Definition.KillHeight;
                for(int i=-64;i<=64;i+=8)Line(context,new(i,y,-64),new(i,y,64),Brushes.IndianRed);
            }
            if (Navigation is { } pathGraph)
                for (int i = 1; i < NavigationPath.Length; i++)
                {
                    int a = NavigationPath[i-1], b = NavigationPath[i];
                    if ((uint)a >= pathGraph.Positions.Length || (uint)b >= pathGraph.Positions.Length) continue;
                    var p = pathGraph.Positions[a]; var q = pathGraph.Positions[b];
                    Line(context, new(p.X,p.Y,p.Z), new(q.X,q.Y,q.Z), Brushes.Gold, 4);
                }
            if(Navigation!=null)
                for(int i=0;i<Navigation.Positions.Length;i++)
                {
                    var p=Navigation.Positions[i];Vector a=new(p.X,p.Y,p.Z);var projectedPoint=Project(a);
                    IBrush color=Navigation.Components[i]%2==0?Brushes.Cyan:Brushes.Orange;
                    if(projectedPoint!=null)context.DrawEllipse(color,null,projectedPoint.Value.Point,2,2);
                    foreach(int n in Navigation.Neighbours[i]){var q=Navigation.Positions[n];Line(context,a,new(q.X,q.Y,q.Z),color);}
                }
            if (_boxSelect)
            {
                Rect box = SelectionRectangle(_start, _boxCurrent);
                context.FillRectangle(new SolidColorBrush(Color.FromArgb(32, 64, 190, 255)), box);
                context.DrawRectangle(new Pen(Brushes.DeepSkyBlue, 1), box);
            }
            var selectedObject=ActiveSelection;
            if(selectedObject!=null)
            {
                Vector center=MapViewportScene.Vector(selectedObject.Position)+_preview;
                Line(context,center,center+GizmoAxis(selectedObject,0)*3,Brushes.Red,3);Line(context,center,center+GizmoAxis(selectedObject,1)*3,Brushes.Lime,3);Line(context,center,center+GizmoAxis(selectedObject,2)*3,Brushes.DeepSkyBlue,3);
            }
        }
        private static Rect SelectionRectangle(Point a, Point b)
        {
            double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
            return new Rect(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }
        private static bool Intersects(Rect selection, Point[] polygon)
        {
            if (polygon.Length == 0) return false;
            double minX = polygon.Min(p => p.X), maxX = polygon.Max(p => p.X);
            double minY = polygon.Min(p => p.Y), maxY = polygon.Max(p => p.Y);
            return selection.Intersects(new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY)));
        }

        private static bool Contains(Point[] polygon,Point p)
        {
            bool inside=false;for(int i=0,j=polygon.Length-1;i<polygon.Length;j=i++)
                if((polygon[i].Y>p.Y)!=(polygon[j].Y>p.Y)&&p.X<(polygon[j].X-polygon[i].X)*(p.Y-polygon[i].Y)/(polygon[j].Y-polygon[i].Y)+polygon[i].X)inside=!inside;
            return inside;
        }
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);Focus();_last=_start=e.GetPosition(this);var props=e.GetCurrentPoint(this).Properties;
            _orbit=props.IsRightButtonPressed;_pan=props.IsMiddleButtonPressed;
            if(props.IsLeftButtonPressed)
            {
                Guid id=Guid.Empty;_axis=-1;
                var selected=ActiveSelection;
                if(selected!=null)
                    for(int i=0;i<3;i++)
                    {
                        var v=GizmoAxis(selected,i);
                        var p=Project(MapViewportScene.Vector(selected.Position)+v*3);
                        if(p!=null&&Math.Pow(p.Value.Point.X-_last.X,2)+Math.Pow(p.Value.Point.Y-_last.Y,2)<144){_axis=i;id=selected.Id;break;}
                    }
                if(id==Guid.Empty)
                {
                    // Entity handles sit above geometry; brush hits use world distance.
                    id=_pick.Where(p=>p.Depth==0&&Contains(p.Points,_last)).Select(p=>p.Id).FirstOrDefault();
                    if(id==Guid.Empty)id=MapViewportPicking.Pick(BuildRenderFrame(Layout),_last.X,_last.Y);
                }
                _boxAdditive=e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                if(!_boxAdditive&&!Document.Selection.Contains(id))Document.Selection.Clear();
                if(id!=Guid.Empty){Document.Selection.Add(id);Document.ActiveObjectId=id;}
                else { _boxSelect=true; _boxCurrent=_start; }
                _drag=id!=Guid.Empty;Document.SelectionChanged();SelectionChanged?.Invoke();InvalidateVisual();
            }
            e.Pointer.Capture(this);e.Handled=true;
        }
        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);Point p=e.GetPosition(this);var delta=p-_last;_last=p;
            if (_boxSelect) { _boxCurrent=p; InvalidateVisual(); return; }
            var(right,up,forward)=Basis();float distance=Vector.Distance(CameraPosition,CameraTarget);
            if(_orbit)
            {
                Vector offset=CameraPosition-CameraTarget;
                offset=Vector.Transform(offset,Quaternion.CreateFromAxisAngle(Vector.UnitY,(float)-delta.X*.01f));
                Vector rotated=Vector.Transform(offset,Quaternion.CreateFromAxisAngle(right,(float)-delta.Y*.01f));
                if(Math.Abs(Vector.Dot(Vector.Normalize(rotated),Vector.UnitY))<.98f)offset=rotated;
                CameraPosition=CameraTarget+offset;
            }
            if(_pan){Vector move=(-right*(float)delta.X+up*(float)delta.Y)*distance/500;CameraPosition+=move;CameraTarget+=move;}
            if(_drag)
            {
                var full=p-_start;_preview=(right*(float)full.X-up*(float)full.Y)*distance/500;
                if (LocalAxes && ActiveSelection?.Value is MapGeometry activeGeometry)
                { var r = activeGeometry.Transform.Rotation; _preview = Vector.Transform(_preview, Quaternion.Inverse(new Quaternion(r[0],r[1],r[2],r[3]))); }
                if(_axis>=0){float value=_preview[_axis];_preview=Vector.Zero;_preview[_axis]=value;}
                else if(Axes!="Free"){for(int i=0;i<3;i++)if(!Axes.Contains("XYZ"[i]))_preview[i]=0;}
                else _preview.Y=0;
                if(Snap>0)for(int i=0;i<3;i++)_preview[i]=MathF.Round(_preview[i]/Snap)*Snap;
                _rotation=MathF.Round((float)full.X/Math.Max(1,AngleSnap))*Math.Max(1,AngleSnap);
                _scale=Math.Max(ScaleSnap,1+MathF.Round((float)full.X/100/ScaleSnap)*ScaleSnap);
                if(Tool!="Move")_preview=Vector.Zero;
            }
            InvalidateVisual();
        }
        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (_boxSelect)
            {
                Rect selection = SelectionRectangle(_start, _boxCurrent);
                if (!_boxAdditive) Document.Selection.Clear();
                foreach (Guid id in _pick.Where(p => p.Id != Guid.Empty && Intersects(selection, p.Points))
                    .Select(p => p.Id).Distinct())
                    Document.Selection.Add(id);
                Document.ActiveObjectId = Document.Selection.FirstOrDefault();
                _boxSelect = false;
                Document.SelectionChanged(); SelectionChanged?.Invoke();
            }
            else if(_drag)
            {
                var ids=Document.Selection.ToHashSet();Vector move=_preview;float angle=_rotation,scale=_scale;
                Document.TransformSelection(ids, Tool, move, angle, scale, LocalAxes, rotationAxis:TurnAxis, scaleAxes:ScaleAxes, pivot:TransformPivot);
            }
            _drag=_orbit=_pan=false;_preview=Vector.Zero;_rotation=0;_scale=1;e.Pointer.Capture(null);InvalidateVisual();e.Handled=true;
        }
        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            float distance = Math.Clamp(Vector.Distance(CameraPosition, CameraTarget) * (float)Math.Pow(.88, e.Delta.Y), .25f, 5000);
            CameraPosition = CameraTarget - Camera.Basis().Forward * distance;
            InvalidateVisual(); e.Handled = true;
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            bool control=e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool shift=e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool alt=e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            if(e.Key==Key.F)FrameSelection();
            else if(e.Key==Key.Escape&&_boxSelect){_boxSelect=false;InvalidateVisual();}
            else if(e.Key==Key.Delete){var ids=Document.Selection.ToHashSet();Document.EditObjects("Delete selection",ids,d=>MapObjects.Delete(d,ids));}
            else if(control&&e.Key==Key.A)Document.SelectAllObjects();
            else if(control&&e.Key==Key.C)Document.CopySelection();
            else if(control&&e.Key==Key.V)Document.PasteClipboard();
            else if(control&&e.Key==Key.D){var ids=Document.Selection.ToHashSet();Document.EditObjects("Duplicate selection",ids,d=>MapObjects.Duplicate(d,ids));}
            else if(control&&e.Key==Key.Z)Document.History.Undo();
            else if(control&&e.Key==Key.Y)Document.History.Redo();
            else if(alt&&e.Key==Key.H)Document.ShowAllGeometry();
            else if(shift&&e.Key==Key.H)Document.IsolateSelection();
            else if(e.Key==Key.H)Document.HideSelection();
            else if(e.Key==Key.G){Tool="Move";}
            else if(e.Key==Key.R){Tool="Rotate";}
            else if(e.Key==Key.T){Tool="Scale";}
            else
            {
                var(right,up,forward)=Basis();Vector move=e.Key switch {Key.W=>forward,Key.S=>-forward,Key.A=>-right,Key.D=>right,Key.Q=>-up,Key.E=>up,_=>Vector.Zero};
                if(move==Vector.Zero){base.OnKeyDown(e);return;}CameraPosition+=move;CameraTarget+=move;InvalidateVisual();
            }
            e.Handled=true;
        }
        public void SetView(string view)
        {
            View=view;float distance=Vector.Distance(CameraPosition,CameraTarget);
            CameraPosition=CameraTarget+(view switch {"Top"=>new Vector(0,distance,.01f),"Front"=>new Vector(0,0,distance),"Side"=>new Vector(distance,0,0),_=>Vector.Normalize(new Vector(1,.8f,1))*distance});InvalidateVisual();
        }
    }
}
