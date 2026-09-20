using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MphRead.Mods.Input;

namespace MphRead.Mods.Launcher.Gui
{
    // A live check in Settings lets players distinguish hardware layout from action bindings.
    internal sealed class GamepadMonitor : Control
    {
        private readonly DispatcherTimer _timer;
        private GamepadState _state, _raw;
        private string _status = "", _buttons = "", _actions = "";
        internal string Status => _status;
        public GamepadMonitor()
        {
            Height = 182;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += (_, _) => { if (IsEffectivelyVisible) Refresh(); };
            AttachedToVisualTree += (_, _) => { Refresh(); _timer.Start(); };
            DetachedFromVisualTree += (_, _) => _timer.Stop();
        }
        internal void Refresh()
        {
            var device = GamepadManager.ActiveDevice;
            var state = GamepadManager.Snapshot.State;
            _raw = device?.RawState ?? default;
            string status = device is { } connected ? connected.Name + " | " + connected.Mapping
                : "No controller detected. Connect it and press a button.";
            string buttons = state.Buttons == 0 ? "Press a button to test it" : PadBindings.Describe(state.Buttons);
            string actions = state.Buttons == 0 ? "Sticks move/aim; triggers should only fill the LT/RT bars."
                : "Assigned: " + GamepadProbe.Actions(state.Buttons);
            if (status == _status && buttons == _buttons && actions == _actions && state.Equals(_state)) return;
            _state = state; _status = status; _buttons = buttons; _actions = actions;
            InvalidateVisual();
#if MPHREAD_SHELL
            UiSurface.Current?.Invalidate();
#endif
        }
        public override void Render(DrawingContext context)
        {
            void Text(string text, double x, double y, double width, bool dim = false)
            {
                var formatted = TrackedText.Make(text, 11, false, dim ? GuiTheme.TextDimBrush : GuiTheme.TextBrush);
                formatted.MaxTextWidth = Math.Max(1, width);
                formatted.MaxTextHeight = 25;
                formatted.Trimming = TextTrimming.CharacterEllipsis;
                context.DrawText(formatted, new Point(x, y));
            }
            Text(_status, 4, 3, Bounds.Width - 8);
            void Stick(string label, double x, float axisX, float axisY, float rawX, float rawY, float dead)
            {
                var center = new Point(x + 27, 60);
                context.DrawEllipse(GuiTheme.PanelLightBrush, new Pen(GuiTheme.TextDimBrush, 1), center, 23, 23);
                context.DrawEllipse(null, new Pen(GuiTheme.TextDimBrush, 1), center, 23 * dead, 23 * dead);
                context.DrawEllipse(null, new Pen(GuiTheme.WarmBrush, 1), new Point(center.X + rawX * 19, center.Y - rawY * 19), 4, 4);
                context.DrawEllipse(GuiTheme.AccentBrush, null, new Point(center.X + axisX * 19, center.Y - axisY * 19), 4, 4);
                Text(label, x, 88, 75, true);
            }
            Stick("Left stick", 10, _state.LeftX, _state.LeftY, _raw.LeftX, _raw.LeftY, GamepadOptions.LeftInner);
            Stick("Right stick", 96, _state.RightX, _state.RightY, _raw.RightX, _raw.RightY, GamepadOptions.RightInner);
            void Trigger(string label, double y, float value)
            {
                Text(label, 190, y - 1, 24, true);
                double width = Math.Clamp(Bounds.Width - 274, 30, 160);
                context.DrawRectangle(GuiTheme.PanelLightBrush, null, new Rect(218, y, width, 12));
                context.DrawRectangle(GuiTheme.AccentBrush, null, new Rect(218, y, width * Math.Clamp(value, 0, 1), 12));
                double threshold = 218 + width * GamepadOptions.TriggerThreshold;
                context.DrawLine(new Pen(GuiTheme.TextBrush, 1), new Point(threshold, y), new Point(threshold, y + 12));
                Text(value.ToString("0.00"), 224 + width, y - 1, 45, true);
            }
            Trigger("LT", 42, _state.LeftTrigger); Trigger("RT", 70, _state.RightTrigger);
            Text(_buttons, 4, 112, Bounds.Width - 8);
            Text(_actions, 4, 135, Bounds.Width - 8, true);
            Text($"Raw LT {_raw.LeftTrigger:0.00} RT {_raw.RightTrigger:0.00} | center L {GamepadOptions.LeftCalibration.CenterX:0.00},{GamepadOptions.LeftCalibration.CenterY:0.00} R {GamepadOptions.RightCalibration.CenterX:0.00},{GamepadOptions.RightCalibration.CenterY:0.00}", 4, 158, Bounds.Width - 8, true);
        }
    }
}
