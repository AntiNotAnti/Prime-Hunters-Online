#if MPHREAD_AVALONIA
using System;
using System.Threading;
using Avalonia.Threading;
namespace MphRead.Mods.Launcher.Gui
{
    // Post bounded UI work from a wall clock. The embedded desktop surface
    // drains dispatcher jobs but does not run a native dispatcher event loop.
    internal sealed class PrimeUiPulse : IDisposable
    {
        private readonly Timer _timer;
        private readonly TimeSpan _interval;
        private readonly Action _dispatch;
        private int _enabled, _queued, _disposed;
        public PrimeUiPulse(TimeSpan interval, Action tick)
        {
            _interval = interval;
            _dispatch = () =>
            {
                Interlocked.Exchange(ref _queued, 0);
                if (Volatile.Read(ref _enabled) != 0) tick();
            };
            _timer = new Timer(_ =>
            {
                if (Volatile.Read(ref _enabled) != 0 && Interlocked.Exchange(ref _queued, 1) == 0)
                    Dispatcher.UIThread.Post(_dispatch, DispatcherPriority.Background);
            }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        public void Start() { if (_disposed != 0) return; Volatile.Write(ref _enabled, 1); _timer.Change(_interval, _interval); }
        public void Stop() { Volatile.Write(ref _enabled, 0); if (_disposed == 0) _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
        public void Dispose() { Volatile.Write(ref _enabled, 0); if (Interlocked.Exchange(ref _disposed, 1) == 0) _timer.Dispose(); }
    }
}
#endif
