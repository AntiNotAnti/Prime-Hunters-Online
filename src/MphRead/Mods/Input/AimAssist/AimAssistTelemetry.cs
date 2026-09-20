using System;
using System.IO;
using System.Text.Json;
using MphRead.Entities;

namespace MphRead.Mods.Input.AimAssist
{
    // Opt-in aggregate diagnostics, kept in memory until process exit. Never transmitted.
    internal static class AimAssistTelemetry
    {
        internal sealed class Bucket
        {
            public string Input { get; set; } = "";
            public string Weapon { get; set; } = "";
            public string Distance { get; set; } = "";
            public int Samples { get; set; }
            public int TargetSamples { get; set; }
            public int Shots { get; set; }
            public int HitEvents { get; set; }
            public long ObservedDamage { get; set; }
            public double HitEventsPerShot => Shots == 0 ? 0 : HitEvents / (double)Shots;
            public double SecondsOnTarget { get; set; }
            public double AssistSeconds { get; set; }
            public double HeadSeconds { get; set; }
            public double ErrorSum { get; set; }
            public double FrictionSum { get; set; }
            public double CorrectionSum { get; set; }
            public double VelocitySum { get; set; }
            public int Switches { get; set; }
            public double MeanError => TargetSamples == 0 ? 0 : ErrorSum / TargetSamples;
            public double MeanFriction => Samples == 0 ? 1 : FrictionSum / Samples;
        }
        private static readonly Bucket?[,,] Buckets = new Bucket[3, 16, 4];
        private static string? _path;
        public static bool Enabled => _path != null;
        private static int _lastTarget = -1;
        private static Bucket? _current;
        private static readonly Bucket?[] LastShot = new Bucket[16];
        public static void Shot(BeamType weapon)
        {
            if (_path != null && _current != null)
            { _current.Shots++; LastShot[Math.Clamp((int)weapon, 0, 15)] = _current; }
        }
        public static void Hit(PlayerEntity? attacker, BeamType weapon, uint damage)
        {
            if (_path != null && attacker?.IsMainPlayer == true && !attacker.IsBot && !SpectatorMode.IsSpectating
                && damage > 0 && (!Network.NetSession.Active || Network.NetSession.IsAuthority)
                && LastShot[Math.Clamp((int)weapon, 0, 15)] is { } bucket)
            { bucket.HitEvents++; bucket.ObservedDamage += damage; }
        }
        public static void Configure(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            _path = Path.GetFullPath(path);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Save();
        }
        public static void Record(BeamType weapon, AimAssistTarget target, AimAssistResult result, float correction, float velocity)
        {
            if (_path == null) return;
            int input = AimInputSourceTracker.Current == AimInputSource.Gamepad ? AimAssistDebug.UnassistedArm ? 1 : 2 : 0;
            int beam = Math.Clamp((int)weapon, 0, 15), range = result.TargetSlot < 0 ? 3 : target.Distance < 5 ? 0 : target.Distance < 25 ? 1 : 2;
            var bucket = Buckets[input, beam, range] ??= new() { Input = input == 0 ? "mouse-or-touch" : input == 1 ? "controller-baseline" : "controller-assisted",
                Weapon = weapon.ToString(), Distance = new[] { "close", "mid", "far", "no-target" }[range] };
            _current = bucket;
            bucket.Samples++; bucket.FrictionSum += result.Friction; bucket.CorrectionSum += correction;
            if (result.TargetSlot >= 0)
            {
                bucket.TargetSamples++;
                bucket.SecondsOnTarget += 1d / 60; bucket.ErrorSum += target.BodyError.Length(); bucket.VelocitySum += velocity;
                if (result.TargetSlot != _lastTarget) bucket.Switches++;
            }
            if (result.RotationStrength > 0) bucket.AssistSeconds += 1d / 60;
            if (result.HeadBlend > 0) bucket.HeadSeconds += 1d / 60;
            _lastTarget = result.TargetSlot;
        }
        private static void Save()
        {
            try
            {
                var output = new System.Collections.Generic.List<Bucket>();
                foreach (var bucket in Buckets) if (bucket != null) output.Add(bucket);
                File.WriteAllText(_path!, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("Aim diagnostics could not be saved: " + ex.Message); }
        }
    }
}
