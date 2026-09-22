using System.Collections.Generic;
using OpenTK.Graphics.OpenGL;

namespace MphRead.Mods.Render
{
    /// <summary>Display lists live on shared model data. Leases prevent one scene
    /// from destroying a list another scene still draws. Called on the GL owner thread.</summary>
    internal static class SharedModelResources
    {
        private static readonly Dictionary<Model, int> Owners = new();
        internal static void Retain(Model model) => Owners[model] = Owners.GetValueOrDefault(model) + 1;
        internal static void Release(Model model)
        {
            if (!Owners.TryGetValue(model, out int count)) return;
            if (count > 1) { Owners[model] = count - 1; return; }
            Owners.Remove(model);
            var deleted = new HashSet<int>();
            foreach (Mesh mesh in model.Meshes)
            {
                if (mesh.ListId != 0 && deleted.Add(mesh.ListId)) GL.DeleteLists(mesh.ListId, 1);
                mesh.ListId = 0;
            }
        }
    }
}
