using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using MphRead.Entities;

namespace MphRead.Mods.Input
{
    internal static class GamepadEnhancementChecks
    {
        private static void Check(bool ok, string message) => GamepadChecks.Check(ok, message);
        public static void Run()
        {
            GamepadOptions.Reset(); PadBindings.Reset();
            var weapons = new[] { PadAction.VoltDriver, PadAction.Battlehammer, PadAction.Imperialist,
                PadAction.Judicator, PadAction.Magmaul, PadAction.ShockCoil, PadAction.OmegaCannon,
                PadAction.AffinitySlot, PadAction.LastWeapon };
            foreach (var action in weapons) Check(PadBindings.Get(action) == 0, action + " defaults to unassigned");
            var actions = new GamepadActions();
            PadBindings.SetSlot(PadAction.Imperialist, 0, GamepadButtons.A, GamepadButtons.LeftBumper);
            actions.Update(GamepadButtons.LeftBumper);
            Check(!actions.Down(PadAction.PrevWeapon), "modifier alone never triggers its ordinary action");
            actions.Update(GamepadButtons.LeftBumper | GamepadButtons.A);
            Check(actions.WasPressed(PadAction.Imperialist) && !actions.Down(PadAction.Jump), "modifier chord selects a weapon without jumping");
            actions.Update(GamepadButtons.LeftBumper | GamepadButtons.A);
            Check(!actions.WasPressed(PadAction.Imperialist), "held chord fires only one weapon selection");
            actions.Update(GamepadButtons.A);
            Check(!actions.Down(PadAction.Jump), "releasing modifier first does not leak the remaining button");
            actions.Update(0); actions.Update(GamepadButtons.A);
            Check(actions.WasPressed(PadAction.Jump), "unmodified binding returns after release");
            actions.Update(0); actions.Update(GamepadButtons.LeftBumper | GamepadButtons.A);
            actions.Update(GamepadButtons.LeftBumper); actions.Update(GamepadButtons.LeftBumper | GamepadButtons.A);
            Check(actions.WasPressed(PadAction.Imperialist), "modifier can remain held across repeated selections");
            PadBindings.SetSlot(PadAction.Jump, 1, GamepadButtons.A, GamepadButtons.RightBumper);
            actions.Reset(); actions.Update(GamepadButtons.A);
            Check(actions.Down(PadAction.Jump), "plain and modified slots may share their action button");
            Check(PadBindings.Conflicts(PadAction.VoltDriver, GamepadButtons.A, GamepadButtons.LeftBumper).SequenceEqual(new[] { PadAction.Imperialist }),
                "conflicts compare the whole combination");
            PadBindings.SetSlot(PadAction.VoltDriver, 0, GamepadButtons.X, GamepadButtons.RightBumper);
            PadBindings.Assign(PadAction.VoltDriver, 0, GamepadButtons.A, "Swap", GamepadButtons.LeftBumper);
            Check(PadBindings.Slot(PadAction.Imperialist, 0) == GamepadButtons.X
                && PadBindings.Modifier(PadAction.Imperialist, 0) == GamepadButtons.RightBumper, "swap preserves both parts of a combination");
            Check(GamepadProbe.Actions(GamepadButtons.LeftBumper | GamepadButtons.A).Contains("Volt Driver")
                && !GamepadProbe.Actions(GamepadButtons.LeftBumper | GamepadButtons.A).Contains("Jump"), "live probe resolves combinations like gameplay");

            PadBindings.Reset(); GamepadOptions.WheelToggle = true; actions.Reset();
            actions.Update(GamepadButtons.RightThumb); actions.Update(0);
            Check(actions.WheelOpen, "toggle wheel stays open after release");
            actions.Update(GamepadButtons.RightThumb);
            Check(!actions.WheelOpen, "second press closes toggle wheel");
            actions.Update(0); actions.Update(GamepadButtons.RightThumb); actions.Reset();
            Check(!actions.WheelOpen, "context reset closes toggle wheel");
            GamepadOptions.SetWheelSlot(0, 4);
            Check(GamepadOptions.WheelOrder.Distinct().Count() == 6 && GamepadOptions.WheelOrder[4] == 0, "wheel rearrangement swaps without duplicate weapons");
            Check(WeaponSelectionDirection.ControllerSlot(.5f, .8f) == 4, "wheel direction follows configured order");
            GamepadOptions.WheelThreshold = .7f;
            Check(WeaponSelectionDirection.ControllerSlot(.5f, 0) == -1, "wheel threshold rejects small stick movement");
            GamepadOptions.Load(new[] { "gamepad_wheel_order=0,0,2,3,4,5", "gamepad_scoped_x=NaN", "gamepad_lt_min=.8", "gamepad_lt_max=.1" });
            Check(GamepadOptions.WheelOrder.SequenceEqual(new[] { 0, 1, 2, 3, 4, 5 }) && GamepadOptions.ScopedX == 1,
                "invalid wheel order and non-finite scoped sensitivity use safe defaults");
            Check(GamepadOptions.LeftTriggerMax >= GamepadOptions.LeftTriggerMin + .099f, "trigger calibration always keeps a nonzero range");

            GamepadOptions.Reset(); GamepadContexts.Current = GamepadContext.Gameplay;
            foreach (var action in weapons.Where(a => a != PadAction.LastWeapon))
            {
                PadBindings.Reset(); PadBindings.SetSlot(action, 0, GamepadButtons.X);
                GamepadManager.UpdateDevice("enhancement-check", new GamepadState(), true); GamepadInput.BeginFrame();
                GamepadManager.UpdateDevice("enhancement-check", new GamepadState { Buttons = GamepadButtons.X }, true); GamepadInput.BeginFrame();
                var controls = PlayerControls.GetDefault(); GamepadInput.ApplyBindings(controls);
                var bind = (Keybind)typeof(PlayerControls).GetProperty(action.ToString())!.GetValue(controls)!;
                Check(bind.IsPressed, action + " reaches its actual gameplay keybind");
            }
            var defaults = PlayerControls.GetDefault();
            Check(ReferenceEquals(GamepadActions.WeaponBind(defaults, BeamType.Imperialist), defaults.Imperialist)
                && GamepadActions.WeaponBind(defaults, BeamType.None) is null, "last weapon resolves an existing weapon action and rejects invalid values");
            GamepadManager.RemoveDevice("enhancement-check");
            CheckCalibration(); CheckMapping(); CheckProfiles();
            GamepadOptions.Reset(); PadBindings.Reset();
        }
        private static void CheckCalibration()
        {
            var calibration = new GamepadCalibration();
            for (int i = 0; i < 20; i++) calibration.Sample(new() { LeftX = .03f, RightY = .06f, LeftTrigger = .1f }, true);
            for (int i = 0; i < 100; i++)
            {
                float angle = i * MathF.PI * 2 / 100;
                calibration.Sample(new() { LeftX = .9f * MathF.Cos(angle), LeftY = .9f * MathF.Sin(angle),
                    RightX = MathF.Cos(angle), RightY = MathF.Sin(angle), LeftTrigger = .9f, RightTrigger = .8f }, false);
            }
            Check(calibration.Valid, "calibration accepts measured rest and range"); calibration.Apply();
            Check(Math.Abs(GamepadOptions.LeftInner - .04f) < .001f && Math.Abs(GamepadOptions.RightCalibration.CenterY - .06f) < .001f,
                "calibration separates center bias from radial noise");
            Check(GamepadCalibration.Trigger(.1f, .1f, .9f) == 0 && GamepadCalibration.Trigger(.9f, .1f, .9f) == 1,
                "calibrated trigger spans released to full press");
            Check(!new GamepadCalibration().Valid, "incomplete calibration cannot be applied");
        }
        private static void CheckMapping()
        {
            const string guid = "030000005e040000130b000099090000";
            GamepadRawSample Rest() => new("mapping-test", guid, "Xbox", new float[] { 0, 0, 0, 0, -1, -1 }, new bool[10], new byte[1]);
            var wizard = new GamepadMappingWizard(Rest());
            for (int i = 0; i < 10; i++)
            {
                var sample = Rest(); sample.Buttons[i] = true; wizard.Sample(sample); wizard.Sample(Rest());
            }
            foreach (byte hat in new byte[] { 1, 2, 4, 8 }) { var sample = Rest(); sample.Hats[0] = hat; wizard.Sample(sample); wizard.Sample(Rest()); }
            for (int i = 0; i < 6; i++)
            {
                var sample = Rest(); sample.Axes[i] = i is 1 or 3 ? -1 : 1; wizard.Sample(sample); wizard.Sample(Rest());
            }
            Check(wizard.Complete && wizard.Mapping.Contains("rightx:a2,") && wizard.Mapping.Contains("lefttrigger:a4,"),
                "mapping wizard produces complete raw axis/button/hat mapping");
            bool rejected = false;
            try { new GamepadMappingWizard(Rest()).Sample(Rest() with { DeviceId = "other" }); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "mapping wizard rejects a controller change");
        }
        private static void CheckProfiles()
        {
            string previous = Launcher.LauncherPrefs.Directory;
            string directory = Path.Combine(Path.GetTempPath(), "fruity-profiles-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                Launcher.LauncherPrefs.Directory = directory; GamepadProfiles.Initialize();
                GamepadOptions.ScopedX = .65f; GamepadOptions.WheelToggle = true; GamepadOptions.SetWheelSlot(0, 3);
                PadBindings.SetSlot(PadAction.Imperialist, 1, GamepadButtons.A, GamepadButtons.LeftBumper);
                GamepadProfiles.Save("Precision");
                string path = Path.Combine(directory, "export.json"); GamepadProfiles.Export("Precision", path);
                Check(GamepadProfiles.Import(path) == "Precision 2", "profile import avoids overwriting an existing name");
                GamepadOptions.Reset(); PadBindings.Reset(); GamepadProfiles.Load("Precision");
                Check(GamepadOptions.ScopedX == .65f && GamepadOptions.WheelToggle && GamepadOptions.WheelOrder[0] == 3
                    && PadBindings.Modifier(PadAction.Imperialist, 1) == GamepadButtons.LeftBumper, "profile round-trip retains aim, wheel and combinations");
                string stable = GamepadProfiles.DeviceKey("glfw:guid:0:1");
                Check(stable == GamepadProfiles.DeviceKey("glfw:guid:3:8"), "automatic profile key survives slot changes and reconnects");
                var device = new GamepadDeviceSnapshot { DeviceId = "glfw:guid:0:1", ProfileKey = stable };
                GamepadOptions.ScopedX = 1; GamepadProfiles.Assign("Precision", device);
                GamepadManager.UpdateDevice(device.DeviceId, default, true); GamepadManager.SelectDevice(device.DeviceId);
                Check(GamepadOptions.ScopedX == .65f, "assigned controller automatically loads its saved profile");
                GamepadManager.RemoveDevice(device.DeviceId);
                Check(GamepadOptions.ScopedX == 1, "unassigned device restores the prior manual settings");
                File.WriteAllText(path, JsonSerializer.Serialize(new GamepadProfile(1, "Bad", new[] { "Jump=Key:Enter" })));
                bool rejected = false;
                try { GamepadProfiles.Import(path); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && GamepadOptions.ScopedX == 1, "invalid imports cannot change keyboard bindings or active controller settings");
                File.WriteAllText(path, "null"); rejected = false;
                try { GamepadProfiles.Import(path); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "null profile is rejected");
            }
            finally
            {
                Launcher.LauncherPrefs.Directory = previous; GamepadProfiles.Initialize();
                foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }
}
