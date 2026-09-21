#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MphRead.Mods.Network;
using MphRead.Mods.Replay;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// First-class Replay Studio library.
    ///
    /// Playback/editor state remains in DemoPlayback/ReplayStudio. This view
    /// only owns library presentation and file-management actions.
    /// </summary>
    internal sealed class HubReplayStudioView : UserControl
    {
        private readonly UiList _list = new() { AutoSelectFirst = true };
        private readonly TextBlock _title;
        private readonly TextBlock _metadata;
        private readonly TextBlock _status;
        private readonly TextBlock _summary;
        private readonly Image _preview = new() { Stretch = Stretch.UniformToFill };
        private readonly DeckField _search;
        private readonly ChoiceRow _filter;
        private readonly ChoiceRow _sort;
        private readonly DeckField _rename;
        private readonly HubNavButton _watch;
        private readonly HubNavButton _favorite;
        private readonly HubNavButton _validate;
        private readonly HubNavButton _recover;
        private readonly HubNavButton _export;
        private readonly HubNavButton _delete;
#if !ANDROID
        private readonly HubNavButton _reveal;
#endif
        private readonly Dictionary<string, DemoRecording> _recordings =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ReplayVirtualClipDocument> _virtual =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ReplayLibraryEntry> _entries = new();
        private readonly DispatcherTimer _previewTimer;

        private Bitmap? _bitmap;
        private string[] _previewPaths = Array.Empty<string>();
        private int _previewIndex;
        private string _backdropRoom = "";
        private string? _selected;
        private string? _deleteArmed;

        private readonly record struct ReplayLibraryEntry(
            string Path,
            string Title,
            string Detail,
            DateTime Recorded,
            uint DurationFrames,
            bool IsClip,
            bool Favorite,
            bool Recoverable,
            string SearchText);

        public event EventHandler? Closed;
        public event EventHandler<LaunchPlan>? Launched;

        public HubReplayStudioView()
        {
            Focusable = true;
            Background = Brushes.Transparent;

            ApplyStoragePolicy();

            var root = new Grid
            {
                Margin = new Thickness(24, 20, 24, 32),
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 12
            };

            root.Children.Add(HubChrome.Header(
                "HOME  /  REPLAY STUDIO",
                "REPLAY STUDIO",
                "Find the moment you want, inspect it, then open the cinematic editor.",
                "LOCAL LIBRARY"));

            _search = new DeckField("", widthEms: 0,
                watermark: "Search name, map, mode, player, annotation...");
            _filter = new ChoiceRow("Show",
                new[] { "All", "Full replays", "Clips", "Favorites", "Needs recovery" }, 0);
            _sort = new ChoiceRow("Sort",
                new[] { "Newest", "Oldest", "Name", "Longest" }, 0);
            _summary = new TextBlock
            {
                FontFamily = HubTheme.Data,
                FontSize = 8.5,
                Foreground = HubTheme.TextDimBrush,
                VerticalAlignment = VerticalAlignment.Center
            };

            _search.Box.TextChanged += (_, _) => Populate(_selected);
            _filter.Changed += (_, _) => Populate(_selected);
            _sort.Changed += (_, _) => Populate(_selected);

            _previewTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1100)
            };
            _previewTimer.Tick += (_, _) =>
            {
                if (_previewPaths.Length <= 1)
                    return;
                _previewIndex = (_previewIndex + 1) % _previewPaths.Length;
                ShowPreview();
            };

            _list.SelectionChanged += (_, row) =>
            {
                if (row is UiListRow line && line.Choice is string path)
                    Select(path);
            };
            _list.Activated += (_, row) =>
            {
                if (row is UiListRow line && line.Choice is string path)
                {
                    Select(path);
                    _ = WatchAsync();
                }
            };

            var libraryControls = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("1.45*,*,*"),
                ColumnSpacing = 8
            };
            libraryControls.Children.Add(_search);
            Grid.SetColumn(_filter, 1);
            libraryControls.Children.Add(_filter);
            Grid.SetColumn(_sort, 2);
            libraryControls.Children.Add(_sort);

            var library = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto,*"),
                RowSpacing = 8
            };
            library.Children.Add(libraryControls);
            Grid.SetRow(_summary, 1);
            library.Children.Add(_summary);
            Grid.SetRow(_list, 2);
            library.Children.Add(_list);

            var listPanel = new Border
            {
                Background = HubTheme.PanelBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                Child = library
            };

            var detailStack = new StackPanel
            {
                Margin = new Thickness(14),
                Spacing = 8
            };
            detailStack.Children.Add(new TextBlock
            {
                Text = "SELECTED REPLAY",
                FontFamily = HubTheme.DataBold,
                FontSize = 8,
                Foreground = HubTheme.AccentBrush
            });
            _title = new TextBlock
            {
                Text = "NO REPLAY SELECTED",
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.Bold,
                FontSize = 19,
                Foreground = HubTheme.TextBrush,
                TextWrapping = TextWrapping.Wrap
            };
            _metadata = new TextBlock
            {
                Text = "Record a match or import a replay to begin.",
                FontFamily = HubTheme.Ui,
                FontSize = 10,
                Foreground = HubTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap
            };
            detailStack.Children.Add(_title);
            detailStack.Children.Add(_metadata);

            detailStack.Children.Add(new Border
            {
                Height = 150,
                Background = HubTheme.InkBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Child = _preview
            });

            _rename = new DeckField("", widthEms: 0, watermark: "Display name");
            detailStack.Children.Add(_rename);

            var actions = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
                ColumnSpacing = 6,
                RowSpacing = 6
            };

            _favorite = Action("FAVORITE", "studio.favorite", ToggleFavorite);
            _validate = Action("CHECK INTEGRITY", "studio.validate",
                () => _ = ValidateAsync());
            _recover = Action("RECOVER", "studio.recover", Recover,
                accent: HubTheme.Warm);
            _export = Action("EXPORT", "studio.export", () => _ = ExportAsync());
            var rename = Action("RENAME", "studio.rename", Rename);
            _delete = Action("DELETE", "studio.delete", Delete,
                accent: HubTheme.Danger);
