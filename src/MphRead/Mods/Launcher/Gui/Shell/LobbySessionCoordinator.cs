#if MPHREAD_AVALONIA
using System;
using MphRead.Mods.Network;
namespace MphRead.Mods.Launcher.Gui
{
    // The one launcher owner of the lobby control-plane pump. Workspace detachment
    // never disconnects it; only Leave/session loss or the game handoff does.
    internal sealed class LobbySessionCoordinator : IDisposable
    {
        private readonly PrimeUiPulse _pulse;
        private bool _attached;
        private LobbyScreen? _screen;
        public LobbyScreen? Screen
        {
            get => _screen;
            set { _screen = value; if (_attached && value != null) _pulse.Start(); else _pulse.Stop(); }
        }
        public Func<bool> IsForeground { get; set; } = () => false;
        public LobbySessionCoordinator()
        {
            _pulse = new PrimeUiPulse(TimeSpan.FromMilliseconds(50), () =>
            {
                if (Screen is not { } screen || NetSession.IsPlaying) return;
                NetSession.Pump();
                screen.SessionTick(IsForeground());
            });
        }
        public void Start() { _attached = true; if (Screen != null) _pulse.Start(); }
        public void Stop() { _attached = false; _pulse.Stop(); }
        public void Dispose() { _attached = false; _pulse.Dispose(); _screen = null; }
    }
}
#endif
