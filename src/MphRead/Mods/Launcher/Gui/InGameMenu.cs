using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MphRead.Mods.Network;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// What Escape shows during a match: resume, the window mode, the
    /// settings, and the two ways out -- drawn inside the game window.
    ///
    /// This replaces <c>PauseMenuWindow</c> and the two windows it opened.
    /// Those were borderless, topmost, sized to the game's client rectangle
    /// and moved onto it again on every frame, because a menu that is a second
    /// window has to keep pretending it is not one -- and it stopped being
    /// convincing the moment anything went wrong: a compositor with no
    /// transparency drew the scrim opaque, alt-tab listed two entries, and a
    /// window manager that would not honour "topmost" put the match in front
    /// of its own menu.
    ///
    /// Now it is a stack of screens over a transparent root, rendered by
    /// <c>UiSurface</c> and composited onto the frame. The match is
    /// still running behind it -- a networked one cannot be paused -- which is
    /// what the scrim is for, and which is now literally true of the picture
    /// rather than a property of a window flag.
    ///
    /// The screens themselves are the ones the front screen and the Android
    /// head already use, so there is still exactly one pause menu, one
    /// settings page and one map picker in this program.
    /// </summary>
    internal sealed class InGameMenu : Panel
    {
        private readonly MenuSettings _settings;
        private readonly ScenePlayerRegistry? _players;
        private readonly List<Control> _stack = new();

        /// <summary>Raised when the last screen is popped: back to the match.</summary>
        public event EventHandler? Emptied;

        public InGameMenu(MenuSettings settings, ScenePlayerRegistry? players = null)
        {
            _settings = settings;
            _players = players;
            // Transparent all the way down: each screen paints its own scrim,
            // and what neither of them paints is the match.
            Background = Brushes.Transparent;
            Focusable = true;
            OpenPauseMenu();
        }

        /// <summary>The screen on top, for the key handling that belongs to it.</summary>
        public Control? Top => _stack.Count > 0 ? _stack[^1] : null;

        private void Push(Control view)
        {
            if (_stack.Count > 0)
            {
                _stack[^1].IsVisible = false;
            }
            _stack.Add(view);
            Children.Add(view);
            view.Focus();
        }

        private void Pop()
        {
            if (_stack.Count == 0)
            {
                return;
            }
            Control top = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            Children.Remove(top);
            if (_stack.Count > 0)
            {
                _stack[^1].IsVisible = true;
                _stack[^1].Focus();
                return;
            }
            Emptied?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Escape, from the game window. The top screen decides what it means:
        /// one level up, or out of the menu altogether.
        /// </summary>
        public void Back()
        {
            Pop();
        }

        private void OpenPauseMenu()
        {
            // The window mode is offered here and not on Android for the
            // reason it always was: there is a window to change.
            var view = new PauseMenuView(offerWindowMode: true);
            view.Resumed += (_, _) => Pop();
            view.FullscreenRequested += (_, _) =>
            {
                PauseMenu.RequestFullscreenToggle();
                Pop();
            };
            view.SettingsRequested += (_, _) => OpenSettings();
            view.ReplayControlsRequested += (_, _) => OpenReplayControls();
            view.VoteMapRequested += (_, _) => OpenVote();
            view.SpectateRequested += (_, _) => { SpectatorMode.Start(); Pop(); };
            view.RejoinRequested += (_, _) => { SpectatorMode.Rejoin(); Pop(); };
            view.RecordToggleRequested += (_, _) =>
            {
                if (DemoRecorder.IsRecording)
                {
                    Console.WriteLine($"[demo] recording saved to {DemoRecorder.CurrentPath}");
                    DemoRecorder.Stop();
                }
                else
                {
                    DemoRecorder.Start();
                }
                Pop();
            };
            view.LeaveRequested += (_, _) => { PauseMenu.RequestLeave(); Pop(); };
            view.QuitRequested += (_, _) => { PauseMenu.RequestQuit(); Pop(); };
            Push(view);
            view.FocusResume();
        }

        private void OpenReplayControls()
        {
            var view = new ReplayControlsView();
            view.Closed += (_, _) => Pop();
            view.ResumeRequested += (_, _) =>
            {
                Pop(); // replay controls
                Pop(); // pause menu
            };
            Push(view);
        }

        private void OpenSettings()
        {
            var view = new SettingsView(_settings, inGame: true, players: _players);
            view.Closed += (_, _) => Pop();
            // Placing the pen zone happens on the game, not in a settings
            // page, so the whole menu gets out of the way: the view has
            // already put itself into placement mode and what is left is for
            // the player to be able to see what they are drawing on.
            view.StylusPlacementRequested += (_, _) =>
            {
                while (_stack.Count > 0)
                {
                    Pop();
                }
            };
            Push(view);
        }

        /// <summary>
        /// Pick a map and put it to the room -- the same screen a match is
        /// chosen from, with the strip of sources taken away, because calling
        /// a vote is picking a map.
        /// </summary>
        private void OpenVote()
        {
            string why = MapVote.WhyNotProposing();
            if (why.Length > 0)
            {
                // In the game's own chat rather than in a box here: it is one
                // sentence, the player is about to go back to the match, and a
                // dialog for it is a second thing to dismiss.
                Chat.ChatBox.System(why);
                Pop();
                return;
            }
            IReadOnlyList<string> rooms;
            try
            {
                rooms = ThumbnailGenerator.MultiplayerRooms();
            }
            catch (Exception ex)
            {
                Chat.ChatBox.System("no maps to vote for");
                Mods.DebugLog.Exception("pause", ex);
                return;
            }
            if (rooms.Count == 0)
            {
                Chat.ChatBox.System("no maps to vote for");
                return;
            }
            var view = new PlayScreen(_settings, rooms, PlayScreen.Face.Vote, overGame: true);
            view.Closed += (_, _) => Pop();
            view.Voted += (_, room) =>
            {
                MapVote.Propose(room);
                // This and the pause menu under it: the answer arrives as a
                // prompt over the match, which is not a thing to read through
                // a menu.
                Pop();
                Pop();
            };
            Push(view);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Pop();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }
    }
}
