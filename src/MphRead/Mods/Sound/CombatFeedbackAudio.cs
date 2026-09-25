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
        Overkill,
        Killtacular,
        Killtrocity,
        Kilimanjaro,
        Killtastrophe,
        Killpocalypse,
        Killionaire,
        KillingSpree,
        KillingFrenzy,
        RunningRiot,
        Rampage,
        Untouchable,
        Invincible
    }

    internal readonly record struct CombatFeedbackOption(string Id, string Label);

    internal readonly record struct CombatFeedbackAwards(
        CombatFeedbackCue? MultiKill,
        CombatFeedbackCue? LifeStreak);

    /// <summary>
    /// Local-only, authoritative combat confirmation audio.
    ///
    /// Gameplay decides that a hit/kill is confirmed before calling here. This
    /// class owns only presentation: embedded defaults, optional user files,
    /// rapid multi-kill timing, and life-streak milestone selection.
    /// </summary>
    internal static class CombatFeedbackAudio
    {
        public const long MultiKillWindowMs = 4000;
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
                // Retain legacy IDs so old launcher.txt selections still play.
                ["quadra"] = "quadra-kill.wav",
                ["penta"] = "penta-kill.wav",
                ["overkill"] = "overkill.wav",
                ["killtacular"] = "killtacular.wav",
                ["killtrocity"] = "killtrocity.wav",
                ["kilimanjaro"] = "kilimanjaro.wav",
                ["killtastrophe"] = "killtastrophe.wav",
                ["killpocalypse"] = "killpocalypse.wav",
                ["killionaire"] = "killionaire.wav",
                ["spree"] = "killing-spree.wav",
                ["frenzy"] = "killing-frenzy.wav",
                ["riot"] = "running-riot.wav",
                ["rampage"] = "rampage.wav",
                ["untouchable"] = "untouchable.wav",
                ["invincible"] = "invincible.wav"
            };

        public static string CustomDirectory =>
            Path.Combine(LauncherPrefs.Directory, "sounds", "combat");

        public static string Label(CombatFeedbackCue cue) => cue switch
        {
            CombatFeedbackCue.ImperialistHeadshot => "Imperialist Headshot",
            CombatFeedbackCue.DoubleKill => "Double Kill",
            CombatFeedbackCue.TripleKill => "Triple Kill",
            CombatFeedbackCue.Overkill => "Overkill",
            CombatFeedbackCue.Killtacular => "Killtacular",
            CombatFeedbackCue.Killtrocity => "Killtrocity",
            CombatFeedbackCue.Kilimanjaro => "Kilimanjaro",
            CombatFeedbackCue.Killtastrophe => "Killtastrophe",
            CombatFeedbackCue.Killpocalypse => "Killpocalypse",
            CombatFeedbackCue.Killionaire => "Killionaire",
            CombatFeedbackCue.KillingSpree => "Killing Spree",
            CombatFeedbackCue.KillingFrenzy => "Killing Frenzy",
            CombatFeedbackCue.RunningRiot => "Running Riot",
            CombatFeedbackCue.Rampage => "Rampage",
            CombatFeedbackCue.Untouchable => "Untouchable",
            CombatFeedbackCue.Invincible => "Invincible",
            _ => cue.ToString()
        };

        public static string DefaultSelection(CombatFeedbackCue cue) => cue switch
        {
            CombatFeedbackCue.ImperialistHeadshot => "prime",
            CombatFeedbackCue.DoubleKill => "double",
            CombatFeedbackCue.TripleKill => "triple",
            CombatFeedbackCue.Overkill => "overkill",
            CombatFeedbackCue.Killtacular => "killtacular",
            CombatFeedbackCue.Killtrocity => "killtrocity",
            CombatFeedbackCue.Kilimanjaro => "kilimanjaro",
            CombatFeedbackCue.Killtastrophe => "killtastrophe",
            CombatFeedbackCue.Killpocalypse => "killpocalypse",
            CombatFeedbackCue.Killionaire => "killionaire",
            CombatFeedbackCue.KillingSpree => "spree",
            CombatFeedbackCue.KillingFrenzy => "frenzy",
            CombatFeedbackCue.RunningRiot => "riot",
            CombatFeedbackCue.Rampage => "rampage",
            CombatFeedbackCue.Untouchable => "untouchable",
            CombatFeedbackCue.Invincible => "invincible",
            _ => "off"
        };

        public static IReadOnlyList<CombatFeedbackOption> GetOptions(CombatFeedbackCue cue)
        {
            var options = new List<CombatFeedbackOption>
            {
                new("off", "Off")
            };
            if (cue == CombatFeedbackCue.ImperialistHeadshot)
            {
                options.Add(new("prime", "Prime"));
                options.Add(new("impact", "Impact"));
                options.Add(new("arena", "Arena"));
            }
            else
            {
                options.Add(new(DefaultSelection(cue), "Built-in"));
            }

            foreach (string file in CustomFiles())
            {
                string name = Path.GetFileName(file);
                options.Add(new(CustomPrefix + name,
                    "Custom: " + Path.GetFileNameWithoutExtension(name)));
            }
            return options;
        }

        public static string GetSelection(CombatFeedbackCue cue) => cue switch
        {
            CombatFeedbackCue.ImperialistHeadshot => LauncherPrefs.ImperialistHeadshotSound,
            CombatFeedbackCue.DoubleKill => LauncherPrefs.DoubleKillSound,
            CombatFeedbackCue.TripleKill => LauncherPrefs.TripleKillSound,
            CombatFeedbackCue.Overkill => LauncherPrefs.OverkillSound,
            CombatFeedbackCue.Killtacular => LauncherPrefs.KilltacularSound,
            CombatFeedbackCue.Killtrocity => LauncherPrefs.KilltrocitySound,
            CombatFeedbackCue.Kilimanjaro => LauncherPrefs.KilimanjaroSound,
            CombatFeedbackCue.Killtastrophe => LauncherPrefs.KilltastropheSound,
            CombatFeedbackCue.Killpocalypse => LauncherPrefs.KillpocalypseSound,
            CombatFeedbackCue.Killionaire => LauncherPrefs.KillionaireSound,
            CombatFeedbackCue.KillingSpree => LauncherPrefs.KillingSpreeSound,
            CombatFeedbackCue.KillingFrenzy => LauncherPrefs.KillingFrenzySound,
            CombatFeedbackCue.RunningRiot => LauncherPrefs.RunningRiotSound,
            CombatFeedbackCue.Rampage => LauncherPrefs.RampageSound,
            CombatFeedbackCue.Untouchable => LauncherPrefs.UntouchableSound,
            CombatFeedbackCue.Invincible => LauncherPrefs.InvincibleSound,
            _ => "off"
        };

        public static void SetSelection(CombatFeedbackCue cue, string selection)
        {
            switch (cue)
            {
            case CombatFeedbackCue.ImperialistHeadshot:
                LauncherPrefs.ImperialistHeadshotSound = selection;
                break;
            case CombatFeedbackCue.DoubleKill:
                LauncherPrefs.DoubleKillSound = selection;
                break;
            case CombatFeedbackCue.TripleKill:
                LauncherPrefs.TripleKillSound = selection;
                break;
            case CombatFeedbackCue.Overkill:
                LauncherPrefs.OverkillSound = selection;
                break;
            case CombatFeedbackCue.Killtacular:
                LauncherPrefs.KilltacularSound = selection;
                break;
            case CombatFeedbackCue.Killtrocity:
                LauncherPrefs.KilltrocitySound = selection;
                break;
            case CombatFeedbackCue.Kilimanjaro:
                LauncherPrefs.KilimanjaroSound = selection;
                break;
            case CombatFeedbackCue.Killtastrophe:
                LauncherPrefs.KilltastropheSound = selection;
                break;
            case CombatFeedbackCue.Killpocalypse:
                LauncherPrefs.KillpocalypseSound = selection;
                break;
            case CombatFeedbackCue.Killionaire:
                LauncherPrefs.KillionaireSound = selection;
                break;
            case CombatFeedbackCue.KillingSpree:
                LauncherPrefs.KillingSpreeSound = selection;
                break;
            case CombatFeedbackCue.KillingFrenzy:
                LauncherPrefs.KillingFrenzySound = selection;
                break;
            case CombatFeedbackCue.RunningRiot:
                LauncherPrefs.RunningRiotSound = selection;
                break;
            case CombatFeedbackCue.Rampage:
                LauncherPrefs.RampageSound = selection;
                break;
            case CombatFeedbackCue.Untouchable:
                LauncherPrefs.UntouchableSound = selection;
                break;
            case CombatFeedbackCue.Invincible:
                LauncherPrefs.InvincibleSound = selection;
                break;
            }
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
                _ = TryGetPlayer(GetSelection(cue));
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

        public static CombatFeedbackAwards OnConfirmedKill(Scene scene, int lifeStreak)
        {
            if (!CanPresent(scene))
            {
                return default;
            }

            CombatFeedbackCue? multiKill;
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
                multiKill = MultiKillCue(_multiKillCount);
            }

            CombatFeedbackCue? life = LifeStreakCue(lifeStreak);
            var awards = new CombatFeedbackAwards(multiKill, life);

            if (multiKill.HasValue)
            {
                // Replace the headshot ping from the same shot.
                Play(multiKill.Value, replaceExisting: true);
            }
            if (life.HasValue)
            {
                // A kill can legitimately earn both ladders. Layer the second
                // stinger instead of silently dropping one of the awards.
                Play(life.Value, replaceExisting: !multiKill.HasValue);
            }
            return awards;
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

        private static CombatFeedbackCue? MultiKillCue(int kills) => kills switch
        {
            2 => CombatFeedbackCue.DoubleKill,
            3 => CombatFeedbackCue.TripleKill,
            4 => CombatFeedbackCue.Overkill,
            5 => CombatFeedbackCue.Killtacular,
            6 => CombatFeedbackCue.Killtrocity,
            7 => CombatFeedbackCue.Kilimanjaro,
            8 => CombatFeedbackCue.Killtastrophe,
            9 => CombatFeedbackCue.Killpocalypse,
            10 => CombatFeedbackCue.Killionaire,
            _ => null
        };

        private static CombatFeedbackCue? LifeStreakCue(int kills) => kills switch
        {
            5 => CombatFeedbackCue.KillingSpree,
            10 => CombatFeedbackCue.KillingFrenzy,
            15 => CombatFeedbackCue.RunningRiot,
            20 => CombatFeedbackCue.Rampage,
            25 => CombatFeedbackCue.Untouchable,
            30 => CombatFeedbackCue.Invincible,
            _ => null
        };

        private static bool CanPresent(Scene scene)
        {
            return !MphRead.Mods.Headless.Active
                && !MphRead.Mods.ThumbnailMode.Active
                && !scene.Services.IsReplica
                && scene.Services.AllowsPresentationSideEffects;
        }

        private static void Play(CombatFeedbackCue cue, bool replaceExisting = true)
        {
            string id = GetSelection(cue);
            if (id.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CuePlayer? player = TryGetPlayer(id);
            if (player == null && id.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
            {
                player = TryGetPlayer(DefaultSelection(cue));
            }
            if (player == null)
            {
                return;
            }

            lock (_gate)
            {
                if (replaceExisting)
                {
                    foreach (CuePlayer cached in _players.Values)
                    {
                        cached.Stop();
                    }
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
