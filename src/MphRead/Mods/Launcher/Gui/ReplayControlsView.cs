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
        private readonly DeckButton _playPause;
        private readonly DeckButton _director;
        private readonly DeckButton _track;
        private readonly DeckButton _collision;
        private readonly DeckButton _first;
        private readonly ChoiceRow _exportResolution;
        private readonly ChoiceRow _exportFps;
        private readonly ToggleRow _exportHud;
        private readonly DispatcherTimer _timer;
        private readonly ReplayHighlight[] _highlights;
        private string _message = "";
        private bool _takeControlArmed;

        public ReplayControlsView()
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
                }
            };
            body.Children.Add(_timeline);
            body.Children.Add(new Note("Drag to scrub · wheel zooms · ←/→ nudge one second · event marks and generated highlights share the same ruler."));

            body.Children.Add(new Caption("Highlights"));
            _highlightPanel = new StackPanel { Spacing = 4 };
            BuildHighlights();
            body.Children.Add(_highlightPanel);

            body.Children.Add(new Caption("Transport & editing"));
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
            AddAction("RESTART", ReplayController.Restart, resume: true);
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
                int slot = PlayerEntity.MainPlayerIndex;
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
            _exportResolution = new ChoiceRow("Export resolution",
                new[] { "720p", "1080p", "1440p", "4K" }, 1);
            _exportFps = new ChoiceRow("Export FPS", new[] { "30", "60", "120" }, 1);
            _exportHud = new ToggleRow("Include game/replay HUD", false);
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

            AddOutput("EXPORT VIDEO", ExportVideo, Deck.Face.Moss);
            AddOutput("SAVE HIGHLIGHTS", SaveHighlights, Deck.Face.Moss);
            AddOutput("TAKE CONTROL", TakeControl, Deck.Face.Rust);
            AddOutput("ANALYTICS HUD", () => ReplayHud.ShowAnalytics = !ReplayHud.ShowAnalytics);
            AddOutput("NETWORK HUD", () => ReplayHud.ShowNetworkDebug = !ReplayHud.ShowNetworkDebug);

            _analytics = new TextBlock
            {
                FontFamily = Deck.Mono,
                FontSize = 11,
                Foreground = GuiTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left
            };
            body.Children.Add(_analytics);

            var shortcuts = new TextBlock
            {
                Text = "Keyboard: Space play/pause · ,/. step · [/] speed · ←/→ seek · "
                    + "1-8 player · F/C/O camera · B/N camera keys",
                FontFamily = GuiTheme.Display,
                FontSize = 11,
                Foreground = GuiTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            };
            body.Children.Add(shortcuts);

            var scroll = new ScrollViewer
            {
                Content = body,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var back = new UiMark(UiMark.Shape.Cancel, "back");
            back.Click += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
            Content = UiLayout.Page(overGame: true, UiLayout.WellSettings,
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
                ? "Standalone .fpdemo clip saved."
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
                string path = ReplayVirtualClips.Save(DemoPlayback.CurrentPath, start, end);
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

        private void ExportVideo()
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

            ReplayVideoResolution resolution = (ReplayVideoResolution)Math.Clamp(
                _exportResolution.Index, 0, Enum.GetValues<ReplayVideoResolution>().Length - 1);
            int fps = _exportFps.Index switch
            {
                0 => 30,
                2 => 120,
                _ => 60
            };
            try
            {
                ReplayVideoExportManifest job = ReplayVideoExport.CreateManifest(
                    DemoPlayback.CurrentPath, start, end,
                    resolution, fps, cleanHud: !_exportHud.On,
                    director: ReplayCamera.Director, cameraTrack: ReplayCamera.PlayTrack);
                if (ReplayVideoExporter.Start(job))
                {
                    _message = "Rendering video frames to "
                        + Path.GetDirectoryName(job.SuggestedOutput);
                    ResumeRequested?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    _message = ReplayVideoExporter.Status;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException)
            {
                _message = "Could not start video export: " + ex.Message;
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
            int slot = PlayerEntity.MainPlayerIndex;
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
            _playPause.Text = ReplayController.IsPaused ? "PLAY" : "PAUSE";
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
            _timeline.Update(ReplayController.DurationFrames, ReplayController.CurrentFrame,
                ReplayController.ClipIn, ReplayController.ClipOut, DemoPlayback.Events, _highlights,
                ReplayCamera.Track.Keys.Select(key => key.Frame).ToArray());

            _cameraStatus.Text =
                $"{ReplayCamera.KeyframeCount} keys · {ReplayCamera.TrackInterpolation} · "
                + $"{ReplayCamera.TrackEase} · {(ReplayCamera.TrackConstantSpeed ? "constant" : "timed")} speed · "
                + $"FOV {ReplayCamera.FieldOfView:0}° · roll {ReplayCamera.Roll:0}° · "
                + $"look-at {(ReplayCamera.LookAtSlot < 0 ? "off" : $"P{ReplayCamera.LookAtSlot + 1}")} · "
                + $"director {ReplayDirector.Reason} ({ReplayDirector.CurrentScore:0}) · "
                + $"{ReplayCheckpointManager.Count} checkpoint candidates";

            ReplayAnalyticsSnapshot analytics = ReplayStudio.Analytics();
            var names = DemoPlayback.Metadata?.Players.ToDictionary(p => p.Slot, p => p.Name);
            string playerText = string.Join("\n", analytics.Players.Select(player =>
            {
                string name = names != null && names.TryGetValue(player.Slot, out string? found)
                    ? found : $"P{player.Slot + 1}";
                return $"{name}: {player.Kills} K / {player.Deaths} D · "
                    + $"{player.Damage} dmg · {player.ObjectiveEvents} obj";
            }));
            _analytics.Text = $"Replay analysis: {analytics.TotalKills} kills · "
                + $"{analytics.TotalDamage} damage · {analytics.ObjectiveEvents} objectives"
                + (playerText.Length == 0 ? "" : "\n" + playerText);
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
