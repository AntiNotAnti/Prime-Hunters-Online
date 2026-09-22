using MphRead.Sound;

namespace MphRead.Mods.Replay;

/// <summary>One presentation may use the existing audio device. A stale scene's
/// release token cannot stop a later presentation or a recreated device.</summary>
internal static class ReplayAudioOwner
{
    private static Scene? _owner, _live;
    private static SfxInstanceBase? _backend;
    private static ulong _version;
    internal static bool MayPlay(Scene scene) => _owner == null
        ? scene.Services.AllowsPresentationSideEffects : ReferenceEquals(_owner, scene);
    internal static ulong Acquire(Scene scene, Scene live)
    {
        _version++; _owner = scene; _live = live; _backend = Sfx.Instance;
        _backend?.StopAllSound(); _backend?.SetListenerScene(scene);
        return _version;
    }
    internal static void Release(ulong version)
    {
        if (version == 0 || version != _version) return;
        if (ReferenceEquals(Sfx.Instance, _backend))
        { _backend?.StopAllSound(); _backend?.SetListenerScene(_live); }
        Reset();
    }
    internal static void Reset() { _version++; _owner = _live = null; _backend = null; }
}
