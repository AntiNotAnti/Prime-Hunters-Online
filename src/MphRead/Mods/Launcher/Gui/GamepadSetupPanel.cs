using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MphRead.Mods.Input;

namespace MphRead.Mods.Launcher.Gui
{
    internal sealed class GamepadSetupPanel : StackPanel
    {
        private readonly Note _status = new("Calibration measures drift and trigger travel. Manual mapping is available on desktop.");
        private readonly DispatcherTimer _timer;
        private readonly Action _changed;
        private readonly Func<long> _clock;
        private GamepadButtons _buttons;
        private readonly UiWord _apply;
        private GamepadCalibration? _calibration;
        private GamepadMappingWizard? _mapping;
        private string? _device;
        private long _started, _revision;
        private bool _mappingMode, _complete;
        public GamepadSetupPanel(Action changed, Func<long>? clock = null)
        {
            _changed = changed; _clock = clock ?? (() => Environment.TickCount64); Spacing = 8;
            void Button(string id, string label, Action action)
            {
                var button = new UiWord(label, 13); ControllerNav.Identify(button, id); button.Click += (_, _) => action(); Children.Add(button);
            }
            Button("setup.calibrate_sticks_and_triggers", "Calibrate sticks and triggers", () => Start(false));
            if (!OperatingSystem.IsAndroid()) Button("setup.map_controller_buttons_and_axes", "Map controller buttons and axes", () => Start(true));
            if (!OperatingSystem.IsAndroid()) Button("setup.reset_custom_controller_mappings", "Reset custom controller mappings", () =>
            {
                try { GamepadMappings.ResetOverrides(); Stop("Custom mappings reset. Restart the game to restore platform mappings."); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { _status.Text = ex.Message; }
            });
            _apply = new UiWord("Apply measured setup", 13) { IsEnabled = false };
            ControllerNav.Identify(_apply, "setup.apply");
            _apply.Click += (_, _) => Apply(); Children.Add(_apply);
            Button("setup.cancel_setup", "Cancel setup", () => Stop("Setup canceled. Settings unchanged."));
            Children.Add(_status);
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _timer.Tick += (_, _) => Tick();
            DetachedFromVisualTree += (_, _) => Stop("");
        }
        private void Start(bool mapping)
        {
            Stop("");
            var snapshot = GamepadManager.Snapshot;
            if (snapshot.DeviceId == null) { _status.Text = "Connect and select a controller first."; return; }
            _buttons = snapshot.State.Buttons;
            _device = snapshot.DeviceId; _revision = snapshot.Revision; _mappingMode = mapping;
            _started = _clock(); _complete = false;
            _calibration = mapping ? null : new(); _mapping = null;
            GamepadContexts.Capturing = true;
            if (mapping) { GamepadMappingWizard.Latest = null; GamepadMappingWizard.RequestedDevice = _device; }
            _status.Text = "Release all controls. Keep both sticks centered. Esc or Cancel stops setup.";
            _timer.Start();
        }
        internal void Tick()
        {
            if (_device == null || _complete) return;
            var snapshot = GamepadManager.Snapshot;
            if (!GamepadContexts.Focused || snapshot.DeviceId != _device || snapshot.Revision != _revision)
            { Stop("Controller or focus changed. Restart setup."); return; }
            long elapsed = _clock() - _started;
            var pressed = snapshot.State.Buttons & ~_buttons; _buttons = snapshot.State.Buttons;
            if (!_mappingMode && (pressed & GamepadButtons.B) != 0) { Stop("Calibration canceled. Settings unchanged."); return; }
            if (_mappingMode)
            {
                var sample = GamepadMappingWizard.Latest;
                if (sample == null || sample.DeviceId != _device) return;
                if (_mapping == null)
                {
                    if (sample.Buttons.Any(b => b) || sample.Hats.Any(h => h != 0)) { _started = _clock(); return; }
                    if (elapsed < 1500) return;
                    _mapping = new(sample);
                }
                try { _mapping.Sample(sample); }
                catch (InvalidOperationException ex) { Stop(ex.Message); return; }
                _status.Text = _mapping.Prompt + " Esc or Cancel stops setup.";
                _complete = _mapping.Complete;
            }
            else
            {
                var device = GamepadManager.ActiveDevice;
                if (device == null) return;
                // Allow the button that opened setup to be released before measuring rest.
                if (elapsed < 1000) return;
                _calibration!.Sample(device.Value.RawState, elapsed < 3500);
                _status.Text = elapsed < 3500 ? "Keep sticks and triggers released. Measuring rest… B cancels."
                    : "Rotate both sticks fully and squeeze/release both triggers. " + Math.Max(0, (10500 - elapsed) / 1000) + " seconds remaining. B cancels.";
                if (elapsed >= 10500) { _complete = true; _status.Text = _calibration.Summary; }
            }
            if (_complete)
            {
                _timer.Stop(); GamepadContexts.Capturing = false; GamepadMappingWizard.RequestedDevice = null;
                _apply.IsEnabled = _mappingMode || _calibration!.Valid;
            }
        }
        private void Apply()
        {
            if (!_complete) return;
            if (GamepadManager.Snapshot.DeviceId != _device) { Stop("Controller changed. Restart setup."); return; }
            try
            {
                if (_mappingMode) GamepadMappings.SaveOverride(_mapping!.Mapping); else _calibration!.Apply();
                Stop("Setup applied. Save settings or a named profile to retain calibration."); _changed();
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { _status.Text = "Could not apply setup: " + ex.Message; }
        }
        private void Stop(string message)
        {
            if (_device != null) GamepadContexts.Capturing = false;
            _device = null; _complete = false; _timer?.Stop(); _apply.IsEnabled = false;
            GamepadMappingWizard.RequestedDevice = null; GamepadMappingWizard.Latest = null; _status.Text = message;
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (_device != null && e.Key == Key.Escape) { Stop("Setup canceled. Settings unchanged."); e.Handled = true; }
            base.OnKeyDown(e);
        }
    }
}