#if !ANDROID
            _reveal = Action("REVEAL FOLDER", "studio.reveal", RevealFolder);
#endif

            HubNavButton[] actionList =
            {
                _favorite, _validate, _recover, _export, rename, _delete
#if !ANDROID
                , _reveal
#endif
            };
            for (int i = 0; i < actionList.Length; i++)
            {
                Grid.SetColumn(actionList[i], i % 2);
                Grid.SetRow(actionList[i], i / 2);
                actions.Children.Add(actionList[i]);
            }
            detailStack.Children.Add(actions);

            var detailPanel = new Border
            {
                Background = HubTheme.PanelStrongBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Child = detailStack
            };

            var body = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("1.12*,0.88*"),
                ColumnSpacing = 12
            };
            body.Children.Add(listPanel);
            Grid.SetColumn(detailPanel, 1);
            body.Children.Add(detailPanel);

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

            var footer = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
                ColumnSpacing = 7
            };
            var back = new HubNavButton("BACK", compact: true);
            ControllerNav.Identify(back, "studio.back");
            back.Click += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
            footer.Children.Add(back);

            var import = new HubNavButton("IMPORT", compact: true);
            ControllerNav.Identify(import, "studio.import");
            import.Click += (_, _) => _ = ImportAsync();
            Grid.SetColumn(import, 1);
            footer.Children.Add(import);

            _status = new TextBlock
            {
                FontFamily = HubTheme.Data,
                FontSize = 8.5,
                Foreground = HubTheme.TextDimBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(_status, 2);
            footer.Children.Add(_status);

            _watch = new HubNavButton("WATCH", compact: true, primary: true)
            {
                IsEnabled = false
            };
            ControllerNav.Identify(_watch, "studio.watch", initial: true);
            _watch.Click += (_, _) => _ = WatchAsync();
            Grid.SetColumn(_watch, 3);
            footer.Children.Add(_watch);

            back.SetValue(ControllerNav.NavRightProperty, "studio.import");
            import.SetValue(ControllerNav.NavLeftProperty, "studio.back");
            import.SetValue(ControllerNav.NavRightProperty, "studio.watch");
            _watch.SetValue(ControllerNav.NavLeftProperty, "studio.import");

            Grid.SetRow(footer, 2);
            root.Children.Add(footer);
            Content = root;
            AttachedToVisualTree += (_, _) =>
            {
                LauncherBackdrop.Set(LauncherBackdropScene.ReplayStudio,
                    _backdropRoom.Length > 0 ? _backdropRoom : null);
                _previewTimer.Start();
            };

            SizeChanged += (_, e) =>
            {
                bool compact = e.NewSize.Width < 760;
                body.ColumnDefinitions = compact
                    ? new ColumnDefinitions("*")
                    : new ColumnDefinitions("1.12*,0.88*");
                body.RowDefinitions = compact
                    ? new RowDefinitions("300,Auto")
                    : new RowDefinitions("*");
                Grid.SetColumn(detailPanel, compact ? 0 : 1);
                Grid.SetRow(detailPanel, compact ? 1 : 0);
                body.RowSpacing = compact ? 10 : 0;
            };

            Reload();
        }

        protected override void OnDetachedFromVisualTree(
            VisualTreeAttachmentEventArgs e)
        {
            _previewTimer.Stop();
            _bitmap?.Dispose();
            _bitmap = null;
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Closed?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (e.Key == Key.F
                && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                _search.Box.Focus();
                _search.Box.SelectAll();
                return;
            }
            base.OnKeyDown(e);
        }

        private HubNavButton Action(string label, string id, System.Action action,
            Color? accent = null)
        {
            var button = new HubNavButton(label, compact: true, accent: accent);
            ControllerNav.Identify(button, id);
            button.Click += (_, _) => action();
            return button;
        }

        private void ApplyStoragePolicy()
        {
            if (!LauncherPrefs.ReplayAutoPrune
                || LauncherPrefs.ReplayStorageLimitGb <= 0)
                return;
            try
            {
                ReplayStorageManager.Apply(new ReplayStoragePolicy(
                    LauncherPrefs.ReplayStorageLimitGb * 1024L * 1024L * 1024L,
                    DeleteFullMatches: true,
                    DeleteMaterializedClips: LauncherPrefs.ReplayDeleteClips,
                    DeleteVirtualClips: false));
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[replay] storage management skipped: {ex.Message}");
            }
        }

        private void Reload(string? preserve = null)
        {
            _recordings.Clear();
            _virtual.Clear();
            _entries.Clear();

            IReadOnlyList<DemoRecording> demos = DemoLibrary.List();
            var demoByPath = demos.ToDictionary(demo => demo.Path,
                StringComparer.OrdinalIgnoreCase);

            foreach (DemoRecording demo in demos)
            {
                _recordings[demo.Path] = demo;
                bool clip = demo.Metadata?.Type == ReplayType.Clip
                    || demo.FileName.Contains("_clip_", StringComparison.OrdinalIgnoreCase);
                bool recoverable = demo.Path.EndsWith(".part",
                        StringComparison.OrdinalIgnoreCase)
                    || demo.Compatibility == ReplayOpenResult.Truncated;
                string people = demo.Metadata == null ? ""
                    : String.Join(" ", demo.Metadata.Players.Select(player => player.Name));
                string mode = demo.Metadata?.Mode.ToString() ?? "";
                string annotations = AnnotationSearchText(demo.Path);
                _entries.Add(new ReplayLibraryEntry(
                    demo.Path,
                    demo.DisplayName,
                    DemoLibrary.Describe(demo),
                    demo.Recorded,
                    demo.DurationFrames,
                    clip,
                    demo.Favorite,
                    recoverable,
                    $"{demo.DisplayName} {demo.Room} {mode} {people} {annotations} {demo.FileName}"));
            }

            foreach (string path in ReplayVirtualClips.List())
            {
                if (!ReplayVirtualClips.TryLoad(path,
                        out ReplayVirtualClipDocument? clip) || clip == null)
                    continue;
                _virtual[path] = clip;
                demoByPath.TryGetValue(clip.SourceReplay, out DemoRecording source);
                string room = source.Path != null ? source.Room : "";
                string people = source.Metadata == null ? ""
                    : String.Join(" ", source.Metadata.Players.Select(player => player.Name));
                string mode = source.Metadata?.Mode.ToString() ?? "";
                uint duration = clip.EndFrame - clip.StartFrame;
                _entries.Add(new ReplayLibraryEntry(
                    path,
                    clip.Name,
                    $"virtual clip / {ReplayHud.Time(duration)}",
                    clip.CreatedUtc.ToLocalTime(),
                    duration,
                    IsClip: true,
                    ReplayVirtualClips.IsFavorite(path),
                    Recoverable: false,
                    $"{clip.Name} {room} {mode} {people} "
                        + $"{AnnotationSearchText(path)} {Path.GetFileName(clip.SourceReplay)}"));
            }

            Populate(preserve ?? _selected);
        }

        private void Populate(string? preserve = null)
        {
            string query = _search.Value.Trim();
            IEnumerable<ReplayLibraryEntry> filtered = _entries;
            if (query.Length > 0)
            {
                filtered = filtered.Where(entry =>
                    entry.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            filtered = _filter.Index switch
            {
                1 => filtered.Where(entry => !entry.IsClip),
                2 => filtered.Where(entry => entry.IsClip),
                3 => filtered.Where(entry => entry.Favorite),
                4 => filtered.Where(entry => entry.Recoverable),
                _ => filtered
            };
            filtered = _sort.Index switch
            {
                1 => filtered.OrderBy(entry => entry.Recorded),
                2 => filtered.OrderBy(entry => entry.Title,
                    StringComparer.OrdinalIgnoreCase),
                3 => filtered.OrderByDescending(entry => entry.DurationFrames),
                _ => filtered.OrderByDescending(entry => entry.Recorded)
            };
            ReplayLibraryEntry[] shown = filtered.ToArray();

            _list.Clear();
            foreach (ReplayLibraryEntry entry in shown)
            {
                _list.Add(new UiListRow(
                    (entry.Favorite ? "★ " : "") + entry.Title,
                    entry.Detail)
                {
                    Choice = entry.Path
                }, _ => Select(entry.Path));
            }

            if (shown.Length == 0)
            {
                _summary.Text = _entries.Count == 0
                    ? "EMPTY LIBRARY"
                    : $"0 OF {_entries.Count} ITEMS";
                _list.AddNote(_entries.Count == 0
                    ? "No recordings yet. Record a match or import a replay."
                    : "No replays match the current search and filters.",
                    HubTheme.TextDim);
                Select(null);
                return;
            }

            string? selected = preserve != null
                && shown.Any(entry => String.Equals(entry.Path, preserve,
                    StringComparison.OrdinalIgnoreCase))
                    ? preserve : shown[0].Path;
            _summary.Text = $"{shown.Length} OF {_entries.Count} ITEMS  /  "
                + $"{shown.Count(entry => entry.Favorite)} FAVORITES";
            _list.SelectTag(selected);
            Select(selected);
        }

        private static string AnnotationSearchText(string path)
            => String.Join(" ", ReplayAnnotations.Bookmarks(path)
                .Select(bookmark => bookmark.Name)
                .Concat(ReplayAnnotations.Highlights(path)
                    .Select(highlight => highlight.Name)));

        private static string AnnotationSummary(string path)
        {
            int bookmarks = ReplayAnnotations.Bookmarks(path).Count;
            int highlights = ReplayAnnotations.Highlights(path).Count;
            if (bookmarks == 0 && highlights == 0)
                return "";
            return $"\n{bookmarks} bookmark{(bookmarks == 1 ? "" : "s")} / "
                + $"{highlights} named highlight{(highlights == 1 ? "" : "s")}";
        }

        private void Select(string? path)
        {
            _selected = path;
            _deleteArmed = null;
            _delete.Label = "DELETE";
            _status.Foreground = HubTheme.TextDimBrush;

            if (path == null)
            {
                _title.Text = "NO REPLAY SELECTED";
                _metadata.Text = "Record a match or import a replay to begin.";
                _rename.Value = "";
                _favorite.Label = "FAVORITE";
                _watch.IsEnabled = false;
                SetActionState(false, interrupted: false);
                SetPreview(null);
                return;
            }

            _watch.IsEnabled = !path.EndsWith(".part",
                StringComparison.OrdinalIgnoreCase);
            bool interrupted = path.EndsWith(".part",
                StringComparison.OrdinalIgnoreCase);
            SetActionState(true, interrupted);

            if (_virtual.TryGetValue(path, out ReplayVirtualClipDocument? clip))
            {
                _title.Text = clip.Name.ToUpperInvariant();
                _rename.Value = clip.Name;
                _metadata.Text =
                    $"VIRTUAL CLIP  /  {ReplayHud.Time(clip.EndFrame - clip.StartFrame)}\n"
                    + $"{ReplayHud.Time(clip.StartFrame)} – {ReplayHud.Time(clip.EndFrame)}\n"
                    + $"SOURCE  {Path.GetFileName(clip.SourceReplay)}"
                    + AnnotationSummary(path);
                _favorite.Label = ReplayVirtualClips.IsFavorite(path)
                    ? "UNFAVORITE" : "FAVORITE";
                SetPreview(RoomForSource(clip.SourceReplay), clip.SourceReplay);
                return;
            }

            if (_recordings.TryGetValue(path, out DemoRecording demo))
            {
                _title.Text = demo.DisplayName.ToUpperInvariant();
                _rename.Value = demo.DisplayName;
                _metadata.Text = $"{DemoLibrary.Describe(demo)}\n"
                    + DemoLibrary.Details(demo)
                    + AnnotationSummary(path);
                _favorite.Label = demo.Favorite ? "UNFAVORITE" : "FAVORITE";
                SetPreview(demo.Room, demo.Path);
                return;
            }

            _title.Text = Path.GetFileName(path).ToUpperInvariant();
            _rename.Value = Path.GetFileNameWithoutExtension(path);
            _metadata.Text = interrupted
                ? "INTERRUPTED RECORDING\nRecover this file before playback."
                : path;
            _favorite.Label = "FAVORITE";
            SetPreview(null, path);
        }

        private void SetActionState(bool selected, bool interrupted)
        {
            _favorite.IsEnabled = selected && !interrupted;
            _validate.IsEnabled = selected;
            _recover.IsVisible = interrupted;
            _recover.IsEnabled = interrupted;
            _export.IsEnabled = selected && !interrupted;
            _delete.IsEnabled = selected;
#if !ANDROID
            _reveal.IsEnabled = selected;
#endif
        }

        private string? RoomForSource(string source)
            => _recordings.TryGetValue(source, out DemoRecording demo)
                ? demo.Room : null;

        private void SetPreview(string? room, string? replay = null)
        {
            _preview.Source = null;
            _bitmap?.Dispose();
            _bitmap = null;
            _previewIndex = 0;
            _previewPaths = Array.Empty<string>();
            _backdropRoom = room?.Trim() ?? "";
            LauncherBackdrop.Set(LauncherBackdropScene.ReplayStudio,
                _backdropRoom.Length > 0 ? _backdropRoom : null);

            if (!String.IsNullOrWhiteSpace(replay))
                _previewPaths = ReplayVideoExporter.Thumbnails(replay);

            if (_previewPaths.Length == 0 && _backdropRoom.Length > 0)
            {
                try
                {
                    string fallback = ThumbnailGenerator.PathFor(_backdropRoom);
                    if (File.Exists(fallback))
                        _previewPaths = new[] { fallback };
                }
                catch
                {
                    // Replay metadata remains useful even without a thumbnail.
                }
            }
            ShowPreview();
        }

        private void ShowPreview()
        {
            _preview.Source = null;
            _bitmap?.Dispose();
            _bitmap = null;
            if (_previewPaths.Length == 0)
                return;

            try
            {
                string path = _previewPaths[
                    Math.Clamp(_previewIndex, 0, _previewPaths.Length - 1)];
                using var stream = new MemoryStream(File.ReadAllBytes(path));
                _bitmap = new Bitmap(stream);
                _preview.Source = _bitmap;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException
                or ArgumentException)
            {
                // A partially written still should not take down the library.
            }
        }

        private void Rename()
        {
            if (_selected is not string path)
                return;
            try
            {
                if (_virtual.ContainsKey(path))
                    ReplayVirtualClips.Rename(path, _rename.Value);
                else
                    DemoLibrary.Rename(path, _rename.Value);
                _status.Text = "RENAMED";
                _status.Foreground = HubTheme.GoodBrush;
                Reload(path);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException
                or ArgumentException)
            {
                Fail(ex.Message);
            }
        }

        private void ToggleFavorite()
        {
            if (_selected is not string path)
                return;
            try
            {
                if (_virtual.ContainsKey(path))
                    ReplayVirtualClips.ToggleFavorite(path);
                else
                    DemoLibrary.ToggleFavorite(path);
                Reload(path);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                Fail(ex.Message);
            }
        }

        private async Task ValidateAsync()
        {
            if (_selected is not string path)
                return;
            _status.Text = "CHECKING INTEGRITY";
            string target = path;
            bool virtualClip = _virtual.ContainsKey(path);
            if (virtualClip)
            {
                (string? resolved, ReplayOpenResult openResult) =
                    await Task.Run(() =>
                    {
                        string? output = ReplayVirtualClips.ResolveForPlayback(
                            path, out ReplayOpenResult result);
                        return (output, result);
                    });
                if (resolved == null)
                {
                    Fail($"Integrity check failed: {openResult}");
                    return;
                }
                target = resolved;
            }

            ReplayOpenResult result =
                await Task.Run(() => ReplayArchive.Validate(target));
            if (!virtualClip)
                DemoLibrary.NoteValidation(path, result);
            _status.Text = $"INTEGRITY  {result}".ToUpperInvariant();
            _status.Foreground = result == ReplayOpenResult.Success
                ? HubTheme.GoodBrush : HubTheme.WarmBrush;
            Reload(path);
        }

        private void Recover()
        {
            if (_selected is not string path
                || !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                return;
            ReplayArchive.Recover(path, out string? output,
                out ReplayOpenResult result);
            _status.Text = output == null
                ? $"RECOVERY FAILED  {result}".ToUpperInvariant()
                : $"RECOVERED  {Path.GetFileName(output)}".ToUpperInvariant();
            _status.Foreground = output == null
                ? HubTheme.DangerBrush : HubTheme.GoodBrush;
            Reload(output);
        }

        private void Delete()
        {
            if (_selected is not string path)
                return;
            if (_deleteArmed != path)
            {
                _deleteArmed = path;
                _delete.Label = "DELETE AGAIN";
                _status.Text = "PRESS DELETE AGAIN TO CONFIRM";
                return;
            }
            try
            {
                if (_virtual.ContainsKey(path))
                    ReplayVirtualClips.Delete(path);
                else
                    DemoLibrary.Delete(path);
                _status.Text = "DELETED";
                _status.Foreground = HubTheme.GoodBrush;
                Reload();
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                Fail(ex.Message);
            }
        }

        private async Task WatchAsync()
        {
            if (_selected is not string path)
                return;
            if (path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            {
                Fail("Recover the interrupted recording before watching it.");
                return;
            }

            string source = path;
            if (_virtual.ContainsKey(path))
            {
                _watch.IsEnabled = false;
                _watch.Label = "PREPARING";
                (string? resolved, ReplayOpenResult openResult) =
                    await Task.Run(() =>
                    {
                        string? output = ReplayVirtualClips.ResolveForPlayback(
                            path, out ReplayOpenResult result);
                        return (output, result);
                    });
                _watch.Label = "WATCH";
                _watch.IsEnabled = true;
                if (resolved == null)
                {
                    Fail($"Could not prepare virtual clip: {openResult}");
                    return;
                }
                source = resolved;
            }

            _watch.IsEnabled = false;
            _watch.Label = "LOADING";
            bool joined = await Task.Run(() => DemoPlayback.Join(source));
            _watch.Label = "WATCH";
            _watch.IsEnabled = true;
            if (!joined)
            {
                Fail(DemoPlayback.LastError
                    ?? "That file could not be read as a replay.");
                return;
            }

            Launched?.Invoke(this, new LaunchPlan
            {
                Kind = LaunchKind.Demo,
                DemoPath = source,
                Hunter = Hunter.Samus,
                PlayerName = "",
                RoomKey = ""
            });
        }

        private async Task ExportAsync()
        {
            if (_selected is not string path)
                return;
            string source = path;
            if (_virtual.ContainsKey(path))
            {
                (string? resolved, ReplayOpenResult result) =
                    await Task.Run(() =>
                    {
                        string? output = ReplayVirtualClips.ResolveForPlayback(
                            path, out ReplayOpenResult open);
                        return (output, open);
                    });
                if (resolved == null)
                {
                    Fail($"Export failed: {result}");
                    return;
                }
                source = resolved;
            }

#if !ANDROID
            try
            {
                string directory = Path.Combine(DemoLibrary.Directory, "exports");
                Directory.CreateDirectory(directory);
                string destination = Path.Combine(directory,
                    Path.GetFileNameWithoutExtension(path)
                    + $"_{Guid.NewGuid():N}{DemoFile.Extension}");
                File.Copy(source, destination, overwrite: false);
                _status.Text = $"EXPORTED  {destination}";
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException
                or ArgumentException)
            {
                Fail("Export failed: " + ex.Message);
            }
            await Task.CompletedTask;
#else
            if (TopLevel.GetTopLevel(this) is not TopLevel top)
                return;
            try
            {
                IStorageFile? target = await top.StorageProvider.SaveFilePickerAsync(
                    new FilePickerSaveOptions
                    {
                        Title = "Export replay",
                        SuggestedFileName =
                            Path.GetFileNameWithoutExtension(path) + DemoFile.Extension,
                        DefaultExtension = DemoFile.Extension.TrimStart('.')
                    });
                if (target == null)
                    return;
                await using Stream output = await target.OpenWriteAsync();
                using Stream input = File.OpenRead(source);
                await input.CopyToAsync(output);
                _status.Text = "REPLAY EXPORTED";
            }
            catch (Exception ex)
            {
                Fail("Export failed: " + ex.Message);
            }
#endif
        }

        private async Task ImportAsync()
        {
            TopLevel? top = TopLevel.GetTopLevel(this);
            if (top == null)
                return;

            if (!top.StorageProvider.CanOpen)
            {
                if (!NativeFilePicker.Available)
                {
                    Fail("No desktop file dialog is available. Install zenity or kdialog.");
                    return;
                }
                string? path = await NativeFilePicker.OpenFile(
                    "Import replay", $"{Branding.Name} replay",
                    DemoFile.Extension.TrimStart('.'));
                if (path != null)
                {
                    _selected = path;
                    await WatchAsync();
                }
                return;
            }

            var options = new FilePickerOpenOptions
            {
                Title = "Import replay",
                AllowMultiple = false
            };
            if (!OperatingSystem.IsAndroid())
            {
                options.FileTypeFilter = new[]
                {
                    new FilePickerFileType($"{Branding.Name} replay")
                    {
                        Patterns = new[] { $"*{DemoFile.Extension}" }
                    },
                    new FilePickerFileType("Every file")
                    {
                        Patterns = new[] { "*" }
                    }
                };
            }

            IReadOnlyList<IStorageFile> files =
                await top.StorageProvider.OpenFilePickerAsync(options);
            if (files.Count == 0)
                return;

            string? local = files[0].TryGetLocalPath();
            if (local == null)
            {
                try
                {
                    Directory.CreateDirectory(DemoLibrary.Directory);
                    local = Path.Combine(DemoLibrary.Directory,
                        $"imported_{Guid.NewGuid():N}{DemoFile.Extension}");
                    await using Stream source = await files[0].OpenReadAsync();
                    await using var target = File.Create(local);
                    await source.CopyToAsync(target);
                }
                catch (Exception ex)
                {
                    Fail("Import failed: " + ex.Message);
                    return;
                }
            }

            _selected = local;
            await WatchAsync();
        }

#if !ANDROID
        private void RevealFolder()
        {
            if (_selected is not string path)
                return;
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(
                        Path.GetDirectoryName(path)!)
                    {
                        UseShellExecute = true
                    });
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
            }
        }
#endif

        private void Fail(string message)
        {
            _status.Text = message.ToUpperInvariant();
            _status.Foreground = HubTheme.DangerBrush;
        }
    }
}
#endif
