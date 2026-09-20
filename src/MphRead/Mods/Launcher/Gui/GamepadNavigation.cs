using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MphRead.Mods.Input;

namespace MphRead.Mods.Launcher.Gui
{
    internal sealed class GamepadNavigation
    {
        public event Action? Changed;
        private readonly GamepadUiRouter _router = new();
        private Control? _root;
        private ControllerKeyboard? _keyboard;
        public GamepadNavigation() { _router.Action += Dispatch; }
        public void Update(Control root)
        {
            if (!GamepadContexts.Focused) { _router.Reset(); return; }
            if (_root != root) { _root = root; _router.Reset(); }
            var snapshot = GamepadManager.Snapshot;
            var focused = FocusNavigator.Focused(root);
            if (focused is PadRow padRow && GamepadContexts.Capturing)
            {
                // Binding capture owns controller input before menu routing. Feed the
                // exact snapshot published by this UI tick directly to the row so a
                // physical press cannot be lost between dispatcher timer callbacks.
                padRow.Check(snapshot);
                _router.Reset();
                return;
            }
            if (focused is KeyRow { Listening: true } keyRow)
            {
                var pressed = keyRow.ControllerPress(snapshot);
                if (pressed != 0)
                {
                    keyRow.OpenControllerBinding(pressed);
                    _router.Reset(); Changed?.Invoke(); return;
                }
            }
            _router.Update(snapshot, GamepadContexts.Capturing
                ? GamepadContext.BindingCapture : GamepadContext.Menu, Environment.TickCount64);
        }
        private void Dispatch(UiAction action)
        {
            Changed?.Invoke();
            Control? root = _keyboard?.NavigationRoot ?? _root;
            if (root == null) return;
            var focused = FocusNavigator.Ensure(root);
            if (focused == null) return;
            if (action == UiAction.Accept && focused is KeyRow keyboardRow)
            {
                keyboardRow.OpenControllerBinding(); _router.Reset(); return;
            }
            if (action == UiAction.Accept && focused is TextBox text && _keyboard == null)
            {
                _keyboard = new ControllerKeyboard(text, () => { _keyboard = null; _router.Reset(); });
                return;
            }
            if (action == UiAction.Back && _keyboard != null) { _keyboard.Close(false); return; }
            if (action == UiAction.PreviousTab || action == UiAction.NextTab)
            {
                var tabs = root.GetVisualDescendants().OfType<UiTabs>().FirstOrDefault(t => t.IsEffectivelyVisible);
                if (tabs != null) { tabs.Index += action == UiAction.NextTab ? 1 : -1; FocusNavigator.Ensure(root); }
                return;
            }
            if (action == UiAction.PageUp || action == UiAction.PageDown)
            {
                var scroll = focused.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
                if (scroll != null) scroll.Offset = new Avalonia.Vector(scroll.Offset.X,
                    Math.Max(0, scroll.Offset.Y + (action == UiAction.PageDown ? 1 : -1) * scroll.Viewport.Height * .8));
                return;
            }
            if (action == UiAction.Accept || action == UiAction.Back)
            {
                FocusNavigator.Key(focused, action == UiAction.Accept ? Key.Enter : Key.Escape);
                return;
            }
            Key key = action switch { UiAction.Up => Key.Up, UiAction.Down => Key.Down,
                UiAction.Left => Key.Left, _ => Key.Right };
            // Editable controls own their horizontal arrows; up/down always leave a row.
            if ((action == UiAction.Left || action == UiAction.Right)
                && (focused is ChoiceRow || focused is SliderRow || focused is PadRow || focused is TextBox))
            {
                if (FocusNavigator.Key(focused, key)) return;
            }
            FocusNavigator.Move(root, action);
        }
    }
}
