using System;
using System.Collections.Generic;
using System.Linq;
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
    // A lightweight authoring renderer, hosted by the existing single-window
    // Avalonia surface. It never packs collision or regenerates navigation.
    internal sealed class MapViewport : Control, IMapViewport
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
        public MapNodePacker.NavigationGraph? Navigation { get; set; }
        public event Action? SelectionChanged;
        private MapViewportScene _scene = new();
        private readonly List<MapViewportFace> _imported = new();
        private readonly List<(Guid Id, Point[] Points, double Depth)> _pick = new();
        private Point _last, _start;
        private bool _orbit, _pan, _drag;
        private int _axis = -1;
        private Vector _preview;
        private float _rotation, _scale = 1;

        public MapViewport(MapDocument document)
        {
            Document=document; Focusable=true; ClipToBounds=true;
            Document.Changed += Rebuild;
            AttachedToVisualTree+=(_,_)=>{Document.Changed-=Rebuild;Document.Changed+=Rebuild;Rebuild();};
            DetachedFromVisualTree += (_,_) => Document.Changed -= Rebuild;
            Rebuild();
        }
        private void Rebuild() { _scene=MapViewportScene.Create(Document.Project.Definition); _scene.Faces.AddRange(_imported); Navigation=null; InvalidateVisual(); }
        public void SetImported(BuiltMap map)
        {
            _imported.Clear();
            foreach(var face in map.Faces)
                _imported.Add(new(Guid.Empty,face.Points.Select(p=>new Vector(p.X,p.Y,p.Z)).ToArray(),face.Shade,face.Material,true));
            Rebuild();
        }
        public void FrameAll()
        {
            var points=_scene.Faces.SelectMany(f=>f.Points).ToArray();
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
        private (Vector Right,Vector Up,Vector Forward) Basis()
        {
            Vector forward=Vector.Normalize(CameraTarget-CameraPosition), up=Math.Abs(forward.Y)>.99f?Vector.UnitZ:Vector.UnitY;
            Vector right=Vector.Normalize(Vector.Cross(forward,up)); return(right,Vector.Cross(right,forward),forward);
        }
        private (Point Point,double Depth)? Project(Vector p)
        {
            var (right,up,forward)=Basis(); Vector offset=p-CameraPosition; float z=Vector.Dot(offset,forward);
            if(z<.05f) return null;
            double scale=View=="Perspective"?Math.Min(Bounds.Width,Bounds.Height)*.9/z:Math.Min(Bounds.Width,Bounds.Height)/Math.Max(2,Vector.Distance(CameraPosition,CameraTarget)) * 1.5;
            return(new Point(Bounds.Width/2+Vector.Dot(offset,right)*scale,Bounds.Height/2-Vector.Dot(offset,up)*scale),z);
        }
        private void Line(DrawingContext context,Vector a,Vector b,IBrush color,double width=1)
        { var x=Project(a);var y=Project(b);if(x!=null&&y!=null)context.DrawLine(new Pen(color,width),x.Value.Point,y.Value.Point); }
        private static StreamGeometry Polygon(Point[] points)
        {
            var geometry=new StreamGeometry(); using var path=geometry.Open(); path.BeginFigure(points[0],true);
            foreach(var p in points.Skip(1))path.LineTo(p);path.EndFigure(true);return geometry;
        }
        public override void Render(DrawingContext context)
        {
            base.Render(context); context.FillRectangle(new SolidColorBrush(Color.Parse("#141c25")),new Rect(Bounds.Size));
            var grid=new SolidColorBrush(Color.Parse("#293641"));
            for(int n=-64;n<=64;n+=4){Line(context,new(n,0,-64),new(n,0,64),grid);Line(context,new(-64,0,n),new(64,0,n),grid);}
            _pick.Clear();
            var projected=new List<(MapViewportFace Face,Point[] Points,double Depth)>();
            var centers=Document.Project.Definition.Geometry.Where(g=>Document.Selection.Contains(g.Id)&&!g.Locked).ToDictionary(g=>g.Id,g=>MapViewportScene.Vector(g.Transform.Position));
            var rotations=Document.Project.Definition.Geometry.Where(g=>centers.ContainsKey(g.Id)).ToDictionary(g=>g.Id,g=>new Quaternion(g.Transform.Rotation[0],g.Transform.Rotation[1],g.Transform.Rotation[2],g.Transform.Rotation[3]));
            foreach(var face in _scene.Faces)
            {
                if(Collision&&!face.Solid)continue;
                Vector delta=Document.Selection.Contains(face.ObjectId)?_preview:Vector.Zero;
                if(LocalAxes&&rotations.TryGetValue(face.ObjectId,out var localRotation))delta=Vector.Transform(delta,localRotation);
                var points=face.Points.Select(p=>
                {
                    if(_drag&&centers.TryGetValue(face.ObjectId,out var center))
                    {
                        if(Tool=="Rotate")p=center+Vector.Transform(p-center,Quaternion.CreateFromAxisAngle(LocalAxes?Vector.Transform(Vector.UnitY,rotations[face.ObjectId]):Vector.UnitY,_rotation*MathF.PI/180));
                        else if(Tool=="Scale")p=center+(p-center)*_scale;
                    }
                    return Project(p+delta);
                }).ToArray();
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
            foreach(var o in MapObjects.All(Document.Project.Definition).Where(o=>o.Value is not MapGeometry && o.Value is not MapBrush))
            {
                Vector position=MapViewportScene.Vector(o.Position)+(Document.Selection.Contains(o.Id)?_preview:Vector.Zero);
                var p=Project(position);if(p==null)continue;
                IBrush color=o.Value is MapSpawn?Brushes.LimeGreen:o.Value is MapItem?Brushes.DeepSkyBlue:Brushes.Magenta;
                context.DrawEllipse(color,new Pen(Document.Selection.Contains(o.Id)?Brushes.Gold:Brushes.White,2),p.Value.Point,6,6);
                _pick.Add((o.Id,new[]{p.Value.Point-new Avalonia.Vector(8,8),p.Value.Point+new Avalonia.Vector(8,-8),p.Value.Point+new Avalonia.Vector(8,8),p.Value.Point+new Avalonia.Vector(-8,8)},0));
                if(o.Value is MapSpawn spawn)
                {
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
                        for(int i=1;i<=60;i++){float t=i*1.5f;Vector point=position+velocity*t-Vector.UnitY*(.5f*77/4096*t*t);Line(context,previous,point,color);previous=point;}
                    }
                    catch(ProgramException){ }
                }
            }
            if(KillPlane)
            {
                float y=Document.Project.Definition.KillHeight;
                for(int i=-64;i<=64;i+=8)Line(context,new(i,y,-64),new(i,y,64),Brushes.IndianRed);
            }
            if(Navigation!=null)
                for(int i=0;i<Navigation.Positions.Length;i++)
                {
                    var p=Navigation.Positions[i];Vector a=new(p.X,p.Y,p.Z);var projectedPoint=Project(a);
                    IBrush color=Navigation.Components[i]%2==0?Brushes.Cyan:Brushes.Orange;
                    if(projectedPoint!=null)context.DrawEllipse(color,null,projectedPoint.Value.Point,2,2);
                    foreach(int n in Navigation.Neighbours[i]){var q=Navigation.Positions[n];Line(context,a,new(q.X,q.Y,q.Z),color);}
                }
            var selectedObject=MapObjects.All(Document.Project.Definition).FirstOrDefault(o=>Document.Selection.Contains(o.Id));
            if(selectedObject!=null)
            {
                Vector center=MapViewportScene.Vector(selectedObject.Position)+_preview;
                Line(context,center,center+Vector.UnitX*3,Brushes.Red,3);Line(context,center,center+Vector.UnitY*3,Brushes.Lime,3);Line(context,center,center+Vector.UnitZ*3,Brushes.DeepSkyBlue,3);
            }
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
                var selected=MapObjects.All(Document.Project.Definition).FirstOrDefault(o=>Document.Selection.Contains(o.Id));
                if(selected!=null)
                    for(int i=0;i<3;i++)
                    {
                        var v=i==0?Vector.UnitX:i==1?Vector.UnitY:Vector.UnitZ;
                        var p=Project(MapViewportScene.Vector(selected.Position)+v*3);
                        if(p!=null&&Math.Pow(p.Value.Point.X-_last.X,2)+Math.Pow(p.Value.Point.Y-_last.Y,2)<144){_axis=i;id=selected.Id;break;}
                    }
                if(id==Guid.Empty) id=_pick.Where(p=>Contains(p.Points,_last)).OrderBy(p=>p.Depth).Select(p=>p.Id).FirstOrDefault();
                if(!e.KeyModifiers.HasFlag(KeyModifiers.Shift)&&!Document.Selection.Contains(id))Document.Selection.Clear();
                if(id!=Guid.Empty)Document.Selection.Add(id);
                _drag=id!=Guid.Empty;SelectionChanged?.Invoke();InvalidateVisual();
            }
            e.Pointer.Capture(this);e.Handled=true;
        }
        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);Point p=e.GetPosition(this);var delta=p-_last;_last=p;
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
                if(_axis>=0){float value=_preview[_axis];_preview=Vector.Zero;_preview[_axis]=value;}
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
            if(_drag)
            {
                var ids=Document.Selection.ToHashSet();Vector move=_preview;float angle=_rotation,scale=_scale;
                Document.Edit(Tool+" selection",d=>
                {
                    foreach(var o in MapObjects.All(d).Where(o=>ids.Contains(o.Id)))
                    {
                        if(o.Value is MapGeometry {Locked:true})continue;
                        if(Tool=="Move")
                        {
                            Vector translated=move;
                            if(LocalAxes&&o.Value is MapGeometry local){var r=local.Transform.Rotation;translated=Vector.Transform(move,new Quaternion(r[0],r[1],r[2],r[3]));}
                            o.Move(new[]{translated.X,translated.Y,translated.Z});
                        }
                        else if(o.Value is MapGeometry g)
                        {
                            if(Tool=="Scale")for(int i=0;i<3;i++)g.Transform.Scale[i]*=scale;
                            else
                            {
                                var r=g.Transform.Rotation;var turn=Quaternion.CreateFromAxisAngle(Vector.UnitY,angle*MathF.PI/180);var current=new Quaternion(r[0],r[1],r[2],r[3]);var q=Quaternion.Normalize(LocalAxes?current*turn:turn*current);
                                g.Transform.Rotation=new[]{q.X,q.Y,q.Z,q.W};
                            }
                        }
                        else if(o.Value is MapSpawn s&&Tool=="Rotate")s.Yaw+=angle;
                    }
                });
            }
            _drag=_orbit=_pan=false;_preview=Vector.Zero;_rotation=0;_scale=1;e.Pointer.Capture(null);InvalidateVisual();e.Handled=true;
        }
        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {CameraPosition=CameraTarget+(CameraPosition-CameraTarget)*(float)Math.Pow(.88,e.Delta.Y);InvalidateVisual();e.Handled=true;}
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if(e.Key==Key.F)FrameSelection();
            else if(e.Key==Key.Delete){var ids=Document.Selection.ToHashSet();Document.Edit("Delete selection",d=>MapObjects.Delete(d,ids));}
            else if(e.KeyModifiers.HasFlag(KeyModifiers.Control)&&e.Key==Key.D){var ids=Document.Selection.ToHashSet();Document.Edit("Duplicate selection",d=>MapObjects.Duplicate(d,ids));}
            else if(e.KeyModifiers.HasFlag(KeyModifiers.Control)&&e.Key==Key.Z)Document.History.Undo();
            else if(e.KeyModifiers.HasFlag(KeyModifiers.Control)&&e.Key==Key.Y)Document.History.Redo();
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
