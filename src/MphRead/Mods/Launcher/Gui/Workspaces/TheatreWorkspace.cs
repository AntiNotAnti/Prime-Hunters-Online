#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
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
    internal sealed class TheatreWorkspace : UserControl, IDisposable
    {
        private readonly ListBox _list = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()) };
        private readonly TextBlock _title;
        private readonly TextBlock _metadata;
        private readonly TextBlock _status;
        private readonly TextBlock _summary;
        private readonly Image _preview = new() { Stretch = Stretch.UniformToFill };
        private readonly DeckField _search;
        private readonly ChoiceRow _filter;
        private readonly ChoiceRow _sort;
        private readonly DeckField _rename;
        private readonly DeckField _tags;
        private readonly DeckField _collections;
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
        private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
        private long _libraryGeneration;

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
            bool Annotated,
            bool Organized,
            string Room,
            string Players,
            string SearchText);

        public event EventHandler? Closed;
        public Func<bool>? CanLaunch { get; set; }
        private Control? _libraryRoot;
        public bool EditorActive { get; private set; }
        public event Action? EditorChanged;
        public void ShowEditor(Action close, Action fullscreen)
        {
            if (EditorActive) return;
            _libraryRoot = Content as Control;
            EditorActive = true;
            var editor = new ReplayControlsView(shell: true);
            editor.Closed += (_, _) => close();
            editor.ResumeRequested += (_, _) => fullscreen();
            var viewport = new Grid { RowDefinitions = new("Auto,*,Auto") };
            viewport.Children.Add(new PrimeBadge("REPLAY VIEWPORT // CINEMATIC EDITOR"));
            var actions = PrimeChrome.Columns("*,*", new PrimeButton("BACK TO ARCHIVE", close),
                new PrimeButton("FULLSCREEN PLAYBACK", fullscreen));
            Grid.SetRow(actions, 2); viewport.Children.Add(actions);
            Content = PrimeChrome.Columns("1.7*,1*", viewport, new PrimePanel(editor));
            EditorChanged?.Invoke();
        }
        public void CloseEditor()
        {
            if (!EditorActive) return;
            EditorActive = false; Content = _libraryRoot; _libraryRoot = null;
            EditorChanged?.Invoke();
        }
        public event EventHandler<LaunchPlan>? Launched;

        public TheatreWorkspace(bool manageStorage = true)
        {
            Focusable = true;
            Background = Brushes.Transparent;

            if (manageStorage) _ = ReplayStorageJobs.Run(() => { ApplyStoragePolicy(); return true; });

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
            _filter = new ChoiceRow("Smart view",
                new[] { "All", "Full replays", "Clips", "Favorites", "Recent 7 days",
                    "Same map", "Same players", "Annotated", "Tagged / collected",
                    "Needs recovery" }, 0);
            _sort = new ChoiceRow("Sort",
                new[] { "Newest", "Oldest", "Name", "Longest" }, 0);
            _summary = new TextBlock
            {
                FontFamily = HubTheme.Data,
                FontSize = 8.5,
                Foreground = HubTheme.TextDimBrush,
                VerticalAlignment = VerticalAlignment.Center
            };

            _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Populate(_selected); };
            _search.Box.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
            _filter.Changed += (_, _) => Populate(_selected);
            _sort.Changed += (_, _) => Populate(_selected);


            _list.ItemTemplate = new FuncDataTemplate<ReplayLibraryEntry>((entry, scope) =>
            {
                var row = new UiListRow((entry.Favorite ? "★ " : "") + entry.Title, entry.Detail)
                    { Choice = entry.Path, Focusable = false };
                row.Clicked += (_, _) => _list.SelectedItem = entry;
                row.Activated += (_, _) => { _list.SelectedItem = entry; Select(entry.Path); _ = WatchAsync(); };
                return row;
            });
            _list.SelectionChanged += (_, _) =>
            {
                if (_list.SelectedItem is ReplayLibraryEntry entry) Select(entry.Path);
            };
            _list.KeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Space && _list.SelectedItem is ReplayLibraryEntry entry)
                { Select(entry.Path); _ = WatchAsync(); e.Handled = true; }
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
                FontFamily = PrimeTypography.Display,
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
            _tags = new DeckField("", widthEms: 0,
                watermark: "Tags, comma separated");
            _collections = new DeckField("", widthEms: 0,
                watermark: "Collections, comma separated");
            detailStack.Children.Add(_tags);
            detailStack.Children.Add(_collections);

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
            var organize = Action("SAVE TAGS", "studio.organize", SaveOrganization);
            _delete = Action("DELETE", "studio.delete", Delete,
                accent: HubTheme.Danger);
