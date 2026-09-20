using System;
using System.Reflection;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using MphRead.Mods.Input;
using MphRead.Entities;
using MphRead.Mods;
using GlfwKeys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;
using GlfwMouse = OpenTK.Windowing.GraphicsLibraryFramework.MouseButton;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// One rebindable control: what it does on the left, what it is bound to on
    /// the right, click and press to change it.
    ///
    /// The awkward part is the same one the WinForms row had: this window is a
    /// toolkit's and the game is GLFW's, and their key enumerations agree only
    /// about printable ASCII -- Escape is 27 in one and 256 in the other. The
    /// map below covers everything a person is likely to bind; anything
    /// unmapped is refused rather than bound to whatever key happens to share
    /// its number.
    /// </summary>
    internal sealed class KeyRow : Control
    {
        static KeyRow() => AffectsRender<KeyRow>(IsFocusedProperty, IsEnabledProperty);

        private readonly PropertyInfo? _property;
        private readonly double _labelWidth;

        // The second mode: a plain Keys setting rather than one of
        // PlayerControls' Keybinds.
        //
        // The chat key is the only one, and it is not a Keybind because it
        // cannot be. Bindings are found by reflecting over PlayerControls,
        // which is upstream's type, and everything this project adds lives
        // under Mods/ so a pull stays a fast-forward -- so chat, which this
        // project added, has nowhere in that list to be. It is a keyboard-only
        // row: there is no sense in opening the chat line with the wheel.
        private readonly string? _label;
        private readonly Func<GlfwKeys>? _get;
        private readonly Action<GlfwKeys>? _set;
        private bool _listening;
        private readonly GamepadEdges _padEdges = new();
        private string? _controllerHint;
        internal bool Listening => _listening;
        internal string? BindingName => _property?.Name ?? _label;
        private bool _hot;
        private readonly Tap _tap = new();

        public event EventHandler? Rebound;

        public KeyRow(PropertyInfo property, double labelWidth = 160)
        {
            _property = property;
            _labelWidth = labelWidth;
            Height = 32;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        public KeyRow(string label, Func<GlfwKeys> get, Action<GlfwKeys> set, double labelWidth = 160)
        {
            _label = label;
            _get = get;
            _set = set;
            _labelWidth = labelWidth;
            Height = 32;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        private Rect Box => new(_labelWidth, 2,
            Math.Max(60, Bounds.Width - _labelWidth - 4), Bounds.Height - 4);

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            Focus();
            PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
            if (!_listening)
            {
                // Listening begins on the release, not here: a press that
                // starts a scroll down the Controls page would otherwise put
                // every row it passed over into "press a key". See Tap.
                if (Box.Contains(e.GetPosition(this)))
                {
                    _tap.Press(e, this);
                }
                e.Handled = true;
                base.OnPointerPressed(e);
                return;
            }
            // Already listening: this press is the new binding.
            GlfwMouse? button = properties.PointerUpdateKind switch
            {
                PointerUpdateKind.LeftButtonPressed => GlfwMouse.Left,
                PointerUpdateKind.RightButtonPressed => GlfwMouse.Right,
                PointerUpdateKind.MiddleButtonPressed => GlfwMouse.Middle,
                PointerUpdateKind.XButton1Pressed => GlfwMouse.Button4,
                PointerUpdateKind.XButton2Pressed => GlfwMouse.Button5,
                _ => null
            };
            if (button != null && _property != null)
            {
                InputSettings.Rebind(_property, ButtonType.Mouse, GlfwKeys.Unknown, button.Value);
                Done();
            }
            e.Handled = true;
            base.OnPointerPressed(e);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            _tap.Moved(e, this);
            base.OnPointerMoved(e);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (!_listening && _tap.Release(e, this) && Box.Contains(e.GetPosition(this)))
            {
                SetListening(true);
                InvalidateVisual();
            }
            base.OnPointerReleased(e);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            _tap.Cancel();
            base.OnPointerCaptureLost(e);
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            if (_listening && e.Delta.Y != 0 && _property != null)
            {
                InputSettings.Rebind(_property,
                    e.Delta.Y > 0 ? ButtonType.ScrollUp : ButtonType.ScrollDown,
                    GlfwKeys.Unknown, GlfwMouse.Left);
                Done();
                e.Handled = true;
            }
            base.OnPointerWheelChanged(e);
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            _hot = true;
            InvalidateVisual();
            base.OnPointerEntered(e);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            _hot = false;
            InvalidateVisual();
            base.OnPointerExited(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!_listening)
            {
                if (e.Key == Key.Enter || e.Key == Key.Space)
                {
                    SetListening(true);
                    InvalidateVisual();
                    e.Handled = true;
                }
                base.OnKeyDown(e);
                return;
            }
            // While listening every key belongs to this row, including the ones
            // the window would otherwise spend on moving the focus or closing.
            e.Handled = true;
            if (e.Key == Key.Escape)
            {
                Done();
                return;
            }
            if (e.Key == Key.Back || e.Key == Key.Delete)
            {
                Assign(GlfwKeys.Unknown);
                Done();
                return;
            }
            GlfwKeys? key = Translate(e.Key);
            if (key != null)
            {
                Assign(key.Value);
                Done();
            }
        }

        private void Assign(GlfwKeys key)
        {
            if (_set != null)
            {
                _set(key);
                return;
            }
            InputSettings.Rebind(_property!, ButtonType.Key, key, GlfwMouse.Left);
        }

        /// <summary>
        /// Whether any row anywhere is waiting for a key.
        ///
        /// The window's own keys -- F11, Alt+Enter -- are handled before the
        /// screens get a look in, because they are gestures at the window
        /// rather than input to a menu. That is right everywhere except here:
        /// a row asking "press a key" has to be able to be told F11, or F11 is
        /// the one key in the game nobody can bind.
        /// </summary>
        public static bool AnyListening { get; private set; }

        /// <summary>The one place the flag moves, so it cannot be left set.</summary>
        private void SetListening(bool value)
        {
            if (_listening == value)
            {
                return;
            }
            _listening = value;
            AnyListening = value;
            if (value)
            {
                _controllerHint = null;
                _padEdges.Update(GamepadManager.Snapshot);
            }
        }

        internal GamepadButtons ControllerPress(GamepadSnapshot snapshot) => _padEdges.Update(snapshot);

        // Controller presses bind game actions, never synthetic keyboard Enter/arrow keys.
        internal void OpenControllerBinding(GamepadButtons pressed = 0)
        {
            PadAction? action = BindingName switch
            {
                "Shoot" or "AltAttack" => PadAction.Shoot, "Jump" or "Boost" => PadAction.Jump,
                "Zoom" => PadAction.Zoom, "Morph" => PadAction.Morph, "Scan" => PadAction.Scan,
                "ScanVisor" => PadAction.ScanVisor, "WeaponMenu" => PadAction.WeaponWheel,
                "Pause" => PadAction.Scoreboard, "NextWeapon" => PadAction.NextWeapon,
                "PrevWeapon" => PadAction.PrevWeapon, "Missile" => PadAction.Missile,
                "PowerBeam" => PadAction.PowerBeam, "Chat" => PadAction.Chat,
                "VoltDriver" => PadAction.VoltDriver, "Battlehammer" => PadAction.Battlehammer,
                "Imperialist" => PadAction.Imperialist, "Judicator" => PadAction.Judicator,
                "Magmaul" => PadAction.Magmaul, "ShockCoil" => PadAction.ShockCoil,
                "OmegaCannon" => PadAction.OmegaCannon, "AffinitySlot" => PadAction.AffinitySlot, _ => null
            };
            var settings = this.GetVisualAncestors().OfType<SettingsView>().FirstOrDefault();
            SetListening(false);
            if (action.HasValue && settings != null)
            {
                settings.ShowSection("Controls", 1);
                TopLevel.GetTopLevel(settings)?.UpdateLayout();
                var row = settings.GetVisualDescendants().OfType<PadRow>().First(r => r.Action == action.Value);
                FocusNavigator.Focus(row);
                row.Capture(pressed);
                return;
            }
            _controllerHint = "Keyboard only; configure sticks under Gamepad";
            InvalidateVisual();
        }

        private void Done()
        {
            SetListening(false);
            InvalidateVisual();
            Rebound?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            SetListening(false);
            _controllerHint = null;
            InvalidateVisual();
            base.OnLostFocus(e);
        }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            InvalidateVisual();
            base.OnGotFocus(e);
        }

        /// <summary>The toolkit's key to the one the game's input layer speaks.</summary>
        private static GlfwKeys? Translate(Key key)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                return GlfwKeys.A + (key - Key.A);
            }
            if (key >= Key.D0 && key <= Key.D9)
            {
                return GlfwKeys.D0 + (key - Key.D0);
            }
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
            {
                return GlfwKeys.KeyPad0 + (key - Key.NumPad0);
            }
            if (key >= Key.F1 && key <= Key.F12)
            {
                return GlfwKeys.F1 + (key - Key.F1);
            }
            return key switch
            {
                Key.Space => GlfwKeys.Space,
                Key.Tab => GlfwKeys.Tab,
                Key.Enter => GlfwKeys.Enter,
                Key.LeftShift => GlfwKeys.LeftShift,
                Key.RightShift => GlfwKeys.RightShift,
                Key.LeftCtrl => GlfwKeys.LeftControl,
                Key.RightCtrl => GlfwKeys.RightControl,
                Key.LeftAlt => GlfwKeys.LeftAlt,
                Key.RightAlt => GlfwKeys.RightAlt,
                Key.Left => GlfwKeys.Left,
                Key.Right => GlfwKeys.Right,
                Key.Up => GlfwKeys.Up,
                Key.Down => GlfwKeys.Down,
                Key.Insert => GlfwKeys.Insert,
                Key.Home => GlfwKeys.Home,
                Key.End => GlfwKeys.End,
                Key.PageUp => GlfwKeys.PageUp,
                Key.PageDown => GlfwKeys.PageDown,
                Key.CapsLock => GlfwKeys.CapsLock,
                Key.OemMinus => GlfwKeys.Minus,
                Key.OemPlus => GlfwKeys.Equal,
                Key.OemOpenBrackets => GlfwKeys.LeftBracket,
                Key.OemCloseBrackets => GlfwKeys.RightBracket,
                Key.OemSemicolon => GlfwKeys.Semicolon,
                Key.OemQuotes => GlfwKeys.Apostrophe,
                Key.OemComma => GlfwKeys.Comma,
                Key.OemPeriod => GlfwKeys.Period,
                Key.OemQuestion => GlfwKeys.Slash,
                Key.OemBackslash or Key.OemPipe => GlfwKeys.Backslash,
                Key.OemTilde => GlfwKeys.GraveAccent,
                Key.Add => GlfwKeys.KeyPadAdd,
                Key.Subtract => GlfwKeys.KeyPadSubtract,
                Key.Multiply => GlfwKeys.KeyPadMultiply,
                Key.Divide => GlfwKeys.KeyPadDivide,
                _ => null
            };
        }

        public override void Render(DrawingContext context)
        {
            // A row on a sub-page that is not showing is attached to the tree
            // and rendered once all the same, and a control that has never
            // been arranged has zero Bounds -- which makes Box four points
            // *negative* and MaxTextHeight below throw. That exception comes
            // out of the compositor's own pass, so nothing here catches it and
            // the process goes down the moment Controls is opened. There is
            // nothing to draw at this size anyway.
            if (Bounds.Width <= 0 || Bounds.Height <= 0)
            {
                return;
            }
            // See UiWord.Render: hit testing follows the drawing.
            context.FillRectangle(Brushes.Transparent,
                new Rect(0, 0, Bounds.Width, Bounds.Height));
            FormattedText label = TrackedText.Make(
                _label ?? InputSettings.ActionName(_property!), 12,
                bold: true, GuiTheme.TextBrush);
            context.DrawText(label, new Point(4, (Bounds.Height - label.Height) / 2));

            Rect box = Box;
            context.DrawRectangle(GuiTheme.PanelLightBrush,
                new Pen(new SolidColorBrush(_listening ? GuiTheme.Warm
                    : IsFocused || _hot ? GuiTheme.Accent : GuiTheme.Edge), 1),
                new RoundedRect(box, 4));

            string text = _listening
                ? (_get != null ? "press a key" : "press a key, a mouse button or the wheel")
                : _controllerHint ?? (_get != null
                    ? (_get() == GlfwKeys.Unknown ? "none" : InputSettings.KeyName(_get()))
                    : InputSettings.Describe(InputSettings.Bind(_property!)));
            FormattedText value = TrackedText.Make(text, 12, bold: true,
                new SolidColorBrush(_listening ? GuiTheme.Warm : GuiTheme.Text));
            // Never wider than the box: a binding nobody has heard of should
            // not push its own frame off the row.
            value.MaxTextWidth = Math.Max(20, box.Width - 12);
            value.MaxTextHeight = box.Height;
            value.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(value, new Point(box.X + (box.Width - value.Width) / 2,
                box.Y + (box.Height - value.Height) / 2));
        }
    }
}
