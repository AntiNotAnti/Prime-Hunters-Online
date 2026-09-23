#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
namespace MphRead.Mods.Launcher.Gui
{
    internal sealed record PrimeDispatch(string Category, string Title, string Summary, string Detail, PrimeRoute Route);
    internal interface INewsProvider { IReadOnlyList<PrimeDispatch> Read(); }
    internal sealed class BundledNewsProvider : INewsProvider
    {
        public IReadOnlyList<PrimeDispatch> Read() => new PrimeDispatch[]
        {
            new("UPDATES", "YOUR NEXT HUNT STARTS HERE", "One command deck. Every way to play Project Prime.",
                "Browse dedicated servers, build a private lobby, or launch a local bot match. Your own Metroid Prime Hunters game files are required. This is bundled project information; check the build indicator for release availability.", PrimeRoute.Play),
            new("UPDATES", "PERSISTENT MULTIPLAYER LOBBIES", "Stay connected while you configure your next match.",
                "Lobbies support up to eight players, owner controls, team layouts, optional ready checks and rematches. Return to an active session using the header's lobby indicator.", PrimeRoute.Play),
            new("TOOLS", "REPLAY STUDIO", "Find, organize and study your recorded matches.",
                "Theatre opens your local replay archive. Search by map or player, organize tags and collections, inspect recording integrity, and launch playback or cinematic tools.", PrimeRoute.Theatre),
            new("TOOLS", "BUILD YOUR ARENA", "Map Studio is part of your command deck.",
                "Forge retains your document, selection, camera and undo history as you move through the shell. Playtest locally and return to the same editor session.", PrimeRoute.Forge),
            new("OFFLINE", "LOCAL COMBAT", "Bot skirmishes and Adventure save slots in one place.",
                "Choose your hunter and arena for a local match, or resume an Adventure save. No server connection is needed for offline play.", PrimeRoute.Offline)
        };
    }
    internal sealed class NewsWorkspace : UserControl
    {
        public NewsWorkspace(Action<PrimeRoute> navigate, PrimeOverlayHost overlays, INewsProvider? provider = null, Action? quit = null)
        {
            var dispatches = (provider ?? new BundledNewsProvider()).Read();
            var root = new Grid { Margin = PrimeMetrics.PageMargin, RowDefinitions = new("Auto,*,Auto"), RowSpacing = 16 };
            var feed = new StackPanel { Spacing = 10 };
            var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var buttons = new List<PrimeTabButton>();
            void Show(string category)
            {
                feed.Children.Clear();
                feed.Children.Add(PrimeChrome.Title("TRANSMISSION ARCHIVE"));
                foreach (var item in dispatches.Skip(1).Where(d => category == "ALL" || d.Category == category))
                {
                    var article = item;
                    feed.Children.Add(new PrimePanel(PrimeChrome.Stack(
                        new PrimeBadge(item.Category), PrimeChrome.Text(item.Title, PrimeTypography.HeadingSmall),
                        PrimeChrome.Text(item.Summary, 12, PrimeTheme.TextSecondaryBrush),
                        new PrimeButton("READ DISPATCH →", () => Details(article)))));
                }
                if (feed.Children.Count == 1) feed.Children.Add(PrimeChrome.Text("No bundled dispatches in this category."));
                foreach (var b in buttons) b.Selected = b.Label == category;
            }
            void Details(PrimeDispatch item)
            {
                overlays.Show(new PrimePanel(PrimeChrome.Stack(new PrimeBadge("BUNDLED PROJECT INTEL"),
                    PrimeChrome.Title(item.Title), PrimeChrome.Text(item.Detail),
                    PrimeChrome.Columns("*,*", new PrimeButton("CLOSE", overlays.Close),
                        new PrimeButton("OPEN " + item.Route.ToString().ToUpperInvariant(), () => { overlays.Close(); navigate(item.Route); }, true)))), PrimeModalSize.Medium);
            }
            foreach (string filter in new[] { "ALL", "UPDATES", "TOOLS", "OFFLINE" })
            {
                var button = new PrimeTabButton(filter, () => Show(filter));
                buttons.Add(button); filters.Children.Add(button);
            }
            filters.Children.Add(new PrimeBadge("LOCAL ARCHIVE // PROJECT INTEL", PrimeTheme.GreenBrush));
            if (quit != null)
                filters.Children.Add(new PrimeButton("SYSTEM", () => overlays.Show(new PrimePanel(PrimeChrome.Stack(
                    PrimeChrome.Title("SYSTEM MENU"), new PrimeButton("SETTINGS", () => { overlays.Close(); navigate(PrimeRoute.Settings); }),
                    new PrimeButton("QUIT PROJECT PRIME", () => { overlays.Close(); quit(); }, danger: true),
                    new PrimeButton("CANCEL", overlays.Close))), PrimeModalSize.Small)));
            root.Children.Add(filters);
            var hero = dispatches[0];
            var art = new Grid { MinHeight = 170, Background = PrimeTheme.BackgroundDeepBrush, ClipToBounds = true };
            art.Children.Add(new Image { Source = MapShot.For("MP1 SANCTORUS"), Stretch = Stretch.UniformToFill });
            art.Children.Add(new TextBlock { Text = "PROJECT PRIME\nCOMBAT SYSTEMS ONLINE", FontFamily = PrimeTypography.Data,
                FontSize = 22, Foreground = PrimeTheme.CyanBrush, Margin = new Thickness(20), VerticalAlignment = VerticalAlignment.Bottom });
            var heroGrid = new Grid { RowDefinitions = new("Auto,Auto,Auto,*,Auto"), RowSpacing = 14 };
            Control[] heroParts = { new PrimeBadge("PROJECT DISPATCH // LOCAL EDITION"),
                PrimeChrome.Text(hero.Title, PrimeTypography.DisplayLarge), PrimeChrome.Text(hero.Summary, 16), art,
                PrimeChrome.Columns("*,*", new PrimeButton("READ FULL DISPATCH →", () => Details(hero), true),
                    new PrimeButton("LAUNCH PLAY", () => navigate(PrimeRoute.Play))) };
            for (int i = 0; i < heroParts.Length; i++) { Grid.SetRow(heroParts[i], i); heroGrid.Children.Add(heroParts[i]); }
            var body = PrimeChrome.Columns("1.8*,1*", new PrimePanel(heroGrid),
                new ScrollViewer { Content = feed, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            Grid.SetRow(body, 1); root.Children.Add(body);
            var cards = PrimeChrome.Columns("*,*,*", dispatches.Skip(2).Select(item => (Control)new PrimePanel(PrimeChrome.Stack(
                new PrimeBadge(item.Category), PrimeChrome.Text(item.Title, PrimeTypography.HeadingMedium),
                new PrimeButton("EXPLORE →", () => navigate(item.Route))))).ToArray());
            Grid.SetRow(cards, 2); root.Children.Add(cards);
            Content = root; Show("ALL");
        }
    }
}
#endif
