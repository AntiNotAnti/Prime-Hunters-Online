#if MPHREAD_SHELL
using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Threading;
using MphRead.Mods.Input;

namespace MphRead.Mods.Launcher.Gui
{
    internal static class GamepadUiChecks
    {
        public static void Run(string? shots = null)
        {
            GamepadChecks.Check(GuiLauncher.EnsureSetup(), "headless UI initialization");
            var panel = new StackPanel();
            var first = new UiWord("First");
            var hidden = new UiWord("Hidden") { IsVisible = false };
            var disabled = new UiWord("Disabled") { IsEnabled = false };
            var last = new UiWord("Last");
            panel.Children.Add(first); panel.Children.Add(hidden); panel.Children.Add(disabled); panel.Children.Add(last);
            var window = new Window { Width = 600, Height = 400, Content = panel };
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            FocusNavigator.Ensure(panel);
            GamepadChecks.Check(first.IsFocused, "controller establishes focus");
            FocusNavigator.Move(panel, UiAction.Down);
            GamepadChecks.Check(last.IsFocused, "navigation skips hidden and disabled controls");
            int clicked = 0; last.Click += (_, _) => clicked++;
            FocusNavigator.Key(last, Avalonia.Input.Key.Enter);
            GamepadChecks.Check(clicked == 1, "controller activates existing UI control");

            // The front door now has two responsive navigation arrangements.
            // Both carry semantic defaults so controller focus does not depend
            // on which control happens to be nearest after a resize.
            var hub = new HubHomeView();
            window.Width = 960; window.Height = 660; window.Content = hub;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var desktopPlay = ControllerNav.Find(hub, "hub.desktop.play");
            GamepadChecks.Check(desktopPlay is { IsEffectivelyVisible: true },
                "FPS hub exposes desktop Play navigation");
            FocusNavigator.Ensure(hub);
            GamepadChecks.Check(desktopPlay!.IsFocused,
                "FPS hub desktop navigation establishes focus on Play");

            HubDestination? clickedHubDestination = null;
            hub.NavigateRequested += destination => clickedHubDestination = destination;
            (string Id, HubDestination Destination)[] desktopActions =
            {
                ("hub.desktop.play", HubDestination.Play),
                ("hub.desktop.map-editor", HubDestination.MapEditor),
                ("hub.desktop.replay-studio", HubDestination.ReplayStudio),
                ("hub.desktop.settings", HubDestination.Settings),
                ("hub.desktop.quit", HubDestination.Quit)
            };
            foreach ((string id, HubDestination destination) in desktopActions)
            {
                Control action = ControllerNav.Find(hub, id)!;
                clickedHubDestination = null;
                Click(window, action);
                GamepadChecks.Check(clickedHubDestination == destination,
                    $"FPS hub pointer click activates {destination}");
            }
            clickedHubDestination = null;
            Click(window, desktopPlay, xFraction: 0.9);
            GamepadChecks.Check(clickedHubDestination == HubDestination.Play,
                "FPS hub Play accepts clicks across the full card");

            window.Width = 700; window.Height = 480;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var compactPlay = ControllerNav.Find(hub, "hub.compact.play");
            GamepadChecks.Check(compactPlay is { IsEffectivelyVisible: true }
                && !desktopPlay.IsEffectivelyVisible,
                "FPS hub switches to compact controller navigation");
            FocusNavigator.Ensure(hub);
            GamepadChecks.Check(compactPlay!.IsFocused,
                "FPS hub compact navigation restores a semantic default");

            (string Id, HubDestination Destination)[] compactActions =
            {
                ("hub.compact.play", HubDestination.Play),
                ("hub.compact.map-editor", HubDestination.MapEditor),
                ("hub.compact.replay-studio", HubDestination.ReplayStudio),
                ("hub.compact.settings", HubDestination.Settings),
                ("hub.compact.quit", HubDestination.Quit)
            };
            foreach ((string id, HubDestination destination) in compactActions)
            {
                Control action = ControllerNav.Find(hub, id)!;
                clickedHubDestination = null;
                Click(window, action);
                GamepadChecks.Check(clickedHubDestination == destination,
                    $"FPS hub compact pointer click activates {destination}");
            }

            var deployment = new HubPlayView();
            window.Width = 960; window.Height = 660; window.Content = deployment;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var multiplayerDeploy = ControllerNav.Find(deployment, "play.multiplayer");
            GamepadChecks.Check(multiplayerDeploy is { IsEffectivelyVisible: true },
                "Play exposes Multiplayer");
            GamepadChecks.Check(LauncherBackdrop.Scene == LauncherBackdropScene.Play,
                "Play selects the cinematic Play backdrop");
            FocusNavigator.Ensure(deployment);
            GamepadChecks.Check(multiplayerDeploy!.IsFocused,
                "Play defaults controller focus to Multiplayer");
            FocusNavigator.Move(deployment, UiAction.Right);
            GamepadChecks.Check(ControllerNav.Find(deployment, "play.offline")!.IsFocused,
                "Play uses explicit controller neighbours");

            HubPlayDestination? selectedDeployment = null;
            int deploymentClosed = 0;
            deployment.Selected += destination => selectedDeployment = destination;
            deployment.Closed += (_, _) => deploymentClosed++;
            foreach ((string id, HubPlayDestination destination) in new[]
            {
                ("play.multiplayer", HubPlayDestination.Multiplayer),
                ("play.offline", HubPlayDestination.Offline),
                ("play.adventure", HubPlayDestination.Adventure)
            })
            {
                selectedDeployment = null;
                Click(window, ControllerNav.Find(deployment, id)!);
                GamepadChecks.Check(selectedDeployment == destination,
                    $"Play pointer click activates {destination}");
            }
            Click(window, ControllerNav.Find(deployment, "play.back")!);
            GamepadChecks.Check(deploymentClosed == 1,
                "Play Back accepts a pointer click");

            var browserSample = new[]
            {
                new ServerBrowserEntry(
                    new Network.MasterListing
                    {
                        Address = "127.0.0.1",
                        Port = Network.NetConfig.DefaultPort,
                        ServerName = "Test Arena",
                        RoomKey = "MP3 PROVING GROUND",
                        Mode = GameMode.Battle,
                        Players = 2,
                        MaxPlayers = 8,
                        Protocol = Network.NetConfig.ProtocolVersion
                    },
                    new Network.ServerStatus
                    {
                        Online = true,
                        RoomKey = "MP3 PROVING GROUND",
                        ServerName = "Test Arena",
                        Mode = GameMode.Battle,
                        Players = 2,
                        MaxPlayers = 8,
                        Protocol = Network.NetConfig.ProtocolVersion,
                        Latency = 31
                    })
            };
            var multiplayer = new HubMultiplayerView(browserSample);
            int multiplayerClosed = 0, lobbyRequested = 0;
            multiplayer.Closed += (_, _) => multiplayerClosed++;
            multiplayer.CreateLobbyRequested += (_, _) => lobbyRequested++;
            window.Width = 960; window.Height = 660; window.Content = multiplayer;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();

            var quickMultiplayer = ControllerNav.Find(multiplayer, "multiplayer.quick");
            var refreshMultiplayer = ControllerNav.Find(multiplayer, "multiplayer.refresh");
            var joinMultiplayer = ControllerNav.Find(multiplayer, "multiplayer.join");
            GamepadChecks.Check(quickMultiplayer is { IsEffectivelyVisible: true }
                && refreshMultiplayer is { IsEffectivelyVisible: true }
                && joinMultiplayer is { IsEffectivelyVisible: true },
                "Multiplayer exposes Quick Play, browser refresh and Join");

            FocusNavigator.Ensure(multiplayer);
            GamepadChecks.Check(quickMultiplayer!.IsFocused,
                "Multiplayer defaults controller focus to Quick Play");

            Click(window, quickMultiplayer);
            GamepadChecks.Check(joinMultiplayer!.IsEnabled,
                "sample Quick Play selects a compatible server without networking");
            GamepadChecks.Check(
                LauncherBackdrop.Scene == LauncherBackdropScene.Multiplayer
                && LauncherBackdrop.RoomKey == "MP3 PROVING GROUND",
                "Multiplayer backdrop follows the selected server map");

            Click(window, ControllerNav.Find(multiplayer, "multiplayer.create")!);
            GamepadChecks.Check(lobbyRequested == 1,
                "Multiplayer Create Lobby accepts a pointer click");

            Click(window, refreshMultiplayer!);
            Click(window, ControllerNav.Find(multiplayer, "multiplayer.back")!);
            GamepadChecks.Check(multiplayerClosed == 1,
                "Multiplayer Back accepts a pointer click");

            var placeholder = new HubPlaceholderView(
                "MAP EDITOR", "WORKSHOP PLACEHOLDER", "Coming soon.");
            int placeholderClosed = 0;
            placeholder.Closed += (_, _) => placeholderClosed++;
            window.Content = placeholder; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Click(window, ControllerNav.Find(placeholder, "placeholder.back")!);
            GamepadChecks.Check(placeholderClosed == 1,
                "Map Editor placeholder Back accepts a pointer click");

            var adventure = new HubAdventureView();
            LaunchPlan? adventurePlan = null;
            int adventureClosed = 0;
            adventure.Launched += (_, plan) => adventurePlan = plan;
            adventure.Closed += (_, _) => adventureClosed++;
            window.Width = 960; window.Height = 660; window.Content = adventure;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            GamepadChecks.Check(ControllerNav.Find(adventure, "adventure.slot1") is { IsEffectivelyVisible: true },
                "Adventure exposes save slots");
            Click(window, ControllerNav.Find(adventure, "adventure.slot2")!);
            Click(window, ControllerNav.Find(adventure, "adventure.newgame")!);
            GamepadChecks.Check(adventurePlan is { SaveSlot: 2, NewGame: true },
                "Adventure pointer flow launches the selected new-game slot");
            Click(window, ControllerNav.Find(adventure, "adventure.back")!);
            GamepadChecks.Check(adventureClosed == 1,
                "Adventure Back accepts a pointer click");

            var offlineSettings = new MenuSettings { RoomKey = "MP3 PROVING GROUND" };
            var offlineView = new HubOfflineView(offlineSettings,
                new[] { "MP3 PROVING GROUND" });
            LaunchPlan? offlinePlan = null;
            int offlineClosed = 0;
            offlineView.Launched += (_, plan) => offlinePlan = plan;
            offlineView.Closed += (_, _) => offlineClosed++;
            window.Width = 960; window.Height = 660; window.Content = offlineView;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            GamepadChecks.Check(
                LauncherBackdrop.Scene == LauncherBackdropScene.Offline
                && LauncherBackdrop.RoomKey == "MP3 PROVING GROUND",
                "Offline backdrop follows the selected map");
            Click(window, ControllerNav.Find(offlineView, "offline.start")!);
            GamepadChecks.Check(offlinePlan is { Kind: LaunchKind.Offline,
                RoomKey: "MP3 PROVING GROUND" },
                "Offline Start Match emits the selected local launch plan");
            Click(window, ControllerNav.Find(offlineView, "offline.back")!);
            GamepadChecks.Check(offlineClosed == 1,
                "Offline Back accepts a pointer click");

            var customMatch = new CreateServerScreen(
                Array.Empty<string>(), discoverHosts: false);
            int customClosed = 0;
            customMatch.Closed += (_, _) => customClosed++;
            window.Width = 960; window.Height = 660; window.Content = customMatch;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            GamepadChecks.Check(ControllerNav.Find(customMatch, "custom.create")
                is { IsEffectivelyVisible: true },
                "Custom Match exposes Create Lobby");
            Click(window, ControllerNav.Find(customMatch, "custom.back")!);
            GamepadChecks.Check(customClosed == 1,
                "Custom Match Back accepts a pointer click");

            var rotationPicker = new MapRotationPicker(
                Array.Empty<string>(), Array.Empty<string>());
            int rotationCancelled = 0;
            rotationPicker.Cancelled += (_, _) => rotationCancelled++;
            window.Content = rotationPicker; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Click(window, ControllerNav.Find(rotationPicker, "rotation.back")!);
            GamepadChecks.Check(rotationCancelled == 1,
                "Custom Match map rotation Back accepts a pointer click");

            var hostPicker = new HostPicker(new[]
            {
                new Network.HostCandidate
                {
                    Label = "Test host",
                    Host = "127.0.0.1",
                    Port = Network.NetConfig.DefaultPort,
                    Answered = true,
                    CanHost = true,
                    Latency = 1
                }
            }, asking: false);
            int hostCancelled = 0;
            hostPicker.Cancelled += (_, _) => hostCancelled++;
            window.Content = hostPicker; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Click(window, ControllerNav.Find(hostPicker, "host.back")!);
            GamepadChecks.Check(hostCancelled == 1,
                "Custom Match host selection Back accepts a pointer click");

            var replayStudio = new HubReplayStudioView();
            int replayStudioClosed = 0;
            replayStudio.Closed += (_, _) => replayStudioClosed++;
            window.Width = 960; window.Height = 660; window.Content = replayStudio;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            GamepadChecks.Check(ControllerNav.Find(replayStudio, "studio.import")
                is { IsEffectivelyVisible: true },
                "Replay Studio exposes Import");
            GamepadChecks.Check(
                LauncherBackdrop.Scene == LauncherBackdropScene.ReplayStudio,
                "Replay Studio selects its cinematic backdrop");
            Click(window, ControllerNav.Find(replayStudio, "studio.back")!);
            GamepadChecks.Check(replayStudioClosed == 1,
                "Replay Studio Back accepts a pointer click");

            var settingsHub = new HubSettingsView();
            window.Content = settingsHub; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var displaySettings = ControllerNav.Find(settingsHub, "settings.display");
            GamepadChecks.Check(displaySettings is { IsEffectivelyVisible: true },
                "settings hub exposes Display");
            GamepadChecks.Check(
                LauncherBackdrop.Scene == LauncherBackdropScene.Settings,
                "Settings selects the subdued cinematic backdrop");
            FocusNavigator.Ensure(settingsHub);
            GamepadChecks.Check(displaySettings!.IsFocused,
                "settings hub defaults controller focus to Display");

            string? selectedSettingsSection = null;
            int settingsClosed = 0;
            settingsHub.SectionRequested += section => selectedSettingsSection = section;
            settingsHub.Closed += (_, _) => settingsClosed++;
            foreach ((string id, string section) in new[]
            {
                ("settings.display", "Display"),
                ("settings.audio", "Audio"),
                ("settings.controls", "Controls"),
                ("settings.replays", "Replays"),
                ("settings.profile", "Profile"),
                ("settings.credits", "Credits")
            })
            {
                selectedSettingsSection = null;
                Click(window, ControllerNav.Find(settingsHub, id)!);
                GamepadChecks.Check(selectedSettingsSection == section,
                    $"settings pointer click activates {section}");
            }

            window.Width = 650; window.Height = 470;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            FocusNavigator.Focus(displaySettings);
            FocusNavigator.Move(settingsHub, UiAction.Down);
            GamepadChecks.Check(ControllerNav.Find(settingsHub, "settings.controls")!.IsFocused,
                "short-wide settings navigation follows its two-column visual order");

            Click(window, ControllerNav.Find(settingsHub, "settings.back")!);
            GamepadChecks.Check(settingsClosed == 1,
                "settings Back accepts a pointer click");

            panel = new StackPanel();
            window.Width = 600; window.Height = 400; window.Content = panel;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var choice = new ChoiceRow("Option", new[] { "One", "Two" }, 0);
            panel.Children.Add(choice); window.UpdateLayout(); FocusNavigator.Focus(choice);
            FocusNavigator.Key(choice, Avalonia.Input.Key.Right);
            GamepadChecks.Check(choice.Index == 1, "choice row supports semantic arrows");
            var text = new TextBox { Text = "Player", Width = 250 };
            panel.Children.Add(text); window.UpdateLayout();
            var keyboard = new ControllerKeyboard(text, () => { });
            Dispatcher.UIThread.RunJobs();
            GamepadChecks.Check(FocusNavigator.Ensure(keyboard.NavigationRoot) != null, "controller text entry has focus");
            keyboard.Close(false);
            GamepadChecks.Check(text.Text == "Player", "cancel text entry preserves value");
            var confirm = new ConfirmScreen("Leave the match?");
            bool? answer = null; confirm.Answered += (_, value) => answer = value;
            window.Content = confirm; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var cancel = FocusNavigator.Ensure(confirm);
            GamepadChecks.Check(cancel != null, "confirmation has focus");
            FocusNavigator.Key(cancel!, Avalonia.Input.Key.Escape);
            GamepadChecks.Check(answer == false, "controller Back dismisses confirmation");
            var settings = new SettingsView(new MenuSettings());
            window.Width = 960; window.Height = 660; window.Content = settings;
            settings.ShowSection("Controls", 1); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var rows = settings.GetVisualDescendants().OfType<PadRow>().ToArray();
            GamepadChecks.Check(rows.Length == PadBindings.Actions.Count, "every pad action appears in settings");
            FocusNavigator.Focus(rows[^1]); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            GamepadChecks.Check(rows[^1].IsFocused, "last binding reachable through scrolling");
            var rowPoint = rows[^1].TranslatePoint(new Point(), window);
            GamepadChecks.Check(rowPoint.HasValue && rowPoint.Value.Y >= 0
                && rowPoint.Value.Y + rows[^1].Bounds.Height <= window.Bounds.Height,
                "focus scrolls binding inside the viewport");
            if (shots != null)
            {
                Directory.CreateDirectory(shots);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var bitmap = window.GetLastRenderedFrame();
                bitmap?.Save(Path.Combine(shots, "controller-bindings.png"));
                var gamepad = settings.GetVisualDescendants().OfType<GamepadSettingsPanel>().First();
                FocusNavigator.Focus(gamepad.GetVisualDescendants().OfType<SliderRow>().First());
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var calibration = window.GetLastRenderedFrame();
                calibration?.Save(Path.Combine(shots, "controller-settings.png"));
            }
            CheckControllerSettings(window, settings, shots);
            var pause = new PauseMenuView(false);
            window.Content = pause; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            GamepadChecks.Check(FocusNavigator.Ensure(pause) != null, "pause menu is controller focusable");
            // Android hosts PauseMenuView in StartScreen rather than InGameMenu,
            // so Back must reach the view's resume callback without a desktop host.
            int resumed = 0;
            pause.Resumed += (_, _) => resumed++;
            var navigation = new GamepadNavigation();
            GamepadManager.UpdateDevice("pause-test", new GamepadState { Name = "Pause test" }, true);
            navigation.Update(pause);
            foreach (var button in new[] { GamepadButtons.B, GamepadButtons.Start })
            {
                GamepadManager.UpdateDevice("pause-test", new GamepadState { Name = "Pause test", Buttons = button }, true);
                navigation.Update(pause);
                GamepadManager.UpdateDevice("pause-test", new GamepadState { Name = "Pause test" }, true);
                navigation.Update(pause);
            }
            GamepadChecks.Check(resumed == 2, "B and Start resume the Android-hosted pause menu");
            GamepadManager.RemoveDevice("pause-test");
            Network.MapVote.Apply(new Network.VoteStatePacket
            {
                State = Network.VoteStatePacket.StateRunning, RoomKey = "test", Proposer = "Player", Seconds = 30
            });
            pause.RefreshVote(); window.UpdateLayout();
            var vote = pause.GetVisualDescendants().OfType<HubNavButton>()
                .First(w => w.Label == "ACCEPT MAP VOTE");
            FocusNavigator.Focus(vote);
            GamepadChecks.Check(vote.IsVisible && vote.IsFocused, "active map vote can be reached with controller focus");
            Network.MapVote.Reset(); pause.RefreshVote(); window.UpdateLayout();
            GamepadChecks.Check(!vote.IsVisible && !vote.IsFocused, "expired vote restores pause focus");
            // Exercise binding through the same navigation pump used by the real UI.
            // The old test called PadRow.Check directly and therefore could not catch
            // the menu-path regression where physical presses never reached the row.
            PadBindings.Reset();
            PadBindings.Set(PadAction.Chat, GamepadButtons.None); // free LeftThumb for an unambiguous capture
            var routedBindings = new StackPanel();
            var routedBinding = new PadRow(PadAction.Scan);
            routedBindings.Children.Add(routedBinding);
            window.Content = routedBindings; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            GamepadManager.UpdateDevice("route-test", new GamepadState
            {
                Connected = true, Name = "Route test"
            }, true);
            FocusNavigator.Focus(routedBinding);
            FocusNavigator.Key(routedBinding, Avalonia.Input.Key.Enter);
            navigation.Update(routedBindings);
            GamepadManager.UpdateDevice("route-test", new GamepadState
            {
                Connected = true, Name = "Route test", Buttons = GamepadButtons.LeftThumb
            }, true);
            navigation.Update(routedBindings);
            GamepadChecks.Check(PadBindings.Slot(PadAction.Scan, 0) == GamepadButtons.LeftThumb
                && !GamepadContexts.Capturing,
                "navigation pump delivers physical controller presses to binding capture");
            GamepadManager.RemoveDevice("route-test");

            // Run the real binding row against synthetic normalized device events.
            PadBindings.Reset();
            var binding = new PadRow(PadAction.Scan);
            window.Content = binding; window.UpdateLayout(); binding.Focus();
            void Pad(GamepadButtons buttons)
            {
                GamepadManager.UpdateDevice("ui-test", new GamepadState { Connected = true, Name = "UI test", Buttons = buttons }, true);
                binding.Check();
            }
            Pad(GamepadButtons.A);
            FocusNavigator.Key(binding, Avalonia.Input.Key.Enter);
            binding.Check();
            GamepadChecks.Check(PadBindings.Get(PadAction.Scan) == GamepadButtons.X, "opening Accept cannot bind itself");
            Pad(0); Pad(GamepadButtons.RightBumper);
            GamepadChecks.Check(GamepadContexts.Capturing, "binding conflict waits for a decision");
            Pad(0); Pad(GamepadButtons.B);
            GamepadChecks.Check(PadBindings.Get(PadAction.Scan) == GamepadButtons.X, "cancel conflict preserves mapping");
            Pad(0); FocusNavigator.Key(binding, Avalonia.Input.Key.Enter); Pad(GamepadButtons.Back);
            GamepadChecks.Check(PadBindings.Get(PadAction.Scan) == 0, "controller can clear a binding");
            Pad(0); FocusNavigator.Key(binding, Avalonia.Input.Key.Enter);
            GamepadManager.RemoveDevice("ui-test"); binding.Check();
            GamepadChecks.Check(!GamepadContexts.Capturing, "disconnect exits binding capture");
            window.Close();
        }

