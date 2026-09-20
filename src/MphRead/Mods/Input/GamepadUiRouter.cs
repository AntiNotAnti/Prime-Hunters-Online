using System;
using System.Threading;

namespace MphRead.Mods.Input
{
    public enum UiAction { Up, Down, Left, Right, Accept, Back, PreviousTab, NextTab, PageUp, PageDown }
    public enum GamepadContext { Gameplay, Menu, BindingCapture, TextEntry, Results }

    public static class GamepadContexts
    {
        private static bool _menu, _capture, _focused = true;
        private static long _revision;
        public static long Revision => Interlocked.Read(ref _revision);
        public static bool MenuVisible
        {
            get => Volatile.Read(ref _menu);
            set { if (_menu != value) { Volatile.Write(ref _menu, value); Interlocked.Increment(ref _revision); if (value) GamepadHaptics.Stop(); } }
        }
        public static bool Capturing
        {
            get => Volatile.Read(ref _capture);
            set { if (_capture != value) { Volatile.Write(ref _capture, value); Interlocked.Increment(ref _revision); } }
        }
        public static bool Focused
        {
            get => Volatile.Read(ref _focused);
            set
            {
                if (_focused == value) return;
                Volatile.Write(ref _focused, value);
                if (!value) { GamepadHaptics.Stop(); AimInputSourceTracker.Reset(); }
                // Polling may run while unfocused, or miss the whole focus
                // transition. Both edges must re-arm the held-input barrier.
                Interlocked.Increment(ref _revision);
            }
        }
        public static GamepadContext Current { get; set; }
        public static GamepadContext Resolve(bool textEntry = false, bool results = false)
            => Capturing ? GamepadContext.BindingCapture : MenuVisible ? GamepadContext.Menu
                : textEntry ? GamepadContext.TextEntry : results ? GamepadContext.Results : GamepadContext.Gameplay;
    }

    public sealed class GamepadEdges
    {
        private GamepadButtons _previous;
        private long _revision = -1;
        public GamepadButtons Update(GamepadSnapshot snapshot)
        {
            var pressed = snapshot.Revision == _revision ? snapshot.State.Buttons & ~_previous : 0;
            _previous = snapshot.State.Buttons;
            _revision = snapshot.Revision;
            return pressed;
        }
    }

    // A router belongs to one UI host. No toolkit dependencies or wall-clock sleeps.
    public sealed class GamepadUiRouter
    {
        private readonly GamepadEdges _edges = new();
        private UiAction? _direction;
        private long _started, _next, _revision = -1, _contextRevision = -1;
        private GamepadContext _context;
        private bool _neutralRequired;
        private bool _leftTrigger, _rightTrigger;
        public event Action<UiAction>? Action;
        public void Reset() { _direction = null; _neutralRequired = true; }
        public void Update(GamepadSnapshot snapshot, GamepadContext context, long milliseconds)
        {
            // Menu thresholds are independent from gameplay actuation preferences.
            var menuState = snapshot.State;
            if (((GamepadManager.ActiveDevice?.Capabilities ?? GamepadCapabilities.None) & GamepadCapabilities.AnalogTriggers) != 0)
            {
                _leftTrigger = menuState.LeftTrigger >= (_leftTrigger ? .30f : .45f);
                _rightTrigger = menuState.RightTrigger >= (_rightTrigger ? .30f : .45f);
                menuState.Buttons &= ~(GamepadButtons.LeftTrigger | GamepadButtons.RightTrigger);
                if (_leftTrigger) menuState.Buttons |= GamepadButtons.LeftTrigger;
                if (_rightTrigger) menuState.Buttons |= GamepadButtons.RightTrigger;
                snapshot = snapshot with { State = menuState };
            }
            var pressed = _edges.Update(snapshot);
            long contextRevision = GamepadContexts.Revision;
            bool changed = _revision != snapshot.Revision || _context != context || _contextRevision != contextRevision;
            _revision = snapshot.Revision; _context = context; _contextRevision = contextRevision;
            if (changed) { Reset(); pressed = 0; }
            if (context != GamepadContext.Menu && context != GamepadContext.Results && context != GamepadContext.TextEntry) return;
            var state = snapshot.State;
            if (!state.Connected) { Reset(); return; }
            if (_neutralRequired)
            {
                if (state.Buttons != 0 || Math.Abs(state.LeftX) > .35f || Math.Abs(state.LeftY) > .35f) return;
                _neutralRequired = false;
            }
            UiAction? direction = state.Down(GamepadButtons.DpadUp) || state.LeftY > .55f ? UiAction.Up
                : state.Down(GamepadButtons.DpadDown) || state.LeftY < -.55f ? UiAction.Down
                : state.Down(GamepadButtons.DpadLeft) || state.LeftX < -.55f ? UiAction.Left
                : state.Down(GamepadButtons.DpadRight) || state.LeftX > .55f ? UiAction.Right : null;
            if (direction != _direction)
            {
                _direction = direction; _started = milliseconds; _next = milliseconds + 300;
                if (direction.HasValue) Action?.Invoke(direction.Value);
            }
            else if (direction.HasValue && milliseconds >= _next)
            {
                _next = milliseconds + (milliseconds - _started >= 1500 ? 55 : 90);
                Action?.Invoke(direction.Value);
            }
            // One action per press; Accept and Back never join the repeat path.
            if ((pressed & GamepadButtons.A) != 0) Action?.Invoke(UiAction.Accept);
            else if ((pressed & (GamepadButtons.B | GamepadButtons.Start)) != 0) Action?.Invoke(UiAction.Back);
            else if ((pressed & GamepadButtons.LeftBumper) != 0) Action?.Invoke(UiAction.PreviousTab);
            else if ((pressed & GamepadButtons.RightBumper) != 0) Action?.Invoke(UiAction.NextTab);
            else if ((pressed & GamepadButtons.LeftTrigger) != 0) Action?.Invoke(UiAction.PageUp);
            else if ((pressed & GamepadButtons.RightTrigger) != 0) Action?.Invoke(UiAction.PageDown);
        }
    }
}
