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
    internal sealed record PrimeDispatch(string Category, string Title, string Summary, string Detail);
    internal interface INewsProvider { IReadOnlyList<PrimeDispatch> Read(); }
    internal sealed class BundledNewsProvider : INewsProvider
    {
        public IReadOnlyList<PrimeDispatch> Read() => new PrimeDispatch[]
        {
            new("NEWS", "PROJECT PRIME COMMUNITY UPDATE", "A new home for your hunts. Catch up on what's changing in Project Prime.",
                "Project Prime brings Metroid Prime Hunters to modern PCs and Android. This news page collects project updates, patch notes and announcements. Join the Discord to follow the community, and check GitHub Releases for published builds."),
            new("PATCH NOTES", "LOBBY & SETTINGS REFINEMENTS", "Clearer controls, more room for settings, and team selection in the roster.",
                "Lobby rule controls have larger OFF/ON buttons. Settings uses a single category strip. Hunter previews stay behind dialogs, and team-mode rosters offer team arrows that respect team locks and available slots. These changes are included in this build."),
            new("ANNOUNCEMENTS", "JOIN THE PROJECT PRIME DISCORD", "Stay connected with the Project Prime community.",
                "Join the Discord using the button at the top of News. Follow project announcements, keep up with patch notes, and connect with other hunters."),
            new("NEWS", "PERSISTENT MULTIPLAYER LOBBIES", "Stay connected between matches.",
                "Project Prime lobbies support up to eight players, owner controls, team layouts, optional ready checks and rematches. The active lobby indicator in the header brings you back to your session.")
        };
    }
    internal sealed class NewsWorkspace : UserControl
    {
        internal const string DiscordUrl = "https://discord.gg/qKp2M8kHd6";

        public NewsWorkspace(PrimeOverlayHost overlays, INewsProvider? provider = null)
        {
            var dispatches = (provider ?? new BundledNewsProvider()).Read();
            var root = new Grid { Margin = PrimeMetrics.PageMargin, RowDefinitions = new("Auto,*"), RowSpacing = 16 };
            var feed = new StackPanel { Spacing = 10 };
            var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var buttons = new List<PrimeTabButton>();
            var headline = PrimeChrome.Text("", PrimeTypography.DisplayLarge);
            var summary = PrimeChrome.Text("", 16);
            var categoryBadge = new ContentControl();
            PrimeDispatch? selected = null;
            var read = new PrimeButton("READ FULL ARTICLE →", () => { if (selected != null) Details(selected); }, true);
            void Select(PrimeDispatch article)
            {
                selected = article;
                headline.Text = article.Title;
                summary.Text = article.Summary;
                categoryBadge.Content = new PrimeBadge(article.Category);
            }
            void Show(string category)
            {
                feed.Children.Clear();
                feed.Children.Add(PrimeChrome.Title("LATEST DISPATCHES"));
                var articles = dispatches.Where(d => category == "ALL" || d.Category == category).ToArray();
                foreach (var item in articles)
                {
                    var article = item;
                    feed.Children.Add(new PrimePanel(PrimeChrome.Stack(
                        new PrimeBadge(item.Category), PrimeChrome.Text(item.Title, PrimeTypography.HeadingSmall),
                        PrimeChrome.Text(item.Summary, 12, PrimeTheme.TextSecondaryBrush),
                        new PrimeButton("READ ARTICLE →", () => { Select(article); Details(article); }))));
                }
                read.IsVisible = articles.Length > 0;
                if (articles.Length > 0) Select(articles[0]);
                else
                {
                    selected = null;
                    headline.Text = "NO DISPATCHES YET";
                    summary.Text = "Check back for project news and announcements.";
                    categoryBadge.Content = new PrimeBadge(category);
                    feed.Children.Add(PrimeChrome.Text("No articles in this category."));
                }
                foreach (var b in buttons) b.Selected = b.Label == category;
            }
            void Details(PrimeDispatch item)
            {
                overlays.Show(new PrimePanel(PrimeChrome.Stack(new PrimeBadge(item.Category),
                    PrimeChrome.Title(item.Title), PrimeChrome.Text(item.Detail),
                    new PrimeButton("CLOSE", overlays.Close))), PrimeModalSize.Medium);
            }
            foreach (string filter in new[] { "ALL", "NEWS", "PATCH NOTES", "ANNOUNCEMENTS" })
            {
                var button = new PrimeTabButton(filter, () => Show(filter));
                buttons.Add(button); filters.Children.Add(button);
            }
            var discord = new PrimeButton("JOIN THE DISCORD ↗", () =>
            {
                if (!Mods.Update.Updater.OpenLink(DiscordUrl))
                    overlays.Show(new PrimePanel(PrimeChrome.Stack(PrimeChrome.Title("JOIN THE DISCORD"),
                        PrimeChrome.Text("Open this invite in your browser:"),
                        new SelectableTextBlock { Text = DiscordUrl, Foreground = PrimeTheme.HighlightBrush },
                        new PrimeButton("CLOSE", overlays.Close))), PrimeModalSize.Small);
            }, primary: true);
            ControllerNav.Identify(discord, "news.discord");
            root.Children.Add(PrimeChrome.Columns("*,Auto", filters, discord));
            var art = new Grid { MinHeight = 170, Background = PrimeTheme.BackgroundDeepBrush, ClipToBounds = true };
            art.Children.Add(new Image { Source = MapShot.For("MP1 SANCTORUS"), Stretch = Stretch.UniformToFill });
            art.Children.Add(new TextBlock { Text = "PROJECT PRIME\nCOMMUNITY TRANSMISSIONS", FontFamily = PrimeTypography.Data,
                FontSize = 22, Foreground = PrimeTheme.HighlightBrush, Margin = new Thickness(20), VerticalAlignment = VerticalAlignment.Bottom });
            var heroGrid = new Grid { RowDefinitions = new("Auto,Auto,Auto,*,Auto"), RowSpacing = 14 };
            Control[] heroParts = { categoryBadge, headline, summary, art, read };
            for (int i = 0; i < heroParts.Length; i++) { Grid.SetRow(heroParts[i], i); heroGrid.Children.Add(heroParts[i]); }
            var body = PrimeChrome.Columns("1.8*,1*", new PrimePanel(heroGrid),
                new ScrollViewer { Content = feed, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            Grid.SetRow(body, 1); root.Children.Add(body);
            Content = root; Show("ALL");
        }
    }
}
#endif
