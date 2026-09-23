#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MphRead.Mods.Update;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Stable-release browser for explicit version changes.
    ///
    /// Automatic updates remain forward-only. This surface is the deliberate
    /// escape hatch for testing, regression comparison and returning to a
    /// known-good published build.
    /// </summary>
    internal sealed class VersionManagerView : UserControl
    {
        public event EventHandler? Closed;
        public event Action<UpdateInfo>? VersionSelected;

        public VersionManagerView(IReadOnlyList<UpdateInfo> releases, Version? current)
        {
            Focusable = true;
            Background = Brushes.Transparent;

            Version? installed = current == null ? null : BuildVersion.Normalise(current);
            var root = new Grid
            {
                Margin = new Thickness(28, 24, 28, 36),
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 16
            };
            root.Children.Add(HubChrome.Header(
                "HOME  /  VERSION MANAGER",
                "VERSION MANAGER",
                "Switch between published stable Project Prime builds.",
                "RELEASE HISTORY"));

            var body = new StackPanel { Spacing = 7 };
            body.Children.Add(new TextBlock
            {
                Text = installed == null
                    ? "INSTALLED  //  LOCAL BUILD"
                    : $"INSTALLED  //  v{installed.ToString(3)}",
                FontFamily = HubTheme.DataBold,
                FontSize = 9,
                Foreground = HubTheme.TextDimBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });

            for (int i = 0; i < releases.Count; i++)
            {
                UpdateInfo release = releases[i];
                bool isCurrent = installed != null && release.Version == installed;
                bool newer = installed != null && release.Version > installed;
                bool older = installed != null && release.Version < installed;
                string state = isCurrent
                    ? (i == 0 ? "CURRENT / LATEST" : "CURRENT")
                    : i == 0 ? "LATEST"
                    : newer ? "NEWER"
                    : older ? "OLDER"
                    : "PUBLISHED";

                Color accent = isCurrent
                    ? HubTheme.Good
                    : newer ? HubTheme.Warm
                    : HubTheme.Accent;
                var button = new HubNavButton(
                    $"{release.Tag.ToUpperInvariant()}  //  {state}",
                    ModeDetail(release, installed, isCurrent),
                    primary: i == 0,
                    accent: accent)
                {
                    Selected = isCurrent,
                    MinHeight = 64
                };
                ControllerNav.Identify(button, $"versions.release.{i}", initial: i == 0);
                if (!isCurrent)
                {
                    UpdateInfo selected = release;
                    button.Click += (_, _) => VersionSelected?.Invoke(selected);
                }
                body.Children.Add(button);
            }

            if (releases.Count == 0)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "NO STABLE RELEASES FOUND",
                    FontFamily = HubTheme.DataBold,
                    FontSize = 10,
                    Foreground = HubTheme.WarmBrush
                });
            }

            var scroll = new ScrollViewer
            {
                Content = body,
                HorizontalScrollBarVisibility =
                    Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility =
                    Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var back = new HubNavButton("BACK", compact: true);
            ControllerNav.Identify(back, "versions.back");
            back.Click += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
            footer.Children.Add(back);
            var note = new TextBlock
            {
                Text = "SELECT A PUBLISHED BUILD TO SWITCH VERSIONS",
                FontFamily = HubTheme.Data,
                FontSize = 8,
                Foreground = HubTheme.TextDimBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(note, 1);
            footer.Children.Add(note);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            Content = root;
            AttachedToVisualTree += (_, _) =>
                LauncherBackdrop.Set(LauncherBackdropScene.Settings);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Closed?.Invoke(this, EventArgs.Empty);
                return;
            }
            base.OnKeyDown(e);
        }

        private static string ModeDetail(UpdateInfo release, Version? installed, bool isCurrent)
        {
            if (isCurrent)
            {
                return "Installed now";
            }
            bool downgrade = installed != null && release.Version < installed;
            if (OperatingSystem.IsAndroid() && downgrade)
            {
                return "Manual downgrade — Android blocks installing a lower APK version over a newer one";
            }
            if (OperatingSystem.IsMacOS())
            {
                return "Manual switch — replace the signed app bundle from this release";
            }
            if (!UpdateInstall.CanInstall(release))
            {
                return release.AssetName.Length == 0
                    ? "Manual switch — no package is published for this platform"
                    : "Manual switch — this package cannot be installed in place";
            }
            string size = release.AssetSize > 0
                ? $" · {release.AssetSize / (1024d * 1024d):0.0} MB"
                : "";
            return $"Verified one-click {(downgrade ? "downgrade" : "switch")} · {release.AssetName}{size}";
        }
    }
}
#endif