#if !ANDROID
            _reveal = Action("REVEAL FOLDER", "studio.reveal", RevealFolder);
#endif

            HubNavButton[] actionList =
            {
                _favorite, _validate, _recover, _export, rename, organize, _delete
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

            detailStack.Children.Remove(_title); detailStack.Children.Remove(_metadata);
            if (_preview.Parent is Border previewFrame) { previewFrame.Child = null; detailStack.Children.Remove(previewFrame); }
            // Thumbnail is a preview; live transport becomes available in the editor.
            var viewer = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 12 };
            viewer.Children.Add(_title);
            Grid.SetRow(_preview, 1); viewer.Children.Add(_preview);
            Grid.SetRow(_metadata, 2); viewer.Children.Add(_metadata);
            library.Children.Remove(libraryControls);
            libraryControls.ColumnDefinitions = new("1.6*,1*,1*");
            var body = PrimeChrome.Columns("1*,1.25*,1*", listPanel, new PrimePanel(viewer),
                new ScrollViewer { Content = detailPanel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            var archive = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 12 };
            archive.Children.Add(libraryControls); Grid.SetRow(body, 1); archive.Children.Add(body);
            Grid.SetRow(archive, 1); root.Children.Add(archive);

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

            _watch = new PrimeButton("LAUNCH CINEMATIC EDITOR", primary: true)
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
                if (_bitmap == null)
                    ShowPreview();
                LauncherBackdrop.Set(LauncherBackdropScene.ReplayStudio,
                    _backdropRoom.Length > 0 ? _backdropRoom : null);
                // Static preview until an explicit selection changes; no idle slideshow timer.
            };

            if (manageStorage) Reload();
        }

        public void Dispose()
        {
            _searchTimer.Stop(); _libraryGeneration++; _recoveryCancellation?.Cancel();
            // A retained launcher view can be measured again on return from
            // playback. Detach the image before releasing its native bitmap.
            _preview.Source = null;
            _bitmap?.Dispose();
            _bitmap = null;
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

        internal int MaximumRealizedRows { get; private set; }
        internal int ShownCount => (_list.ItemsSource as ReplayLibraryEntry[])?.Length ?? 0;
        internal void LoadCheckEntries(int count)
        {
            _libraryGeneration++; _entries.Clear();
            for (int i = 0; i < count; i++)
            {
                string name = $"Replay {i:D5}", path = Path.Combine(Path.GetTempPath(), "prime-library-check-model", name + ".ppdemo");
                _entries.Add(new(path, name, "Battle · 05:00", DateTime.UnixEpoch.AddMinutes(i), 18000,
                    false, i % 10 == 0, false, false, false, "Test arena", "Players", name));
            }
            _list.LayoutUpdated += (_, _) => MaximumRealizedRows = Math.Max(MaximumRealizedRows,
                _list.GetVisualDescendants().OfType<UiListRow>().Count());
            Populate();
        }
        internal void SearchCheck(string text) { _search.Value = text; _searchTimer.Stop(); Populate(_selected); }

        private async void Reload(string? preserve = null)
        {
            long generation = ++_libraryGeneration;
            string? selection = preserve ?? _selected;
            _summary.Text = "SCANNING LIBRARY...";
            try
            {
                var snapshot = await ReplayStorageJobs.Run(ScanLibrary);
                if (generation != _libraryGeneration) return;
                _recordings.Clear(); foreach (var pair in snapshot.Recordings) _recordings.Add(pair.Key, pair.Value);
                _virtual.Clear(); foreach (var pair in snapshot.Clips) _virtual.Add(pair.Key, pair.Value);
                _entries.Clear(); _entries.AddRange(snapshot.Entries);
                Populate(preserve ?? _selected ?? selection);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { if (generation == _libraryGeneration) Fail("Could not load replay library: " + ex.Message); }
        }
        private sealed record LibrarySnapshot(Dictionary<string, DemoRecording> Recordings,
            Dictionary<string, ReplayVirtualClipDocument> Clips, List<ReplayLibraryEntry> Entries);
        private static LibrarySnapshot ScanLibrary()
        {
            var recordings = new Dictionary<string, DemoRecording>(StringComparer.OrdinalIgnoreCase);
            var virtualClips = new Dictionary<string, ReplayVirtualClipDocument>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<ReplayLibraryEntry>();
            IReadOnlyList<DemoRecording> demos = DemoLibrary.List();
            var demoByPath = demos.ToDictionary(demo => demo.Path,
                StringComparer.OrdinalIgnoreCase);

            foreach (DemoRecording demo in demos)
            {
                recordings[demo.Path] = demo;
                bool clip = demo.Metadata?.Type == ReplayType.Clip
                    || demo.FileName.Contains("_clip_", StringComparison.OrdinalIgnoreCase);
                bool recoverable = demo.Path.EndsWith(".part",
                        StringComparison.OrdinalIgnoreCase)
                    || demo.Compatibility == ReplayOpenResult.Truncated;
                string people = demo.Metadata == null ? ""
                    : String.Join(" ", demo.Metadata.Players.Select(player => player.Name));
                string mode = demo.Metadata?.Mode.ToString() ?? "";
                string annotations = AnnotationSearchText(demo.Path);
                string organization = OrganizationSearchText(demo.Path);
                bool annotated = annotations.Length > 0;
                bool organized = organization.Length > 0;
                entries.Add(new ReplayLibraryEntry(
                    demo.Path,
                    demo.DisplayName,
                    DemoLibrary.Describe(demo),
                    demo.Recorded,
                    demo.DurationFrames,
                    clip,
                    demo.Favorite,
                    recoverable,
                    annotated,
                    organized,
                    demo.Room,
                    people,
                    $"{demo.DisplayName} {demo.Room} {mode} {people} "
                        + $"{annotations} {organization} {demo.FileName}"));
            }

            foreach (string path in ReplayVirtualClips.List())
            {
                if (!ReplayVirtualClips.TryLoad(path,
                        out ReplayVirtualClipDocument? clip) || clip == null)
                    continue;
                virtualClips[path] = clip;
                demoByPath.TryGetValue(clip.SourceReplay, out DemoRecording source);
                string room = source.Path != null ? source.Room : "";
                string people = source.Metadata == null ? ""
                    : String.Join(" ", source.Metadata.Players.Select(player => player.Name));
                string mode = source.Metadata?.Mode.ToString() ?? "";
                uint duration = clip.EndFrame - clip.StartFrame;
                string annotations = AnnotationSearchText(path);
                string organization = OrganizationSearchText(path);
                entries.Add(new ReplayLibraryEntry(
                    path,
                    clip.Name,
                    $"virtual clip / {ReplayHud.Time(duration)}",
                    clip.CreatedUtc.ToLocalTime(),
                    duration,
                    IsClip: true,
                    ReplayVirtualClips.IsFavorite(path),
                    Recoverable: false,
                    Annotated: annotations.Length > 0,
                    Organized: organization.Length > 0,
                    room,
                    people,
                    $"{clip.Name} {room} {mode} {people} "
                        + $"{annotations} {organization} {Path.GetFileName(clip.SourceReplay)}"));
            }

            return new(recordings, virtualClips, entries);
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

            DateTime recent = DateTime.Now.AddDays(-7);
            ReplayLibraryEntry anchor = _entries.FirstOrDefault(entry =>
                preserve != null && String.Equals(entry.Path, preserve,
                    StringComparison.OrdinalIgnoreCase));
            string anchorRoom = anchor.Room ?? "";
            string anchorPlayers = anchor.Players ?? "";
            filtered = _filter.Index switch
            {
                1 => filtered.Where(entry => !entry.IsClip),
                2 => filtered.Where(entry => entry.IsClip),
                3 => filtered.Where(entry => entry.Favorite),
                4 => filtered.Where(entry => entry.Recorded >= recent),
                5 => anchorRoom.Length == 0 ? filtered
                    : filtered.Where(entry => String.Equals(entry.Room, anchorRoom,
                        StringComparison.OrdinalIgnoreCase)),
                6 => anchorPlayers.Length == 0 ? filtered
                    : filtered.Where(entry => SharesPlayer(entry.Players, anchorPlayers)),
                7 => filtered.Where(entry => entry.Annotated),
                8 => filtered.Where(entry => entry.Organized),
                9 => filtered.Where(entry => entry.Recoverable),
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

            _list.ItemsSource = shown;

            if (shown.Length == 0)
            {
                _summary.Text = _entries.Count == 0
                    ? "EMPTY LIBRARY"
                    : $"0 OF {_entries.Count} ITEMS";
                Select(null);
                return;
            }

            string? selected = preserve != null
                && shown.Any(entry => String.Equals(entry.Path, preserve,
                    StringComparison.OrdinalIgnoreCase))
                    ? preserve : shown[0].Path;
            _summary.Text = $"{shown.Length} OF {_entries.Count} ITEMS  /  "
                + $"{shown.Count(entry => entry.Favorite)} FAVORITES";
            _list.SelectedItem = shown.First(entry => entry.Path == selected);
            _list.ScrollIntoView(_list.SelectedItem);
            Select(selected);
        }

        private static string AnnotationSearchText(string path)
            => String.Join(" ", ReplayAnnotations.Bookmarks(path)
                .Select(bookmark => bookmark.Name)
                .Concat(ReplayAnnotations.Highlights(path)
                    .Select(highlight => highlight.Name)));

        private static bool SharesPlayer(string left, string right)
        {
            if (String.IsNullOrWhiteSpace(left) || String.IsNullOrWhiteSpace(right))
                return false;
            string[] names = right.Split(' ',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return names.Any(name => left.Contains(name,
                StringComparison.OrdinalIgnoreCase));
        }

        private static string OrganizationSearchText(string path)
            => String.Join(" ", ReplayAnnotations.Tags(path)
                .Concat(ReplayAnnotations.Collections(path)));

        private static string OrganizationSummary(string path)
        {
            string tags = String.Join(", ", ReplayAnnotations.Tags(path));
            string collections = String.Join(", ", ReplayAnnotations.Collections(path));
            if (tags.Length == 0 && collections.Length == 0)
                return "";
            return "\n"
                + (tags.Length == 0 ? "" : $"TAGS  {tags}")
                + (tags.Length > 0 && collections.Length > 0 ? "\n" : "")
                + (collections.Length == 0 ? "" : $"COLLECTIONS  {collections}");
        }

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
                _tags.Value = "";
                _collections.Value = "";
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
                _tags.Value = String.Join(", ", ReplayAnnotations.Tags(path));
                _collections.Value = String.Join(", ", ReplayAnnotations.Collections(path));
                _metadata.Text =
                    $"VIRTUAL CLIP  /  {ReplayHud.Time(clip.EndFrame - clip.StartFrame)}\n"
                    + $"{ReplayHud.Time(clip.StartFrame)} – {ReplayHud.Time(clip.EndFrame)}\n"
                    + $"SOURCE  {Path.GetFileName(clip.SourceReplay)}"
                    + AnnotationSummary(path)
                    + OrganizationSummary(path);
                _favorite.Label = ReplayVirtualClips.IsFavorite(path)
                    ? "UNFAVORITE" : "FAVORITE";
                SetPreview(RoomForSource(clip.SourceReplay), clip.SourceReplay);
                return;
            }

            if (_recordings.TryGetValue(path, out DemoRecording demo))
            {
                _title.Text = demo.DisplayName.ToUpperInvariant();
                _rename.Value = demo.DisplayName;
                _tags.Value = String.Join(", ", ReplayAnnotations.Tags(path));
                _collections.Value = String.Join(", ", ReplayAnnotations.Collections(path));
                _metadata.Text = $"{DemoLibrary.Describe(demo)}\n"
                    + DemoLibrary.Details(demo)
                    + AnnotationSummary(path)
                    + OrganizationSummary(path);
                _favorite.Label = demo.Favorite ? "UNFAVORITE" : "FAVORITE";
                SetPreview(demo.Room, demo.Path);
                return;
            }

            _title.Text = Path.GetFileName(path).ToUpperInvariant();
            _rename.Value = Path.GetFileNameWithoutExtension(path);
            _tags.Value = "";
            _collections.Value = "";
            _metadata.Text = interrupted
                ? "INTERRUPTED RECORDING\nRecover this file before playback."
                : path;
            _favorite.Label = "FAVORITE";
            SetPreview(null, path);
        }

        private void SetActionState(bool selected, bool interrupted)
        {
            _favorite.IsEnabled = selected && !interrupted;
            _tags.IsEnabled = selected && !interrupted;
            _collections.IsEnabled = selected && !interrupted;
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

        private void SaveOrganization()
        {
            if (_selected is not string path
                || path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                ReplayAnnotations.SetOrganization(path,
                    SplitLabels(_tags.Value), SplitLabels(_collections.Value));
                _status.Text = "ORGANIZATION SAVED";
                _status.Foreground = HubTheme.GoodBrush;
                Reload(path);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or ArgumentException)
            {
                Fail(ex.Message);
            }
        }

        private static IEnumerable<string> SplitLabels(string value)
            => value.Split(',', StringSplitOptions.TrimEntries
                | StringSplitOptions.RemoveEmptyEntries);

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
                    await ReplayStorageJobs.Run(() =>
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
                await ReplayStorageJobs.Run(() => ReplayArchive.Validate(target));
            if (!virtualClip)
                DemoLibrary.NoteValidation(path, result);
            _status.Text = $"INTEGRITY  {result}".ToUpperInvariant();
            _status.Foreground = result == ReplayOpenResult.Success
                ? HubTheme.GoodBrush : HubTheme.WarmBrush;
            Reload(path);
        }

        private bool _recovering;
        private System.Threading.CancellationTokenSource? _recoveryCancellation;
        private async void Recover()
        {
            if (_selected is not string path
                || !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                return;
            if (_recovering) { _recoveryCancellation?.Cancel(); return; }
            using var cancellation = new System.Threading.CancellationTokenSource();
            _recoveryCancellation = cancellation; _recover.Label = "CANCEL RECOVERY";
            _recovering = true; _status.Text = "RECOVERING...";
            string? output; ReplayOpenResult result;
            try
            {
                (output, result) = await ReplayStorageJobs.Run(() =>
                { ReplayArchive.Recover(path, out string? recovered, out var status, cancellation.Token); return (recovered, status); }, cancellation.Token);
            }
            catch (OperationCanceledException) { _status.Text = "RECOVERY CANCELLED"; return; }
            catch (Exception ex) { if (_selected == path && TopLevel.GetTopLevel(this) != null) Fail("Recovery failed: " + ex.Message); return; }
            finally { _recovering = false; _recoveryCancellation = null; _recover.Label = "RECOVER"; }
            if (_selected != path || TopLevel.GetTopLevel(this) == null) return;
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

        private readonly record struct ReplayLaunchProbe(
            ReplayOpenResult Result, string? Error);

        private static ReplayLaunchProbe ProbeReplayLaunch(string source)
        {
            using DemoReader? reader = DemoReader.Open(source,
                out ReplayOpenResult result, metadataOnly: true);
            if (reader == null)
                return new(result, $"Cannot open replay: {result}.");

            if (reader.ProtocolVersion != NetConfig.ProtocolVersion)
            {
                return new(ReplayOpenResult.ProtocolMismatch,
                    $"This replay uses network protocol {reader.ProtocolVersion}. "
                    + $"This build uses protocol {NetConfig.ProtocolVersion}.");
            }

            if (reader.Metadata is ReplayMetadata metadata)
            {
                result = ReplayMapIdentity.Validate(metadata);
                if (result != ReplayOpenResult.Success)
                    return new(result, $"Cannot load replay map: {result}.");
            }

            return new(ReplayOpenResult.Success, null);
        }

        private async Task WatchAsync()
        {
            if (CanLaunch?.Invoke() == false) return;
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
                    await ReplayStorageJobs.Run(() =>
                    {
                        string? output = ReplayVirtualClips.ResolveForPlayback(
                            path, out ReplayOpenResult result);
                        return (output, result);
                    });
                _watch.Label = "LAUNCH CINEMATIC EDITOR";
                _watch.IsEnabled = true;
                if (resolved == null)
                {
                    Fail($"Could not prepare virtual clip: {openResult}");
                    return;
                }
                source = resolved;
            }

            _watch.IsEnabled = false;
            _watch.Label = "CHECKING";
            ReplayLaunchProbe probe =
                await ReplayStorageJobs.Run(() => ProbeReplayLaunch(source));
            _watch.Label = "LAUNCH CINEMATIC EDITOR";
            _watch.IsEnabled = true;
            if (probe.Result != ReplayOpenResult.Success)
            {
                Fail(probe.Error
                    ?? $"That replay cannot be opened: {probe.Result}.");
                return;
            }

            _status.Text = "OPENING CINEMATIC EDITOR";
            _status.Foreground = HubTheme.GoodBrush;
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
                    await ReplayStorageJobs.Run(() =>
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
                string destination = Path.Combine(directory,
                    Path.GetFileNameWithoutExtension(path)
                    + $"_{Guid.NewGuid():N}{DemoFile.Extension}");
                await ReplayStorageJobs.Run(() => { Directory.CreateDirectory(directory); File.Copy(source, destination, overwrite: false); return true; });
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
