#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
namespace MphRead.Mods.Launcher.Gui
{
    internal sealed class PrimeHeader : Border
    {
        private readonly Dictionary<PrimeRoute, PrimeTabButton> _tabs = new();
        private readonly TextBlock _status = PrimeChrome.Text("LOCAL SYSTEM", 10, PrimeTheme.GreenBrush, true);
        private readonly PrimeButton _profile;
        private readonly PrimeButton _lobby;
        public PrimeHeader(Action<PrimeRoute> navigate)
        {
            Height = PrimeMetrics.HeaderHeight;
            Background = PrimeTheme.BackgroundDeepBrush;
            BorderBrush = PrimeTheme.BorderBrush;
            BorderThickness = new Thickness(0, 0, 0, 1);
            Padding = new Thickness(24, 12);
            var identity = PrimeChrome.Stack(PrimeChrome.Title("PROJECT PRIME"), _status);
            identity.Spacing = 4;
            var nav = new Grid { ColumnDefinitions = new("*,*,1.55*,*,*,*,*"), ColumnSpacing = 4 };
            int index = 0;
            foreach (var route in PrimeRouter.Tabs)
            {
                var button = new PrimeTabButton(route == PrimeRoute.HunterLicense ? "HUNTER LICENSE" : route.ToString().ToUpperInvariant(), () => navigate(route));
                ControllerNav.Identify(button, "prime.nav." + route);
                Grid.SetColumn(button, index++); nav.Children.Add(button); _tabs.Add(route, button);
            }
            _profile = new PrimeButton("PLAYER", () => navigate(PrimeRoute.HunterLicense));
            _lobby = new PrimeButton("LOBBY ACTIVE", () => navigate(PrimeRoute.Lobby)) { IsVisible = false };
            var session = new Grid(); session.Children.Add(_profile); session.Children.Add(_lobby);
            Child = PrimeChrome.Columns("230,*,185", identity, nav, session);
        }
        public void SetRoute(PrimeRoute route)
        {
            foreach (var (key, tab) in _tabs) tab.Selected = key == (route == PrimeRoute.Lobby ? PrimeRoute.Play : route);
        }
        public void Refresh(PrimeGlobalState state)
        {
            _status.Text = state.GameFilesReady ? "● LOCAL SYSTEM READY" : "● GAME FILES REQUIRED";
            _profile.Label = state.PlayerName.ToUpperInvariant() + " // " + state.Hunter.ToUpperInvariant();
            _profile.IsVisible = !state.LobbyActive;
            _lobby.IsVisible = state.LobbyActive;
            _lobby.Label = $"● LOBBY ACTIVE // {state.LobbyPlayerCount}";
        }
    }
    internal sealed class PrimeFooter : Border
    {
        private readonly TextBlock _hints = PrimeChrome.Text("", 10, data: true);
        public readonly PrimeButton Version = new("BUILD // LOCAL");
        public PrimeFooter(Action update)
        {
            Height = PrimeMetrics.FooterHeight;
            Background = PrimeTheme.BackgroundDeepBrush;
            BorderBrush = PrimeTheme.BorderBrush; BorderThickness = new Thickness(0, 1, 0, 0);
            Padding = new Thickness(24, 2);
            _hints.VerticalAlignment = VerticalAlignment.Center;
            Version.MinHeight = 32;
            Version.Click += (_, _) => update();
            Child = PrimeChrome.Columns("*,Auto", _hints, Version);
        }
        public void Refresh(PrimeGlobalState state)
        {
            _hints.Text = Mods.Input.InputSourceTracker.Current == Mods.Input.InputSource.Gamepad
                ? $"{Mods.Input.InputPrompt.For(Mods.Input.UiAction.Accept).Glyph} SELECT  //  {Mods.Input.InputPrompt.For(Mods.Input.UiAction.PreviousTab).Glyph} / {Mods.Input.InputPrompt.For(Mods.Input.UiAction.NextTab).Glyph} TABS  //  {Mods.Input.InputPrompt.For(Mods.Input.UiAction.Back).Glyph} BACK"
                : "[ENTER] SELECT   //   [Q / E] SWITCH TAB   //   [ESC] BACK";
            Version.Label = state.UpdateAvailable ? "UPDATE AVAILABLE // INSTALL" : "SIM: 60 HZ  //  BUILD " + state.BuildVersion;
        }
    }
}
#endif
