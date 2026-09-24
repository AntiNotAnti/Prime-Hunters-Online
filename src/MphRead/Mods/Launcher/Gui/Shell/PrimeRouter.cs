using System;
using System.Collections.Generic;
namespace MphRead.Mods.Launcher.Gui
{
    internal enum PrimeRoute { News, Play, HunterLicense, Theatre, Forge, Offline, Settings, Lobby }

    // Navigation policy contains no rendering or session ownership.
    internal sealed class PrimeRouter
    {
        public static readonly PrimeRoute[] Tabs = { PrimeRoute.News, PrimeRoute.Play,
            PrimeRoute.HunterLicense, PrimeRoute.Theatre, PrimeRoute.Forge, PrimeRoute.Offline, PrimeRoute.Settings };
        private readonly List<PrimeRoute> _history = new();
        public PrimeRoute Current { get; private set; } = PrimeRoute.News;
        public PrimeRoute? Previous => _history.Count == 0 ? null : _history[^1];
        public Func<PrimeRoute, bool>? CanNavigate { get; set; }
        public event Action<PrimeRoute>? Changed;
        public bool Navigate(PrimeRoute route)
        {
            if (route == Current) return true;
            if (CanNavigate?.Invoke(route) == false) return false;
            _history.Add(Current);
            if (_history.Count > 64) _history.RemoveAt(0);
            Current = route;
            Changed?.Invoke(route);
            return true;
        }
        public bool Back()
        {
            if (_history.Count == 0) return false;
            var route = _history[^1];
            if (CanNavigate?.Invoke(route) == false) return true;
            _history.RemoveAt(_history.Count - 1);
            Current = route;
            Changed?.Invoke(route);
            return true;
        }
        public void Forget(PrimeRoute route) => _history.RemoveAll(r => r == route);
        public void NextRoute() => Step(1);
        public void PreviousRoute() => Step(-1);
        private void Step(int direction)
        {
            int index = Array.IndexOf(Tabs, Current == PrimeRoute.Lobby ? PrimeRoute.Play : Current);
            Navigate(Tabs[(index + direction + Tabs.Length) % Tabs.Length]);
        }
    }
}
