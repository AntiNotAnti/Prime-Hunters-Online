using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MphRead.Mods.Input
{
    internal static class ControllerRuntimeChecks
    {
        public static void Run()
        {
            void Check(bool ok, string message) => GamepadChecks.Check(ok, "controller runtime: " + message);
            foreach (var device in GamepadManager.Devices) GamepadManager.RemoveDevice(device.DeviceId);
            GamepadOptions.Reset(); PadBindings.Reset();
            Check((GamepadMappings.ParseCapabilities("guid,name,leftx:a0,lefty:a1,lefttrigger:b6,righttrigger:b7,")
                & GamepadCapabilities.AnalogTriggers) == 0, "mapped digital triggers are not advertised as analog");
            GamepadManager.UpdateDevice("runtime-a", default, true); GamepadManager.SelectDevice("runtime-a");
            GamepadOptions.LeftTriggerMin = .3f; GamepadOptions.LeftCalibration = new(.1f, 0, -1, 1, -1, 1);
            GamepadManager.UpdateDevice("runtime-b", default, true); GamepadManager.SelectDevice("runtime-b");
            GamepadOptions.LeftTriggerMin = .1f;
            GamepadManager.UpdateDevice("runtime-a", new() { LeftTrigger = .3f, LeftX = .1f }, true);
            GamepadManager.UpdateDevice("runtime-b", new() { LeftTrigger = .3f, LeftX = .1f }, true);
            var devices = GamepadManager.Devices;
            var a = devices.First(d => d.DeviceId == "runtime-a"); var b = devices.First(d => d.DeviceId == "runtime-b");
            Check(a.State.LeftTrigger == 0 && b.State.LeftTrigger > .2f && a.State.LeftX == 0 && b.State.LeftX == .1f,
                "simultaneous devices process their own calibration before selection");
            GamepadManager.SelectDevice("runtime-a");
            Check(GamepadOptions.LeftTriggerMin == .3f && GamepadManager.ActiveState.LeftX == 0, "switch publishes correct runtime immediately");
            GamepadInput.BeginFrame();
            Task.Run(() => GamepadManager.SelectDevice("runtime-b")).GetAwaiter().GetResult();
            Check(GamepadOptions.LeftTriggerMin == .3f, "input frame retains its runtime during a concurrent device switch");
            GamepadInput.BeginFrame();
            Check(GamepadOptions.LeftTriggerMin == .1f, "next input frame adopts the newly selected runtime");
            GamepadManager.SelectDevice("runtime-a");
            GamepadManager.UpdateDevice("runtime-a", new() { LeftX = .6f }, true);
            Check(a.State.LeftX == 0, "published snapshot is immutable after device updates");
            bool reentered = false;
            void Changed() => reentered = Task.Run(() => GamepadManager.Devices.Count).Wait(1000);
            GamepadManager.ActiveChanged += Changed;
            GamepadManager.SelectDevice("runtime-b"); GamepadManager.ActiveChanged -= Changed;
            Check(reentered, "events dispatch outside manager lock");
            GamepadManager.SelectDevice("runtime-a");
            InputSourceTracker.Note(InputSource.KeyboardMouse, long.MaxValue / 4);
            GamepadManager.UpdateDevice("runtime-b", new() { Buttons = GamepadButtons.A }, true);
            Check(GamepadManager.Snapshot.DeviceId == "runtime-a" && InputSourceTracker.Current == InputSource.KeyboardMouse,
                "inactive device cannot override explicit selection or prompt source");
            foreach (var device in GamepadManager.Devices) GamepadManager.RemoveDevice(device.DeviceId);
            GamepadOptions.Reset(); PadBindings.Reset();
            var saved = GamepadProfiles.Capture("before");
            foreach (var bad in new[] { new GamepadProfile(2, "future", new[] { "gamepad_look_x=1" }),
                new GamepadProfile(1, "bad", new[] { "gamepad_look_x=NaN" }),
                new GamepadProfile(1, "bad", new[] { "pad_Jump_primary=A, B" }) })
            {
                bool rejected = false;
                try { GamepadProfiles.Apply(bad); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && GamepadProfiles.Capture("after").Settings.SequenceEqual(saved.Settings), "invalid profile has no partial application");
            }
            string prior = Launcher.LauncherPrefs.Directory, temp = Path.Combine(Path.GetTempPath(), "controller-library-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                string path = Path.Combine(temp, "controller-profiles.json"); File.WriteAllText(path, "{ broken");
                Launcher.LauncherPrefs.Directory = temp; GamepadProfiles.Initialize();
                Check(GamepadProfiles.Profiles.Count == 0 && GamepadProfiles.Status.Length > 0 && File.ReadAllText(path) == "{ broken",
                    "corrupt library preserved and reported without crashing");
            }
            finally { Launcher.LauncherPrefs.Directory = prior; GamepadProfiles.Initialize(); File.Delete(Path.Combine(temp, "controller-profiles.json")); Directory.Delete(temp); }
            var scheduler = new HapticScheduler();
            Check(scheduler.Accept(GamepadFeedback.Fire, 0, 45) && scheduler.Accept(GamepadFeedback.Damage, 1, 100)
                && !scheduler.Accept(GamepadFeedback.Fire, 80, 45) && scheduler.Accept(GamepadFeedback.Fire, 102, 45), "damage preempts fire and retains its full duration");
            const string guid = "030000005e040000130b000099090000";
            string first = guid + ",One,a:b0,platform:Windows,";
            string second = guid + ",Two,a:b1,platform:Windows,";
            string other = guid + ",Mac,a:b2,platform:Mac OS X,";
            string merged = GamepadMappings.ReplaceOverride("# comment\n" + first + "\n" + other + "\n" + first, second);
            Check(!merged.Contains(first) && merged.Contains(other) && merged.Contains("# comment") && merged.Split(second).Length == 2,
                "mapping replacement deduplicates only matching GUID/platform");
            AimInputSourceTracker.Reset(); InputSourceTracker.Reset();
            GamepadManager.UpdateDevice("ui-triggers", default, true, capabilities: GamepadCapabilities.AnalogTriggers);
            GamepadManager.SelectDevice("ui-triggers"); GamepadOptions.TriggerThreshold = .95f;
            var router = new GamepadUiRouter(); int pages = 0;
            router.Action += action => { if (action == UiAction.PageDown) pages++; };
            void Trigger(float value, long now)
            {
                GamepadManager.UpdateDevice("ui-triggers", new() { RightTrigger = value }, true, capabilities: GamepadCapabilities.AnalogTriggers);
                router.Update(GamepadManager.Snapshot, GamepadContext.Menu, now);
            }
            Trigger(0, 0); Trigger(.5f, 1); Trigger(.4f, 2); Trigger(.5f, 3);
            Check(pages == 1 && !GamepadManager.ActiveState.Down(GamepadButtons.RightTrigger), "menu threshold independent of gameplay and retains hysteresis");
            Trigger(.2f, 4); Trigger(.5f, 5); Check(pages == 2, "menu trigger rearms below release threshold");
            GamepadManager.RemoveDevice("ui-triggers");
            var layout = GamepadRuntimeConfig.Current.Layout; layout.Apply("Bumper Jumper");
            GamepadOptions.LookX = 2; GamepadOptions.ScopedX = .7f; GamepadOptions.LeftInner = .1f;
            Check(layout.Name == "Bumper Jumper", "sensitivity and calibration preserve control layout identity");
            PadBindings.SetSlot(PadAction.Jump, 0, GamepadButtons.Y, GamepadButtons.LeftBumper);
            Check(InputPrompt.For(PadAction.Jump).Button == GamepadButtons.Y && InputPrompt.For(PadAction.Jump).Modifier == GamepadButtons.LeftBumper,
                "semantic prompts follow rebound combinations");
            GamepadOptions.Reset(); PadBindings.Reset();
        }
    }
}
