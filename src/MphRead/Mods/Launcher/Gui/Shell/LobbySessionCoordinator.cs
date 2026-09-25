#if MPHREAD_AVALONIA
using System;
#if ANDROID
using Avalonia.Threading;
#endif
using MphRead.Mods.Network;
namespace MphRead.Mods.Launcher.Gui
{
    // The one launcher owner of the lobby control-plane pump. Workspace detachment
    // never disconnects it; only Leave/session loss or the game handoff does.
    internal sealed class LobbySessionCoordinator : IDisposable
    {
#if ANDROID
        // Android runs Avalonia on its native dispatcher. Use that dispatcher as
        // the lobby clock instead of posting a background callback from a
        // Threading.Timer. The latter is required by the embedded desktop shell,
        // but on Android a starved post leaves the network session alive while
        // the roster/map/rules presentation never receives another tick.
        private readonly DispatcherTimer _timer;
#else
        private readonly PrimeUiPulse _pulse;
#endif
        private bool _attached;
        private LobbyScreen? _screen;
        public LobbyScreen? Screen
        {
            get => _screen;
            set
            {
                _screen = value;
                if (!_attached || value == null)
                {
                    StopClock();
                    return;
                }
                StartClock();
                // Hydrate the first lobby frame immediately. Waiting for the
                // first periodic callback can otherwise render a connected
                // shell with an empty roster and arena until the clock fires.
                Tick(forceForeground: true);
            }
        }
        public Func<bool> IsForeground { get; set; } = () => false;
        public LobbySessionCoordinator()
        {
#if ANDROID
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50),
                DispatcherPriority.Input, (_, _) => Tick());
#else
            _pulse = new PrimeUiPulse(TimeSpan.FromMilliseconds(50), () => Tick());
#endif
        }

        internal void Tick(bool forceForeground = false)
        {
            // InMatch describes the server, not this client's scene. A late
            // join already has that phase while its lobby still needs to emit
            // MatchRequested. Yield the pump only after the local handoff.
            if (Screen is not { } screen || (screen.IsSuspended && NetSession.IsPlaying)) return;
            NetSession.Pump();
            screen.SessionTick(forceForeground || IsForeground());
        }

        private void StartClock()
        {
#if ANDROID
            _timer.Start();
#else
            _pulse.Start();
#endif
        }

        private void StopClock()
        {
#if ANDROID
            _timer.Stop();
#else
            _pulse.Stop();
#endif
        }

        public void Start()
        {
            _attached = true;
            if (Screen != null)
            {
                StartClock();
                Tick(forceForeground: true);
            }
        }

        public void Stop()
        {
            _attached = false;
            StopClock();
        }

        public void Dispose()
        {
            _attached = false;
            StopClock();
#if !ANDROID
            _pulse.Dispose();
#endif
            _screen = null;
        }
    }
}
#endif
