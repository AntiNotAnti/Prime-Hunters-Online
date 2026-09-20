using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Modal confirmation in the same visual language as the FPS hub.
    /// Cancel is always the initial action so an accidental Accept can never
    /// walk straight through a destructive prompt.
    /// </summary>
    internal sealed class ConfirmScreen : UserControl
    {
        public event EventHandler<bool>? Answered;

        private readonly HubNavButton _no;

        public ConfirmScreen(string question, string yes = "yes", string no = "no",
            bool overGame = false)
        {
            this.SetValue(ControllerNav.NavScopeProperty, "confirmation");
            this.SetValue(ControllerNav.ModalProperty, true);
            Focusable = true;

            var root = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(
                    overGame ? (byte)0xb8 : (byte)0xcc, 0x02, 0x07, 0x0d))
            };

            var cardBody = new StackPanel
            {
                Margin = new Thickness(22),
                Spacing = 12
            };
            cardBody.Children.Add(new TextBlock
            {
                Text = "CONFIRM ACTION",
                FontFamily = HubTheme.DataBold,
                FontSize = 8.5,
                Foreground = HubTheme.WarmBrush
            });
            cardBody.Children.Add(new TextBlock
            {
                Text = question.ToUpperInvariant(),
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.Bold,
                FontSize = 24,
                Foreground = HubTheme.TextBrush,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left
            });
            cardBody.Children.Add(new Border
            {
                Height = 1,
                Background = HubTheme.EdgeBrush,
                Margin = new Thickness(0, 1, 0, 2)
            });

            var actions = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                ColumnSpacing = 8
            };
            _no = new HubNavButton(no.ToUpperInvariant(),
                "Return without applying this action",
                compact: true, primary: true);
            ControllerNav.Identify(_no, "confirmation.cancel", initial: true);
            _no.SetValue(ControllerNav.NavRightProperty, "confirmation.accept");
            _no.Click += (_, _) => Answered?.Invoke(this, false);
            actions.Children.Add(_no);

            var ok = new HubNavButton(yes.ToUpperInvariant(),
                compact: true, accent: HubTheme.Danger);
            ControllerNav.Identify(ok, "confirmation.accept");
            ok.SetValue(ControllerNav.NavLeftProperty, "confirmation.cancel");
            ok.Click += (_, _) => Answered?.Invoke(this, true);
            Grid.SetColumn(ok, 1);
            actions.Children.Add(ok);
            cardBody.Children.Add(actions);

            cardBody.Children.Add(new TextBlock
            {
                Text = "ESC / BACK  CANCEL",
                FontFamily = HubTheme.Data,
                FontSize = 8,
                Foreground = HubTheme.TextDimBrush
            });

            var card = new Border
            {
                MaxWidth = 460,
                Margin = new Thickness(24),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = HubTheme.PanelStrongBrush,
                BorderBrush = HubTheme.WarmBrush,
                BorderThickness = new Thickness(1, 1, 1, 2),
                Child = cardBody
            };
            root.Children.Add(card);
            Content = root;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Dispatcher.UIThread.Post(() => _no.Focus(),
                DispatcherPriority.Background);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Answered?.Invoke(this, false);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }
    }
}
