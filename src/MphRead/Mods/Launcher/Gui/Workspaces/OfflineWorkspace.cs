#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MphRead.Entities;
namespace MphRead.Mods.Launcher.Gui
{
    internal sealed class OfflineWorkspace : UserControl, IPrimeWorkspace
    {
        public event EventHandler<LaunchPlan>? Launched;
        private readonly MenuSettings _settings;
        private readonly IReadOnlyList<string> _rooms;
        private readonly PrimeOverlayHost _overlays;
        private readonly ChoiceRow _hunter, _suit, _mode, _bots, _skill;
        private readonly PrimeButton _map, _resume, _newRun, _start;
        private readonly TextBlock _saveDetail;
        private readonly PrimeButton[] _slots = new PrimeButton[AdventureSave.SlotCount];
        private readonly Image _preview = new() { Height = 120, Stretch = Stretch.UniformToFill };
        private string _room;
        private byte _slot = 1;
        public OfflineWorkspace(MenuSettings settings, IReadOnlyList<string> rooms, PrimeOverlayHost overlays)
        {
            _settings = settings; _rooms = rooms; _overlays = overlays;
            _room = rooms.Contains(settings.RoomKey) ? settings.RoomKey : rooms.FirstOrDefault() ?? "";
            var names = Enumerable.Range(0, Hunters.Playable).Select(i => ((Hunter)i).ToString()).Append("Random").ToArray();
            _hunter = new ChoiceRow("Hunter", names, Math.Max(0, Array.IndexOf(names, LauncherPrefs.LastHunter.ToString())));
            _suit = new ChoiceRow("Suit", new[] { "1", "2", "3", "4" }, Math.Clamp(LauncherPrefs.LastColor, 0, 3));
            _mode = new ChoiceRow("Mode", OfflineLaunch.Modes.Select(m => m.Label).ToArray());
            _bots = new ChoiceRow("Combatants", Enumerable.Range(0, PlayerEntity.SlotCapacity).Select(i => i + " BOTS").ToArray(), Math.Clamp(LauncherPrefs.Bots, 0, 7));
            _skill = new ChoiceRow("Difficulty", new[] { "Easy", "Normal", "Hard", "Insane" }, Math.Clamp(LauncherPrefs.BotLevel, 0, 3));
            _map = new PrimeButton("SELECT ARENA", PickMap);
            ControllerNav.Identify(_map, "offline.map");
            var start = _start = new PrimeButton("▷ INITIATE BOT SIMULATION", () =>
            {
                if (_room.Length == 0) return;
                Launched?.Invoke(this, OfflineLaunch.Create(settings, _room, OfflineLaunch.Modes[_mode.Index].Mode,
                    SelectedHunter(), _suit.Index, _bots.Index, _skill.Index));
            }, true) { IsEnabled = rooms.Count > 0 };
            ControllerNav.Identify(start, "offline.start");
            var botBody = PrimeChrome.Stack(new PrimeBadge("MODE 01 // TACTICAL SIMULATION"), PrimeChrome.Title("BOT SKIRMISH"),
                PrimeChrome.Text("Configure a local arena match with Hunter bots.", 14, PrimeTheme.TextSecondaryBrush),
                _bots, _skill, _map, _preview, _mode, new PrimeButton("ADVANCED MATCH RULES", Rules));
            var bot = WithAction(botBody, start);
            _saveDetail = PrimeChrome.Text("", 13, PrimeTheme.TextSecondaryBrush);
            _resume = new PrimeButton("▷ RESUME", () => Adventure(false), true);
            ControllerNav.Identify(_resume, "offline.adventure.resume");
            var adventureBody = PrimeChrome.Stack(new PrimeBadge("MODE 02 // NARRATIVE CAMPAIGN", PrimeTheme.GreenBrush),
                PrimeChrome.Title("ADVENTURE RUNS"), PrimeChrome.Text("Explore the Alimbic Cluster and recover the Octoliths.", 14, PrimeTheme.TextSecondaryBrush));
            for (byte i = 1; i <= AdventureSave.SlotCount; i++)
            {
                byte slot = i;
                var button = new PrimeButton("SLOT " + i, () => SelectSlot(slot));
                ControllerNav.Identify(button, $"offline.adventure.slot{i}");
                _slots[i - 1] = button; adventureBody.Children.Add(button);
            }
            adventureBody.Children.Add(_saveDetail);
            _newRun = new PrimeButton("+ NEW RUN", () => Adventure(true));
            ControllerNav.Identify(_newRun, "offline.adventure.new");
            var adventure = WithAction(adventureBody, PrimeChrome.Columns("*,*", _resume, _newRun));
            var stand = new HunterStand { MinHeight = 220, Name2 = _hunter.Value, Suit = _suit.Index };
            _hunter.Changed += (_, _) => stand.Name2 = _hunter.Value;
            _suit.Changed += (_, _) => stand.Suit = _suit.Index;
            var avatar = new PrimePanel(PrimeChrome.Stack(new PrimeBadge("SIMULACRUM SPEC"), PrimeChrome.Title("OFFLINE AVATAR"),
                stand, _hunter, _suit, PrimeChrome.Text("LOCAL RIG // READY\nSimulation: 60 Hz", 12, PrimeTheme.GreenBrush, true)));
            var root = new Grid { Margin = PrimeMetrics.PageMargin, RowDefinitions = new("Auto,*"), RowSpacing = 20 };
            root.Children.Add(HubChrome.Header("OPERATIONS // OFFLINE ARCHIVE", "OFFLINE COMBAT MATRIX",
                "Local combat simulations and Adventure save data.", "STANDALONE"));
            var body = PrimeChrome.Columns("1.2*,1.05*,.8*", bot, adventure, avatar);
            Grid.SetRow(body, 1); root.Children.Add(body); Content = root;
            Refresh();
        }
        private static Control WithAction(Control content, Control action)
        {
            var grid = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 12 };
            grid.Children.Add(new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            Grid.SetRow(action, 1); grid.Children.Add(action); return new PrimePanel(grid);
        }
        private void SelectSlot(byte slot)
        {
            _slot = slot;
            for (byte i = 1; i <= _slots.Length; i++)
            {
                var info = AdventureSave.Read(i);
                _slots[i - 1].Label = $"SLOT {i:00} // " + (info.Used ? info.Area.ToUpperInvariant() : "EMPTY");
                _slots[i - 1].Selected = i == slot;
            }
            var save = AdventureSave.Read(slot);
            _resume.IsEnabled = save.Used;
            _saveDetail.Text = save.Used ? $"{save.Area}\nOCTOLITHS: {save.Octoliths} / 8\nENERGY: {save.Health} / {save.HealthMax}"
                : "Initialize a fresh expedition from Celestial Archives.";
        }
        private Hunter SelectedHunter()
        {
            return Enum.TryParse(_hunter.Value, ignoreCase: true, out Hunter hunter)
                && Enum.IsDefined(hunter) ? hunter : Hunter.Samus;
        }

