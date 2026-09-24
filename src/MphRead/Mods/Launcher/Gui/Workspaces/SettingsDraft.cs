#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using MphRead.Mods.Input;
namespace MphRead.Mods.Launcher.Gui
{
    // Capture values, not controls reconstructed from settings.json. This preserves
    // scroll, focus and category state during Apply/Discard and route changes.
    internal sealed class SettingsDraft
    {
        private readonly List<(Func<object?> Read, Action<object?> Write, object? Saved)> _values = new();
        private readonly Dictionary<GamepadRuntimeConfig, string[]> _pads = new();
        public SettingsDraft(Control pages)
        {
            foreach (var node in pages.GetLogicalDescendants().OfType<Control>())
            {
                if (node.GetLogicalAncestors().OfType<GamepadSettingsPanel>().Any()) continue;
                switch (node)
                {
                    case ChoiceRow row: Add(() => row.Index, v => row.Index = (int)v!); break;
                    case SliderRow row: Add(() => row.Value, v => row.Value = (int)v!); break;
                    case ToggleRow row: Add(() => row.On, v => row.On = (bool)v!); break;
                    case ButtonToggleRow row: Add(() => row.On, v => row.On = (bool)v!); break;
                    case FieldRow row: Add(() => row.Value, v => row.Value = (string)v!); break;
                }
            }
            foreach (var property in InputSettings.Bindings)
            {
                Add(() => { var bind = InputSettings.Bind(property); return (bind.Type, bind.Key, bind.MouseButton); }, v =>
                {
                    var saved = ((MphRead.Entities.ButtonType Type, OpenTK.Windowing.GraphicsLibraryFramework.Keys Key,
                        OpenTK.Windowing.GraphicsLibraryFramework.MouseButton MouseButton))v!;
                    // Restore the exact snapshot, including unused key fields in
                    // default mouse bindings; Rebind normalizes those fields.
                    var bind = InputSettings.Bind(property);
                    bind.Type = saved.Type; bind.Key = saved.Key; bind.MouseButton = saved.MouseButton;
                });
            }
            foreach (var property in typeof(InputSettings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                if (property.CanRead && property.CanWrite && (property.PropertyType.IsValueType || property.PropertyType == typeof(string)))
                    Add(() => property.GetValue(null), v => property.SetValue(null, v));
            TrackController();
        }
        private void Add(Func<object?> read, Action<object?> write) => _values.Add((read, write, read()));
        public void TrackController()
        {
            var runtime = GamepadRuntimeConfig.Current;
            if (!_pads.ContainsKey(runtime)) _pads.Add(runtime, Lines(runtime));
        }
        private static string[] Lines(GamepadRuntimeConfig runtime)
        {
            var lines = new List<string>(); runtime.Options.Write(lines); runtime.Bindings.Write(lines);
            lines.Add("prime_preset=" + runtime.Bindings.Preset);
            return lines.ToArray();
        }
        public bool IsDirty => _values.Any(v => !Equals(v.Saved, v.Read()))
            || _pads.Any(p => !p.Value.SequenceEqual(Lines(p.Key)));
        public void Accept()
        {
            for (int i = 0; i < _values.Count; i++) { var v = _values[i]; _values[i] = (v.Read, v.Write, v.Read()); }
            foreach (var pad in _pads.Keys.ToArray()) _pads[pad] = Lines(pad);
        }
        public void Discard()
        {
            foreach (var v in _values) v.Write(v.Saved);
            foreach (var (pad, lines) in _pads)
            {
                pad.Options.Load(lines); pad.Bindings.Reset();
                foreach (var line in lines)
                {
                    int split = line.IndexOf('=');
                    if (split < 0) continue;
                    string key = line[..split], value = line[(split + 1)..];
                    if (key != "prime_preset") pad.Bindings.TryLoad(key, value);
                }
                pad.Bindings.LoadSlots(lines);
                // Loading individual slots marks the runtime Custom; retain the
                // original preset only after all bindings have been restored.
                pad.Bindings.Preset = lines.First(l => l.StartsWith("prime_preset=", StringComparison.Ordinal))[13..];
            }
        }
    }
}
#endif
