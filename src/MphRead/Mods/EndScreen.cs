using System;
using MphRead.Entities;
using MphRead.Mods.Launcher;
using MphRead.Mods.Network;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MphRead.Mods
{
    /// <summary>
    /// Who to come back as, asked at the one moment there is nothing else to
    /// do: the results screen.
    ///
    /// The question used to live in the pause menu, two rows down a list of
    /// eight, and it was the wrong place for it in every way that matters.
    /// Opening the pause menu mid-match is a thing you do instead of playing,
    /// so the one screen where changing hunter is free was the one screen that
    /// never offered it -- and a drop-down row reading "Respawn as: SYLUX" is
    /// a settings control, not a choice anybody makes in the eight seconds
    /// between two maps. So both rows are gone from there, and the question is
    /// asked here instead, over the results, with the hunter's own portrait
    /// and an arrow either side of it.
    ///
    /// The answer is <see cref="RespawnChoice"/>'s, unchanged: it is cashed in
    /// at the next spawn, which after a match is the first spawn on the next
    /// map. Nothing here applies anything itself.
    ///
    /// <para>
    /// Only for a player. A demo has nobody to ask, a spectator has no player
    /// to change, and the adventure is one hunter's story.
    /// </para>
    /// </summary>
    public static class EndScreen
    {
        /// <summary>
        /// Post-match hunter/suit changes are intentionally disabled for now.
        /// The results/map-choice lifecycle remains active; players can change
        /// their hunter from the lobby before the next match instead.
        /// </summary>
        public const bool CharacterChangeEnabled = false;

        /// <summary>
        /// Whether the themed results side panel has something to show. A
        /// persistent lobby has no results ballot, so with character changes
        /// disabled it should leave the scoreboard unobstructed.
        /// </summary>
        public static bool PanelAvailable => Available
            && (CharacterChangeEnabled || !NetSession.PersistentLobby);
        /// <summary>
        /// Whether the results screen is up and this machine has a player who
        /// could pick something.
        /// </summary>
        public static bool Available
        {
            get
            {
                if (!GameState.Multiplayer || GameState.MenuPause
                    || DemoPlayback.IsActive || KillCam.IsFinal
                    || SpectatorMode.IsSpectating
                    || SpectatorMode.FreeCamera || PlayerEntity.Main == null)
                {
                    return false;
                }
                return GameState.MatchState == MatchState.GameOver
                    || GameState.MatchState == MatchState.Ending;
            }
        }

        /// <summary>
        /// Whether the deck panel is drawn over the results, so the HUD's own
        /// picker knows to leave the right-hand side alone.
        ///
        /// Set by whichever head is showing it -- the desktop shell through
        /// its surface, Android through its own -- because the two put the
        /// same panel on the screen by different routes and the engine must
        /// not have to know which. The scoreboard beside it is untouched
        /// either way: that is the engine's screen and a scoreboard is not a
        /// place to put a theme.
        /// </summary>
        public static bool PanelUp
        {
            // A field, because it is written on the toolkit's thread and read
            // inside the render loop: an auto-property there is one the JIT
            // may hoist out of the loop, and a results HUD that goes on
            // drawing its own picker under an opaque panel publishes a preview
            // slot the panel's own model is then painted into.
            get => _panelUp;
            set => _panelUp = value;
        }

        private static volatile bool _panelUp;

        /// <summary>
        /// Legacy post-match ready state. The post-match lifecycle no longer
        /// waits on it; map/return selection is the only between-match action.
        /// Kept so older intent/state plumbing remains source-compatible.
        /// </summary>
        public static bool Ready { get; private set; }

        /// <summary>
        /// Forget the answer. Called when the results screen stops being
        /// available, which is the start of the next match.
        /// </summary>
        public static void ClearReady()
        {
            Ready = false;
        }

        public static void ToggleReady()
        {
            // Compatibility shim for older callers. Ready no longer participates
            // in post-match progression, and current UIs expose no Ready action.
            Ready = false;
        }

        /// <summary>The hunter queued for the next spawn, which is what is drawn.</summary>
        public static Hunter Hunter => RespawnChoice.Hunter;

        /// <summary>The suit queued for the next spawn, 0-3.</summary>
        public static int Suit => PlayerColors.Clamp(RespawnChoice.Color);

        /// <summary>
        /// The map the server says is next, or "" when there is no server or
        /// it has not said.
        ///
        /// <c>MatchStatePacket.NextRoomKey</c> has been on the wire since the
        /// rotation was written and nothing has ever read it. It is the one
        /// thing a results screen can say that a scoreboard cannot: everybody
        /// is about to be moved somewhere, and this is where they find out
        /// where.
        /// </summary>
        public static string NextRoomKey
        {
            get
            {
                MatchStatePacket? state = NetSession.ServerMatch;
                if (state == null)
                {
                    return "";
                }
                return state.Value.NextRoomKey ?? "";
            }
        }

        /// <summary>The next map's name as a person knows it, or its key.</summary>
        public static string NextRoomName
        {
            get
            {
                string key = NextRoomKey;
                if (key.Length == 0)
                {
                    return "";
                }
                try
                {
                    (RoomMetadata? meta, _) = Metadata.GetRoomByName(key);
                    return meta?.InGameName ?? key;
                }
                catch (Exception)
                {
                    return key;
                }
            }
        }

        // ------------------------------------------------------- the pointer

        /// <summary>
        /// A rectangle on the window, 0-1 each way.
        ///
        /// Kept in the window's own coordinates rather than the HUD's 256x192
        /// because that is what a mouse position arrives in, and because the
        /// HUD's horizontal unit is a different size on every window shape
        /// (see <c>HudAspectFix</c>) -- converting once, in the draw, is one
        /// place to be wrong instead of two.
        /// </summary>
        public readonly struct Hit
        {
            public readonly float Left;
            public readonly float Top;
            public readonly float Right;
            public readonly float Bottom;

            public Hit(float left, float top, float right, float bottom)
            {
                Left = left;
                Top = top;
                Right = right;
                Bottom = bottom;
            }

            public bool Contains(float x, float y)
            {
                return Right > Left && Bottom > Top
                    && x >= Left && x < Right && y >= Top && y < Bottom;
            }
        }

        /// <summary>
        /// What the panel put where, last time it was drawn.
        ///
        /// Published by the draw rather than worked out again here, so the
        /// boxes cannot drift from the picture: there is one layout, it is
        /// computed once a frame in <c>ModDrawEndScreen</c>, and this is a
        /// copy of it. Empty until the panel has been drawn at least once,
        /// which is also exactly when there is nothing to click.
        /// </summary>
        private static Hit _hitPrev;
        private static Hit _hitNext;
        private static readonly Hit[] _hitSuits = new Hit[PlayerColors.Count];

        public static float PointerX { get; private set; } = -1;
        public static float PointerY { get; private set; } = -1;

        /// <summary>Called once a frame by the window, in window fractions.</summary>
        public static void NotePointer(float x, float y)
        {
            PointerX = x;
            PointerY = y;
        }

        private static bool _wasUp;
        private static double _resentAt;

        /// <summary>
        /// The screen coming up and going away again, which two other things
        /// hang off: the map ballot (<see cref="MapPick"/>) and the previews
        /// drawn on it, whose textures belong to the room that is about to be
        /// unloaded.
        ///
        /// Called once a frame by the window, beside
        /// <see cref="NotePointer"/>, because this is the only code that runs
        /// every frame of a results screen whether or not anybody is drawing
        /// one -- a spectator, a demo and a player all reach it.
        /// </summary>
        public static void Tick(string roomKey, double time)
        {
            bool up = Available;
            if (up == _wasUp)
            {
                if (up && time - _resentAt >= 1)
                {
                    _resentAt = time;
                    MapPick.Resend();
                }
                return;
            }
            _wasUp = up;
            _resentAt = time;
            // The previews are textures in the scene's own counted names, and
            // the counter restarts with every room: nothing may survive a map
            // change. Cleared on both edges rather than only on the way in, so
            // a match that ends some other way (a disconnect, a leave) does not
            // leave four bindings behind for the next room to overwrite.
            Render.MapThumbnail.Clear();
            if (!up)
            {
                MapPick.Reset();
                return;
            }
            // The room list, read once. Open straight away offline, where this
            // machine is the whole room; online it waits for the server to say
            // the ballot is open, since a server built before this exists
            // never will and the screen should then look exactly as it did.
            MapPick.Begin(roomKey, open: !Network.NetSession.Active);
        }

        public static void NoteLayout(Hit previous, Hit next, Hit[] suits, Hit ready = default)
        {
            _hitPrev = previous;
            _hitNext = next;
            for (int i = 0; i < _hitSuits.Length && i < suits.Length; i++)
            {
                _hitSuits[i] = suits[i];
            }
        }

        /// <summary>Which suit swatch the pointer is over, or -1.</summary>
        public static int HoveredSuit()
        {
            if (!CharacterChangeEnabled || !Available)
            {
                return -1;
            }
            for (int i = 0; i < _hitSuits.Length; i++)
            {
                if (_hitSuits[i].Contains(PointerX, PointerY))
                {
                    return i;
                }
            }
            return -1;
        }

        public static bool HoveredPrev => CharacterChangeEnabled && Available
            && _hitPrev.Contains(PointerX, PointerY);
        public static bool HoveredNext => CharacterChangeEnabled && Available
            && _hitNext.Contains(PointerX, PointerY);
        // Kept for source compatibility with older result renderers. There is
        // no post-match Ready target anymore.
        public static bool HoveredReady => false;

        /// <summary>
        /// A left click, offered before anything else sees it. Returns true
        /// when the picker took it.
        ///
        /// A suit is chosen by clicking it rather than by stepping through
        /// four of them, because there are four and they are all on screen --
        /// arrows are for the hunter, where there are seven and only one is
        /// shown at a time.
        /// </summary>
        public static bool HandleClick()
        {
            if (!Available)
            {
                return false;
            }
            if (CharacterChangeEnabled)
            {
                if (_hitPrev.Contains(PointerX, PointerY))
                {
                    Step(-1, 0);
                    return true;
                }
                if (_hitNext.Contains(PointerX, PointerY))
                {
                    Step(1, 0);
                    return true;
                }
                for (int i = 0; i < _hitSuits.Length; i++)
                {
                    if (_hitSuits[i].Contains(PointerX, PointerY))
                    {
                        Choose(Hunter, i);
                        return true;
                    }
                }
            }
            // The ballot under the picker. Last only because it is the
            // cheapest test to reach; the two layouts do not overlap.
            return MapPick.HandleClick();
        }

        /// <summary>
        /// A key press, taken before the game sees it. Returns true when it
        /// was one of ours, so nothing else acts on it.
        ///
        /// The arrow keys, and only while the results are up. Nothing is bound
        /// to them during a match, and there is nothing else to press here --
        /// every control the player has is switched off for the length of the
        /// end sequence -- so this claims no key anybody could want back.
        /// </summary>
        public static bool HandleKeyDown(Keys key)
        {
            if (!Available)
            {
                return false;
            }
            switch (key)
            {
                case Keys.Left:
                    if (CharacterChangeEnabled)
                    {
                        Step(-1, 0);
                        return true;
                    }
                    return false;
                case Keys.Right:
                    if (CharacterChangeEnabled)
                    {
                        Step(1, 0);
                        return true;
                    }
                    return false;
                case Keys.Up:
                    StepList(-1);
                    return true;
                case Keys.Down:
                    StepList(1);
                    return true;
                case Keys.Space:
                case Keys.Enter:
                case Keys.KeyPadEnter:
                    if (MapPick.Available)
                    {
                        MapPick.ChooseCursor();
                        return true;
                    }
                    return false;
            }
            return false;
        }

        /// <summary>
        /// The same from a pad's d-pad, taken once a frame rather than from an
        /// event: GLFW reports a pad by polling, so there is no press to hook.
        /// </summary>
        private static readonly Input.GamepadUiRouter ResultPad = CreateResultPad();
        private static Input.GamepadUiRouter CreateResultPad()
        {
            var router = new Input.GamepadUiRouter();
            router.Action += action =>
            {
                switch (action)
                {
                    case Input.UiAction.Left:
                        if (CharacterChangeEnabled) Step(-1, 0);
                        break;
                    case Input.UiAction.Right:
                        if (CharacterChangeEnabled) Step(1, 0);
                        break;
                    case Input.UiAction.Up: StepList(-1); break;
                    case Input.UiAction.Down: StepList(1); break;
                    case Input.UiAction.Accept:
                    case Input.UiAction.NextTab:
                        if (MapPick.Available) MapPick.ChooseCursor();
                        break;
                }
            };
            return router;
        }
        public static void PollGamepad()
        {
            ResultPad.Update(Input.GamepadManager.Snapshot, Available && Input.GamepadContexts.Focused
                && Input.GamepadContexts.Current == Input.GamepadContext.Results
                ? Input.GamepadContext.Results : Input.GamepadContext.Gameplay,
                Environment.TickCount64);
        }

        /// <summary>
        /// Move the choice and keep it.
        ///
        /// Written to <c>launcher.txt</c> on every press rather than on the
        /// way out, for the reason the pause menu did the same: a results
        /// screen is somewhere people alt-F4 from, and a choice made and lost
        /// is worse than no choice at all. It is a handful of writes across a
        /// ten-second screen, which is nothing next to what the match itself
        /// was doing a second ago.
        /// </summary>
        /// <summary>
        /// Up and down: the map ballot while there is one, and the suit
        /// otherwise.
        ///
        /// The ballot wins the arrows because a list is what they are for and
        /// because the suit already has a better answer -- all four swatches
        /// are on screen and are clicked directly, which is why they were laid
        /// out that way rather than stepped through. The arrows were a bonus
        /// there and are the only way to move a list.
        /// </summary>
        private static void StepList(int by)
        {
            if (MapPick.Available)
            {
                MapPick.Step(by);
                return;
            }
            if (CharacterChangeEnabled)
            {
                Step(0, by);
            }
        }

        private static void Step(int hunterBy, int suitBy)
        {
            if (!CharacterChangeEnabled)
            {
                return;
            }
            int hunter = (int)Launcher.Hunters.Resolve(Hunter);
            if (hunterBy != 0)
            {
                hunter = ((hunter + hunterBy) % Hunters.Playable + Hunters.Playable)
                    % Hunters.Playable;
            }
            int suit = Suit;
            if (suitBy != 0)
            {
                suit = ((suit + suitBy) % PlayerColors.Count + PlayerColors.Count)
                    % PlayerColors.Count;
            }
            Choose((Hunter)hunter, suit);
        }

        /// <summary>
        /// Set both, from a screen that offers them as rows rather than as
        /// arrows and swatches.
        ///
        /// The deck panel over the results asks the question with a stepper
        /// and a turntable, which is the same question this screen has always
        /// asked; what it does not have is the HUD's own hit rectangles, so it
        /// needs a way in that is not "pretend the player clicked a swatch".
        /// </summary>
        public static void Pick(Hunter hunter, int suit)
        {
            if (CharacterChangeEnabled)
            {
                Choose(hunter, suit);
            }
        }

        private static void Choose(Hunter hunter, int suit)
        {
            RespawnChoice.Request(hunter, suit);
            LauncherPrefs.LastHunter = hunter;
            LauncherPrefs.LastColor = suit;
            LauncherPrefs.Save();
        }
    }
}
