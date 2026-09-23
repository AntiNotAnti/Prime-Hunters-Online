using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using MphRead.Entities;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MphRead.Mods.Input
{
    /// <summary>Deterministic source-ownership regressions; no assets, window or tablet required.</summary>
    public static class PointerCheck
    {
        private static int _checks;

        public static int Run()
        {
            try
            {
                _checks = 0;
                CheckBindings();
                CheckMovement();
                CheckZone();
                CheckPlayerInput();
                CheckSettings();
                Require(!WindowsPenInput.IsPromotedPointer(0), "physical mouse signature");
                Require(WindowsPenInput.IsPromotedPointer(0xFF515701), "promoted pen signature");
                Require(WindowsPenInput.IsPromotedPointer(0xFF515781), "promoted touch signature");
                Require(WindowsPenInput.IsPromotedPrimaryRelease(0x0202, 0xFF515701),
                    "promoted pen mouse-up is a release fallback");
                Require(!WindowsPenInput.IsPromotedPrimaryRelease(0x0201, 0xFF515701),
                    "promoted pen mouse-down is not a release fallback");
                Console.WriteLine($"POINTERCHECK PASS ({_checks} assertions)");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"POINTERCHECK FAIL: {ex}");
                return 1;
            }
            finally
            {
                PointerDevice.Reset();
            }
        }

        private static void Require(bool condition, string label)
        {
            _checks++;
            if (!condition)
            {
                throw new InvalidOperationException(label);
            }
        }

        private static void CheckBindings()
        {
            var source = new PointerBindings();
            source.Update(rawDown: true, captured: true);
            Keybind[] independent = { new(Keys.F), new(Keys.G), new(MouseButton.Right),
                new(MouseButton.Middle), new(ButtonType.ScrollUp), new(ButtonType.ScrollDown) };
            foreach (Keybind bind in independent)
            {
                bind.IsDown = bind.IsPressed = true;
                Require(!source.Resolve(bind) && bind.IsDown && bind.IsPressed,
                    $"independent {bind.Type}/{bind} preserved during capture");
            }
            var controls = PlayerControls.GetDefault();
            controls.Shoot.Type = controls.AltAttack.Type = ButtonType.Mouse;
            controls.Shoot.MouseButton = controls.AltAttack.MouseButton = MouseButton.Left;
            Keybind[] captured = { controls.Shoot, controls.AltAttack, new(MouseButton.Left) };
            foreach (Keybind bind in captured)
            {
                bind.IsDown = bind.IsPressed = bind.IsReleased = true;
                Require(source.Resolve(bind) && !bind.IsDown && !bind.IsPressed && !bind.IsReleased,
                    "every primary binding suppressed before additive actions");
            }
            source.Update(false, false);
            source.Resolve(controls.Shoot);
            Require(!controls.Shoot.IsReleased, "captured tip release does not leak an action edge");
            source.Update(true, false);
            source.Resolve(controls.Shoot);
            Require(controls.Shoot.IsDown && controls.Shoot.IsPressed, "ordinary mouse press");
            source.Update(true, false);
            source.Resolve(controls.Shoot);
            Require(controls.Shoot.IsDown && !controls.Shoot.IsPressed, "ordinary mouse hold");
            source.Update(false, false);
            source.Resolve(controls.Shoot);
            Require(!controls.Shoot.IsDown && controls.Shoot.IsReleased, "ordinary mouse release");
            source.Update(true, true, independentDown: true);
            source.Resolve(controls.Shoot);
            Require(controls.Shoot.IsDown && controls.Shoot.IsPressed, "physical mouse fires beside captured native pen");
            source.Update(true, true, independentDown: false);
            source.Resolve(controls.Shoot);
            Require(!controls.Shoot.IsDown && controls.Shoot.IsReleased, "physical mouse releases while pen stays down");
        }

        private static void CheckMovement()
        {
            PointerInput.StylusMode = PointerInput.GuardJumps = true;
            PointerInput.JumpPixels = 600;
            PointerInput.Reset();
            Require(PointerInput.Filter(700, 400) == (0f, 0f), "vector teleport");
            Require(PointerInput.JumpsIgnored == 1, "one rejected sample counted once");
            Require(PointerInput.Filter(450, 450) == (0f, 0f), "diagonal threshold uses distance");
            Require(PointerInput.Filter(300, 250) == (300f, 250f), "legal fast movement");
            Require(PointerInput.Filter(-600, 0) == (0f, 0f), "negative threshold boundary");
            PointerInput.GuardJumps = false;
            Require(PointerInput.Filter(700, 400) == (700f, 400f), "filter off independently of stylus mode");
            PointerInput.GuardJumps = true;
            PointerInput.StylusMode = false;
            Require(PointerInput.Filter(900, 900) == (900f, 900f), "normal high-DPI mouse unchanged");
            PointerInput.StylusMode = true;
            PointerInput.JumpPixels = 0;
            Require(PointerInput.Filter(900, 900) == (900f, 900f), "zero threshold disables filtering");
            PointerInput.JumpPixels = 600;
        }

        private static void Frame(float x, float y, bool down, bool independentDown = false,
            bool acceptsInput = true, uint id = 1)
        {
            PointerDevice.Update(new PointerSample(PointerDeviceType.Pen, id, x, y, down, down, true),
                1920, 1080, independentDown, acceptsInput);
        }

        private static void CheckZone()
        {
            PointerDevice.Reset();
            PointerInput.StylusMode = true;
            PointerInput.GuardJumps = false; // first-contact protection must not depend on jump filtering
            StylusZone.Enabled = true;
            StylusZone.AspectCorrection = 1920f / 1080;
            StylusZone.SetRect(0, 0, 1);
            Frame(100, 600, false);
            Frame(1500, 600, true);
            Require(StylusZone.Held == StylusRegion.Aim && !StylusZone.Aiming, "first contact belongs to aim without rotating");
            Require(!PointerDevice.PrimaryDown && PointerDevice.TakeDelta() == (0f, 0f), "contact teleport cannot aim or fire");
            Frame(1510, 605, true);
            Frame(1520, 610, true); // two pictures before the simulation reads
            Require(StylusZone.Aiming && PointerDevice.TakeDelta() == (20f, 10f), "drag accumulates between simulation steps");
            Require(PointerDevice.TakeDelta() == (0f, 0f), "catch-up simulation cannot apply movement twice");
            Frame(1530, 615, true, independentDown: true);
            Require(PointerDevice.PrimaryDown, "native independent mouse is not captured");
            Frame(1540, 620, false);
            Frame(100, 600, false);
            Frame(1500, 600, true);
            Frame(1505, 602, true); // touchdown picture had no simulation step
            Require(PointerDevice.TakeDelta() == (5f, 2f), "high-refresh touchdown excludes hover teleport");
            Frame(1510, 604, true, acceptsInput: false);
            Frame(1800, 650, true, acceptsInput: false);
            Frame(1810, 650, true);
            Require(PointerDevice.TakeDelta() == (0f, 0f), "pause/focus return cannot replay accumulated aim");
            Frame(1820, 650, true, id: 2);
            Require(StylusZone.Aiming && PointerDevice.TakeDelta() == (0f, 0f),
                "pointer id churn keeps the held stylus gesture without injecting aim");

            // A real tablet can rotate native pointer IDs while the tip is still
            // physically touching the same WPN/affinity icon. That must remain
            // one press; only a real pen-up may re-arm the one-shot action.
            PointerDevice.Reset();
            PointerInput.StylusMode = true;
            StylusZone.Enabled = true;
            StylusZone.AspectCorrection = 1920f / 1080;
            StylusZone.SetRect(0, 0, 1);
            StylusZone.Button weapons = Array.Find(StylusZone.Buttons,
                button => button.Region == StylusRegion.Weapons);
            float weaponX = weapons.X / StylusZone.DsWidth * 1920;
            float weaponY = weapons.Y / StylusZone.DsHeight * StylusZone.Height * 1080;
            Frame(weaponX, weaponY, true, id: 10);
            Require(StylusZone.TakePressed() == StylusRegion.Weapons,
                "WPN contact produces one action");
            Frame(weaponX, weaponY, true, id: 11);
            Require(StylusZone.Held == StylusRegion.Weapons
                && StylusZone.TakePressed() == StylusRegion.None,
                "WPN id churn does not repeat the action");

            // XP-Pen and similar drivers can report the old WM_POINTER id up,
            // then the replacement id down on the next picture while the tip
            // never left the tablet. The one-frame gap must not re-arm WPN.
            Frame(weaponX, weaponY, false, id: 11);
            Require(StylusZone.Held == StylusRegion.Weapons
                && StylusZone.TakePressed() == StylusRegion.None,
                "transient WPN release stays inside the active gesture");
            Frame(weaponX, weaponY, true, id: 12);
            Require(StylusZone.Held == StylusRegion.Weapons
                && StylusZone.TakePressed() == StylusRegion.None,
                "WPN handoff after transient release does not repeat the action");

            // Render frequency must not decide when the action rearms. At 240 Hz
            // several up samples can arrive before gameplay advances once; all of
            // them still belong to the same gesture until a 60 Hz step observes it.
            Frame(weaponX, weaponY, false, id: 12);
            Frame(weaponX, weaponY, false, id: 12);
            Frame(weaponX, weaponY, false, id: 12);
            Require(StylusZone.Held == StylusRegion.Weapons
                && StylusZone.TakePressed() == StylusRegion.None,
                "high-refresh release samples cannot rearm WPN before simulation");
            PointerDevice.AdvanceSimulationStep();
            Frame(weaponX, weaponY, false, id: 12);
            Require(StylusZone.Held == StylusRegion.None,
                "release ends only after crossing a simulation boundary");
            Frame(weaponX, weaponY, true, id: 13);
            Require(StylusZone.TakePressed() == StylusRegion.Weapons,
                "stable WPN release rearms the next touch");

            // A settings/menu click may still be physically held when the overlay
            // closes. That contact is UI-owned and must stay quarantined until a
            // real release, rather than becoming a fresh gameplay WPN press.
            PointerDevice.Reset();
            PointerInput.StylusMode = true;
            StylusZone.Enabled = true;
            StylusZone.AspectCorrection = 1920f / 1080;
            StylusZone.SetRect(0, 0, 1);
            Frame(weaponX, weaponY, true, acceptsInput: false, id: 20);
            Frame(weaponX, weaponY, true, acceptsInput: true, id: 20);
            Require(!StylusZone.CapturingPointer
                && StylusZone.TakePressed() == StylusRegion.None,
                "held UI contact cannot click through into gameplay");
            Frame(weaponX, weaponY, false, acceptsInput: true, id: 20);
            Frame(weaponX, weaponY, true, acceptsInput: true, id: 21);
            Require(StylusZone.TakePressed() == StylusRegion.Weapons,
                "UI-owned contact rearms only after a real release");

            foreach (StylusZone.Button button in StylusZone.Buttons)
            {
                StylusZone.Reset();
                float x = button.X / StylusZone.DsWidth;
                float y = button.Y / StylusZone.DsHeight * StylusZone.Height;
                StylusZone.Update(x, y, true);
                Require(StylusZone.CapturingPointer && StylusZone.CapturingPrimaryButton, $"{button.Label} captures tip");
                Require(!StylusZone.Aiming, $"{button.Label} never aims");
                if (button.Region == StylusRegion.WeaponSelect)
                {
                    StylusZone.Update(-1, -1, true);
                    Require(StylusZone.MenuHeld, "SEL holds wheel after dragging outside");
                }
                else
                {
                    Require(StylusZone.TakePressed() == button.Region, $"{button.Label} action latched");
                    Require(StylusZone.TakePressed() == StylusRegion.None, "latched action consumed once");
                }
                StylusZone.Update(x, y, false);
                Require(!StylusZone.CapturingPointer && !StylusZone.MenuHeld, "release ends ownership");
            }
            StylusZone.Reset();
            StylusZone.Update(-1, -1, true);
            StylusZone.Update(.5f, .4f, true);
            Require(!StylusZone.CapturingPointer && StylusZone.Held == StylusRegion.None, "outside contact stays ordinary");
            StylusZone.Reset();
            StylusZone.Update(.5f, .4f, true);
            StylusZone.Update(-1, -1, true);
            Require(StylusZone.Aiming && StylusZone.Held == StylusRegion.Aim, "aim ownership stays sticky outside zone");
            StylusZone.BeginPlacement();
            StylusZone.Update(.5f, .4f, true);
            Require(StylusZone.CapturingPointer && !StylusZone.CapturingPrimaryButton && !StylusZone.Aiming,
                "placement owns pointer separately from zone contact");
            StylusZone.CancelPlacement();
            PointerInput.StylusMode = false;
            StylusZone.Update(.5f, .4f, true);
            Require(!StylusZone.Enabled && !StylusZone.CapturingPointer, "master off disables DS zone");
        }

        private static void CheckPlayerInput()
        {
            // Only the input pass runs. Construct the real owned state without
            // loading cartridge music, models or a GL context.
            var scene = new Scene(new OpenTK.Mathematics.Vector2i(256, 192),
                SyntheticInput.CreateKeyboard(), SyntheticInput.CreateMouse(), _ => { }, () => { },
                initializeRuntime: false);
            typeof(Scene).GetField("_cameraMode", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(scene, CameraMode.Player);
            var player = PlayerEntity.Main;
            player.LoadFlags = LoadFlags.Active;
            var keyboard = SyntheticInput.CreateKeyboard();
            var mouse = SyntheticInput.CreateMouse();
            var setKey = typeof(KeyboardState).GetMethod("SetKeyState",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                .CreateDelegate<Action<KeyboardState, Keys, bool>>();
            var setButton = typeof(MouseState).GetProperty("Item",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetMethod!
                .CreateDelegate<Action<MouseState, MouseButton, bool>>();
            var controls = player.Controls;
            var processTouchInput = typeof(PlayerEntity).GetMethod("ProcessTouchInput",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            // Suppressed gameplay must clear the shared Keybind surface and still
            // advance its raw baselines. Otherwise a one-frame press stays asserted
            // for every suppressed step, or a key held in Settings becomes a new
            // gameplay edge when Settings closes.
            controls.NextWeapon.Type = ButtonType.Key;
            controls.NextWeapon.Key = Keys.H;
            setKey(keyboard, Keys.H, true);
            PlayerEntity.ProcessInput(keyboard, mouse, false);
            Require(controls.NextWeapon.IsDown && controls.NextWeapon.IsPressed,
                "keyboard weapon edge begins normally");
            PlayerEntity.ProcessInput(keyboard, mouse, true);
            Require(!controls.NextWeapon.IsDown && !controls.NextWeapon.IsPressed
                && !controls.NextWeapon.IsReleased,
                "suppressed gameplay clears stale held and pressed keybind state");
            PlayerEntity.ProcessInput(keyboard, mouse, false);
            Require(controls.NextWeapon.IsDown && !controls.NextWeapon.IsPressed,
                "held UI key resumes as state, not a new gameplay edge");
            setKey(keyboard, Keys.H, false);
            PlayerEntity.ProcessInput(keyboard, mouse, false);

            // The overlay can open and close entirely between two simulation steps
            // on a high-refresh display. The context revision must still quarantine
            // the held key for the first gameplay step after that transition.
            setKey(keyboard, Keys.H, true);
            Mods.Input.GamepadContexts.MenuVisible = true;
            Mods.Input.GamepadContexts.MenuVisible = false;
            PlayerEntity.ProcessInput(keyboard, mouse, false);
            Require(!controls.NextWeapon.IsDown && !controls.NextWeapon.IsPressed,
                "between-step UI transition cannot leak a held key into gameplay");
            PlayerEntity.ProcessInput(keyboard, mouse, false);
            Require(controls.NextWeapon.IsDown && !controls.NextWeapon.IsPressed,
                "post-transition held key remains edge-neutral until release");
            setKey(keyboard, Keys.H, false);
            PlayerEntity.ProcessInput(keyboard, mouse, false);

            PointerDevice.Reset();
            PointerInput.StylusMode = true;
            StylusZone.Enabled = true;
            StylusZone.SetRect(0, 0, 1);
            Frame(1000, 600, false);
            Frame(1000, 600, true);
            setButton(mouse, MouseButton.Left, true);
            controls.Shoot.Type = controls.AltAttack.Type = ButtonType.Key;
            controls.Shoot.Key = Keys.F;
            controls.AltAttack.Key = Keys.G;
            setKey(keyboard, Keys.F, true);
            setKey(keyboard, Keys.G, true);
            PlayerEntity.ProcessInput(keyboard, mouse, false);
            Require(controls.Shoot.IsDown && controls.Shoot.IsPressed && controls.AltAttack.IsDown,
                "real input pass preserves rebound Shoot and AltAttack during tip contact");
            Frame(1010, 605, true);
            PlayerEntity.ProcessInput(keyboard, mouse, false);
            Require(controls.Shoot.IsDown && !controls.Shoot.IsPressed && StylusZone.Aiming,
                "real input pass keeps firing while aiming");
            controls.Shoot.Type = controls.AltAttack.Type = controls.Jump.Type = ButtonType.Mouse;
            controls.Shoot.MouseButton = controls.AltAttack.MouseButton = controls.Jump.MouseButton = MouseButton.Left;
            PlayerEntity.ProcessInput(keyboard, mouse, false);
            Require(!controls.Shoot.IsDown && !controls.AltAttack.IsDown && !controls.Jump.IsDown,
                "real input pass captures all LMB-bound actions");
            GamepadContexts.Current = GamepadContext.Gameplay;
            // Establish the default binding revision on a neutral frame first. A binding
            // change intentionally blocks buttons already held at that transition so
            // remapping cannot leak the capture press into gameplay.
            PadBindings.Reset();
            GamepadManager.UpdateDevice("pointercheck", new GamepadState { Connected = true }, mapped: true);
            GamepadInput.BeginFrame();
            GamepadManager.UpdateDevice("pointercheck", new GamepadState { Connected = true, Buttons = GamepadButtons.RightTrigger }, mapped: true);
            GamepadInput.BeginFrame();
            GamepadInput.Apply(player);
            Require(controls.Shoot.IsDown && controls.AltAttack.IsDown, "real controller contribution survives stylus capture");
            GamepadManager.RemoveDevice("pointercheck");
            GamepadInput.BeginFrame();
            foreach (StylusZone.Button button in StylusZone.Buttons)
            {
                // PointerDevice deliberately requires a pen-up to survive a
                // complete additional sample before re-arming one-shot DS
                // buttons. Two neutral samples model a genuine lift; one is
                // reserved for the tablet handoff glitch covered above.
                Frame(0, 0, false);
                Frame(0, 0, false);
                Frame(button.X / StylusZone.DsWidth * 1920, button.Y / StylusZone.DsHeight * StylusZone.Height * 1080, true);
                PlayerEntity.ProcessInput(keyboard, mouse, false);
                Require(!controls.Shoot.IsDown && !controls.AltAttack.IsDown, $"{button.Label} does not fire in real input pass");
                Keybind bind = button.Region switch
                {
                    StylusRegion.PowerBeam => controls.PowerBeam,
                    StylusRegion.Missile => controls.Missile,
                    StylusRegion.Weapons => controls.NextWeapon,
                    StylusRegion.WeaponSelect => controls.WeaponMenu,
                    _ => controls.Morph
                };
                if (button.Region == StylusRegion.WeaponSelect)
                {
                    Require(!bind.IsDown && StylusZone.MenuHeld,
                        "SEL stays source-owned instead of mutating the shared WeaponMenu bind");
                }
                else
                {
                    Require(bind.IsDown, $"{button.Label} reaches its gameplay action");
                }
            }

            // SEL is a source-owned hold: contact opens the weapon menu and pen-up
            // must close it without depending on a stale value in WeaponMenu.IsDown.
            // Keep selection empty so closing this synthetic menu has no audio/equip side effect.
            typeof(PlayerEntity).GetField("<WeaponSelection>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(player, BeamType.None);
            Frame(0, 0, false);
            Frame(0, 0, false);
            var select = Array.Find(StylusZone.Buttons,
                button => button.Region == StylusRegion.WeaponSelect);
            Frame(select.X / StylusZone.DsWidth * 1920,
                select.Y / StylusZone.DsHeight * StylusZone.Height * 1080, true);
            PlayerEntity.ProcessInput(keyboard, mouse, false);
            processTouchInput.Invoke(player, null);
            Require(player.Flags1.TestFlag(PlayerFlags1.WeaponMenuOpen),
                "stylus SEL opens weapon menu");
            Frame(select.X / StylusZone.DsWidth * 1920,
                select.Y / StylusZone.DsHeight * StylusZone.Height * 1080, false);
            Frame(select.X / StylusZone.DsWidth * 1920,
                select.Y / StylusZone.DsHeight * StylusZone.Height * 1080, false);
            PlayerEntity.ProcessInput(keyboard, mouse, false);
            processTouchInput.Invoke(player, null);
            Require(!player.Flags1.TestFlag(PlayerFlags1.WeaponMenuOpen)
                && !player.Flags1.TestFlag(PlayerFlags1.NoAimInput),
                "stylus SEL release closes weapon menu");

            PointerInput.StylusMode = false;
            Frame(1000, 600, true);
            setButton(mouse, MouseButton.Left, false);
            PlayerEntity.ProcessInput(keyboard, mouse, false);
            setButton(mouse, MouseButton.Left, true);
            PlayerEntity.ProcessInput(keyboard, mouse, false);
            Require(controls.Shoot.IsDown && controls.AltAttack.IsDown && controls.Jump.IsPressed,
                "normal mouse restores all primary bindings");
            PlayerEntity.Reset();
        }

        private static void CheckSettings()
        {
            string originalDirectory = Launcher.LauncherPrefs.Directory;
            string directory = Path.Combine(Path.GetTempPath(), "project-prime-pointer-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "controls.txt");
            try
            {
                Launcher.LauncherPrefs.Directory = directory;
                File.WriteAllText(path, "pointer_jump_guard=true\nstylus_zone=true\n");
                InputSettings.Load();
                Require(PointerInput.StylusMode && PointerInput.GuardJumps && StylusZone.Enabled, "legacy enabled file migrates");
                File.WriteAllText(path, "pointer_jump_guard=false\n");
                InputSettings.Load();
                Require(!PointerInput.StylusMode, "legacy disabled file migrates");
                File.WriteAllText(path, "stylus_mode=true\npointer_jump_guard=false\n");
                InputSettings.Load();
                Require(PointerInput.StylusMode && !PointerInput.GuardJumps && StylusZone.Enabled, "mode independent of guard, explicit setting first");
                File.WriteAllText(path, "pointer_jump_guard=false\nstylus_mode=true\n");
                InputSettings.Load();
                Require(PointerInput.StylusMode && !PointerInput.GuardJumps, "explicit setting last");
                InputSettings.Save();
                PointerInput.StylusMode = false;
                PointerInput.GuardJumps = true;
                InputSettings.Load();
                Require(PointerInput.StylusMode && !PointerInput.GuardJumps, "independent settings round trip");

                File.WriteAllText(path,
                    "mouse_movement_boost=false\nstylus_movement_boost=false\n");
                InputSettings.Load();
                Require(!InputSettings.MouseMovementBoost && !InputSettings.StylusMovementBoost,
                    "mouse and stylus movement boost can be disabled independently of button boost");
                InputSettings.Save();
                InputSettings.MouseMovementBoost = true;
                InputSettings.StylusMovementBoost = true;
                InputSettings.Load();
                Require(!InputSettings.MouseMovementBoost && !InputSettings.StylusMovementBoost,
                    "movement boost settings round trip");

                File.WriteAllText(path,
                    "stylus_mode=true\nstylus_zone=true\nstylus_zone_opacity=0.4\n");
                InputSettings.Load();
                Require(Math.Abs(StylusZone.OutlineOpacity - 0.4f) < 0.0001f
                    && Math.Abs(StylusZone.ButtonOpacity - 0.2f) < 0.0001f,
                    "legacy combined overlay opacity preserves its appearance");

                File.WriteAllText(path,
                    "stylus_mode=true\nstylus_zone=true\nstylus_zone_opacity=0.4\n"
                    + "stylus_cursor_opacity=0\nstylus_zone_outline_opacity=0\n"
                    + "stylus_zone_button_opacity=0.75\n");
                InputSettings.Load();
                Require(StylusZone.CursorOpacity == 0 && StylusZone.OutlineOpacity == 0
                    && Math.Abs(StylusZone.ButtonOpacity - 0.75f) < 0.0001f,
                    "per-element opacity accepts zero and overrides legacy value");

                InputSettings.Save();
                StylusZone.CursorOpacity = 1;
                StylusZone.OutlineOpacity = 1;
                StylusZone.ButtonOpacity = 1;
                InputSettings.Load();
                Require(StylusZone.CursorOpacity == 0 && StylusZone.OutlineOpacity == 0
                    && Math.Abs(StylusZone.ButtonOpacity - 0.75f) < 0.0001f,
                    "per-element opacity round trip");

                InputSettings.Reset();
                Require(!PointerInput.StylusMode && PointerInput.GuardJumps && !StylusZone.Enabled,
                    "reset restores ordinary mouse defaults");
                Require(InputSettings.MouseMovementBoost && InputSettings.StylusMovementBoost,
                    "reset restores movement-triggered boost defaults");
                Require(Math.Abs(StylusZone.CursorOpacity - StylusZone.DefaultCursorOpacity) < 0.0001f
                    && Math.Abs(StylusZone.OutlineOpacity - StylusZone.DefaultOutlineOpacity) < 0.0001f
                    && Math.Abs(StylusZone.ButtonOpacity - StylusZone.DefaultButtonOpacity) < 0.0001f,
                    "reset restores stylus appearance defaults");
            }
            finally
            {
                Launcher.LauncherPrefs.Directory = originalDirectory;
                File.Delete(path);
                Directory.Delete(directory);
            }
        }
    }
}
