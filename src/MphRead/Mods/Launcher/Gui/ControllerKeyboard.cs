using System;
using System.Linq;
using Avalonia.VisualTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace MphRead.Mods.Launcher.Gui
{
    // Text entry stays inside the existing UI surface, including the headless desktop host.
    internal sealed class ControllerKeyboard
    {
        private readonly Popup _popup;
        private readonly Panel? _parent;
        private readonly TextBox _target;
        private readonly TextBlock _preview;
        private readonly Action _closed;
        private string _text;
        private bool _upper, _finished;
        public Control NavigationRoot { get; }
        public ControllerKeyboard(TextBox target, Action closed, bool captureOnly = false)
        {
            _target = target; _closed = closed; _text = target.Text ?? "";
            var panel = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
            _preview = new TextBlock { Text = _text, Foreground = GuiTheme.TextBrush, MaxWidth = 540,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            panel.Children.Add(_preview);
            foreach (string row in new[] { "1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm", ".:-_/@[]+" })
            {
                var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                foreach (char c in row)
                {
                    char character = c;
                    var button = new UiWord(c.ToString(), size: 24) { MinWidth = 28 };
                    ControllerNav.Identify(button, "keyboard.key." + (int)c, initial: c == '1');
                    button.Click += (_, _) => Append(_upper ? char.ToUpperInvariant(character).ToString() : character.ToString());
                    keys.Children.Add(button);
                }
                panel.Children.Add(keys);
            }
            var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            void Command(string label, Action act)
            {
                var word = new UiWord(label, size: 19); word.Click += (_, _) => act(); commands.Children.Add(word);
            }
            Command("Space", () => Append(" "));
            Command("Shift", () => _upper = !_upper);
            Command("Delete", () => { if (_text.Length > 0) _text = _text[..^1]; _preview.Text = _text; });
            Command("Done", () => Close(true)); Command("Cancel", () => Close(false));
            panel.Children.Add(commands);
            NavigationRoot = new Border { Background = GuiTheme.PanelBrush, BorderBrush = GuiTheme.AccentBrush,
                BorderThickness = new Thickness(1), Child = panel };
            NavigationRoot.SetValue(ControllerNav.NavScopeProperty, "keyboard");
            NavigationRoot.SetValue(ControllerNav.ModalProperty, true);
            if (captureOnly)
            {
                NavigationRoot.HorizontalAlignment = HorizontalAlignment.Center;
                NavigationRoot.VerticalAlignment = VerticalAlignment.Center;
            }
            _popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Center,
                IsLightDismissEnabled = false, ShouldUseOverlayLayer = true, Child = captureOnly ? null : NavigationRoot };
            _parent = target.GetVisualAncestors().OfType<Panel>().FirstOrDefault();
            _parent?.Children.Add(_popup);
            _popup.Closed += (_, _) => Close(false);
            _target.DetachedFromVisualTree += TargetDetached;
            if (!captureOnly) _popup.Open();
            TopLevel.GetTopLevel(target)?.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.Post(() => FocusNavigator.Ensure(NavigationRoot));
        }
        private void Append(string value)
        {
            if (_target.MaxLength > 0 && _text.Length + value.Length > _target.MaxLength) return;
            if (_text.Length >= 1024) return;
            _text += value; _preview.Text = _text;
        }
        private void TargetDetached(object? sender, VisualTreeAttachmentEventArgs e) => Close(false);
        public void Close(bool accept)
        {
            if (_finished) return;
            _finished = true;
            _target.DetachedFromVisualTree -= TargetDetached;
            if (accept) _target.SetCurrentValue(TextBox.TextProperty, _text);
            _popup.Close(); _popup.Child = null; _parent?.Children.Remove(_popup);
            if (TopLevel.GetTopLevel(_target) != null) _target.Focus();
            _closed();
        }
    }
}
