#if MPHREAD_SHELL
using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Mods.MapEditor;
using MphRead.Mods.Network;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using Numerics = System.Numerics;

namespace MphRead;

/// <summary>Editor meshes use the game's shader, material and display-list path.</summary>
public partial class Scene
{
    private readonly Dictionary<Guid, EditorMesh> _editorMeshes = new();
    private RenderItem? _editorGrid;
    private sealed record EditorPart(bool Solid, bool CollisionOnly, bool SurfaceOnly, RenderItem Fill, RenderItem Edges);
    private sealed record EditorMesh(MapViewportMesh Source, EditorPart[] Parts);
    public int EditorMeshUploads { get; private set; }
    public int EditorMeshCount => _editorMeshes.Count;

    public static Scene CreateEditorRenderer(Vector2i size)
    {
        var scene = new Scene(size, null!, null!, _ => { }, () => { }, new EditorSceneServices(), initializeRuntime: false)
            { SideScene = true };
        int draw = GL.GetInteger(GetPName.DrawFramebufferBinding), read = GL.GetInteger(GetPName.ReadFramebufferBinding);
        int program = GL.GetInteger(GetPName.CurrentProgram);
        GL.PushAttrib(AttribMask.AllAttribBits);
        try { scene.InitShaders(); return scene; }
        catch { scene.UnloadGl(); throw; }
        finally
        {
            GL.UseProgram(program);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, draw);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, read);
            GL.PopAttrib();
        }
    }
    private sealed class EditorSceneServices : ISceneServices
    {
        public bool IsReplica => true;
        public bool MayEndOnScore => false;
        public bool ShouldLeaveAfterMatch => false;
        public bool SuppressDamage => true;
        public bool AllowsPresentationSideEffects => false;
        public IPlayerReplicationHost PlayerReplication { get; } = new ReplayPlayerReplicationHost(new ReplayReplicaState());
        public bool DisablePowerups => true;
        public Mods.Multiplayer.MatchWorldProfile? NetworkWorldProfile => null;
        public bool ReplicatesHealthSpawns => false;
        public bool TryGetHealthSpawn(short id, out HealthSpawnState state) { state = default; return false; }
    }
    private static Matrix4 EditorMatrix(Numerics.Matrix4x4 m) => new(
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
    private static Vector3 EditorVector(Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Draw into the current context; no simulation or foreground state is touched.</summary>
    public void DrawEditorFrame(MapRenderFrame frame, Vector2i targetSize, bool capture = false)
    {
        if (!frame.Layout.IsValid) return;
        SynchronizeEditorMeshes(frame.Meshes);
        int previousFramebuffer = GL.GetInteger(GetPName.FramebufferBinding);
        int previousReadFramebuffer = GL.GetInteger(GetPName.ReadFramebufferBinding);
        int previousProgram = GL.GetInteger(GetPName.CurrentProgram);
        GL.PushAttrib(AttribMask.AllAttribBits);
        try
        {
            var layout = frame.Layout;
            if (capture)
            {
                Size = new(layout.PixelWidth, layout.PixelHeight);
                OnResize();
                targetSize = RenderSize;
                _targetSize = targetSize;
                layout = layout with { RenderScale = targetSize.X / layout.Width, PixelX = 0, PixelY = 0 };
            }
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, capture ? _frameBuffer : previousFramebuffer);
            GL.Viewport(layout.PixelX, targetSize.Y - layout.PixelY - layout.PixelHeight, layout.PixelWidth, layout.PixelHeight);
            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(layout.PixelX, targetSize.Y - layout.PixelY - layout.PixelHeight, layout.PixelWidth, layout.PixelHeight);
            GL.DepthMask(true);
            GL.ColorMask(true, true, true, true);
            GL.ClearColor(20f / 255, 28f / 255, 37f / 255, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            GL.UseProgram(_shaderProgramId);
            var camera = frame.Camera;
            var view = Matrix4.LookAt(EditorVector(camera.Position), EditorVector(camera.Target), EditorVector(camera.Basis().Up));
            var projection = EditorMatrix(camera.Projection(frame.Layout));
            GL.UniformMatrix4(_shaderLocations.ProjectionMatrix, false, ref projection);
            GL.UniformMatrix4(_shaderLocations.ViewMatrix, false, ref view);
            GL.Uniform1(_shaderLocations.UseFog, 0);
            GL.Uniform1(_shaderLocations.ShowColors, 1);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.Disable(EnableCap.StencilTest);
            GL.Disable(EnableCap.AlphaTest);
            GL.Disable(EnableCap.Blend);
            GL.ActiveTexture(TextureUnit.Texture0);
            if (_editorGrid == null)
            {
                int grid = GL.GenLists(1);
                if (grid == 0) throw new InvalidOperationException("The renderer could not allocate the editor grid.");
                GL.NewList(grid, ListMode.Compile);
                GL.TexCoord3(0f, 0f, 0f);
                GL.Begin(PrimitiveType.Lines);
                for (int n = -64; n <= 64; n += 4)
                {
                    GL.Vertex3(n, 0, -64); GL.Vertex3(n, 0, 64);
                    GL.Vertex3(-64, 0, n); GL.Vertex3(64, 0, n);
                }
                GL.End(); GL.EndList();
                _editorGrid = EditorItem(grid);
                _editorGrid.OverrideColor = new Vector4(41f / 255, 54f / 255, 65f / 255, 1);
            }
            GL.DepthMask(false);
            RenderItem(_editorGrid);
            GL.DepthMask(true);
            foreach (var mesh in frame.Meshes)
            {
                bool selected = frame.Selection.Contains(mesh.ObjectId);
                var transform = EditorMatrix(frame.PreviewTransforms.GetValueOrDefault(mesh.ObjectId, Numerics.Matrix4x4.Identity));
                foreach (var part in _editorMeshes[mesh.ObjectId].Parts)
                {
                    if (frame.Collision ? !part.Solid || part.SurfaceOnly : part.CollisionOnly) continue;
                    part.Fill.Transform = part.Edges.Transform = transform;
                    part.Fill.OverrideColor = selected ? new Vector4(.73f, .55f, .28f, 1)
                        : frame.Collision ? new Vector4(.2f, .6f, .4f, 1) : null;
                    if (!frame.Wireframe) RenderItem(part.Fill);
                    if (frame.Wireframe || selected)
                    {
                        part.Edges.OverrideColor = selected ? new Vector4(1, .8f, .15f, 1) : new Vector4(.45f, .6f, .7f, 1);
                        // Edges at exactly the same depth are visible with Lequal.
                        RenderItem(part.Edges);
                    }
                }
            }
        }
        finally
        {
            GL.UseProgram(previousProgram);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, previousFramebuffer);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, previousReadFramebuffer);
            GL.PopAttrib();
        }
    }
    private void SynchronizeEditorMeshes(IReadOnlyList<MapViewportMesh> meshes)
    {
        var ids = meshes.Select(m => m.ObjectId).ToHashSet();
        foreach (var id in _editorMeshes.Keys.Where(id => !ids.Contains(id)).ToArray())
        { DeleteEditorMesh(_editorMeshes[id]); _editorMeshes.Remove(id); }
        foreach (var mesh in meshes)
        {
            if (_editorMeshes.TryGetValue(mesh.ObjectId, out var previous))
            {
                if (ReferenceEquals(previous.Source, mesh)) continue;
                DeleteEditorMesh(previous); _editorMeshes.Remove(mesh.ObjectId);
            }
            var parts = new List<EditorPart>();
            try
            {
                foreach (var variant in mesh.CollisionFaces == null
                    ? new[] { (Faces: mesh.Faces, CollisionOnly: false, SurfaceOnly: false) }
                    : new[] { (Faces: mesh.Faces, CollisionOnly: false, SurfaceOnly: true),
                        (Faces: mesh.CollisionFaces, CollisionOnly: true, SurfaceOnly: false) })
                foreach (var group in variant.Faces.GroupBy(f => f.Solid))
                {
                    int fill = CompileEditorList(group, edges: false);
                    int edges;
                    try { edges = CompileEditorList(group, edges: true); }
                    catch { GL.DeleteLists(fill, 1); throw; }
                    var surface = EditorItem(fill);
                    surface.CullingMode = CullingMode.Back;
                    parts.Add(new(group.Key, variant.CollisionOnly, variant.SurfaceOnly, surface, EditorItem(edges)));
                }
                _editorMeshes.Add(mesh.ObjectId, new(mesh, parts.ToArray()));
                EditorMeshUploads++;
            }
            catch { foreach (var part in parts) { GL.DeleteLists(part.Fill.ListId, 1); GL.DeleteLists(part.Edges.ListId, 1); } throw; }
        }
    }
    private static RenderItem EditorItem(int list) => new()
    {
        Type = RenderItemType.Mesh, ListId = list, Alpha = 1, Diffuse = Vector3.One,
        CullingMode = CullingMode.Neither, Transform = Matrix4.Identity, TexcoordMatrix = Matrix4.Identity
    };
    private static int CompileEditorList(IEnumerable<MapViewportFace> faces, bool edges)
    {
        int list = GL.GenLists(1);
        if (list == 0) throw new InvalidOperationException("The renderer could not allocate an editor mesh.");
        GL.NewList(list, ListMode.Compile);
        GL.TexCoord3(0f, 0f, 0f);
        GL.Normal3(0f, 1f, 0f);
        GL.Begin(edges ? PrimitiveType.Lines : PrimitiveType.Triangles);
        foreach (var face in faces)
        {
            int shade = (int)Math.Clamp(100 * face.Shade, 35, 200);
            GL.Color3((shade + face.Material % 3 * 15) / 255f, (shade + 15) / 255f, (shade + 30) / 255f);
            if (edges)
                for (int i = 0; i < face.Points.Length; i++)
                { GL.Vertex3(EditorVector(face.Points[i])); GL.Vertex3(EditorVector(face.Points[(i + 1) % face.Points.Length])); }
            else
                for (int i = 1; i < face.Points.Length - 1; i++)
                { GL.Vertex3(EditorVector(face.Points[0])); GL.Vertex3(EditorVector(face.Points[i])); GL.Vertex3(EditorVector(face.Points[i + 1])); }
        }
        GL.End(); GL.EndList();
        return list;
    }
    private static void DeleteEditorMesh(EditorMesh mesh)
    {
        foreach (var part in mesh.Parts) { GL.DeleteLists(part.Fill.ListId, 1); GL.DeleteLists(part.Edges.ListId, 1); }
    }
    private void DisposeEditorMeshes()
    {
        if (_editorGrid != null) { GL.DeleteLists(_editorGrid.ListId, 1); _editorGrid = null; }
        if (_editorMeshes == null) return;
        foreach (var mesh in _editorMeshes.Values) DeleteEditorMesh(mesh);
        _editorMeshes.Clear();
    }
}
#endif
