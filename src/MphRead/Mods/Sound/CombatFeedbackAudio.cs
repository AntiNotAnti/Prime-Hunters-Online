using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MphRead.Mods.Launcher;
using MphRead.Sound;
using SoundFlow.Components;
using SoundFlow.Providers;

namespace MphRead.Mods.Sound
{
    internal enum CombatFeedbackCue
    {
        ImperialistHeadshot,
        DoubleKill,
        TripleKill,
        QuadraKill,
        KillingSpree
    }

    internal readonly record struct CombatFeedbackOption(string Id, string Label);

    /// <summary>
    /// Local-only, authoritative combat confirmation audio.
    ///
    /// Gameplay decides that a hit/kill is confirmed before calling here. This
    /// class owns only presentation: a single feedback lane, embedded defaults,
    /// optional user files, and rapid multi-kill timing.
    /// </summary>
    internal static class CombatFeedbackAudio
    {
        private const long MultiKillWindowMs = 4000;
        private const long MaxCustomBytes = 8 * 1024 * 1024;
        private const string CustomPrefix = "file:";

        private static readonly object _gate = new();
        private static readonly Dictionary<string, CuePlayer> _players =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);

        private static Scene? _killScene;
        private static long _lastKillMs = -1;
        private static int _multiKillCount;