        private static void Click(Window window, Control control,
            double xFraction = 0.5, double yFraction = 0.5)
        {
            Point? origin = control.TranslatePoint(new Point(), window);
            GamepadChecks.Check(origin.HasValue,
                $"{control.GetType().Name} has a window-space pointer target");
            Point point = origin!.Value + new Vector(
                control.Bounds.Width * Math.Clamp(xFraction, 0.05, 0.95),
                control.Bounds.Height * Math.Clamp(yFraction, 0.05, 0.95));
            window.MouseMove(point);
            window.MouseDown(point, Avalonia.Input.MouseButton.Left);
            window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        }

        private static void CheckControllerSettings(Window window, SettingsView settings, string? shots)
        {
            var panel = settings.GetVisualDescendants().OfType<GamepadSettingsPanel>().First();
            var advancedButton = ControllerNav.Find(panel, "controller.advanced");
            GamepadChecks.Check(advancedButton != null, "controller settings expose Advanced");
            var monitor = panel.GetVisualDescendants().OfType<GamepadMonitor>().Single();
            GamepadChecks.Check(!monitor.IsEffectivelyVisible, "advanced controller settings are collapsed by default");
            PadBindings.ApplyPreset("Default"); panel.Reload(); window.UpdateLayout();
            var preset = panel.Children.OfType<ChoiceRow>().First(r => r.Value == "Default");
            FocusNavigator.Focus(preset); preset.Index = 1;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            preset = panel.Children.OfType<ChoiceRow>().First(r => r.Value == "Bumper Jumper");
            GamepadChecks.Check(preset.IsFocused && PadBindings.Get(PadAction.Jump) == GamepadButtons.LeftBumper,
                "changing controller preset applies bindings and retains focus");
            FocusNavigator.Key(preset, Avalonia.Input.Key.Right);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            GamepadChecks.Check(GamepadOptions.Southpaw && PadBindings.Preset == "Southpaw",
                "consecutive controller preset changes remain usable");
            PadBindings.ApplyPreset("Default"); panel.Reload(); window.UpdateLayout();

            var navigation = new GamepadNavigation();
            void Pad(GamepadButtons buttons = 0, float rt = 0)
                => GamepadManager.UpdateDevice("settings-xbox", new GamepadState
                    { Name = "Xbox Series controller", Buttons = buttons, RightTrigger = rt }, true,
                    GamepadFamily.Xbox, mapping: "Xbox Bluetooth compatibility");
            Pad(); navigation.Update(settings);
            settings.ShowSection("Controls", 0); window.UpdateLayout();
            var jumpKey = settings.GetVisualDescendants().OfType<KeyRow>().First(r => r.BindingName == "Jump");
            var jumpProperty = InputSettings.Bindings.First(p => p.Name == "Jump");
            string keyboardBefore = InputSettings.Describe(InputSettings.Bind(jumpProperty));
            FocusNavigator.Focus(jumpKey);
            FocusNavigator.Key(jumpKey, Avalonia.Input.Key.Enter);
            Pad(rt: 1); navigation.Update(settings); window.UpdateLayout();
            var jumpPad = settings.GetVisualDescendants().OfType<PadRow>().First(r => r.Action == PadAction.Jump);
            GamepadChecks.Check(jumpPad.IsFocused && GamepadContexts.Capturing && !jumpKey.Listening,
                "controller trigger in keyboard capture opens the matching controller action");
            Pad(); jumpPad.Check(); Pad(GamepadButtons.A); jumpPad.Check();
            GamepadChecks.Check(PadBindings.Get(PadAction.Jump) == GamepadButtons.RightTrigger
                && PadBindings.Get(PadAction.Shoot) == GamepadButtons.A && !GamepadContexts.Capturing,
                "Accept confirms the default Swap instead of silently cancelling a rebind");
            GamepadChecks.Check(InputSettings.Describe(InputSettings.Bind(jumpProperty)) == keyboardBefore,
                "controller rebinding preserves the actual keyboard key");

            panel.RefreshLabels();
            GamepadChecks.Check(panel.Children.OfType<ChoiceRow>().Any(r => r.Value == "Custom"),
                "binding changes update the displayed controller preset");
            settings.ShowSection("Controls", 0); window.UpdateLayout(); FocusNavigator.Focus(jumpKey);
            Pad(); navigation.Update(settings); Pad(GamepadButtons.A); navigation.Update(settings);
            GamepadChecks.Check(jumpPad.IsFocused && GamepadContexts.Capturing && !jumpKey.Listening,
                "controller Accept on a keyboard action enters controller capture");
            Pad(); jumpPad.Check(); Pad(GamepadButtons.B); jumpPad.Check();
            GamepadChecks.Check(!GamepadContexts.Capturing, "Back cancels redirected capture");
            settings.ShowSection("Controls", 0); window.UpdateLayout();
            var moveKey = settings.GetVisualDescendants().OfType<KeyRow>().First(r => r.BindingName == "MoveUp");
            var moveProperty = InputSettings.Bindings.First(p => p.Name == "MoveUp");
            string movementBefore = InputSettings.Describe(InputSettings.Bind(moveProperty));
            FocusNavigator.Focus(moveKey); Pad(); navigation.Update(settings);
            Pad(GamepadButtons.A); navigation.Update(settings);
            GamepadChecks.Check(!moveKey.Listening && InputSettings.Describe(InputSettings.Bind(moveProperty)) == movementBefore,
                "keyboard-only rows never bind synthetic controller Enter");
            settings.ShowSection("Controls", 1); window.UpdateLayout();
            FocusNavigator.Focus(advancedButton);
            FocusNavigator.Key(advancedButton!, Avalonia.Input.Key.Enter);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            GamepadChecks.Check(monitor.IsEffectivelyVisible, "Advanced reveals controller diagnostics");
            Pad(rt: 1); monitor.Refresh();
            GamepadChecks.Check(monitor.Status.Contains("Xbox Bluetooth compatibility"), "live controller test identifies hardware mapping");
            if (shots != null)
            {
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                FocusNavigator.Focus(panel.Children.OfType<ChoiceRow>().First());
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                // Flush the headless compositor after scrolling and deferred row updates.
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
                Dispatcher.UIThread.RunJobs();
                using var bitmap = window.CaptureRenderedFrame();
                bitmap?.Save(Path.Combine(shots, "controller-live-test.png"));
            }
            GamepadManager.RemoveDevice("settings-xbox"); PadBindings.Reset(); GamepadOptions.Reset();
        }
    }
}
#endif
