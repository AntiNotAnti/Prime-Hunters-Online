#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Threading;
namespace MphRead.Mods.Launcher.Gui
{
    internal interface IPrimeWorkspace
    {
        void OnActivated();
        void OnDeactivated();
        void Refresh();
    }
    internal sealed class PrimeWorkspaceHost : ContentControl
    {
        private readonly Dictionary<PrimeRoute, Control> _cache = new();
        private readonly Dictionary<PrimeRoute, Control?> _focus = new();
        private readonly Func<PrimeRoute, Control> _create;
        private PrimeRoute? _active;
        public PrimeWorkspaceHost(Func<PrimeRoute, Control> create) { _create = create; ClipToBounds = true; }
        public Control Get(PrimeRoute route)
        {
            if (!_cache.TryGetValue(route, out var view)) _cache[route] = view = _create(route);
            return view;
        }
        public void Set(PrimeRoute route, Control view) => _cache[route] = view;
        public void Remove(PrimeRoute route)
        {
            if (_cache.Remove(route, out var view) && view is IDisposable disposable) disposable.Dispose();
            _focus.Remove(route);
        }
        public void Show(PrimeRoute route)
        {
            if (_active is { } previous && Content is Control old)
            {
                _focus[previous] = FocusNavigator.Focused(old);
                (old as IPrimeWorkspace)?.OnDeactivated();
            }
            var next = Get(route);
            _active = route;
            Content = next;
            (next as IPrimeWorkspace)?.OnActivated();
            PrimeMotion.Enter(next);
            Dispatcher.UIThread.Post(() =>
            {
                if (_active != route) return;
                if (_focus.TryGetValue(route, out var focused) && focused is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true })
                    FocusNavigator.Focus(focused);
                else FocusNavigator.Ensure(next);
            }, DispatcherPriority.Background);
        }
    }
}
#endif
