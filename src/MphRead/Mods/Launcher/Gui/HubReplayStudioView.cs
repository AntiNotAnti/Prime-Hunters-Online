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
        private readonly Image _preview = new() { Stretch = Stretch.UniformToFill };
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

        private Bitmap? _bitmap;
        private string? _selected;
        private string? _deleteArmed;

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

            var header = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };
            var heading = new StackPanel { Spacing = 2 };
            heading.Children.Add(new TextBlock
            {
                Text = "REPLAY STUDIO",
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.Bold,
                FontSize = 26,
                Foreground = HubTheme.TextBrush
            });
            heading.Children.Add(new TextBlock
            {
                Text = "RECORDINGS  /  CLIPS  /  CINEMATIC EDITING",
                FontFamily = HubTheme.DataBold,
                FontSize = 8.5,
                Foreground = HubTheme.AccentBrush
            });
            header.Children.Add(heading);

            var location = new TextBlock
            {
                Text = DemoLibrary.Directory,
                FontFamily = HubTheme.Data,
                FontSize = 8,
                Foreground = HubTheme.TextDimBrush,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 390,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(location, 1);
            header.Children.Add(location);
            root.Children.Add(header);

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

            var listPanel = new Border
            {
                Background = HubTheme.PanelBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                Child = _list
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
            _list.Clear();
            _recordings.Clear();
            _virtual.Clear();

            IReadOnlyList<DemoRecording> demos = DemoLibrary.List();
            foreach (DemoRecording demo in demos)
            {
                _recordings[demo.Path] = demo;
                _list.Add(new UiListRow(
                    (demo.Favorite ? "★ " : "") + demo.DisplayName,
                    DemoLibrary.Describe(demo))
                {
                    Choice = demo.Path
                }, _ => Select(demo.Path));
            }

            foreach (string path in ReplayVirtualClips.List())
            {
                if (!ReplayVirtualClips.TryLoad(path,
                        out ReplayVirtualClipDocument? clip) || clip == null)
                    continue;
                _virtual[path] = clip;
                string duration = ReplayHud.Time(clip.EndFrame - clip.StartFrame);
                _list.Add(new UiListRow(
                    (ReplayVirtualClips.IsFavorite(path) ? "★ " : "") + clip.Name,
                    $"virtual clip / {duration}")
                {
                    Choice = path
                }, _ => Select(path));
            }

            if (demos.Count == 0 && _virtual.Count == 0)
            {
                _list.AddNote("No recordings yet. Record a match or import a replay.",
                    HubTheme.TextDim);
                Select(null);
                return;
            }

            if (preserve != null)
            {
                _list.SelectTag(preserve);
                Select(preserve);
            }
            else
            {
                _list.FocusFirst();
            }
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
                    + $"SOURCE  {Path.GetFileName(clip.SourceReplay)}";
                SetPreview(RoomForSource(clip.SourceReplay));
                return;
            }

            if (_recordings.TryGetValue(path, out DemoRecording demo))
            {
                _title.Text = demo.DisplayName.ToUpperInvariant();
                _rename.Value = demo.DisplayName;
                _metadata.Text = $"{DemoLibrary.Describe(demo)}\n"
                    + DemoLibrary.Details(demo);
                SetPreview(demo.Room);
                return;
            }

            _title.Text = Path.GetFileName(path).ToUpperInvariant();
            _rename.Value = Path.GetFileNameWithoutExtension(path);
            _metadata.Text = interrupted
                ? "INTERRUPTED RECORDING\nRecover this file before playback."
                : path;
            SetPreview(null);
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
        {
            DemoRecording demo = DemoLibrary.List().FirstOrDefault(d =>
                String.Equals(d.Path, source, StringComparison.OrdinalIgnoreCase));
            return demo.Path != null ? demo.Room : null;
        }

        private void SetPreview(string? room)
        {
            _preview.Source = null;
            _bitmap?.Dispose();
            _bitmap = null;
            if (String.IsNullOrWhiteSpace(room))
                return;
            try
            {
                string path = ThumbnailGenerator.PathFor(room);
                if (File.Exists(path))
                    _bitmap = new Bitmap(path);
            }
            catch
            {
                // Replay metadata remains useful even without a thumbnail.
            }
            _preview.Source = _bitmap;
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