        private void Adventure(bool fresh)
        {
            void Launch() => Launched?.Invoke(this,
                AdventureLaunch.Create(_slot, fresh, SelectedHunter()));
            if (fresh && AdventureSave.Read(_slot).Used)
            {
                _overlays.Show(new PrimePanel(PrimeChrome.Stack(PrimeChrome.Title("REPLACE SAVE SLOT " + _slot + "?"),
                    PrimeChrome.Text("Starting a new run replaces this slot's existing progress when saved."),
                    PrimeChrome.Columns("*,*", new PrimeButton("CANCEL", _overlays.Close),
                        new PrimeButton("START NEW RUN", () => { _overlays.Close(); Launch(); }, danger: true)))), PrimeModalSize.Small);
            }
            else Launch();
        }
        private void PickMap()
        {
            var list = new UiList();
            foreach (var room in _rooms) list.Add(new UiListRow(RoomName(room), room) { Choice = room });
            list.Activated += (_, row) =>
            {
                if (row is UiListRow { Choice: string room }) { _room = room; Refresh(); _overlays.Close(); }
            };
            _overlays.Show(new PrimePanel(PrimeChrome.Stack(PrimeChrome.Title("SELECT ARENA"),
                new Border { Height = 360, Child = list }, new PrimeButton("CANCEL", _overlays.Close))), PrimeModalSize.Medium);
        }
        private void Rules()
        {
            var score = new FieldRow("Point goal", _settings.PointGoal);
            var time = new FieldRow("Time limit (m:ss)", _settings.TimeLimit);
            var objective = new FieldRow("Time goal (m:ss)", _settings.TimeGoal);
            var fire = new ToggleRow("Friendly fire", _settings.FriendlyFire == "on");
            var affinity = new ToggleRow("Affinity weapons", _settings.AffinityWeapons == "on");
            var freeze = new ToggleRow("Shadow freeze", _settings.ShadowFreeze == "on");
            var spawnProtection = new ToggleRow("Spawn protection (3s)", _settings.SpawnProtection != "off");
            var radar = new ToggleRow("Hunter radar", _settings.HunterRadar == "on");
            var damage = new ChoiceRow("Damage", new[] { "low", "medium", "high" }, _settings.DamageLevel == "low" ? 0 : _settings.DamageLevel == "high" ? 2 : 1);
            var error = PrimeChrome.Text("", 12, PrimeTheme.DangerBrush);
            _overlays.Show(new PrimePanel(PrimeChrome.Stack(PrimeChrome.Title("ENGAGEMENT PROTOCOL"),
                score, time, objective, damage, fire, affinity, freeze, spawnProtection, radar, error,
                PrimeChrome.Columns("*,*", new PrimeButton("CANCEL", _overlays.Close), new PrimeButton("APPLY RULES", () =>
                {
                    bool Duration(string value) => TimeSpan.TryParseExact(value, @"m\:ss", null, out _)
                        || TimeSpan.TryParseExact(value, @"mm\:ss", null, out _);
                    if (!int.TryParse(score.Value, out int points) || points < 0 || points > 999 || !Duration(time.Value) || !Duration(objective.Value))
                    { error.Text = "Use a point goal from 0–999 and durations as m:ss."; return; }
                    // The shell reloads settings.json after every completed or abandoned
                    // match. These controls used to change only this MenuSettings instance,
                    // so the first match saw the edit and the next reload restored 7:00/7.
                    // Treat APPLY RULES like the main settings screen: persist first, then
                    // keep the runtime settings facade in sync with the committed values.
                    string oldPointGoal = _settings.PointGoal;
                    string oldTimeLimit = _settings.TimeLimit;
                    string oldTimeGoal = _settings.TimeGoal;
                    string oldFriendlyFire = _settings.FriendlyFire;
                    string oldAffinityWeapons = _settings.AffinityWeapons;
                    string oldShadowFreeze = _settings.ShadowFreeze;
                    string oldSpawnProtection = _settings.SpawnProtection;
                    string oldHunterRadar = _settings.HunterRadar;
                    string oldDamageLevel = _settings.DamageLevel;
                    try
                    {
                        _settings.PointGoal = score.Value;
                        _settings.TimeLimit = time.Value;
                        _settings.TimeGoal = objective.Value;
                        _settings.FriendlyFire = fire.On ? "on" : "off";
                        _settings.AffinityWeapons = affinity.On ? "on" : "off";
                        _settings.ShadowFreeze = freeze.On ? "on" : "off";
                        _settings.SpawnProtection = spawnProtection.On ? "on" : "off";
                        _settings.HunterRadar = radar.On ? "on" : "off";
                        _settings.DamageLevel = damage.Value;
                        GameState.CommitSettings(_settings);
                        Mods.GameSettings.Apply(_settings);
                        _overlays.Close();
                    }
                    catch (Exception ex)
                    {
                        // Do not leave a one-match-only in-memory configuration behind if
                        // the disk write fails. The error stays in this sheet so the player
                        // can retry or cancel without silently diverging from settings.json.
                        _settings.PointGoal = oldPointGoal;
                        _settings.TimeLimit = oldTimeLimit;
                        _settings.TimeGoal = oldTimeGoal;
                        _settings.FriendlyFire = oldFriendlyFire;
                        _settings.AffinityWeapons = oldAffinityWeapons;
                        _settings.ShadowFreeze = oldShadowFreeze;
                        _settings.SpawnProtection = oldSpawnProtection;
                        _settings.HunterRadar = oldHunterRadar;
                        _settings.DamageLevel = oldDamageLevel;
                        error.Text = $"Could not save rules: {ex.Message}";
                    }
                }, true)))), PrimeModalSize.Medium);
        }
        private static string RoomName(string key) => Metadata.RoomMetadata.TryGetValue(key, out var meta) ? meta.InGameName ?? key : key;
        public void OnActivated() => Refresh();
        public void OnDeactivated() { }
        public void Refresh()
        {
            if (_room.Length == 0 && _rooms.Count > 0) _room = _rooms[0];
            _start.IsEnabled = _rooms.Count > 0;
            _map.Label = _room.Length == 0 ? "NO ARENAS // SET UP GAME FILES" : RoomName(_room).ToUpperInvariant();
            _preview.Source = _room.Length == 0 ? null : MapShot.For(_room);
            SelectSlot(_slot);
        }
    }
}
#endif
