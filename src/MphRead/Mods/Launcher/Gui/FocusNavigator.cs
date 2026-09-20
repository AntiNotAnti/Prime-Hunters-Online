using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MphRead.Mods.Input;

namespace MphRead.Mods.Launcher.Gui
{
    internal static class FocusNavigator
    {
        private static bool Eligible(Control control, Control root)
        {
            if (!control.Focusable || !control.IsEffectivelyVisible || !control.IsEffectivelyEnabled
                || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return false;
            // Screen roots use Focusable for keyboard bubbling, not as menu entries.
            if (control is UserControl) return false;
            for (Visual? v = control; v != null && v != root; v = v.GetVisualParent())
                if (!v.IsVisible || (v is InputElement input && !input.IsHitTestVisible)) return false;
            return true;
        }
        public static Control? Focused(Control root)
        {
            root = ControllerNav.ModalRoot(root);
            var focused = TopLevel.GetTopLevel(root)?.FocusManager?.GetFocusedElement() as Control;
            return focused != null && (focused == root || root.IsVisualAncestorOf(focused))
                && Eligible(focused, root) ? focused : null;
        }
        public static Control? Ensure(Control root)
        {
            root = ControllerNav.ModalRoot(root);
            var current = Focused(root);
            if (current != null) return current;
            current = root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => Eligible(c, root) && c.GetValue(ControllerNav.NavDefaultProperty))
                ?? root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => Eligible(c, root));
            Focus(current);
            return current;
        }
        public static void Focus(Control? control)
        {
            if (control == null) return;
            control.Focus(NavigationMethod.Directional);
            TopLevel.GetTopLevel(control)?.UpdateLayout();
            control.BringIntoView();
        }
        public static bool Key(Control control, Key key)
        {
            var down = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = control };
            control.RaiseEvent(down);
            control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, Source = control });
            return down.Handled;
        }
        public static void Move(Control root, UiAction direction)
        {
            var current = Ensure(root);
            if (current == null) return;
            Control scope = ControllerNav.Scope(current, root);
            var explicitTarget = ControllerNav.Find(scope, ControllerNav.Neighbor(current, direction));
            if (explicitTarget != null && Eligible(explicitTarget, scope)) { Focus(explicitTarget); return; }
            root = scope;
            var origin = current.TranslatePoint(new Point(current.Bounds.Width / 2, current.Bounds.Height / 2), root);
            if (!origin.HasValue) return;
            bool vertical = direction == UiAction.Up || direction == UiAction.Down;
            double sign = direction == UiAction.Up || direction == UiAction.Left ? -1 : 1;
            Control? best = null;
            Control? wrapped = null;
            double score = double.MaxValue;
            double wrapScore = double.MaxValue;
            foreach (var candidate in root.GetVisualDescendants().OfType<Control>())
            {
                if (candidate == current || !Eligible(candidate, root)) continue;
                var point = candidate.TranslatePoint(new Point(candidate.Bounds.Width / 2, candidate.Bounds.Height / 2), root);
                if (!point.HasValue) continue;
                double dx = point.Value.X - origin.Value.X, dy = point.Value.Y - origin.Value.Y;
                double forward = (vertical ? dy : dx) * sign, across = Math.Abs(vertical ? dx : dy);
                if (forward <= 1)
                {
                    double distanceBack = forward + across * 3;
                    if (distanceBack < wrapScore) { wrapScore = distanceBack; wrapped = candidate; }
                    continue;
                }
                double distance = forward + across * 3;
                if (distance < score) { score = distance; best = candidate; }
            }
            if (best == null && root.GetValue(ControllerNav.NavWrapProperty))
                best = wrapped;
            Focus(best);
        }
    }
}
