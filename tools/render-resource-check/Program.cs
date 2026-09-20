using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using MphRead;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using GLFWBindingsContext = OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext;

// Exercise the real cel target allocator and scene teardown in one persistent
// GL context. No Scene constructor: that loads music/game data. Only the empty
// texture registry and the synthetic color attachment are needed for teardown.
using var window = new NativeWindow(new NativeWindowSettings
{
    ClientSize = new Vector2i(32, 32),
    StartVisible = false,
    StartFocused = false,
    Profile = ContextProfile.Compatability,
    APIVersion = new Version(3, 2)
});
window.Context.MakeCurrent();
GL.LoadBindings(new GLFWBindingsContext());
int failures = 0;
for (int cycle = 0; cycle < 3; cycle++)
{
    var scene = (Scene)RuntimeHelpers.GetUninitializedObject(typeof(Scene));
    FieldInfo registry = Field("_texPalMap");
    registry.SetValue(scene, Activator.CreateInstance(registry.FieldType));
    int texture = GL.GenTexture();
    GL.BindTexture(TextureTarget.Texture2D, texture);
    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb,
        32, 32, 0, PixelFormat.Rgb, PixelType.UnsignedByte, IntPtr.Zero);
    Field("_screenTexture").SetValue(scene, texture);
    MethodInfo allocate = typeof(Scene).GetMethod("CelFrameBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!;
    int framebuffer = (int)allocate.Invoke(scene, null)!;
    Check(GL.IsFramebuffer(framebuffer), "cel framebuffer allocated");
    Check(GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == FramebufferErrorCode.FramebufferComplete,
        "synthetic cel attachment complete");
    Check((int)allocate.Invoke(scene, null)! == framebuffer, "cel target reused within a scene");
    // Gameplay ends a frame on the default framebuffer. The leaked, unbound
    // cel framebuffer can keep its deleted color texture's storage alive.
    GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    GL.BindTexture(TextureTarget.Texture2D, 0);
    scene.UnloadGl();
    Check(!GL.IsFramebuffer(framebuffer), "scene unload deletes cel framebuffer");
    Check((int)Field("_celFrameBuffer").GetValue(scene)! == 0
        && (int)Field("_celFrameBufferColor").GetValue(scene)! == 0, "cel handles cleared");
    Check(!GL.IsTexture(texture), "scene color texture deleted");
    scene.UnloadGl();
    Check(GL.GetError() == ErrorCode.NoError, "repeated unload is harmless");
    // Clean up the negative-control leak too, so a failing run is bounded.
    if (GL.IsFramebuffer(framebuffer)) GL.DeleteFramebuffer(framebuffer);
}
Console.WriteLine($"RENDERRESOURCES failures={failures}");
return failures == 0 ? 0 : 1;

static FieldInfo Field(string name) => typeof(Scene).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

void Check(bool success, string name)
{
    Console.WriteLine($"RENDERRESOURCES {(success ? "PASS" : "FAIL")} {name}");
    if (!success) failures++;
}
