using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MphRead.Mods.Input
{
    public sealed record GamepadProfile(int Version, string Name, string[] Settings);
    public sealed record GamepadProfileLibrary(List<GamepadProfile> Profiles, Dictionary<string, string> Assignments);

    // Disk access occurs at startup or explicit settings actions, never during input polling.
    public static class GamepadProfiles
    {
        private static GamepadProfileLibrary _library = new(new(), new());
        private static string _directory = "";

        public static long Revision { get; private set; }
        public static string Status { get; private set; } = "";
        public static string ActiveName { get; private set; } = "Custom settings";
        internal static void NoteActive(string name) => ActiveName = name;
        public static IReadOnlyList<GamepadProfile> Profiles => _library.Profiles;
        private static string LibraryPath => Path.Combine(_directory, "controller-profiles.json");

        public static void Initialize()
        {
            string directory = Launcher.LauncherPrefs.Directory;
            if (directory == _directory) return;
            _directory = directory; _library = new(new(), new()); Status = "";
            try
            {
                if (!File.Exists(LibraryPath)) return;
                if (new FileInfo(LibraryPath).Length > 1048576) throw new InvalidDataException("Profile library is too large.");
                var library = JsonSerializer.Deserialize<GamepadProfileLibrary>(File.ReadAllText(LibraryPath));
                if (library?.Profiles == null || library.Assignments == null || library.Profiles.Count > 32 || library.Assignments.Count > 128)
                    throw new InvalidDataException("Invalid profile library.");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var profile in library.Profiles)
                {
                    BuildRuntime(profile);
                    if (!names.Add(profile.Name)) throw new InvalidDataException("Duplicate controller profile name.");
                }
                foreach (var assignment in library.Assignments)
                    if (string.IsNullOrWhiteSpace(assignment.Key) || assignment.Key.Length > 512 || !names.Contains(assignment.Value))
                        throw new InvalidDataException("Invalid controller profile assignment.");
                _library = library;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { Status = "Could not load controller profiles: " + ex.Message; }
        }
        public static GamepadProfile Capture(string name)
        {
            var lines = new List<string>();
            GamepadOptions.Write(lines); PadBindings.Write(lines);
            lines.Add("gamepad_preset=" + PadBindings.Preset);
            return new(1, name, lines.ToArray());
        }
        public static void Apply(GamepadProfile profile)
        {
            Validate(profile);
            var runtime = BuildRuntime(profile);
            GamepadManager.ReplaceRuntime(runtime);
            ActiveName = profile.Name; Revision++;
        }
        private static GamepadRuntimeConfig BuildRuntime(GamepadProfile profile)
        {
            Validate(profile);
            var runtime = new GamepadRuntimeConfig();
            runtime.Options.Load(profile.Settings);
            foreach (string line in profile.Settings)
            {
                int split = line.IndexOf('=');
                if (split > 0) runtime.Bindings.TryLoad(line[..split], line[(split + 1)..]);
            }
            runtime.Bindings.LoadSlots(profile.Settings);
            foreach (string line in profile.Settings)
            {
                int split = line.IndexOf('='); string key = line[..split];
                if (!key.StartsWith("pad_", StringComparison.Ordinal) || !key.EndsWith("_modifier", StringComparison.Ordinal)) continue;
                string actionName = key[4..].Split('_')[0];
                var action = Enum.Parse<PadAction>(actionName);
                int slot = key.EndsWith("_primary_modifier", StringComparison.Ordinal) ? 0 : 1;
                if (runtime.Bindings.Modifier(action, slot) != Enum.Parse<GamepadButtons>(line[(split + 1)..]))
                    throw new InvalidDataException("A modifier requires a distinct bound button.");
            }
            string? preset = profile.Settings.LastOrDefault(l => l.StartsWith("gamepad_preset=", StringComparison.Ordinal));
            if (preset != null && new[] { "Default", "Bumper Jumper", "Southpaw", "Classic", "Custom" }.Contains(preset[15..]))
                runtime.Bindings.Preset = preset[15..];
            runtime.ProfileName = profile.Name;
            return runtime;
        }
        private static void Validate(GamepadProfile profile)
        {
            if (profile == null || profile.Version != 1 || string.IsNullOrWhiteSpace(profile.Name)
                || profile.Name.Length > 48 || profile.Name.Any(char.IsControl)
                || profile.Settings == null || profile.Settings.Length == 0 || profile.Settings.Length > 256)
                throw new InvalidDataException("Invalid controller profile or unsupported version.");
            foreach (var line in profile.Settings)
                if (line == null || line.Length > 256 || line.Any(char.IsControl) || !line.Contains('=')
                    || !(line.StartsWith("pad_", StringComparison.Ordinal) || line.StartsWith("gamepad_", StringComparison.Ordinal)))
                    throw new InvalidDataException("A profile may contain only controller settings.");
            var defaults = new List<string>(); new GamepadOptionState().Write(defaults);
            var types = defaults.ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);
            types["gamepad_deadzone"] = "0.2"; types["gamepad_look"] = "1";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in profile.Settings)
            {
                int split = line.IndexOf('='); string key = line[..split], value = line[(split + 1)..];
                if (!seen.Add(key)) throw new InvalidDataException("Duplicate controller setting: " + key);
                bool valid;
                if (key.StartsWith("pad_", StringComparison.Ordinal))
                {
                    string action = key[4..].Split('_')[0];
                    string suffix = key[(4 + action.Length)..];
                    valid = Enum.TryParse<PadAction>(action, out var parsed) && Enum.IsDefined(parsed)
                        && new[] { "", "_primary", "_secondary", "_primary_modifier", "_secondary_modifier" }.Contains(suffix)
                        && Enum.TryParse<GamepadButtons>(value, out var buttons) && ((int)buttons & ~0xffff) == 0
                        && (key.IndexOf('_', 4) < 0 || PadBindings.Single(buttons));
                }
                else if (key == "gamepad_preset") valid = new[] { "Default", "Bumper Jumper", "Southpaw", "Classic", "Custom" }.Contains(value);
                else if (!types.TryGetValue(key, out var sample)) valid = false;
                else if (key == "gamepad_curve") valid = Enum.TryParse<GamepadCurve>(value, out var curve) && Enum.IsDefined(curve);
                else if (key == "gamepad_glyph_style") valid = Enum.TryParse<GamepadFamily>(value, out var family) && Enum.IsDefined(family);
                else if (key == "gamepad_binding_modifier") valid = Enum.TryParse<GamepadButtons>(value, out var modifier) && PadBindings.Single(modifier);
                else if (key == "gamepad_wheel_order") valid = value.Split(',').OrderBy(v => v).SequenceEqual(new[] { "0", "1", "2", "3", "4", "5" });
                else if (bool.TryParse(sample, out _)) valid = bool.TryParse(value, out _);
                else valid = float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float number) && float.IsFinite(number);
                if (!valid) throw new InvalidDataException("Invalid controller setting: " + key);
            }
        }
        private static GamepadProfile Find(string name) => _library.Profiles.FirstOrDefault(p => p.Name == name)
            ?? throw new InvalidDataException("Choose a saved controller profile.");
        public static void Save(string name)
        {
            Initialize(); Store(Capture(name.Trim())); ActiveName = name.Trim();
            GamepadRuntimeConfig.Current.ProfileName = ActiveName; Revision++;
        }
        private static void Store(GamepadProfile profile)
        {
            Validate(profile);
            var profiles = _library.Profiles.Where(p => p.Name != profile.Name).ToList();
            if (profiles.Count >= 32) throw new InvalidDataException("The library holds up to 32 profiles.");
            profiles.Add(profile); Commit(new(profiles, new(_library.Assignments)));
        }
        public static void Load(string name) { Apply(Find(name)); }
        public static void Export(string name, string path)
            => WriteAtomic(path, JsonSerializer.Serialize(Find(name), new JsonSerializerOptions { WriteIndented = true }));
        public static string Import(string path)
        {
            Initialize();
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Profile file is too large.");
            var profile = JsonSerializer.Deserialize<GamepadProfile>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Invalid controller profile.");
            Validate(profile);
            // Import never overwrites a named profile or applies settings implicitly.
            string name = profile.Name; int suffix = 2;
            while (_library.Profiles.Any(p => p.Name == name)) name = profile.Name[..Math.Min(40, profile.Name.Length)] + " " + suffix++;
            Store(profile with { Name = name }); Revision++; return name;
        }
        public static void Assign(string name, GamepadDeviceSnapshot device)
        {
            Find(name);
            var assignments = new Dictionary<string, string>(_library.Assignments) { [device.ProfileKey] = name };
            if (assignments.Count > 128) throw new InvalidDataException("Too many controller assignments.");
            Commit(new(new(_library.Profiles), assignments)); GamepadManager.RefreshProfiles();
        }
        public static void Unassign(GamepadDeviceSnapshot device)
        {
            var assignments = new Dictionary<string, string>(_library.Assignments); assignments.Remove(device.ProfileKey);
            Commit(new(new(_library.Profiles), assignments)); GamepadManager.RefreshProfiles();
        }
        public static string DeviceKey(string id)
        {
            // GLFW has a model/firmware GUID, not a serial number. Android supplies a descriptor.
            int last = id.LastIndexOf(':'); int before = last > 0 ? id.LastIndexOf(':', last - 1) : -1;
            string stable = before > 0 ? id[..before] : id;
            return (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsAndroid() ? "android" : "linux") + ":" + stable;
        }
        internal static GamepadRuntimeConfig Resolve(string key)
        {
            if (_library.Assignments.TryGetValue(key, out var name)
                && _library.Profiles.FirstOrDefault(p => p.Name == name) is { } profile)
                return BuildRuntime(profile);
            return GamepadRuntimeConfig.Fallback.Clone();
        }
        private static void Commit(GamepadProfileLibrary library)
        {
            Directory.CreateDirectory(_directory);
            WriteAtomic(LibraryPath, JsonSerializer.Serialize(library)); _library = library;
        }
        internal static void WriteAtomic(string path, string text)
        {
            path = Path.GetFullPath(path);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temp, text); File.Move(temp, path, overwrite: true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
