using System;
using System.IO;

namespace MphRead.Mods.MapGen
{
    public static class MapPackageInstaller
    {
        public static MapDefinition Install(string download,Guid id,string contentHash,string archiveHash,string library)
        {
            if(MapBuildFingerprint.HashFile(download)!=archiveHash)throw new InvalidDataException("Downloaded package hash does not match the server.");
            using(var package=new MapPackageReader(download))
            {
                if(package.Manifest?.MapId!=id||package.Manifest.ContentHash!=contentHash)throw new InvalidDataException("Downloaded map identity does not match the server.");
            }
            var definition=MapDefinition.Load(download);
            var compiled=MapCompiler.Compile(definition);MapCompiler.ThrowIfInvalid(compiled.Validation);
            string stage=Path.Combine(library,".staging",Guid.NewGuid().ToString("N"));
            try
            {
                MapPacker.Generate(compiled.Map!,stage,stage,stage,false);
                Directory.CreateDirectory(library);
                string destination=Path.Combine(library,id.ToString("N")+MapBundle.Extension);
                AtomicFile.Write(destination,File.ReadAllBytes(download));
                var installed=MapDefinition.Load(destination);
                MapOutputSet staged=MapOutputSet.Create(installed,stage,stage,stage),output=CustomRooms.OutputsFor(installed);
                if(File.Exists(output.Manifest))File.Delete(output.Manifest);
                using var from=staged.Files.GetEnumerator();using var to=output.Files.GetEnumerator();
                while(from.MoveNext()&&to.MoveNext())AtomicFile.Write(to.Current,File.ReadAllBytes(from.Current));
                MapBuildManifest.Write(installed,output);
                return installed;
            }
            finally{if(Directory.Exists(stage))Directory.Delete(stage,true);}
        }
    }
}
