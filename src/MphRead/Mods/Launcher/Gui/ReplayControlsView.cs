#if MPHREAD_AVALONIA
using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Mods.Network;
using MphRead.Mods.Replay;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Replay Studio: transport, zoomable timeline, highlight/analytics inspection,
    /// non-destructive clip marking, camera authoring and replay-lab branching.
    /// </summary>
    internal sealed class ReplayControlsView : UserControl
    {
        public event EventHandler? Closed;
        public event EventHandler? ResumeRequested;

        private readonly TextBlock _status;
        private readonly TextBlock _analytics;
        private readonly TextBlock _cameraStatus;
        private readonly ReplayTimeline _timeline;
        private readonly StackPanel _highlightPanel;
        private readonly StackPanel _reelPanel;
        private readonly StackPanel _bookmarkPanel;
        private readonly StackPanel _analyticsPanel;
        private readonly FieldRow _bookmarkName;
        private readonly FieldRow _clipName;
        private readonly TextBlock _shortcuts;
        private ReplayBookmark[] _bookmarks = Array.Empty<ReplayBookmark>();
        private ReplayNamedHighlight[] _namedHighlights = Array.Empty<ReplayNamedHighlight>();
        private readonly DeckButton _playPause;
        private readonly DeckButton _director;
        private readonly DeckButton _track;
        private readonly DeckButton _collision;
        private readonly DeckButton _first;
        private readonly ChoiceRow _timelinePlayer;
        private readonly ChoiceRow _timelineEvents;
        private readonly byte[] _timelineSlots;
        private readonly ChoiceRow _exportPreset;
        private readonly ChoiceRow _exportResolution;
        private readonly ChoiceRow _exportFps;
        private readonly ToggleRow _exportHud;
        private readonly TextBlock _exportQueueStatus;
        private readonly DispatcherTimer _timer;
        private readonly ReplayHighlight[] _highlights;
        private string _message = "";
        private bool _takeControlArmed;

        public ReplayControlsView(bool shell = false)
        {
            Background = Brushes.Transparent;
            Focusable = true;
            _highlights = ReplayStudio.Highlights().ToArray();

            var body = new StackPanel { Spacing = 9 };
            _status = new TextBlock
            {
                FontFamily = GuiTheme.Display,
                FontSize = 14,
                Foreground = GuiTheme.TextBrush,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 2)
            };
            body.Children.Add(_status);

            ReplayPlayerInfo[] timelinePlayers = (DemoPlayback.Metadata?.Players
                ?? Array.Empty<ReplayPlayerInfo>())
                .OrderBy(player => player.Slot).ToArray();
            _timelineSlots = new byte[timelinePlayers.Length + 1];
            _timelineSlots[0] = byte.MaxValue;
            string[] timelineNames = new string[timelinePlayers.Length + 1];
            timelineNames[0] = "All players";
            for (int i = 0; i < timelinePlayers.Length; i++)
            {
                _timelineSlots[i + 1] = timelinePlayers[i].Slot;
                timelineNames[i + 1] = timelinePlayers[i].Name;
            }
            _timelinePlayer = new ChoiceRow("Timeline player", timelineNames, 0);
            _timelineEvents = new ChoiceRow("Timeline events",
                new[] { "All events", "Combat", "Kills / deaths", "Damage",
                    "Objectives", "Annotations" }, 0);
            _timelinePlayer.Changed += (_, _) => Refresh();
            _timelineEvents.Changed += (_, _) => Refresh();
            body.Children.Add(_timelinePlayer);
            body.Children.Add(_timelineEvents);

            body.Children.Add(new Caption("Timeline"));
            _timeline = new ReplayTimeline
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 2),
                FrameRequested = frame =>
                {
                    _message = "";
                    _takeControlArmed = false;
                    ReplayController.Seek(frame, resume: false);
                    Refresh();
                },
                MarkInRequested = frame =>
                {
                    ReplayController.SetMarkIn(frame);
                    _message = "Clip In moved to " + Time(frame) + ".";
                    Refresh();
                },
                MarkOutRequested = frame =>
                {
                    ReplayController.SetMarkOut(frame);
                    _message = "Clip Out moved to " + Time(frame) + ".";
                    Refresh();
                }
            };
            body.Children.Add(_timeline);
            body.Children.Add(new Note("Drag to scrub · drag the gold In/Out handles to trim · wheel zooms · "
                + "←/→ nudge one second. Lanes: kills · deaths · damage · objectives · annotations."));

            body.Children.Add(new Caption("Highlights"));
            _highlightPanel = new StackPanel { Spacing = 4 };
            BuildHighlights();
            body.Children.Add(_highlightPanel);

            body.Children.Add(new Caption("Highlight reel"));
            _reelPanel = new StackPanel { Spacing = 4 };
            body.Children.Add(_reelPanel);
            var reelActions = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                ColumnSpacing = 8
            };
            var addSelection = new DeckButton("ADD SELECTION", Deck.Face.Brass,
                sizeEms: .92, padXEms: .65, padYEms: .4, lip: 3)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            addSelection.Click += (_, _) => AddSelectionToReel();
            reelActions.Children.Add(addSelection);
            var addHighlights = new DeckButton("ADD TOP HIGHLIGHTS", Deck.Face.Moss,
                sizeEms: .92, padXEms: .65, padYEms: .4, lip: 3)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            addHighlights.Click += (_, _) => AddHighlightsToReel();
            Grid.SetColumn(addHighlights, 1);
            reelActions.Children.Add(addHighlights);
            body.Children.Add(reelActions);
            BuildReel();

            ReloadAnnotations();
            body.Children.Add(new Caption("Bookmarks & named highlights"));
            _bookmarkName = new FieldRow("Name", "", boxWidth: 220);
            _bookmarkName.Box.MaxLength = 80;
            body.Children.Add(_bookmarkName);
            var bookmarkActions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
            var addBookmark = new DeckButton("ADD BOOKMARK", Deck.Face.Brass,
                sizeEms: .92, padXEms: .65, padYEms: .4, lip: 3)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 4, 0)
            };
            addBookmark.Click += (_, _) => AddBookmark();
            bookmarkActions.Children.Add(addBookmark);
            var addNamedHighlight = new DeckButton("NAME SELECTION", Deck.Face.Brass,
                sizeEms: .92, padXEms: .65, padYEms: .4, lip: 3)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(4, 0, 0, 0)
            };
            addNamedHighlight.Click += (_, _) => AddNamedHighlight();
            Grid.SetColumn(addNamedHighlight, 1);
            bookmarkActions.Children.Add(addNamedHighlight);
            body.Children.Add(bookmarkActions);
            _bookmarkPanel = new StackPanel { Spacing = 4 };
            body.Children.Add(_bookmarkPanel);
            BuildBookmarks();

            body.Children.Add(new Caption("Playback"));
            body.Children.Add(new Note(
                "Basic playback stays available while watching. Use the on-screen replay HUD "
                + "shortcuts for play/pause, seek, speed and frame stepping without opening this menu."));
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*")
            };
            body.Children.Add(grid);
            int index = 0;

            DeckButton AddAction(string text, Action action, bool resume = false,
                Deck.Face? face = null)
            {
                if (index % 2 == 0)
                    grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var button = new DeckButton(text, face ?? Deck.Face.Slate,
                    sizeEms: 1.0, padXEms: 0.72, padYEms: 0.46, lip: 4)
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(index % 2 == 0 ? 0 : 4,
                        index < 2 ? 0 : 4,
                        index % 2 == 0 ? 4 : 0, 0)
                };
                button.Click += (_, _) =>
                {
                    _message = "";
                    _takeControlArmed = false;
                    action();
                    Refresh();
                    if (resume)
                        ResumeRequested?.Invoke(this, EventArgs.Empty);
                };
                Grid.SetRow(button, index / 2);
                Grid.SetColumn(button, index % 2);
                grid.Children.Add(button);
                index++;
                return button;
            }

            void BeginActionGroup(string caption)
            {
                body.Children.Add(new Caption(caption));
                grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
                body.Children.Add(grid);
                index = 0;
            }

            _playPause = AddAction("PAUSE", ReplayController.TogglePause, resume: true,
                face: Deck.Face.Moss);
            _first = _playPause;
            AddAction("STEP FRAME", ReplayController.StepForward, resume: true);
            AddAction("-5 SECONDS", () => ReplayController.Seek(
                ReplayController.CurrentFrame > 300 ? ReplayController.CurrentFrame - 300 : 0,
                resume: false), resume: true);
            AddAction("+5 SECONDS", () => ReplayController.Seek(
                (uint)Math.Min((ulong)ReplayController.CurrentFrame + 300,
                    ReplayController.DurationFrames), resume: false), resume: true);
            AddAction("SLOWER", () => ReplayController.ChangeRate(-1));
            AddAction("FASTER", () => ReplayController.ChangeRate(1));
            AddAction("NORMAL SPEED", () => ReplayController.SetPlaybackRate(1));
            AddAction("RESTART", ReplayController.Restart, resume: true);

            BeginActionGroup("Navigate replay");
            AddAction("PREV EVENT", () => ReplayController.JumpEvent(false), resume: true);
            AddAction("NEXT EVENT", () => ReplayController.JumpEvent(true), resume: true);
            AddAction("PREV PLAYER", () =>
            {
                SpectatorMode.CyclePrevious();
                ReplayController.NoteInput();
            }, resume: true);
            AddAction("NEXT PLAYER", () =>
            {
                SpectatorMode.CycleNext();
                ReplayController.NoteInput();
            }, resume: true);
            AddAction("CAMERA MODE", CycleCamera, resume: true);

            BeginActionGroup("Clip editing");
            _clipName = new FieldRow("Clip / highlight name", "", boxWidth: 220);
            _clipName.Box.MaxLength = 80;
            body.Children.Add(_clipName);
            AddAction("MARK IN", ReplayController.MarkIn, face: Deck.Face.Brass);
            AddAction("MARK OUT", ReplayController.MarkOut, face: Deck.Face.Brass);
            AddAction("SAVE REPLAY CLIP", SaveSelection, face: Deck.Face.Moss);
            AddAction("SAVE VIRTUAL CLIP", SaveVirtualSelection, face: Deck.Face.Moss);

            body.Children.Add(new Caption("Cinematic camera"));
            var cameraGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
            body.Children.Add(cameraGrid);
            int cameraIndex = 0;
            DeckButton AddCamera(string text, Action action, Deck.Face? face = null)
            {
                if (cameraIndex % 2 == 0)
                    cameraGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var button = new DeckButton(text, face ?? Deck.Face.Slate,
                    sizeEms: 0.96, padXEms: 0.68, padYEms: 0.44, lip: 4)
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(cameraIndex % 2 == 0 ? 0 : 4,
                        cameraIndex < 2 ? 0 : 4,
                        cameraIndex % 2 == 0 ? 4 : 0, 0)
                };
                button.Click += (_, _) =>
                {
                    _message = "";
                    _takeControlArmed = false;
                    action();
                    Refresh();
                };
                Grid.SetRow(button, cameraIndex / 2);
                Grid.SetColumn(button, cameraIndex % 2);
                cameraGrid.Children.Add(button);
                cameraIndex++;
                return button;
            }

            _director = AddCamera("DIRECTOR", ToggleDirector, Deck.Face.Brass);
            _track = AddCamera("CAMERA TRACK", ToggleTrack, Deck.Face.Brass);
            AddCamera("ADD KEYFRAME", ReplayCamera.Bookmark);
            AddCamera("REMOVE KEYFRAME", ReplayCamera.RemoveKeyframe);
            AddCamera("INTERPOLATION", CycleInterpolation);
            AddCamera("EASING", CycleEase);
            AddCamera("ROLL -5°", () => ReplayCamera.Roll = Math.Clamp(ReplayCamera.Roll - 5, -180, 180));
            AddCamera("ROLL +5°", () => ReplayCamera.Roll = Math.Clamp(ReplayCamera.Roll + 5, -180, 180));
            AddCamera("FOV -5°", () => ReplayCamera.FieldOfView = Math.Clamp(ReplayCamera.FieldOfView - 5, 20, 140));
            AddCamera("FOV +5°", () => ReplayCamera.FieldOfView = Math.Clamp(ReplayCamera.FieldOfView + 5, 20, 140));
            AddCamera("LOOK AT PLAYER", () =>
            {
                int slot = DemoPlayback.PresentationScene?.Players.MainPlayerIndex ?? PlayerEntity.MainPlayerIndex;
                ReplayCamera.LookAtSlot = ReplayCamera.LookAtSlot == slot ? -1 : slot;
            });
            AddCamera("CONSTANT SPEED", () =>
                ReplayCamera.TrackConstantSpeed = !ReplayCamera.TrackConstantSpeed);
            _collision = AddCamera("PATH COLLISION", () =>
                ReplayCamera.TrackCollisionAvoidance = !ReplayCamera.TrackCollisionAvoidance);

            _cameraStatus = new TextBlock
            {
                FontFamily = Deck.Mono,
                FontSize = 11,
                Foreground = GuiTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            };
            body.Children.Add(_cameraStatus);

            body.Children.Add(new Caption("Analysis & output"));
            _exportPreset = new ChoiceRow("Export preset",
                ReplayExportPresets.All.Select(preset => preset.Name).ToArray(), 0);
            _exportResolution = new ChoiceRow("Export resolution",
                new[] { "720p", "1080p", "1440p", "4K" }, 1);
            _exportFps = new ChoiceRow("Export FPS", new[] { "30", "60", "120" }, 1);
            _exportHud = new ToggleRow("Include game/replay HUD", false);
            _exportPreset.Changed += (_, _) =>
            {
                ReplayExportPreset preset = SelectedPreset();
                _exportResolution.Index = (int)preset.Resolution;
                _exportFps.Index = preset.Fps <= 30 ? 0 : preset.Fps >= 120 ? 2 : 1;
                _exportHud.On = !preset.CleanHud;
            };
            body.Children.Add(_exportPreset);
            body.Children.Add(_exportResolution);
            body.Children.Add(_exportFps);
            body.Children.Add(_exportHud);

            var outputGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
            body.Children.Add(outputGrid);
            int outputIndex = 0;
            DeckButton AddOutput(string text, Action action, Deck.Face? face = null)
            {
                if (outputIndex % 2 == 0)
                    outputGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var button = new DeckButton(text, face ?? Deck.Face.Slate,
                    sizeEms: 0.96, padXEms: 0.68, padYEms: 0.44, lip: 4)
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(outputIndex % 2 == 0 ? 0 : 4,
                        outputIndex < 2 ? 0 : 4,
                        outputIndex % 2 == 0 ? 4 : 0, 0)
                };
                button.Click += (_, _) =>
                {
                    _message = "";
                    action();
                    Refresh();
                };
                Grid.SetRow(button, outputIndex / 2);
                Grid.SetColumn(button, outputIndex % 2);
                outputGrid.Children.Add(button);
                outputIndex++;
                return button;
            }

            AddOutput("QUEUE EXPORT", QueueExport, Deck.Face.Moss);
            AddOutput("EXPORT REEL", QueueReelExport, Deck.Face.Moss);
            AddOutput("CANCEL EXPORT", CancelExport, Deck.Face.Rust);
            AddOutput("RETRY EXPORT", RetryExport);
            AddOutput("SAVE HIGHLIGHTS", SaveHighlights, Deck.Face.Moss);
            AddOutput("TAKE CONTROL", TakeControl, Deck.Face.Rust);
            AddOutput("ANALYTICS HUD", () => ReplayHud.ShowAnalytics = !ReplayHud.ShowAnalytics);
            AddOutput("NETWORK HUD", () => ReplayHud.ShowNetworkDebug = !ReplayHud.ShowNetworkDebug);

            _exportQueueStatus = new TextBlock
            {
                FontFamily = Deck.Mono,
                FontSize = 10,
                Foreground = GuiTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            };
            body.Children.Add(_exportQueueStatus);

            _analytics = new TextBlock
            {
                FontFamily = Deck.Mono,
                FontSize = 11,
                Foreground = GuiTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left
            };
            body.Children.Add(_analytics);
            _analyticsPanel = new StackPanel { Spacing = 4 };
            body.Children.Add(_analyticsPanel);
            BuildAnalytics();

            _shortcuts = new TextBlock
            {
                Text = ShortcutText(),
                FontFamily = GuiTheme.Display,
                FontSize = 11,
                Foreground = GuiTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            };
            body.Children.Add(_shortcuts);

            var scroll = new ScrollViewer
            {
                Content = body,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var back = new UiMark(UiMark.Shape.Cancel, "back");
            back.Click += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
            Content = shell ? scroll : UiLayout.Page(overGame: true, UiLayout.WellSettings,
                "replay studio", strip: null, body: scroll, no: back);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += (_, _) =>
            {
                if (DemoPlayback.IsActive) Refresh();
            };
            Refresh();
        }

        private void BuildHighlights()
        {
            _highlightPanel.Children.Clear();
            ReplayHighlight[] ordered = _highlights
                .OrderBy(h => h.FocusFrame)
                .Take(8)
                .ToArray();
            if (ordered.Length == 0)
            {
                _highlightPanel.Children.Add(new Note("No strong automatic highlights were detected in this recording."));
                return;
            }
            foreach (ReplayHighlight highlight in ordered)
            {
                var button = new DeckButton(
                    $"{Time(highlight.FocusFrame)}  {highlight.Label.ToUpperInvariant()}",
                    highlight.Kind is ReplayHighlightKind.Objective or ReplayHighlightKind.CloseFinish
                        ? Deck.Face.Brass : Deck.Face.Slate,
                    sizeEms: 0.94, padXEms: 0.7, padYEms: 0.4, lip: 3)
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                button.Click += (_, _) =>
                {
                    ReplayController.Seek(highlight.FocusFrame, resume: false);
                    _message = $"Highlight: {highlight.Label}.";
                    Refresh();
                };
                _highlightPanel.Children.Add(button);
            }
        }

        private void BuildReel()
        {
            _reelPanel.Children.Clear();
            if (DemoPlayback.CurrentPath == null)
            {
                _reelPanel.Children.Add(new Note("No replay is open."));
                return;
            }

            ReplayReelSegment[] segments = ReplayReels
                .Segments(DemoPlayback.CurrentPath).ToArray();
            if (segments.Length == 0)
            {
                _reelPanel.Children.Add(new Note(
                    "The reel is empty. Add the In/Out selection or seed it from automatic highlights."));
                return;
            }

            for (int index = 0; index < segments.Length; index++)
            {
                ReplayReelSegment segment = segments[index];
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5
                };
                var jump = new DeckButton(
                    $"{index + 1}. {segment.Name.ToUpperInvariant()}  "
                        + $"{Time(segment.StartFrame)}-{Time(segment.EndFrame)}  "
                        + $"[{segment.Camera.ToString().ToUpperInvariant()}]",
                    Deck.Face.Slate, sizeEms: .86, padXEms: .55, padYEms: .32, lip: 3)
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    MinWidth = 300
                };
                jump.Click += (_, _) =>
                {
                    ReplayController.SetMarkIn(segment.StartFrame);
                    ReplayController.SetMarkOut(segment.EndFrame);
                    ReplayController.Seek(segment.StartFrame, resume: false);
                    _message = "Reel segment: " + segment.Name + ".";
                    Refresh();
                };
                row.Children.Add(jump);
                UiWord Command(string label, Action action)
                {
                    var word = new UiWord(label);
                    word.Click += (_, _) =>
                    {
                        action();
                        BuildReel();
                        Refresh();
                    };
                    return word;
                }
                row.Children.Add(Command("up", () =>
                    ReplayReels.Move(DemoPlayback.CurrentPath!, segment.Id, -1)));
                row.Children.Add(Command("down", () =>
                    ReplayReels.Move(DemoPlayback.CurrentPath!, segment.Id, 1)));
                row.Children.Add(Command("trim", () =>
                {
                    if (TrySelection(out uint start, out uint end))
                        ReplayReels.Trim(DemoPlayback.CurrentPath!, segment.Id, start, end);
                }));
                row.Children.Add(Command("camera", () =>
                    ReplayReels.CycleCamera(DemoPlayback.CurrentPath!, segment.Id)));
                row.Children.Add(Command("remove", () =>
                    ReplayReels.Remove(DemoPlayback.CurrentPath!, segment.Id)));
                _reelPanel.Children.Add(row);
            }
        }

        private void AddSelectionToReel()
        {
            if (DemoPlayback.CurrentPath == null
                || !TrySelection(out uint start, out uint end))
            {
                _message = "Set MARK IN and MARK OUT before adding a reel segment.";
                return;
            }
            try
            {
                string? name = String.IsNullOrWhiteSpace(_clipName.Value)
                    ? null : _clipName.Value.Trim();
                ReplayReels.Add(DemoPlayback.CurrentPath, start, end, name);
                BuildReel();
                _message = "Selection added to highlight reel.";
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or ArgumentException)
            {
                _message = "Could not add reel segment: " + ex.Message;
            }
            Refresh();
        }

        private void AddHighlightsToReel()
        {
            if (DemoPlayback.CurrentPath == null)
            {
                _message = "No replay is open.";
                return;
            }
            try
            {
                int added = ReplayReels.AddHighlights(
                    DemoPlayback.CurrentPath, _highlights, 8);
                BuildReel();
                _message = added == 0
                    ? "No automatic highlights were available."
                    : $"Added {added} highlight{(added == 1 ? "" : "s")} to the reel.";
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or ArgumentException)
            {
                _message = "Could not build reel: " + ex.Message;
            }
            Refresh();
        }

        private void ReloadAnnotations()
        {
            _bookmarks = ReplayAnnotations.Bookmarks().ToArray();
            _namedHighlights = ReplayAnnotations.Highlights().ToArray();
        }

        private void AddBookmark()
        {
            if (DemoPlayback.CurrentPath == null)
            {
                _message = "No replay is open.";
                return;
            }
            try
            {
                ReplayBookmark bookmark = ReplayAnnotations.AddBookmark(
                    DemoPlayback.CurrentPath, ReplayController.CurrentFrame, _bookmarkName.Value);
                _bookmarkName.Value = "";
                ReloadAnnotations();
                BuildBookmarks();
                _message = $"Bookmark saved: {bookmark.Name}.";
                Refresh();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException)
            {
                _message = "Could not save bookmark: " + ex.Message;
            }
        }

        private void AddNamedHighlight()
        {
            if (DemoPlayback.CurrentPath == null
                || !TrySelection(out uint start, out uint end))
            {
                _message = "Set both MARK IN and MARK OUT before naming a highlight.";
                return;
            }
            try
            {
                ReplayNamedHighlight highlight = ReplayAnnotations.AddHighlight(
                    DemoPlayback.CurrentPath, start, end, _bookmarkName.Value);
                _bookmarkName.Value = "";
                ReloadAnnotations();
                BuildBookmarks();
                _message = $"Named highlight saved: {highlight.Name}.";
                Refresh();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException)
            {
                _message = "Could not save named highlight: " + ex.Message;
            }
        }

        private void BuildBookmarks()
        {
            _bookmarkPanel.Children.Clear();
            if (_bookmarks.Length == 0 && _namedHighlights.Length == 0)
            {
                _bookmarkPanel.Children.Add(new Note(
                    "No bookmarks yet. Add one at the playhead, or name the current In/Out selection."));
                return;
            }

            foreach (ReplayBookmark bookmark in _bookmarks.Take(24))
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                var jump = new DeckButton(
                    $"{Time(bookmark.Frame)}  {bookmark.Name.ToUpperInvariant()}",
                    Deck.Face.Slate, sizeEms: .9, padXEms: .6, padYEms: .35, lip: 3)
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                jump.Click += (_, _) =>
                {
                    ReplayController.Seek(bookmark.Frame, resume: false);
                    _message = "Bookmark: " + bookmark.Name + ".";
                    Refresh();
                };
                row.Children.Add(jump);
                var remove = new UiWord("remove") { Margin = new Thickness(8, 0, 0, 0) };
                remove.Click += (_, _) =>
                {
                    if (DemoPlayback.CurrentPath == null) return;
                    ReplayAnnotations.RemoveBookmark(DemoPlayback.CurrentPath, bookmark.Id);
                    ReloadAnnotations();
                    BuildBookmarks();
                    Refresh();
                };
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);
                _bookmarkPanel.Children.Add(row);
            }

            foreach (ReplayNamedHighlight highlight in _namedHighlights.Take(24))
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                var jump = new DeckButton(
                    $"{Time(highlight.StartFrame)}-{Time(highlight.EndFrame)}  "
                        + highlight.Name.ToUpperInvariant(),
                    Deck.Face.Brass, sizeEms: .9, padXEms: .6, padYEms: .35, lip: 3)
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                jump.Click += (_, _) =>
                {
                    ReplayController.SetMarkIn(highlight.StartFrame);
                    ReplayController.SetMarkOut(highlight.EndFrame);
                    ReplayController.Seek(highlight.StartFrame, resume: false);
                    _message = "Named highlight: " + highlight.Name + ".";
                    Refresh();
                };
                row.Children.Add(jump);
                var remove = new UiWord("remove") { Margin = new Thickness(8, 0, 0, 0) };
                remove.Click += (_, _) =>
                {
                    if (DemoPlayback.CurrentPath == null) return;
                    ReplayAnnotations.RemoveHighlight(DemoPlayback.CurrentPath, highlight.Id);
                    ReloadAnnotations();
                    BuildBookmarks();
                    Refresh();
                };
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);
                _bookmarkPanel.Children.Add(row);
            }
        }

        private void BuildAnalytics()
        {
            _analyticsPanel.Children.Clear();
            ReplayAnalyticsSnapshot analytics = ReplayStudio.Analytics();
            var names = DemoPlayback.Metadata?.Players
                .ToDictionary(player => player.Slot, player => player.Name)
                ?? new System.Collections.Generic.Dictionary<byte, string>();

            string Name(byte slot) => names.TryGetValue(slot, out string? value)
                ? value : $"P{slot + 1}";

            void JumpRow(string text, uint frame, Deck.Face? face = null)
            {
                var button = new DeckButton(text, face ?? Deck.Face.Slate,
                    sizeEms: .84, padXEms: .55, padYEms: .3, lip: 3)
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                button.Click += (_, _) =>
                {
                    ReplayController.Seek(frame, resume: false);
                    _message = text;
                    Refresh();
                };
                _analyticsPanel.Children.Add(button);
            }

            _analyticsPanel.Children.Add(new Caption("Player performance"));
            foreach (ReplayPlayerAnalytics player in analytics.Players
                .OrderByDescending(player => player.Kills)
                .ThenByDescending(player => player.Damage)
                .Take(8))
            {
                ReplayEvent? first = DemoPlayback.Events.FirstOrDefault(e =>
                    e.ActorSlot == player.Slot
                    && e.Type is ReplayEventType.Kill or ReplayEventType.Damage
                        or ReplayEventType.Objective);
                uint frame = first?.Frame ?? 0;
                JumpRow($"{Name(player.Slot)}  {player.Kills}K/{player.Deaths}D  "
                    + $"{player.Damage} DMG  {player.ObjectiveEvents} OBJ", frame);
            }

            _analyticsPanel.Children.Add(new Caption("Damage over time"));
            foreach (ReplayTimeBucket bucket in analytics.DamageTimeline
                .Where(bucket => bucket.Damage > 0 || bucket.Kills > 0
                    || bucket.Objectives > 0)
                .Take(12))
            {
                JumpRow($"{Time(bucket.StartFrame)}  {bucket.Damage} DMG  "
                    + $"{bucket.Kills} KILLS  {bucket.Objectives} OBJ",
                    bucket.StartFrame);
            }

            _analyticsPanel.Children.Add(new Caption("Kill chronology"));
            foreach (ReplayEvent kill in DemoPlayback.Events
                .Where(e => e.Type == ReplayEventType.Kill)
                .Take(16))
            {
                JumpRow($"{Time(kill.Frame)}  {Name(kill.ActorSlot)} > "
                    + $"{Name(kill.TargetSlot)}", kill.Frame, Deck.Face.Brass);
            }

            if (analytics.WeaponUsage.Count > 0)
            {
                _analyticsPanel.Children.Add(new Caption("Weapon usage"));
                foreach (ReplayWeaponUsage weapon in analytics.WeaponUsage.Take(9))
                {
                    string label = Enum.IsDefined(typeof(BeamType), weapon.Weapon)
                        ? ((BeamType)weapon.Weapon).ToString()
                        : $"Weapon {weapon.Weapon}";
                    ReplayEvent? first = DemoPlayback.Events.FirstOrDefault(e =>
                        e.Type == ReplayEventType.WeaponFired && e.Value == weapon.Weapon);
                    JumpRow($"{label.ToUpperInvariant()}  {weapon.Shots} SHOTS",
                        first?.Frame ?? 0);
                }
            }
        }

        private static string ShortcutText()
        {
            string Key(OpenTK.Windowing.GraphicsLibraryFramework.Keys key)
                => key == OpenTK.Windowing.GraphicsLibraryFramework.Keys.Unknown
                    ? "unbound" : InputSettings.KeyName(key);
            string Pad(Mods.Input.PadAction action)
            {
                for (int slot = 0; slot < 2; slot++)
                {
                    if (Mods.Input.PadBindings.Slot(action, slot)
                        != Mods.Input.GamepadButtons.None)
                        return Mods.Input.PadBindings.DescribeSlot(action, slot);
                }
                return "unbound";
            }
            return $"Keyboard: {Key(InputSettings.ReplayPlayPauseKey)} play/pause · "
                + $"{Key(InputSettings.ReplayStepBackKey)}/{Key(InputSettings.ReplayStepForwardKey)} step · "
                + $"{Key(InputSettings.ReplaySlowerKey)}/{Key(InputSettings.ReplayFasterKey)} speed · "
                + $"{Key(InputSettings.ReplaySeekBackKey)}/{Key(InputSettings.ReplaySeekForwardKey)} seek\n"
                + $"Gamepad: {Pad(Mods.Input.PadAction.ReplayPlayPause)} play/pause · "
                + $"{Pad(Mods.Input.PadAction.ReplayStep)} step · "
                + $"{Pad(Mods.Input.PadAction.ReplaySeekBack)}/{Pad(Mods.Input.PadAction.ReplaySeekForward)} seek";
        }

        private void ToggleDirector()
        {
            ReplayCamera.SetProfile(ReplayPresentationProfile.Presentation);
            ReplayCamera.Director = !ReplayCamera.Director;
            if (ReplayCamera.Director) ReplayCamera.PlayTrack = false;
        }

        private void ToggleTrack()
        {
            ReplayCamera.SetProfile(ReplayPresentationProfile.Presentation);
            ReplayCamera.PlayTrack = !ReplayCamera.PlayTrack;
            if (ReplayCamera.PlayTrack) ReplayCamera.Director = false;
        }

        private static void CycleInterpolation()
        {
            int count = Enum.GetValues<ReplayCameraInterpolation>().Length;
            ReplayCamera.TrackInterpolation = (ReplayCameraInterpolation)
                (((int)ReplayCamera.TrackInterpolation + 1) % count);
        }

        private static void CycleEase()
        {
            int count = Enum.GetValues<ReplayCameraEase>().Length;
            ReplayCamera.TrackEase = (ReplayCameraEase)(((int)ReplayCamera.TrackEase + 1) % count);
        }

        private void CycleCamera()
        {
            ReplayCameraMode next = ReplayCamera.Mode switch
            {
                ReplayCameraMode.FirstPerson => ReplayCameraMode.Chase,
                ReplayCameraMode.Chase => ReplayCameraMode.Orbit,
                ReplayCameraMode.Orbit => ReplayCameraMode.Free,
                _ => ReplayCameraMode.FirstPerson
            };
            ReplayCamera.SetMode(next);
        }

        private void SaveSelection()
        {
            ReplayOpenResult result = ReplayController.SaveSelection();
            _message = result == ReplayOpenResult.Success
                ? "Standalone .ppdemo clip saved."
                : result == ReplayOpenResult.Empty
                    ? "Set both MARK IN and MARK OUT before saving."
                    : $"Could not save selection: {result}.";
        }

        private void SaveVirtualSelection()
        {
            if (!TrySelection(out uint start, out uint end) || DemoPlayback.CurrentPath == null)
            {
                _message = "Set both MARK IN and MARK OUT before saving.";
                return;
            }
            try
            {
                string? name = String.IsNullOrWhiteSpace(_clipName.Value)
                    ? null : _clipName.Value.Trim();
                string path = ReplayVirtualClips.Save(DemoPlayback.CurrentPath, start, end, name);
                _message = "Virtual clip saved: " + Path.GetFileName(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException)
            {
                _message = "Could not save virtual clip: " + ex.Message;
            }
        }

        private void SaveHighlights()
        {
            if (DemoPlayback.CurrentPath == null)
            {
                _message = "No replay is open.";
                return;
            }
            int saved = 0;
            try
            {
                foreach (ReplayHighlight highlight in _highlights
                    .OrderByDescending(h => h.Score)
                    .Take(8))
                {
                    ReplayVirtualClips.Save(DemoPlayback.CurrentPath,
                        highlight.StartFrame, highlight.EndFrame, highlight.Label);
                    saved++;
                }
                _message = saved == 0
                    ? "No strong automatic highlights were detected."
                    : $"Saved {saved} non-destructive highlight clip{(saved == 1 ? "" : "s")}.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException)
            {
                _message = $"Saved {saved} highlight clips before an error: {ex.Message}";
            }
        }

        private ReplayExportPreset SelectedPreset()
        {
            int index = Math.Clamp(_exportPreset.Index, 0,
                ReplayExportPresets.All.Length - 1);
            ReplayExportPreset selected = ReplayExportPresets.All[index];
            ReplayVideoResolution resolution = (ReplayVideoResolution)Math.Clamp(
                _exportResolution.Index, 0,
                Enum.GetValues<ReplayVideoResolution>().Length - 1);
            int fps = _exportFps.Index switch
            {
                0 => 30,
                2 => 120,
                _ => 60
            };
            return selected with
            {
                Resolution = resolution,
                Fps = fps,
                CleanHud = !_exportHud.On
            };
        }

        private void QueueExport()
        {
            if (DemoPlayback.CurrentPath == null)
            {
                _message = "No replay is open.";
                return;
            }

            uint start = 0;
            uint end = ReplayController.DurationFrames;
            if (TrySelection(out uint selectedStart, out uint selectedEnd))
            {
                start = selectedStart;
                end = selectedEnd;
            }

            try
            {
                ReplayVideoExportManifest job = ReplayVideoExport.CreatePresetManifest(
                    DemoPlayback.CurrentPath, start, end, SelectedPreset(),
                    director: ReplayCamera.Director,
                    cameraTrack: ReplayCamera.PlayTrack);
                ReplayExportQueue.Enqueue(job);
                bool active = ReplayExportQueue.Pump();
                _message = active
                    ? "Export rendering started."
                    : ReplayExportQueue.Status;
                if (active)
                    ResumeRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or ArgumentException)
            {
                _message = "Could not queue video export: " + ex.Message;
            }
        }

        private void QueueReelExport()
        {
            if (DemoPlayback.CurrentPath == null)
            {
                _message = "No replay is open.";
                return;
            }
            try
            {
                ReplayReelSegment[] segments = ReplayReels
                    .Segments(DemoPlayback.CurrentPath).ToArray();
                ReplayVideoExportManifest job = ReplayVideoExport.CreateReelManifest(
                    DemoPlayback.CurrentPath, segments, SelectedPreset());
                ReplayExportQueue.Enqueue(job);
                bool active = ReplayExportQueue.Pump();
                _message = active
                    ? $"Rendering {segments.Length}-segment highlight reel."
                    : ReplayExportQueue.Status;
                if (active)
                    ResumeRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or ArgumentException)
            {
                _message = "Could not queue reel export: " + ex.Message;
            }
        }

        private void CancelExport()
        {
            ReplayExportQueue.CancelActive();
            _message = ReplayExportQueue.Status;
        }

        private void RetryExport()
        {
            if (ReplayExportQueue.RetryLast())
            {
                bool active = ReplayExportQueue.Pump();
                _message = ReplayExportQueue.Status;
                if (active)
                    ResumeRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _message = ReplayExportQueue.Status;
            }
        }

        private void TakeControl()
        {
            if (!_takeControlArmed)
            {
                _takeControlArmed = true;
                _message = "Replay Lab forks history here. Press TAKE CONTROL again to confirm.";
                return;
            }
            int slot = DemoPlayback.PresentationScene?.Players.MainPlayerIndex ?? PlayerEntity.MainPlayerIndex;
            if (DemoPlayback.TakeControl(slot, out string? branch))
            {
                _message = "Replay Lab branch: " + (branch == null ? "created" : Path.GetFileName(branch));
                ResumeRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _message = DemoPlayback.LastError ?? "That player cannot be controlled at this frame.";
            }
            _takeControlArmed = false;
        }

        private static bool TrySelection(out uint start, out uint end)
        {
            start = 0;
            end = 0;
            if (!ReplayController.ClipIn.HasValue || !ReplayController.ClipOut.HasValue)
                return false;
            start = Math.Min(ReplayController.ClipIn.Value, ReplayController.ClipOut.Value);
            end = Math.Max(ReplayController.ClipIn.Value, ReplayController.ClipOut.Value);
            return start < end;
        }

        private void Refresh()
        {
            _playPause.Text = ReplayController.AtEnd ? "RESTART"
                : ReplayController.IsPaused ? "PLAY" : "PAUSE";
            _director.Text = ReplayCamera.Director ? "DIRECTOR: ON" : "DIRECTOR: OFF";
            _track.Text = ReplayCamera.PlayTrack ? "CAMERA TRACK: ON" : "CAMERA TRACK: OFF";
            _collision.Text = ReplayCamera.TrackCollisionAvoidance
                ? "PATH COLLISION: ON" : "PATH COLLISION: OFF";

            string marks = $"IN {Mark(ReplayController.ClipIn)}  ·  OUT {Mark(ReplayController.ClipOut)}";
            string mode = ReplayCamera.Mode.ToString();
            _status.Text = $"{ReplayController.State}  ·  {Time(ReplayController.CurrentFrame)} / "
                + $"{Time(ReplayController.DurationFrames)}  ·  {ReplayController.PlaybackRate:0.##}x\n"
                + $"{mode} camera  ·  {marks}  ·  zoom {_timeline.Zoom:0.0}x"
                + (_message.Length == 0 ? "" : "\n" + _message);

            ReplayCamera.EnsureTrack();
            int timelineSlot = _timelinePlayer.Index <= 0
                ? -1
                : _timelineSlots[Math.Clamp(_timelinePlayer.Index, 1,
                    _timelineSlots.Length - 1)];
            ReplayTimelineFilter timelineFilter = (ReplayTimelineFilter)Math.Clamp(
                _timelineEvents.Index, 0, Enum.GetValues<ReplayTimelineFilter>().Length - 1);
            _timeline.Update(ReplayController.DurationFrames, ReplayController.CurrentFrame,
                ReplayController.ClipIn, ReplayController.ClipOut, DemoPlayback.Events, _highlights,
                ReplayCamera.Track.Keys.Select(key => key.Frame).ToArray(),
                _bookmarks.Select(bookmark => bookmark.Frame).ToArray(), _namedHighlights,
                timelineSlot, timelineFilter);
            _shortcuts.Text = ShortcutText();
            _exportQueueStatus.Text = ReplayVideoExporter.Active
                ? $"{ReplayVideoExporter.Status} · {ReplayVideoExporter.Progress:P0} · "
                    + $"{ReplayExportQueue.PendingCount} queued"
                : ReplayExportQueue.Status;

            _cameraStatus.Text =
                $"{ReplayCamera.KeyframeCount} keys · {ReplayCamera.TrackInterpolation} · "
                + $"{ReplayCamera.TrackEase} · {(ReplayCamera.TrackConstantSpeed ? "constant" : "timed")} speed · "
                + $"FOV {ReplayCamera.FieldOfView:0}° · roll {ReplayCamera.Roll:0}° · "
                + $"look-at {(ReplayCamera.LookAtSlot < 0 ? "off" : $"P{ReplayCamera.LookAtSlot + 1}")} · "
                + $"director {ReplayDirector.Reason} ({ReplayDirector.CurrentScore:0}) · "
                + $"{DemoPlayback.CheckpointCount} world checkpoints";

            ReplayAnalyticsSnapshot analytics = ReplayStudio.Analytics();
            var names = DemoPlayback.Metadata?.Players.ToDictionary(p => p.Slot, p => p.Name);
            string playerText = string.Join("\n", analytics.Players.Select(player =>
            {
                string name = names != null && names.TryGetValue(player.Slot, out string? found)
                    ? found : $"P{player.Slot + 1}";
                return $"{name}: {player.Kills} K / {player.Deaths} D · "
                    + $"{player.Damage} dmg · {player.ObjectiveEvents} obj";
            }));
            string weaponText = analytics.WeaponUsage.Count == 0 ? ""
                : "\nWeapons: " + String.Join(", ", analytics.WeaponUsage.Take(6)
                    .Select(weapon =>
                    {
                        string label = Enum.IsDefined(typeof(BeamType), weapon.Weapon)
                            ? ((BeamType)weapon.Weapon).ToString()
                            : $"W{weapon.Weapon}";
                        return $"{label} {weapon.Shots}";
                    }));
            _analytics.Text = $"Replay analysis: {analytics.TotalKills} kills · "
                + $"{analytics.TotalDamage} damage · {analytics.ObjectiveEvents} objectives"
                + (playerText.Length == 0 ? "" : "\n" + playerText)
                + weaponText;
        }

        private static string Mark(uint? frame) => frame.HasValue ? Time(frame.Value) : "--:--";
        private static string Time(uint frame) => $"{frame / 3600:00}:{frame / 60 % 60:00}";

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _timer.Start();
            Dispatcher.UIThread.Post(() => _first.Focus(), DispatcherPriority.Background);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _timer.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Closed?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }
    }
}
#endif
