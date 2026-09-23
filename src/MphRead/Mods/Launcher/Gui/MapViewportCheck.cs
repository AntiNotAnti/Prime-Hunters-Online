#if MPHREAD_SHELL
using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MphRead.Mods.MapEditor;
using MphRead.Mods.MapGen;
using MphRead.Mods.Render;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using GLFWBindingsContext = OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext;

namespace MphRead.Mods.Launcher.Gui;

internal static class MapViewportCheck
{
    internal static int Run(string directory, string? projectPath = null)
    {
        directory = Path.GetFullPath(Path.Combine(ConsoleSetup.LaunchDirectory, directory));
        Directory.CreateDirectory(directory);
        var settings = DesktopGlContext.Settings(background: true);
        settings.StartVisible = true;
        settings.StartFocused = false;
        settings.ClientSize = new(960, 600);
        using var window = new NativeWindow(settings);
        window.Context.MakeCurrent();
        GL.LoadBindings(new GLFWBindingsContext());
        var surface = UiSurface.Ensure() ?? throw new InvalidOperationException("No UI surface.");
        int checks = 0;
        var foregroundPlayers = MphRead.Entities.PlayerEntity.LegacyRegistry;
        var foregroundState = GameState.Current;
        var foregroundRandom = Rng.Current;
        void Check(bool success, string name)
        { if (!success) throw new InvalidOperationException(name); Console.WriteLine("MAPVIEWPORT PASS " + name); checks++; }
        var definition = MapTemplates.Create("Renderer check", false).Definition;
        var far = new MapBox { Transform = new() { Position = new[] { 7f, 2f, 0f } } };
        definition.Geometry.Add(far);
        var document = new MapDocument(new MapProject(definition));
        var viewport = new MapViewport(document);
        var panel = new Panel { Background = Brushes.DarkRed, Margin = new Thickness(70, 60, 90, 80) };
        panel.Children.Add(viewport);
        var label = new Border { Background = Brushes.DeepPink, Width = 90, Height = 40,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, IsVisible = false };
        panel.Children.Add(label);
        try
        {
            foreach (var size in new[] { new Vector2i(960, 600), new Vector2i(1440, 900), new Vector2i(2880, 1800) })
            {
                window.ClientSize = size;
                NativeWindow.ProcessWindowEvents(false);
                window.Context.SwapBuffers(); // GLX commits the resized drawable at swap.
                NativeWindow.ProcessWindowEvents(false);
                // The UI and rendering consume framebuffer pixels, including Retina.
                var target = window.FramebufferSize;
                surface.Resize(target.X, target.Y); surface.Show(panel);
                viewport.FrameAll();
                surface.PrepareMapRenderer();
                for (int i = 0; i < 5; i++) { System.Threading.Thread.Sleep(20); surface.Invalidate(); surface.Tick(); }
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                GL.Viewport(0, 0, target.X, target.Y);
                GL.ClearColor(0, 0, 0, 1); GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                surface.DrawMapViewport(target.X, target.Y); UiOverlay.Draw(target.X, target.Y);
                Check(viewport.GpuMeshUploads == 2, "stable meshes upload once at " + size);
                Check(ScreenCapture.SaveWindow(target.X, target.Y, Path.Combine(directory, $"viewport-{size.X}.png")), "window capture " + size);
                using var overlay = new RenderTargetBitmap(new PixelSize((int)viewport.Bounds.Width, (int)viewport.Bounds.Height), new Avalonia.Vector(96, 96));
                overlay.Render(viewport);
                using var stream = new MemoryStream(); overlay.Save(stream, PngBitmapEncoderOptions.Default);
                byte[] picture = viewport.CaptureGpuPreview(stream.ToArray());
                File.WriteAllBytes(Path.Combine(directory, $"preview-{size.X}.png"), picture);
                using var rendered = SkiaSharp.SKBitmap.Decode(picture);
                Check(rendered.Pixels.Count(p => p.Blue > 60 && p.Red > 40) > rendered.Width * rendered.Height / 100,
                    "GPU preview contains visible mesh " + size);
                Check(GL.GetError() == ErrorCode.NoError, "renderer restores GL state " + size);
                var logical = new MapViewportLayout(viewport.Bounds.Width, viewport.Bounds.Height);
                var frame = viewport.BuildRenderFrame(logical);
                var point = frame.Camera.Project(logical, new System.Numerics.Vector3(7, 2, 0))!.Value;
                var surfacePoint = viewport.TranslatePoint(new Point(point.X, point.Y), surface.Root)!.Value;
                document.Selection.Clear();
                surface.PointerMoved(surfacePoint.X / surface.WindowWidth * target.X, surfacePoint.Y / surface.WindowHeight * target.Y);
                surface.PointerButton(Avalonia.Input.MouseButton.Left, true);
                surface.PointerButton(Avalonia.Input.MouseButton.Left, false);
                Check(document.Selection.Contains(far.Id), "window-pixel pointer selects rendered brush " + size);
                document.Selection.Clear(); document.SelectionChanged();
            }
            int uploads = viewport.GpuMeshUploads;
            document.Selection.Add(far.Id); document.SelectionChanged();
            surface.DrawMapViewport(window.FramebufferSize.X, window.FramebufferSize.Y);
            Check(viewport.GpuMeshUploads == uploads, "selection does not upload geometry");
            viewport.SetView("Top"); viewport.Collision = true; viewport.Wireframe = true;
            surface.DrawMapViewport(window.FramebufferSize.X, window.FramebufferSize.Y);
            Check(viewport.GpuMeshUploads == uploads, "camera and overlays do not upload geometry");
            document.TransformSelection(new[] { far.Id }, "Move", System.Numerics.Vector3.UnitX, 0, 1, false);
            surface.DrawMapViewport(window.FramebufferSize.X, window.FramebufferSize.Y);
            Check(viewport.GpuMeshUploads == uploads + 1, "moving one object uploads only that object");
            label.IsVisible = true;
            for (int i = 0; i < 4; i++) { System.Threading.Thread.Sleep(20); surface.Invalidate(); surface.Tick(); }
            surface.DrawMapViewport(window.FramebufferSize.X, window.FramebufferSize.Y);
            UiOverlay.Draw(window.FramebufferSize.X, window.FramebufferSize.Y);
            Check(ScreenCapture.SaveWindow(window.FramebufferSize.X, window.FramebufferSize.Y,
                Path.Combine(directory, "overlay-order.png")), "overlay order capture");
            var center = label.TranslatePoint(new Point(45, 20), surface.Root)!.Value;
            int px = (int)(center.X / surface.WindowWidth * window.FramebufferSize.X);
            int py = window.FramebufferSize.Y - (int)(center.Y / surface.WindowHeight * window.FramebufferSize.Y);
            byte[] marker = new byte[4];
            GL.ReadPixels(px, py, 1, 1, OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, marker);
            Check(marker[0] > 240 && marker[1] < 40 && marker[2] > 100,
                $"UI overlay remains above GPU geometry ({px},{py}; {string.Join(',', marker)})");
            surface.Hide(); surface.PrepareMapRenderer();
            Check(viewport.GpuMeshUploads == 0 && GL.GetError() == ErrorCode.NoError, "leaving editor releases renderer");
            window.ClientSize = new(1440, 900);
            NativeWindow.ProcessWindowEvents(false);
            window.Context.SwapBuffers();
            NativeWindow.ProcessWindowEvents(false);
            surface.Resize(window.FramebufferSize.X, window.FramebufferSize.Y);
            var studio = new MapStudioScreen(); studio.Load(MapTemplates.Create("Renderer check", false));
            surface.Show(studio); surface.PrepareMapRenderer();
            for (int i = 0; i < 5; i++) { System.Threading.Thread.Sleep(20); surface.Invalidate(); surface.Tick(); }
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            surface.DrawMapViewport(window.FramebufferSize.X, window.FramebufferSize.Y);
            UiOverlay.Draw(window.FramebufferSize.X, window.FramebufferSize.Y);
            Check(ScreenCapture.SaveWindow(window.FramebufferSize.X, window.FramebufferSize.Y,
                Path.Combine(directory, "map-studio-renderer.png")), "full editor composite capture");
            var back = studio.GetVisualDescendants().OfType<PrimeButton>().First(b => b.Label == "BACK");
            var backCenter = back.TranslatePoint(new Point(back.Bounds.Width / 2, back.Bounds.Height / 2), surface.Root)!.Value;
            GL.ReadPixels((int)(backCenter.X / surface.WindowWidth * window.FramebufferSize.X),
                window.FramebufferSize.Y - (int)(backCenter.Y / surface.WindowHeight * window.FramebufferSize.Y),
                1, 1, OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, marker);
            Check(marker[0] > 20 && marker[1] > 20 && marker[2] > 20, "editor toolbar survives GPU composite");
            Check(ReferenceEquals(foregroundPlayers, MphRead.Entities.PlayerEntity.LegacyRegistry)
                && ReferenceEquals(foregroundState, GameState.Current) && ReferenceEquals(foregroundRandom, Rng.Current),
                "editor renderer preserves foreground scene ownership");
            if (projectPath != null)
            {
                var imported = MapDefinition.Load(Path.GetFullPath(Path.Combine(ConsoleSetup.LaunchDirectory, projectPath)));
                var analysis = MapBuildScheduler.Shared.AnalyzeAsync(MapBuildSnapshot.Capture(imported)).GetAwaiter().GetResult();
                Check(analysis.Succeeded, "imported benchmark map compiles");
                var importedViewport = new MapViewport(new MapDocument(new MapProject(imported)));
                importedViewport.SetImported(analysis); importedViewport.FrameAll();
                var importedPanel = new Panel { Margin = new Thickness(40) }; importedPanel.Children.Add(importedViewport);
                surface.Show(importedPanel); surface.PrepareMapRenderer();
                for (int i = 0; i < 5; i++) { System.Threading.Thread.Sleep(20); surface.Invalidate(); surface.Tick(); }
                var target = window.FramebufferSize;
                GL.Disable(EnableCap.ScissorTest);
                GL.Viewport(0, 0, target.X, target.Y);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 20; i++) { surface.DrawMapViewport(target.X, target.Y); GL.Finish(); }
                double gpuMs = clock.Elapsed.TotalMilliseconds / 20;
                int importedUploads = importedViewport.GpuMeshUploads;
                UiOverlay.Draw(target.X, target.Y);
                Check(ScreenCapture.SaveWindow(target.X, target.Y, Path.Combine(directory, "imported.png")), "imported map renderer capture");
                importedViewport.Collision = true;
                surface.DrawMapViewport(target.X, target.Y);
                Check(importedViewport.GpuMeshUploads == importedUploads, "imported collision switch reuses GPU geometry");
                UiOverlay.Draw(target.X, target.Y);
                Check(ScreenCapture.SaveWindow(target.X, target.Y, Path.Combine(directory, "imported-collision.png")), "imported collision capture");
                importedViewport.Collision = false; importedViewport.ReleaseRenderer();
                using var cpuTarget = new RenderTargetBitmap(new PixelSize((int)importedViewport.Bounds.Width,
                    (int)importedViewport.Bounds.Height), new Avalonia.Vector(96, 96));
                cpuTarget.Render(importedViewport); clock.Restart();
                for (int i = 0; i < 5; i++) cpuTarget.Render(importedViewport);
                Console.WriteLine($"MAPVIEWPORT PROFILE faces={analysis.Faces.Length} collision={analysis.CollisionFaces.Length} "
                    + $"uploads={importedUploads} GPU-draw-ms={gpuMs:F3} CPU-fallback-raster-ms={clock.Elapsed.TotalMilliseconds / 5:F3}; "
                    + "GPU includes driver completion; CPU includes UI polygon rasterization, excludes upload. Not whole-editor frame times.");
            }
            Console.WriteLine($"MAPVIEWPORT {checks} checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("MAPVIEWPORT " + ex); return 1; }
        finally { surface.ReleaseMapRenderer(); surface.Hide(); UiOverlay.Release(); }
    }
}
#endif
