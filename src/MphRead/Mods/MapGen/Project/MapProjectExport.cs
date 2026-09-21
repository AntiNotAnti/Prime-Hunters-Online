using System.IO;

namespace MphRead.Mods.MapGen
{
    public static class MapProjectExport
    {
        // Copy only referenced, validated assets, never raw archive paths.
        public static void Save(MapDefinition definition,string path)
        {
            string destination=Path.GetDirectoryName(Path.GetFullPath(path))!;
            foreach(var asset in definition.Assets)
            {
                byte[] bytes=MapAssets.Read(definition,asset.Path);
                AtomicFile.Write(Path.Combine(destination,asset.Path),bytes);
            }
            if(definition.Import is {} import)
            {
                if(definition.BundlePath is {} bundle)
                {
                    using var package=new MapPackageReader(bundle);
                    string assetFolder=Path.GetFileNameWithoutExtension(path)+"-assets";
                    string folder=Path.Combine(destination,assetFolder);
                    string level=Path.Combine(folder,"level.bsp");
                    AtomicFile.Write(level,package.Read(import.Source)??throw new InvalidDataException("Packaged level is missing."));
                    byte[]? texture=string.IsNullOrEmpty(import.Textures)?null:package.Read(import.Textures);
                    if(texture!=null){string target=Path.Combine(folder,"map.tex");AtomicFile.Write(target,texture);import.Textures=Path.Combine(assetFolder,"map.tex").Replace('\\','/');}
                    import.Source=Path.Combine(assetFolder,"level.bsp").Replace('\\','/');import.MapName=null;
                }
                else
                {
                    import.Source=import.Resolve() is {} level?Path.GetFullPath(level):import.Source;
                    import.Textures=import.ResolveTextures() is {} texture?Path.GetFullPath(texture):import.Textures;
                }
                import.BundlePath=null;import.BaseDirectory=Path.GetDirectoryName(Path.GetFullPath(path));
            }
            if (definition.Collision is { } collision)
            {
                if (definition.BundlePath != null)
                {
                    string assetFolder = Path.GetFileNameWithoutExtension(path) + "-assets";
                    string target = Path.Combine(destination, assetFolder, "collision.obj");
                    AtomicFile.Write(target, collision.ReadBytes() ?? throw new InvalidDataException("Packaged collision mesh is missing."));
                    collision.Source = Path.Combine(assetFolder, "collision.obj").Replace('\\','/');
                }
                else
                {
                    collision.Source = collision.Resolve() is { } source ? Path.GetFullPath(source) : collision.Source;
                }
                collision.BundlePath = null;
                collision.BaseDirectory = destination;
            }
            definition.BundlePath=null;definition.SourcePath=Path.GetFullPath(path);definition.BaseDirectory=Path.GetDirectoryName(definition.SourcePath);
            definition.Save(path);
        }
    }
}
