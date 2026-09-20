using System;
using System.Collections.Generic;
using System.Linq;

namespace MphRead.Mods.Input
{
    public sealed record GamepadRawSample(string DeviceId, string Guid, string Name, float[] Axes, bool[] Buttons, byte[] Hats);

    // Samples are supplied by the desktop host. The wizard never enters GLFW from a UI callback.
    public sealed class GamepadMappingWizard
    {
        public static string? RequestedDevice { get; set; }
        public static GamepadRawSample? Latest { get; internal set; }
        private static readonly (string Key, string Prompt)[] Steps =
        {
            ("a", "Press the south face button (A / Cross)."), ("b", "Press the east face button (B / Circle)."),
            ("x", "Press the west face button (X / Square)."), ("y", "Press the north face button (Y / Triangle)."),
            ("leftshoulder", "Press the left bumper."), ("rightshoulder", "Press the right bumper."),
            ("back", "Press Back / Share."), ("start", "Press Start / Options."),
            ("leftstick", "Click the left stick."), ("rightstick", "Click the right stick."),
            ("dpup", "Press D-pad up."), ("dpright", "Press D-pad right."),
            ("dpdown", "Press D-pad down."), ("dpleft", "Press D-pad left."),
            ("leftx", "Push the left stick fully right."), ("lefty", "Push the left stick fully up."),
            ("rightx", "Push the right stick fully right."), ("righty", "Push the right stick fully up."),
            ("lefttrigger", "Fully squeeze the left trigger."), ("righttrigger", "Fully squeeze the right trigger.")
        };
        private readonly GamepadRawSample _rest;
        private readonly List<string> _bindings = new();
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);
        private bool _release;
        public int Step => _bindings.Count;
        public bool Complete => Step == Steps.Length && !_release;
        public string Prompt => _release ? "Release the control and center both sticks."
            : Step == Steps.Length ? "Mapping complete. Apply to save it for this controller."
            : $"{Step + 1}/{Steps.Length}: {Steps[Step].Prompt}";
        public GamepadMappingWizard(GamepadRawSample rest) { _rest = rest; }
        public void Sample(GamepadRawSample sample)
        {
            if (sample.DeviceId != _rest.DeviceId || sample.Axes.Length != _rest.Axes.Length
                || sample.Buttons.Length != _rest.Buttons.Length || sample.Hats.Length != _rest.Hats.Length)
                throw new InvalidOperationException("Controller changed. Restart mapping.");
            if (_release)
            {
                bool resting = true;
                for (int i = 0; i < sample.Axes.Length; i++) resting &= Math.Abs(sample.Axes[i] - _rest.Axes[i]) < .2f;
                for (int i = 0; i < sample.Buttons.Length; i++) resting &= sample.Buttons[i] == _rest.Buttons[i];
                for (int i = 0; i < sample.Hats.Length; i++) resting &= sample.Hats[i] == _rest.Hats[i];
                if (resting) _release = false;
                return;
            }
            if (Complete) return;
            string key = Steps[Step].Key; string? binding = null;
            bool stick = key.EndsWith("x", StringComparison.Ordinal) && key.Length > 1 || key.EndsWith("y", StringComparison.Ordinal) && key.Length > 1;
            if (!stick)
            {
                for (int i = 0; i < sample.Buttons.Length; i++)
                    if (sample.Buttons[i] && !_rest.Buttons[i]) { binding = "b" + i; break; }
                if (key.StartsWith("dp", StringComparison.Ordinal)) for (int i = 0; i < sample.Hats.Length; i++)
                    if (sample.Hats[i] is 1 or 2 or 4 or 8) { binding = $"h{i}.{sample.Hats[i]}"; break; }
            }
            if (stick || key.EndsWith("trigger", StringComparison.Ordinal))
                for (int i = 0; i < sample.Axes.Length; i++)
                {
                    float delta = sample.Axes[i] - _rest.Axes[i];
                    if (!float.IsFinite(delta) || Math.Abs(delta) < .65f) continue;
                    if (stick) binding = "a" + i + ((key.EndsWith("y", StringComparison.Ordinal) ? delta > 0 : delta < 0) ? "~" : "");
                    else binding = Math.Abs(_rest.Axes[i]) < .2f ? (delta > 0 ? "+a" : "-a") + i : "a" + i + (delta < 0 ? "~" : "");
                    break;
                }
            if (binding == null || !_used.Add(binding.TrimEnd('~'))) return;
            _bindings.Add(key + ":" + binding); _release = true;
        }
        public string Mapping
        {
            get
            {
                if (!Complete) throw new InvalidOperationException("Finish every mapping step first.");
                if (_rest.Guid.Length != 32 || !_rest.Guid.All(Uri.IsHexDigit)) throw new InvalidOperationException("Invalid controller identifier.");
                string name = new(_rest.Name.Where(c => !char.IsControl(c) && c != ',').Take(100).ToArray());
                string platform = OperatingSystem.IsMacOS() ? "Mac OS X" : OperatingSystem.IsWindows() ? "Windows" : "Linux";
                return _rest.Guid + "," + name + "," + string.Join(",", _bindings) + ",platform:" + platform + ",";
            }
        }
    }
}
