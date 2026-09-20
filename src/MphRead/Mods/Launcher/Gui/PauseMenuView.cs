using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MphRead.Entities;
using MphRead.Mods.Network;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// What Escape shows during a match.
    ///
    /// The same shape as the front screen -- a column of words in the
    /// bottom-left corner over a dim line saying where you are -- because it
    /// is the front screen's job during a match, and a pause menu that looks
    /// like a different program is a pause menu that has to be read rather
    /// than glanced at. What differs is the backdrop: the scrim alone, so the
    /// match shows through. A networked match cannot be paused, and covering
    /// it with a photograph would be a lie about what the program is doing.
    ///
    /// The entries are not fewer than they were. Voting on a map, going
    /// fullscreen, spectating and recording are things you can only want
    /// *during* a match, so this is the one screen they can live on -- the
    /// list is shorter everywhere else precisely so it can be long here.
    ///
    /// A view rather than a window, because nothing shows it in one any
    /// more: the desktop pushes it onto <see cref="InGameMenu"/>'s stack,
    /// which is rendered into the game window itself, and Android pushes it
    /// onto <see cref="StartScreen"/>'s. One menu either way, so an entry
    /// added here turns up on both.
    ///
    /// It decides nothing itself. Every entry raises an event and the host
    /// acts on it: leaving a match is closing a window on one platform and
    /// swapping two views on the other, and neither belongs in a menu.
    /// </summary>
    internal sealed class PauseMenuView : UserControl
    {
        public event EventHandler? Resumed;
        public event EventHandler? SettingsRequested;
        public event EventHandler? LeaveRequested;
        public event EventHandler? QuitRequested;
        public event EventHandler? FullscreenRequested;
        public event EventHandler? SpectateRequested;
        public event EventHandler? RejoinRequested;
        public event EventHandler? RecordToggleRequested;
        public event EventHandler? VoteMapRequested;
        public event EventHandler? ReplayControlsRequested;

        private readonly DeckButton _resume;
        private readonly DeckButton _voteYes;
        private readonly DeckButton _voteNo;
        private readonly StackPanel _menu;

        /// <param name="offerWindowMode">
        /// Show the fullscreen/windowed entry. False on a phone, which has one
        /// window, it is already the whole screen, and there is no F11.
        /// </param>
        public PauseMenuView(bool offerWindowMode)
        {
            Background = Brushes.Transparent;
            Focusable = true;

            // Tighter than the column of words it replaces: each entry now
            // carries its own edge, and fourteen points between two objects
            // that already have a bottom lip is a gap.
            var menu = new StackPanel { Spacing = 6, Width = 230 };
            _menu = menu;
            // Titles only. Every entry here used to say what it did twice --
            // "Quit", "Close FruityPrime" -- and the second saying is what
            // made a seven-line menu tall enough to be cut off by the window
            // it is drawn over.
            _resume = Add(menu, "Resume", () => Resumed?.Invoke(this, EventArgs.Empty),
                Deck.Face.Moss);
            if (DemoPlayback.IsActive)
            {
                Add(menu, "Replay controls",
                    () => ReplayControlsRequested?.Invoke(this, EventArgs.Empty),
                    Deck.Face.Blue);
            }
            _voteYes = Add(menu, "Accept map vote", () => AnswerVote(true), Deck.Face.Moss);
            _voteNo = Add(menu, "Deny map vote", () => AnswerVote(false), Deck.Face.Rust);
            RefreshVote();
            var voteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            voteTimer.Tick += (_, _) => RefreshVote();
            AttachedToVisualTree += (_, _) => voteTimer.Start();
            DetachedFromVisualTree += (_, _) => voteTimer.Stop();
            if (!DemoPlayback.IsActive && NetSession.Active)
            {
                // Offered whenever there is a server to ask, rather than only
                // when a vote could pass right now: the reasons it cannot --
                // somebody else's vote is running, the room is still cooling
                // down -- are things the player wants told to them, and an
                // entry that quietly disappears tells them nothing.
                Add(menu, "Vote map", () => VoteMapRequested?.Invoke(this, EventArgs.Empty));
            }
            if (!DemoPlayback.IsActive)
            {
                if (SpectatorMode.IsSpectating)
                {
                    Add(menu, "Rejoin match",
                        () => RejoinRequested?.Invoke(this, EventArgs.Empty));
                }
                else if (SpectatorMode.CanSpectate)
                {
                    Add(menu, "Spectate", () => SpectateRequested?.Invoke(this, EventArgs.Empty));
                }
            }
            if (offerWindowMode)
            {
                // The game thread does it on the next frame; the label is
                // rebuilt here straight away so it is not a lie for 16
                // milliseconds.
                Add(menu, WindowLabel(),
                    () => FullscreenRequested?.Invoke(this, EventArgs.Empty));
            }
            if (!DemoPlayback.IsActive && NetSession.Active)
            {
                Add(menu, DemoRecorder.IsRecording ? "Stop recording" : "Record demo",
                    () => RecordToggleRequested?.Invoke(this, EventArgs.Empty));
            }
            Add(menu, "Settings", () => SettingsRequested?.Invoke(this, EventArgs.Empty));
            Add(menu, "Leave match", () => LeaveRequested?.Invoke(this, EventArgs.Empty),
                Deck.Face.Brass);
            Add(menu, "Quit", () => QuitRequested?.Invoke(this, EventArgs.Empty),
                Deck.Face.Rust);

            // Centred, like every other screen behind the front one. Each
            // word is centred in the column rather than the column being
            // centred with the words left-aligned inside it: a ragged edge
            // down the middle of the frame is the thing that makes a centred
            // menu look like an accident.
            foreach (Control child in menu.Children)
            {
                child.HorizontalAlignment = HorizontalAlignment.Center;
            }
            // Shrunk to fit rather than scrolled. The host is the game window
            // and the game window is whatever size the player dragged it to;
            // a scrollbar's answer to that is a menu with its top and bottom
            // cut off, which is what "the menu is always bitten" was. There is
            // nothing here to reflow -- eight words in a column stay eight
            // words in a column, just smaller.
            _scaler = new LayoutTransformControl
            {
                Child = menu,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            // The menu and nothing else. It carried a "paused" heading and a
            // line saying which match you were in, and both were dropped: the
            // first says what the player has just done, with the match frozen
            // behind it saying the same thing, and the second names a match
            // they are looking straight at. Neither is something anybody
            // pressed Escape to find out. Every other screen keeps its
            // heading, because on every other screen the heading is the only
            // thing that says where you are.
            //
            // No pair of marks either, and that is deliberate. Every entry
            // here is an action; there is no question being asked, so there
            // is no yes and no to answer it with -- and Resume as a tick in
            // the corner while it is also the first word of the menu is one
            // action drawn twice.
            Content = UiLayout.Page(overGame: true, UiLayout.WellShort, "",
                strip: null, body: _scaler, centreBody: true);
            SizeChanged += (_, e) => FitToHost(e.NewSize.Height);
        }

        private readonly LayoutTransformControl _scaler;

        /// <summary>
        /// What the column needs at full size: eight words, their spacing, and
        /// the corner it is anchored in.
        /// </summary>
        private double NeededHeight
        {
            get
            {
                int count = 0;
                foreach (Control child in _menu.Children)
                {
                    if (child.IsVisible) count++;
                }
                return count * 26 + Math.Max(0, count - 1) * 6
                    + UiLayout.WellTop + UiLayout.WellBottom + 70;
            }
        }

        internal void RefreshVote()
        {
            bool visible = MapVote.Active && !MapVote.Answered && !DemoPlayback.IsActive;
            if (_voteYes.IsVisible == visible && _voteNo.IsVisible == visible) return;
            bool refocus = !visible && (_voteYes.IsFocused || _voteNo.IsFocused);
            _voteYes.IsVisible = _voteNo.IsVisible = visible;
            if (refocus) _resume.Focus();
            FitToHost(Bounds.Height);
        }

        private void AnswerVote(bool yes)
        {
            MapVote.Cast(yes);
            RefreshVote();
            Resumed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Fit the column to the height it has been given, down to half size.
        /// Below that there is no menu either way, and a window that short is
        /// not one anybody is playing in.
        /// </summary>
        private void FitToHost(double height)
        {
            if (height <= 0)
            {
                return;
            }
            double scale = Math.Clamp(height / NeededHeight, 0.5, 1);
            if (_scaler.LayoutTransform is ScaleTransform current
                && Math.Abs(current.ScaleY - scale) < 0.001)
            {
                return;
            }
            _scaler.LayoutTransform = scale >= 1 ? null : new ScaleTransform(scale, scale);
        }

        /// <summary>
        /// Somebody who just asked for this is looking at a short list and
        /// expects the top entry to be the one already chosen.
        /// </summary>
        public void FocusResume()
        {
            Dispatcher.UIThread.Post(() => _resume.Focus(), DispatcherPriority.Background);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Resumed?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        private static string WindowLabel()
        {
            return WindowMode.IsFullscreen ? "Windowed" : "Fullscreen";
        }

        /// <summary>
        /// One entry. Still titles only -- what changed is that an entry is
        /// now an object you press rather than a word that brightens, which
        /// is what lets a menu over a running match read as a menu rather
        /// than as text that happens to be on top of the game.
        /// </summary>
        private static DeckButton Add(StackPanel menu, string text, Action action,
            Deck.Face? face = null)
        {
            // `.pentry`: `font-size: 1.2em`, `padding: .5em .8em`, a
            // four-point edge, and the full width of the card. Ems, not
            // points -- the third argument is a multiple of the frame's em
            // now, and 17 of them is a label the height of the menu.
            var entry = new DeckButton(text, face ?? Deck.Face.Slate,
                sizeEms: 1.2, padXEms: 0.8, padYEms: 0.5, lip: 4)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            entry.Click += (_, _) => action();
            menu.Children.Add(entry);
            return entry;
        }
    }
}
