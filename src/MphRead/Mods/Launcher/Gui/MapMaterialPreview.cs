using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MphRead.Mods.MapGen;

namespace MphRead.Mods.Launcher.Gui
{
    internal static class MapMaterialPreview
    {
        public static (Bitmap Bitmap,string Details) Create(MapDefinition definition,MapMaterial material)
        {
            ColorRgba[] pixels;int width,height;string details;
            if(material.Texture is {} path)
            {
                var texture=MapTexturePack.Load(MapAssets.Read(definition,path),path).Entries.Single();
                width=texture.Width;height=texture.Height;
                pixels=texture.Pixels.Select(p=>new ColorRgba(texture.Palette[p])).ToArray();
                details=$"Custom · {width} × {height} · Palette8Bit";
            }
            else
            {
                var model=Read.GetRoomModelInstance(definition.TextureSource).Model;
                if(material.SourceMaterial<0||material.SourceMaterial>=model.Materials.Count)throw new ArgumentException("Choose an existing source material.");
                var source=model.Materials[material.SourceMaterial];var recolor=model.Recolors[0];
                if(source.TextureId<0||source.PaletteId<0)throw new ArgumentException("Source material has no texture.");
                var texture=recolor.Textures[source.TextureId];width=texture.Width;height=texture.Height;
                pixels=recolor.GetPixels(source.TextureId,source.PaletteId).ToArray();
                details=$"{definition.TextureSource}\n{source.Name} · {width} × {height} · {texture.Format}";
            }
            var bitmap=new WriteableBitmap(new PixelSize(64,64),new Avalonia.Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Unpremul);
            using(var buffer=bitmap.Lock())
            {
                var row=new byte[64*4];
                for(int y=0;y<64;y++)
                {
                    for(int x=0;x<64;x++)
                    {var pixel=pixels[y*height/64*width+x*width/64];row[x*4]=pixel.Blue;row[x*4+1]=pixel.Green;row[x*4+2]=pixel.Red;row[x*4+3]=pixel.Alpha;}
                    Marshal.Copy(row,0,buffer.Address+y*buffer.RowBytes,row.Length);
                }
            }
            return(bitmap,details);
        }
    }
}
