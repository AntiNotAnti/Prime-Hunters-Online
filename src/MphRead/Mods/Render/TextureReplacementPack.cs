using System;
using System.IO;
using System.Linq;
using OpenTK.Graphics.OpenGL;
using ReFuel.Stb;

namespace MphRead.Mods.Render
{
    /// <summary>
    /// Optional user-owned high-resolution texture replacements.
    ///
    /// Nothing is shipped here. A player may put PNGs under
    /// texture-packs/default/&lt;model&gt;/ and the renderer substitutes them at
    /// upload time. Every failure falls through to the cartridge texture.
    /// Companion _n/_s/_e files are reserved for normal/specular/emissive data
    /// so texture packs can grow without changing their naming convention.
    /// </summary>
    internal static class TextureReplacementPack
    {
        private static readonly char[] _invalid = Path.GetInvalidFileNameChars();

        public static bool TryUpload(Model model, int textureId, int paletteId, int recolorId,
            out int width, out int height)
        {
            width = height = 0;
            if (!RenderOptions.TextureReplacements || OperatingSystem.IsAndroid())
            {
                return false;
            }

            string modelName = Safe(model.Name);
            string root = Path.Combine(AppContext.BaseDirectory, "texture-packs", "default");
            string folder = Path.Combine(root, modelName);
            string[] candidates =
            {
                Path.Combine(folder, $"{textureId}_{paletteId}_{recolorId}.png"),
                Path.Combine(folder, $"{textureId}_{paletteId}.png"),
                Path.Combine(folder, $"{textureId}.png"),
                Path.Combine(root, $"{modelName}_{textureId}_{paletteId}_{recolorId}.png"),
                Path.Combine(root, $"{modelName}_{textureId}.png")
            };

            foreach (string path in candidates)
            {
                if (!File.Exists(path))
                {
                    continue;
                }
                try
                {
                    using FileStream stream = File.OpenRead(path);
                    using StbImage image = StbImage.Load(stream, StbiImageFormat.Rgba);
                    if (image.Width <= 0 || image.Height <= 0 || image.ImagePointer == IntPtr.Zero)
                    {
                        continue;
                    }
                    width = image.Width;
                    height = image.Height;
                    GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
                    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                        width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte,
                        image.ImagePointer);
                    DebugLog.Line("render", $"HD texture {model.Name}:{textureId}/{paletteId}/{recolorId} "
                        + $"<- {Path.GetFileName(path)} ({width}x{height})");
                    return true;
                }
                catch (Exception ex) when (ex is IOException
                    or UnauthorizedAccessException or InvalidDataException)
                {
                    DebugLog.Line("render", $"HD texture ignored {path}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    // Decoder/native errors must not make a texture pack capable
                    // of taking down the game. The original upload immediately follows.
                    DebugLog.Line("render", $"HD texture failed {path}: {ex.Message}");
                }
            }
            return false;
        }

        public static string CompanionPath(string albedoPath, char kind)
            => Path.Combine(Path.GetDirectoryName(albedoPath) ?? "",
                Path.GetFileNameWithoutExtension(albedoPath) + "_" + kind + ".png");

        private static string Safe(string name)
        {
            if (String.IsNullOrWhiteSpace(name))
            {
                return "unnamed";
            }
            char[] chars = name.Select(c => _invalid.Contains(c) ? '_' : c).ToArray();
            return new string(chars).Trim();
        }
    }
}