        private static readonly IReadOnlyDictionary<string, string> _resources =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["prime"] = "headshot-prime.wav",
                ["impact"] = "headshot-impact.wav",
                ["arena"] = "headshot-arena.wav",
                ["double"] = "double-kill.wav",
                ["triple"] = "triple-kill.wav",
                ["quadra"] = "quadra-kill.wav",
                ["spree"] = "killing-spree.wav"
            };

        public static string CustomDirectory =>
            Path.Combine(LauncherPrefs.Directory, "sounds", "combat");

        public static string DefaultSelection(CombatFeedbackCue cue) => cue switch
        {
            CombatFeedbackCue.ImperialistHeadshot => "prime",
            CombatFeedbackCue.DoubleKill => "double",
            CombatFeedbackCue.TripleKill => "triple",
            CombatFeedbackCue.QuadraKill => "quadra",
            CombatFeedbackCue.KillingSpree => "spree",
            _ => "off"
        };

        public static IReadOnlyList<CombatFeedbackOption> GetOptions(CombatFeedbackCue cue)
        {
            var options = new List<CombatFeedbackOption>
            {
                new("off", "Off")
            };
            switch (cue)
            {
            case CombatFeedbackCue.ImperialistHeadshot:
                options.Add(new("prime", "Prime"));
                options.Add(new("impact", "Impact"));
                options.Add(new("arena", "Arena"));
                break;
            case CombatFeedbackCue.DoubleKill:
                options.Add(new("double", "Built-in"));
                break;
            case CombatFeedbackCue.TripleKill:
                options.Add(new("triple", "Built-in"));
                break;
            case CombatFeedbackCue.QuadraKill:
                options.Add(new("quadra", "Built-in"));
                break;
            case CombatFeedbackCue.KillingSpree:
                options.Add(new("spree", "Built-in"));
                break;
            }

            foreach (string file in CustomFiles())
            {
                string name = Path.GetFileName(file);
                options.Add(new(CustomPrefix + name,
                    "Custom: " + Path.GetFileNameWithoutExtension(name)));
            }
            return options;
        }

        public static void Warm()
        {
            if (MphRead.Mods.Headless.Active || MphRead.Mods.ThumbnailMode.Active
                || !MusicPlayer.Available)
            {
                return;
            }
            foreach (CombatFeedbackCue cue in Enum.GetValues<CombatFeedbackCue>())
            {
                _ = TryGetPlayer(Selection(cue));
            }
        }

        public static void Reload()
        {
            lock (_gate)
            {
                DisposePlayers();
                _failed.Clear();
            }
        }

        public static void Shutdown()
        {
            lock (_gate)
            {
                DisposePlayers();
                _failed.Clear();
                _killScene = null;
                _lastKillMs = -1;
                _multiKillCount = 0;
            }
        }

        public static void OnConfirmedHeadshot(Scene scene, BeamType beam)
        {
            if (beam != BeamType.Imperialist || !CanPresent(scene))
            {
                return;
            }
            Play(CombatFeedbackCue.ImperialistHeadshot);
        }

        public static void OnConfirmedKill(Scene scene, int lifeStreak)
        {
            if (!CanPresent(scene))
            {
                return;
            }

            CombatFeedbackCue? cue = null;
            long now = Environment.TickCount64;
            lock (_gate)
            {
                if (!ReferenceEquals(_killScene, scene)
                    || _lastKillMs < 0
                    || now - _lastKillMs > MultiKillWindowMs)
                {
                    _multiKillCount = 1;
                }
                else
                {
                    _multiKillCount++;
                }
                _killScene = scene;
                _lastKillMs = now;

                if (lifeStreak == 5)
                {
                    cue = CombatFeedbackCue.KillingSpree;
                }
                else
                {
                    cue = _multiKillCount switch
                    {
                        2 => CombatFeedbackCue.DoubleKill,
                        3 => CombatFeedbackCue.TripleKill,
                        4 => CombatFeedbackCue.QuadraKill,
                        _ => null
                    };
                }
            }

            if (cue.HasValue)
            {
                Play(cue.Value);
            }
        }

        public static void OnLocalDeath(Scene scene)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_killScene, scene))
                {
                    _lastKillMs = -1;
                    _multiKillCount = 0;
                }
            }
        }

        private static bool CanPresent(Scene scene)
        {
            return !MphRead.Mods.Headless.Active
                && !MphRead.Mods.ThumbnailMode.Active
                && !scene.Services.IsReplica
                && scene.Services.AllowsPresentationSideEffects;
        }

        private static string Selection(CombatFeedbackCue cue) => cue switch
        {
            CombatFeedbackCue.ImperialistHeadshot => LauncherPrefs.ImperialistHeadshotSound,
            CombatFeedbackCue.DoubleKill => LauncherPrefs.DoubleKillSound,
            CombatFeedbackCue.TripleKill => LauncherPrefs.TripleKillSound,
            CombatFeedbackCue.QuadraKill => LauncherPrefs.QuadraKillSound,
            CombatFeedbackCue.KillingSpree => LauncherPrefs.KillingSpreeSound,
            _ => "off"
        };

        private static void Play(CombatFeedbackCue cue)
        {
            string id = Selection(cue);
            if (id.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CuePlayer? player = TryGetPlayer(id);
            if (player == null)
            {
                return;
            }

            lock (_gate)
            {
                // One presentation lane: a kill callout replaces the headshot
                // ping from the same shot instead of stacking two confirmations.
                foreach (CuePlayer cached in _players.Values)
                {
                    cached.Stop();
                }
                player.Play(Math.Clamp(
                    Sfx.Volume * LauncherPrefs.CombatFeedbackVolume, 0, 1.5f));
            }
        }

        private static CuePlayer? TryGetPlayer(string id)
        {
            if (String.IsNullOrWhiteSpace(id)
                || id.Equals("off", StringComparison.OrdinalIgnoreCase)
                || MphRead.Mods.Headless.Active
                || MphRead.Mods.ThumbnailMode.Active
                || !MusicPlayer.Available)
            {
                return null;
            }

            lock (_gate)
            {
                if (_players.TryGetValue(id, out CuePlayer? existing))
                {
                    return existing;
                }
                if (_failed.Contains(id))
                {
                    return null;
                }

                try
                {
                    if (!TryRead(id, out byte[] bytes))
                    {
                        _failed.Add(id);
                        return null;
                    }
                    var player = new CuePlayer(bytes);
                    _players.Add(id, player);
                    return player;
                }
                catch (Exception ex)
                {
                    _failed.Add(id);
                    Console.WriteLine(
                        $"[sound] Combat feedback '{id}' could not load ({ex.Message})");
                    return null;
                }
            }
        }

        private static bool TryRead(string id, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (_resources.TryGetValue(id, out string? fileName))
            {
                Assembly assembly = typeof(CombatFeedbackAudio).Assembly;
                string? resource = assembly.GetManifestResourceNames().FirstOrDefault(name =>
                    name.EndsWith(".Sounds.Combat." + fileName,
                        StringComparison.OrdinalIgnoreCase));
                if (resource == null)
                {
                    return false;
                }
                using Stream? source = assembly.GetManifestResourceStream(resource);
                if (source == null)
                {
                    return false;
                }
                using var copy = new MemoryStream();
                source.CopyTo(copy);
                bytes = copy.ToArray();
                return bytes.Length > 0;
            }

            if (!id.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string rawName = id[CustomPrefix.Length..];
            string safeName = Path.GetFileName(rawName);
            if (!safeName.Equals(rawName, StringComparison.Ordinal)
                || !AllowedExtension(Path.GetExtension(safeName)))
            {
                return false;
            }
            string path = Path.Combine(CustomDirectory, safeName);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxCustomBytes)
            {
                return false;
            }
            bytes = File.ReadAllBytes(path);
            return true;
        }

        private static IEnumerable<string> CustomFiles()
        {
            try
            {
                Directory.CreateDirectory(CustomDirectory);
                return Directory.EnumerateFiles(CustomDirectory)
                    .Where(path => AllowedExtension(Path.GetExtension(path)))
                    .Where(path =>
                    {
                        string name = Path.GetFileName(path);
                        if (name.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                        {
                            return false;
                        }
                        var info = new FileInfo(path);
                        return info.Length > 0 && info.Length <= MaxCustomBytes;
                    })
                    .OrderBy(path => Path.GetFileName(path),
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static bool AllowedExtension(string extension)
        {
            return extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".flac", StringComparison.OrdinalIgnoreCase);
        }

        private static void DisposePlayers()
        {
            foreach (CuePlayer player in _players.Values)
            {
                player.Dispose();
            }
            _players.Clear();
        }

        private sealed class CuePlayer : IDisposable
        {
            private readonly MemoryStream _stream;
            private readonly StreamDataProvider _provider;
            private readonly SoundPlayer _player;

            public CuePlayer(byte[] data)
            {
                _stream = new MemoryStream(data, writable: false);
                _provider = new StreamDataProvider(
                    MusicPlayer.Engine!, MusicPlayer.Format, _stream);
                _player = new SoundPlayer(
                    MusicPlayer.Engine!, MusicPlayer.Format, _provider);
                MusicPlayer.PlaybackDevice!.MasterMixer.AddComponent(_player);
                MusicPlayer.PlaybackDevice.Start();
            }

            public void Play(float gain)
            {
                _player.Stop();
                _provider.Seek(0);
                _player.Volume = gain;
                _player.Play();
            }

            public void Stop()
            {
                _player.Stop();
            }

            public void Dispose()
            {
                _player.Stop();
                MusicPlayer.PlaybackDevice?.MasterMixer.RemoveComponent(_player);
                _player.Dispose();
                _provider.Dispose();
                _stream.Dispose();
            }
        }
    }
}
