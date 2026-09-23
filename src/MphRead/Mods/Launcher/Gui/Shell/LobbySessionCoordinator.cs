#if MPHREAD_AVALONIA
using System;
using Avalonia.Threading;
using MphRead.Mods.Network;
namespace MphRead.Mods.Launcher.Gui
{
    // The one launcher owner of the lobby control-plane pump. Workspace detachment
    // never disconnects it; only Leave/session loss or the game handoff does.
    internal sealed class LobbySessionCoordinator
    {
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
        public LobbyScreen? Screen { get; set; }
        public Func<bool> IsForeground { get; set; } = () => false;
        public LobbySessionCoordinator()
        {
            _timer.Tick += (_, _) =>
            {
                if (Screen is not { } screen || NetSession.IsPlaying) return;
                NetSession.Pump();
                screen.SessionTick(IsForeground());
            };
        }
        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
    }
}
#endif
