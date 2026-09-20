using System;
using System.Collections.Generic;
using System.IO;

namespace MphRead.Mods.Input
{
    internal static class GamepadChecks
    {
        private static int _checks;
        public static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            _checks++;
        }
        private static void Near(float actual, float expected, string message)
            => Check(Math.Abs(actual - expected) < .0001f, message);
        private static GamepadState State(GamepadButtons buttons = 0, float x = 0, float trigger = 0)
            => new() { Connected = true, Name = "test", Buttons = buttons, LeftX = x, RightTrigger = trigger };
        public static int Run(string? shots = null)
        {
            try
            {
                GamepadPlatformChecks.Run();
                string install = Path.Combine(Path.GetTempPath(), "mapping fixture", "Fruity Prime.app", "Contents", "MacOS");
                string resources = Path.Combine(Path.GetDirectoryName(install)!, "Resources");
                string settings = Path.Combine(Path.GetTempPath(), "mapping user settings");
                string[] mappingPaths = GamepadMappings.Paths(resources, settings);
                Check(mappingPaths.Length == 2
                    && mappingPaths[0] == Path.Combine(Path.GetDirectoryName(install)!, "Resources", GamepadMappings.FileName)
                    && mappingPaths[1] == Path.Combine(settings, GamepadMappings.FileName), "macOS mappings load resources then user overrides");
                mappingPaths = GamepadMappings.Paths(install, install);
                Check(mappingPaths.Length == 1 && mappingPaths[0] == Path.Combine(install, GamepadMappings.FileName),
                    "portable mapping paths remain beside executable without duplicate loads");
                mappingPaths = GamepadMappings.Paths(settings, install);
                Check(mappingPaths.Length == 2 && mappingPaths[0] == Path.Combine(settings, GamepadMappings.FileName),
                    "unbundled macOS mapping path remains portable");
                GamepadPlatformChecks.Run();
                GamepadEnhancementChecks.Run();
                AimAssist.AimAssistChecks.Run();
                ControllerRuntimeChecks.Run();
                GamepadOptions.Reset();
                var dead = GamepadAnalog.ApplyRadialDeadZone(.1f, .1f, .2f);
                Check(dead == (0, 0), "radial inner deadzone");
                var diagonal = GamepadAnalog.ApplyRadialDeadZone(1, 1, .2f);
                Near(diagonal.X, MathF.Sqrt(.5f), "diagonal normalized");
                Near(diagonal.X * diagonal.X + diagonal.Y * diagonal.Y, 1, "maximum magnitude");
                Near(GamepadAnalog.ApplyRadialDeadZone(.8f, 0, .2f, .2f).X, 1, "outer deadzone");
                Check(GamepadAnalog.ApplyRadialDeadZone(float.NaN, 0, .2f) == (0, 0), "invalid axis neutral");
                Near(GamepadAnalog.ApplyResponseCurve(.5f, GamepadCurve.Classic), .25f, "classic curve");
                Near(GamepadAnalog.ApplyResponseCurve(-.5f, GamepadCurve.Linear), -.5f, "linear sign");
                Check(GamepadAnalog.QuantizeMovement(.4f, .4f) == (1, 1), "diagonal movement threshold");
                Check(GamepadAnalog.QuantizeMovement(.49f, 0) == (0, 0), "neutral movement threshold");
                for (int i = 0; i < 8; i++)
                {
                    float angle = i * MathF.PI / 4;
                    var direction = GamepadAnalog.QuantizeMovement(.6f * MathF.Cos(angle), .6f * MathF.Sin(angle));
                    Check(direction != (0, 0), "all directions engage equally");
                }
                Check(!GamepadAnalog.Trigger(0, false), "resting trigger");
                Check(GamepadAnalog.Trigger(.61f, false), "trigger press");
                Check(GamepadAnalog.Trigger(.57f, true), "trigger hysteresis hold");
                Check(!GamepadAnalog.Trigger(.44f, true), "trigger release");
                Check(!GamepadAnalog.Trigger(0, true, .05f), "low threshold still releases");
                var bind = new Entities.Keybind(OpenTK.Windowing.GraphicsLibraryFramework.Keys.Space) { IsReleased = true };
                GamepadInput.Hold(bind, true, false);
                Check(bind.IsDown && !bind.IsReleased, "held controller cannot release through idle keyboard");
                bind.IsDown = true; bind.IsPressed = true;
                GamepadInput.Hold(bind, false, false);
                Check(bind.IsDown && bind.IsPressed, "controller never clears keyboard input");
                for (int i = 0; i < 6; i++)
                {
                    float angle = (i + .5f) * MathF.PI / 3;
                    var direction = WeaponSelectionDirection.FromStick(MathF.Sin(angle), MathF.Cos(angle));
                    Check(WeaponSelectionDirection.Resolve(direction.X, direction.Y) == i, "controller wheel sector " + i);
                    float arc = (i + .5f) * MathF.PI / 12;
                    Check(WeaponSelectionDirection.Resolve(MathF.Sin(arc), MathF.Cos(arc)) == i, "pointer wheel sector " + i);
                }
                Check(WeaponSelectionDirection.Resolve(0, 0) == -1, "resting wheel keeps weapon");
                var edge = new GamepadEdges();
                Check(edge.Update(new("one", State(), 1)) == 0, "first controller neutral");
                Check(edge.Update(new("one", State(GamepadButtons.A), 1)) == GamepadButtons.A, "button press");
                Check(edge.Update(new("one", State(GamepadButtons.A), 1)) == 0, "button hold");
                Check(edge.Update(new("one", State(), 1)) == 0, "button release");
                Check(edge.Update(new("one", State(GamepadButtons.A), 2)) == 0, "reconnect held");
                Check(edge.Update(new("two", State(GamepadButtons.B), 3)) == 0, "switch held");
                foreach (var device in GamepadManager.Devices) GamepadManager.RemoveDevice(device.DeviceId);
                GamepadManager.SelectDevice(null);
                GamepadManager.UpdateDevice("raw", State(), false);
                Check(GamepadManager.Snapshot.DeviceId == "raw", "first controller");
                GamepadManager.UpdateDevice("mapped", State(), true);
                Check(GamepadManager.Snapshot.DeviceId == "mapped", "mapped discovery preference");
                GamepadManager.UpdateDevice("raw", State(GamepadButtons.A), false);
                Check(GamepadManager.Snapshot.DeviceId == "raw", "button switches controller");
                GamepadManager.UpdateDevice("mapped", State(x: .1f), true);
                Check(GamepadManager.Snapshot.DeviceId == "raw", "drift cannot switch");
                GamepadManager.UpdateDevice("mapped", State(GamepadButtons.B), true);
                Check(GamepadManager.ActiveState.Buttons == GamepadButtons.B, "two controllers never merge");
                GamepadManager.SelectDevice("raw");
                GamepadManager.UpdateDevice("mapped", State(GamepadButtons.X), true);
                Check(GamepadManager.Snapshot.DeviceId == "raw", "explicit selection wins");
                GamepadManager.RemoveDevice("raw");
                Check(!GamepadManager.ActiveState.Connected && GamepadManager.ActiveState.Buttons == 0, "disconnect clears immediately");
                GamepadManager.UpdateDevice("mapped", State(x: .8f), true);
                Check(GamepadManager.Snapshot.DeviceId == "mapped", "fallback after removal");
                GamepadManager.UpdateDevice("mapped", State(trigger: .61f), true);
                Check(GamepadManager.ActiveState.Down(GamepadButtons.RightTrigger), "manager trigger press");
                GamepadManager.UpdateDevice("mapped", State(trigger: .57f), true);
                Check(GamepadManager.ActiveState.Down(GamepadButtons.RightTrigger), "manager trigger hold");
                GamepadManager.UpdateDevice("mapped", State(trigger: .44f), true);
                Check(!GamepadManager.ActiveState.Down(GamepadButtons.RightTrigger), "manager trigger release");
                GamepadContexts.Current = GamepadContext.Gameplay;
                GamepadManager.UpdateDevice("mapped", State(), true); GamepadInput.BeginFrame();
                GamepadManager.UpdateDevice("mapped", State(GamepadButtons.A), true); GamepadInput.BeginFrame();
                Check(GamepadInput.TakePress(GamepadButtons.A), "gameplay press before context transition");
                GamepadContexts.MenuVisible = true; GamepadContexts.MenuVisible = false;
                GamepadManager.UpdateDevice("mapped", State(GamepadButtons.B), true); GamepadInput.BeginFrame();
                Check(!GamepadInput.TakePress(GamepadButtons.B), "menu closed between simulation steps cannot leak held accept/back");
                GamepadManager.UpdateDevice("mapped", State(), true); GamepadInput.BeginFrame();
                GamepadManager.UpdateDevice("mapped", State(GamepadButtons.B), true); GamepadInput.BeginFrame();
                Check(GamepadInput.TakePress(GamepadButtons.B), "gameplay resumes after release and repress");
                CheckFocusLifecycle();
                CheckMenuLifecycle();
                foreach (var key in new[] { GamepadButtons.DpadUp, GamepadButtons.RightTrigger })
                {
                    var events = new GamepadEventState();
                    events.Key(key, true); events.Motion = State(x: .8f);
                    Check(events.Snapshot.Down(key), "motion preserves held key " + key);
                    events.Motion = State(key); events.Key(key, false);
                    Check(events.Snapshot.Down(key), "motion contribution survives key release " + key);
                    events.Motion = State(); Check(events.Snapshot.Buttons == 0, "motion release " + key);
                    events.Key(key, true); events.Clear(); Check(events.Snapshot.Buttons == 0, "event lifecycle clear");
                }
                var actions = new List<UiAction>();
                var router = new GamepadUiRouter(); router.Action += actions.Add;
                router.Update(new("one", State(), 1), GamepadContext.Menu, 0);
                router.Update(new("one", State(GamepadButtons.DpadDown), 1), GamepadContext.Menu, 10);
                router.Update(new("one", State(GamepadButtons.DpadDown), 1), GamepadContext.Menu, 309);
                Check(actions.Count == 1, "repeat initial delay");
                router.Update(new("one", State(GamepadButtons.DpadDown), 1), GamepadContext.Menu, 310);
                router.Update(new("one", State(GamepadButtons.DpadDown), 1), GamepadContext.Menu, 400);
                Check(actions.Count == 3, "repeat cadence");
                router.Update(new("one", State(GamepadButtons.DpadUp), 1), GamepadContext.Menu, 401);
                Check(actions[^1] == UiAction.Up, "direction change immediate");
                router.Update(new("one", State(GamepadButtons.A), 1), GamepadContext.Menu, 402);
                router.Update(new("one", State(GamepadButtons.A), 1), GamepadContext.Menu, 3000);
                Check(actions.FindAll(a => a == UiAction.Accept).Count == 1, "accept never repeats");
                router.Update(new("one", State(GamepadButtons.B), 1), GamepadContext.BindingCapture, 3010);
                Check(!actions.Contains(UiAction.Back), "capture does not navigate");
                GamepadOptions.Load(new[] { "gamepad_left_inner_deadzone=.3", "gamepad_deadzone=.25", "gamepad_look=2", "gamepad_invert_y=true" });
                Near(GamepadOptions.LeftInner, .3f, "explicit setting wins independent of order");
                Near(GamepadOptions.RightInner, .25f, "legacy deadzone migration");
                Near(GamepadOptions.LookX, 2, "legacy horizontal sensitivity");
                Near(GamepadOptions.LookY, 2, "legacy vertical sensitivity");
                Check(GamepadOptions.InvertY, "legacy inversion");
                PadBindings.Reset();
                Check(PadBindings.Conflicts(PadAction.Morph, GamepadButtons.A).Count == 1, "binding conflict visible");
                PadBindings.Assign(PadAction.Morph, 0, GamepadButtons.A, "Swap");
                Check(PadBindings.Get(PadAction.Jump) == GamepadButtons.B, "swap old button");
                PadBindings.Assign(PadAction.Scan, 0, GamepadButtons.A, "Keep Both");
                Check(PadBindings.Get(PadAction.Morph) == GamepadButtons.A, "intentional shared binding");
                PadBindings.Assign(PadAction.Zoom, 0, GamepadButtons.A, "Replace");
                Check(PadBindings.Get(PadAction.Morph) == 0 && PadBindings.Get(PadAction.Scan) == 0, "replace clears all conflicts");
                PadBindings.Reset(); PadBindings.SetSlot(PadAction.NextWeapon, 0, 0);
                Check(PadBindings.Slot(PadAction.NextWeapon, 0) == 0 && PadBindings.Slot(PadAction.NextWeapon, 1) == GamepadButtons.DpadRight,
                    "clearing primary preserves secondary position");
                Check(!PadBindings.TryLoad("pad_99999", "A"), "invalid action rejected");
                Check(!PadBindings.TryLoad("pad_Jump", "-1"), "invalid buttons rejected");
                Check(GamepadGlyphs.Resolve(GamepadButtons.A, GamepadFamily.PlayStation) == "Cross", "PlayStation label");
                Check(GamepadGlyphs.Resolve(GamepadButtons.A, GamepadFamily.Nintendo) == "B", "Nintendo position");
                long clock = Environment.TickCount64 + 1000;
                InputSourceTracker.Note(InputSource.KeyboardMouse, clock - 200);
                InputSourceTracker.Note(InputSource.Gamepad, clock);
                InputSourceTracker.Note(InputSource.Touch, clock + 1);
                Check(InputSourceTracker.Current == InputSource.Gamepad, "input source cooldown");
                InputSourceTracker.Note(InputSource.Touch, clock + 200);
                Check(InputSourceTracker.Current == InputSource.Touch, "meaningful touch takeover");
                Check(GamepadGlyphs.Detect("Wireless Controller", "030000004c0500000000000000000000") == GamepadFamily.PlayStation,
                    "GUID identifies generic PlayStation name");
                Check(GamepadGlyphs.Detect("Controller", vendorId: 0x057e) == GamepadFamily.Nintendo, "Android vendor family");
                var rumble = new FakeHaptics();
                GamepadHaptics.Register("mapped", rumble);
                GamepadManager.SelectDevice("mapped");
                GamepadOptions.Vibration = true; GamepadOptions.VibrationStrength = .5f;
                InputSourceTracker.Note(InputSource.Gamepad, clock + 400);
                GamepadHaptics.Play(GamepadFeedback.Damage);
                Near(rumble.Low, .16f, "haptic intensity scales");
                GamepadHaptics.Stop(); GamepadContexts.Focused = false;
                GamepadHaptics.Play(GamepadFeedback.Damage);
                Check(rumble.Stopped, "background gameplay cannot restart vibration");
                GamepadContexts.Focused = true;
                GamepadOptions.Vibration = false; GamepadHaptics.Play(GamepadFeedback.Death);
                Near(rumble.Low, .16f, "disabled vibration does not start an effect");
                GamepadManager.RemoveDevice("mapped");
                Check(rumble.Stopped, "disconnect stops haptics immediately");
                GamepadHaptics.Unregister("mapped");
                PadBindings.Set(PadAction.Shoot, GamepadButtons.Y);
                Check(GamepadProbe.Actions(GamepadButtons.Y).Contains("Fire / alt attack"), "probe uses remapped actions");
                CheckPersistence();
#if MPHREAD_SHELL
                Launcher.Gui.GamepadUiChecks.Run(shots);
#endif
                Console.WriteLine($"[gamepadcheck] PASS: {_checks} deterministic checks");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine("[gamepadcheck] FAIL: " + ex); return 1; }
            finally
            {
                foreach (var device in GamepadManager.Devices) GamepadManager.RemoveDevice(device.DeviceId);
                GamepadManager.SelectDevice(null); GamepadOptions.Reset(); PadBindings.Reset();
            }
        }
        private sealed class FakeHaptics : IGamepadHaptics
        {
            public float Low;
            public bool Stopped;
            public void Rumble(float lowFrequency, float highFrequency, TimeSpan duration) { Low = lowFrequency; Stopped = false; }
            public void Stop() { Stopped = true; }
        }
        private static void CheckFocusLifecycle()
        {
            PadBindings.Reset();
            GamepadContexts.Current = GamepadContext.Gameplay;
            GamepadContexts.Focused = true;
            void Frame(GamepadButtons buttons)
            {
                GamepadManager.UpdateDevice("mapped", State(buttons), true);
                GamepadInput.BeginFrame();
            }
            Frame(0);
            Frame(GamepadButtons.RightThumb);
            Check(GamepadInput.WheelHeld, "wheel can open before losing focus");
            // Desktop continues polling during focus loss. The clear's device
            // revision can already have been consumed when the window returns.
            GamepadContexts.Focused = false;
            GamepadManager.ClearAll();
            Frame(0);
            Frame(GamepadButtons.RightThumb);
            Frame(GamepadButtons.RightThumb);
            Check(!GamepadInput.WheelHeld, "background wheel is suppressed");
            GamepadContexts.Focused = true;
            Frame(GamepadButtons.RightThumb);
            Check(!GamepadInput.WheelHeld, "focus regain blocks buttons held during background polling");
            Frame(0);
            Frame(GamepadButtons.RightThumb);
            Check(GamepadInput.WheelHeld, "focus regain allows release and repress");
            Frame(0);
            GamepadContexts.Focused = false;
            GamepadContexts.Focused = true;
            Frame(GamepadButtons.RightThumb);
            Check(!GamepadInput.WheelHeld && !GamepadInput.TakePress(GamepadButtons.RightThumb),
                "focus change between simulation steps blocks held input");
            Frame(0);
        }
        private static void CheckMenuLifecycle()
        {
            var actions = new List<UiAction>();
            var router = new GamepadUiRouter();
            router.Action += actions.Add;
            GamepadContexts.MenuVisible = true;
            router.Update(new("one", State(), 1), GamepadContext.Menu, 0);
            // Android keeps the same StartScreen/navigation object and only
            // polls it while visible. No router update occurs during gameplay.
            GamepadContexts.MenuVisible = false;
            GamepadContexts.MenuVisible = true;
            router.Update(new("one", State(GamepadButtons.Start), 1), GamepadContext.Menu, 100);
            Check(actions.Count == 0, "reopened menu ignores the Start press that opened it");
            router.Update(new("one", State(), 1), GamepadContext.Menu, 110);
            router.Update(new("one", State(GamepadButtons.Start), 1), GamepadContext.Menu, 120);
            Check(actions.Count == 1 && actions[0] == UiAction.Back, "reopened menu accepts a fresh Back press");
            GamepadContexts.Focused = false;
            GamepadContexts.Focused = true;
            router.Update(new("one", State(GamepadButtons.A), 1), GamepadContext.Menu, 130);
            Check(actions.Count == 1, "menu blocks held input after focus changes between UI ticks");
            router.Update(new("one", State(), 1), GamepadContext.Menu, 140);
            router.Update(new("one", State(GamepadButtons.A), 1), GamepadContext.Menu, 150);
            Check(actions.Count == 2 && actions[1] == UiAction.Accept, "menu accepts a fresh press after regaining focus");
            GamepadContexts.MenuVisible = false;
        }
        private static void CheckPersistence()
        {
            string previous = Launcher.LauncherPrefs.Directory;
            string directory = Path.Combine(Path.GetTempPath(), "fruity-input-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                Launcher.LauncherPrefs.Directory = directory;
                string path = Path.Combine(directory, "controls.txt");
                File.WriteAllLines(path, new[] { "future_control=keep-me", "gamepad_deadzone=.27", "gamepad_look=1.8", "pad_Jump=A" });
                InputSettings.Load();
                Near(GamepadOptions.RightInner, .27f, "file migration");
                PadBindings.SetSlot(PadAction.NextWeapon, 0, 0);
                InputSettings.Save();
                Check(File.ReadAllText(path).Contains("future_control=keep-me"), "unknown settings preserved");
                PadBindings.Reset(); InputSettings.Load();
                Check(PadBindings.Slot(PadAction.NextWeapon, 0) == 0 && PadBindings.Slot(PadAction.NextWeapon, 1) == GamepadButtons.DpadRight,
                    "primary/secondary slots round-trip");
            }
            finally { Launcher.LauncherPrefs.Directory = previous; File.Delete(Path.Combine(directory, "controls.txt")); Directory.Delete(directory); }
        }
    }
}
