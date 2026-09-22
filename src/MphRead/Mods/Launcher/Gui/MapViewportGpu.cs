#if MPHREAD_SHELL
using System;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using MphRead.Mods.MapEditor;
using OpenTK.Mathematics;
using SkiaSharp;

namespace MphRead.Mods.Launcher.Gui;

internal sealed partial class MapViewport
{
    private Scene? _renderer;
    private bool _rendererFailed;
    private bool GpuActive => _renderer != null;
    internal int GpuMeshUploads => _renderer?.EditorMeshUploads ?? 0;
    internal void PrepareRenderer()
    {
        if (_renderer != null || _rendererFailed) return;
        try
        {
            _renderer = Scene.CreateEditorRenderer(new Vector2i(1, 1));
            InvalidateVisual(); UiSurface.Current?.Invalidate();
        }
        catch (Exception ex)
        {
            _rendererFailed = true;
            Mods.DebugLog.Line("map", "Editor renderer unavailable: " + ex.Message);
        }
    }
    internal void ReleaseRenderer()
    {
        _renderer?.UnloadGl(); _renderer = null;
        InvalidateVisual();
    }
    internal void DrawInWindow(UiSurface surface, int width, int height)
    {
        if (_renderer == null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var origin = this.TranslatePoint(new Point(0, 0), surface.Root);
        var far = this.TranslatePoint(new Point(Bounds.Width, Bounds.Height), surface.Root);
        if (origin == null || far == null) return;
        double scale = (far.Value.X - origin.Value.X) / surface.WindowWidth * width / Bounds.Width;
        var layout = new MapViewportLayout(Bounds.Width, Bounds.Height, scale,
            (int)Math.Round(origin.Value.X / surface.WindowWidth * width),
            (int)Math.Round(origin.Value.Y / surface.WindowHeight * height));
        try { _renderer.DrawEditorFrame(BuildRenderFrame(layout), new(width, height)); }
        catch (Exception ex)
        {
            _rendererFailed = true;
            ReleaseRenderer(); UiSurface.Current?.Invalidate();
            Mods.DebugLog.Line("map", "Editor renderer failed: " + ex.Message);
        }
    }
    internal byte[] CaptureGpuPreview(byte[] overlay)
    {
        if (_renderer == null) return overlay;
        _renderer.DrawEditorFrame(BuildRenderFrame(Layout), new(Layout.PixelWidth, Layout.PixelHeight), capture: true);
        var rgb = _renderer.ReadSceneTarget(out int width, out int height)
            ?? throw new InvalidOperationException("Editor preview target is unavailable.");
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        // GL rows start at the bottom. Readback occurs only for an explicit capture.
        unsafe
        {
            byte* pixels = (byte*)bitmap.GetPixels();
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int source = ((height - 1 - y) * width + x) * 3, target = y * bitmap.RowBytes + x * 4;
                    pixels[target] = rgb[source]; pixels[target + 1] = rgb[source + 1];
                    pixels[target + 2] = rgb[source + 2]; pixels[target + 3] = 255;
                }
        }
        using var canvas = new SKCanvas(bitmap);
        using var ui = SKBitmap.Decode(overlay);
        canvas.DrawBitmap(ui, new SKRect(0, 0, width, height));
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
    /// <summary>Clear the parent background in the shared UI bitmap before drawing
    /// lightweight editor overlays. Later menus/tooltips remain above the GPU world.</summary>
    private sealed class ViewportHole(Rect bounds) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => other is ViewportHole hole && hole.Bounds == Bounds;
        public void Dispose() { }
        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature == null) return;
            using var lease = feature.Lease();
            using var clear = new SKPaint { BlendMode = SKBlendMode.Clear };
            lease.SkCanvas.DrawRect(new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height), clear);
        }
    }
}
#endif
