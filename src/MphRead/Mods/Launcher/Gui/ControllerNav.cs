using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MphRead.Mods.Input;

namespace MphRead.Mods.Launcher.Gui
{
    // Stable semantic identifiers survive rebuilding a screen and translated labels.
    public sealed class ControllerNav : AvaloniaObject
    {
        public static readonly AttachedProperty<string?> NavIdProperty = AvaloniaProperty.RegisterAttached<ControllerNav, Control, string?>("NavId");
        public static readonly AttachedProperty<string?> NavScopeProperty = AvaloniaProperty.RegisterAttached<ControllerNav, Control, string?>("NavScope");
        public static readonly AttachedProperty<bool> NavDefaultProperty = AvaloniaProperty.RegisterAttached<ControllerNav, Control, bool>("NavDefault");
        public static readonly AttachedProperty<bool> NavWrapProperty = AvaloniaProperty.RegisterAttached<ControllerNav, Control, bool>("NavWrap");
        public static readonly AttachedProperty<bool> ModalProperty = AvaloniaProperty.RegisterAttached<ControllerNav, Control, bool>("Modal");
        internal static Control ModalRoot(Control root) => root.GetVisualDescendants().OfType<Control>()
            .LastOrDefault(c => c.GetValue(ModalProperty) && c.IsEffectivelyVisible) ?? root;
        public static readonly AttachedProperty<string?> NavUpProperty = AvaloniaProperty.RegisterAttached<ControllerNav, Control, string?>("NavUp");
        public static readonly AttachedProperty<string?> NavDownProperty = AvaloniaProperty.RegisterAttached<ControllerNav, Control, string?>("NavDown");
        public static readonly AttachedProperty<string?> NavLeftProperty = AvaloniaProperty.RegisterAttached<ControllerNav, Control, string?>("NavLeft");
        public static readonly AttachedProperty<string?> NavRightProperty = AvaloniaProperty.RegisterAttached<ControllerNav, Control, string?>("NavRight");
        internal static Control Scope(Control control, Control root)
            => control.GetVisualAncestors().OfType<Control>().FirstOrDefault(c => c.GetValue(NavScopeProperty) != null) ?? root;
        internal static string? Neighbor(Control control, UiAction action) => control.GetValue(action switch {
            UiAction.Up => NavUpProperty, UiAction.Down => NavDownProperty, UiAction.Left => NavLeftProperty, _ => NavRightProperty });
        internal static Control? Find(Control root, string? id) => id == null ? null
            : root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.GetValue(NavIdProperty) == id);
        public static void Identify(Control control, string id, bool initial = false)
        { control.SetValue(NavIdProperty, id); control.SetValue(NavDefaultProperty, initial); }
    }
    internal sealed class ControllerNavScope
    {
        private string? _focusedId;
        public void Capture(Control root) => _focusedId = FocusNavigator.Focused(root)?.GetValue(ControllerNav.NavIdProperty);
        public void Restore(Control root)
        {
            var control = ControllerNav.Find(root, _focusedId);
            if (control is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }) FocusNavigator.Focus(control);
            else FocusNavigator.Ensure(root);
        }
    }
}
