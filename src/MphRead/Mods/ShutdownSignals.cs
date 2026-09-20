using System;
using System.Runtime.InteropServices;

namespace MphRead.Mods
{
    /// <summary>
    /// Every way a long-running headless process is asked to stop, in one
    /// place, so that the thing being stopped gets a chance to tidy up.
    ///
    /// `Console.CancelKeyPress` covers Ctrl+C and nothing else. The dedicated
    /// server and the directory are both run by systemd, and systemd stops a
    /// service with **SIGTERM** -- which by default kills the process where it
    /// stands. That is how a server that had just been restarted stayed on
    /// everybody's list for the best part of a minute: it never reached the
    /// line that tells the directory it is going away.
    ///
    /// The handler runs once. A second signal is left to do what a second
    /// signal should, which is end the process regardless of what it thinks it
    /// is in the middle of.
    /// </summary>
    public sealed class ShutdownSignals : IDisposable
    {
        private readonly System.Collections.Generic.List<IDisposable> _registrations = new();
        private Action? _action;
        private int _fired;

        public void OnShutdown(Action action)
        {
            _action = action;
            Console.CancelKeyPress += OnCancelKey;
            Register(PosixSignal.SIGTERM);
            Register(PosixSignal.SIGINT);
            // Not SIGHUP: a server started from a terminal that is then closed
            // should keep running, which is what nohup and systemd both expect.
        }

        private void Register(PosixSignal signal)
        {
            try
            {
                _registrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    // Handled here rather than by the default action, so the
                    // run loop can exit through its own cleanup.
                    context.Cancel = true;
                    Fire();
                }));
            }
            catch (Exception)
            {
                // Not every platform offers every signal; Ctrl+C still works.
            }
        }

        private void OnCancelKey(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            Fire();
        }

        private void Fire()
        {
            if (System.Threading.Interlocked.Exchange(ref _fired, 1) != 0)
            {
                return;
            }
            _action?.Invoke();
        }

        public void Dispose()
        {
            Console.CancelKeyPress -= OnCancelKey;
            foreach (IDisposable registration in _registrations)
            {
                registration.Dispose();
            }
            _registrations.Clear();
        }
    }
}
