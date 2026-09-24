using System;
using System.IO;
using System.Text.Json;
using MphRead.Entities;

namespace MphRead.Mods.Input.AimAssist
{
    internal static class AimAssistTelemetry
    {
        internal sealed class Bucket
        {
            public string Input { get; set; } = "";
            public string Weapon { get; set; } = "";
            public string Distance { get; set; } = "";
            public int HeadRegionSamples { get; set; }
            public int HeadRegionEntries { get; set; }
            public int HeadRegionExits { get; set; }
            public int HeadshotAttempts { get; set; }
            public int ConfirmedHeadshots { get; set; }
            public int FlickAttempts { get; set; }
            public int FlickCaptures { get; set; }
            public int FlickHeadshots { get; set; }
            public int TargetSwitchesWhileFiring { get; set; }
            public int OpposingInputBreaks { get; set; }
            public double RetainedStrafeTrackingSeconds { get; set; }
            public double HeadHorizontalErrorSum { get; set; }
            public double HeadVerticalErrorSum { get; set; }
            public double BodyRegionErrorSum { get; set; }
            public double TrackingCorrectionSum { get; set; }
            public double PositionCorrectionSum { get; set; }
            public double MeanHeadHorizontalError => HeadRegionSamples == 0 ? 0 : HeadHorizontalErrorSum / HeadRegionSamples;
            public double MeanHeadVerticalError => HeadRegionSamples == 0 ? 0 : HeadVerticalErrorSum / HeadRegionSamples;
            public double MeanBodyRegionError => TargetSamples == 0 ? 0 : BodyRegionErrorSum / TargetSamples;
            public double MeanTrackingCorrection => Samples == 0 ? 0 : TrackingCorrectionSum / Samples;
            public double MeanPositionCorrection => Samples == 0 ? 0 : PositionCorrectionSum / Samples;
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
            public double InputAlignmentSum { get; set; }
            public double HeadPredictionSum { get; set; }
            public double HeadAcquireSeconds { get; set; }
            public int Switches { get; set; }
            public int InputAlignedSwitches { get; set; }
            public int HeadAcquisitions { get; set; }
            public int OccludedSamples { get; set; }
            public int SaturatedSamples { get; set; }
            public double MeanError => TargetSamples == 0 ? 0 : ErrorSum / TargetSamples;
            public double MeanFriction => Samples == 0 ? 1 : FrictionSum / Samples;
            public double MeanInputAlignment => TargetSamples == 0 ? 0 : InputAlignmentSum / TargetSamples;
            public double MeanHeadPrediction => TargetSamples == 0 ? 0 : HeadPredictionSum / TargetSamples;
            public double MeanHeadAcquireSeconds => HeadAcquisitions == 0 ? 0 : HeadAcquireSeconds / HeadAcquisitions;
        }

        private static readonly Bucket?[,,] Buckets = new Bucket[3, 16, 4];
        private static string? _path;
        public static bool Enabled => _path != null;
        private static int _lastTarget = -1, _lastHeadTarget = -1;
        private static Bucket? _current;
        private static readonly Bucket?[] LastShot = new Bucket[16];

        private static bool _insideHead, _nearHead, _flick, _capture;
        private static readonly bool[] LastFlickShot = new bool[16];

        public static void Shot(BeamType weapon)
        {
            if (_path != null && _current != null)
            {
                _current.Shots++;
                if (_nearHead && AimInputSourceTracker.Current == AimInputSource.Gamepad) _current.HeadshotAttempts++;
                LastFlickShot[Math.Clamp((int)weapon, 0, 15)] = _capture;
                LastShot[Math.Clamp((int)weapon, 0, 15)] = _current;
            }
        }

        public static void Hit(PlayerEntity? attacker, BeamType weapon, uint damage, DamageFlags flags)
        {
            if (_path != null && attacker?.IsMainPlayer == true && !attacker.IsBot && !SpectatorMode.IsSpectating
                && damage > 0 && (!Network.NetSession.Active || Network.NetSession.IsAuthority)
                && LastShot[Math.Clamp((int)weapon, 0, 15)] is { } bucket)
            {
                bucket.HitEvents++;
                if ((flags & DamageFlags.Headshot) != 0)
                {
                    bucket.ConfirmedHeadshots++;
                    if (LastFlickShot[Math.Clamp((int)weapon, 0, 15)]) bucket.FlickHeadshots++;
                }
                bucket.ObservedDamage += damage;
            }
        }

        public static void Configure(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            _path = Path.GetFullPath(path);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Save();
        }

        public static void Record(BeamType weapon, AimAssistTarget target, AimAssistResult result,
            float correction, float velocity)
        {
            if (_path == null) return;
            int input = AimInputSourceTracker.Current == AimInputSource.Gamepad
                ? AimAssistDebug.UnassistedArm ? 1 : 2 : 0;
            int beam = Math.Clamp((int)weapon, 0, 15);
            int range = result.TargetSlot < 0 ? 3 : target.Distance < 5 ? 0 : target.Distance < 25 ? 1 : 2;
            var bucket = Buckets[input, beam, range] ??= new()
            {
                Input = input == 0 ? "mouse-or-touch" : input == 1 ? "controller-baseline" : "controller-assisted",
                Weapon = weapon.ToString(),
                Distance = new[] { "close", "mid", "far", "no-target" }[range]
            };
            _current = bucket;
            bucket.Samples++;
            bool validHead = result.TargetSlot >= 0 && target.HeadVisible
                && AimAssistMath.CanHeadshotAtDistance(weapon, target.Distance);
            var headError = AimAssistMath.HeadError(target);
            bool inside = validHead && headError.LengthSquared() == 0;
            _nearHead = validHead && headError.Length() <= .8f;
            if (validHead)
            {
                bucket.HeadRegionSamples++;
                bucket.HeadHorizontalErrorSum += Math.Abs(headError.X);
                bucket.HeadVerticalErrorSum += Math.Abs(headError.Y);
            }
            if (inside && (!_insideHead || result.TargetSlot != _lastTarget)) bucket.HeadRegionEntries++;
            if (_insideHead && (!inside || result.TargetSlot != _lastTarget)) bucket.HeadRegionExits++;
            _insideHead = inside;
            bool capturing = result.TrackingState == AimAssistTrackingState.FlickCapturingHead;
            if (result.FlickActive && !_flick) bucket.FlickAttempts++;
            if (capturing && !_capture) bucket.FlickCaptures++;
            _flick = result.FlickActive; _capture = capturing;
            if (result.OpposingBreak) bucket.OpposingInputBreaks++;
            if (result.StrafeTracking) bucket.RetainedStrafeTrackingSeconds += 1d / 60;
            if (result.Firing && _lastTarget >= 0 && result.TargetSlot >= 0 && result.TargetSlot != _lastTarget)
                bucket.TargetSwitchesWhileFiring++;
            bucket.TrackingCorrectionSum += result.TrackingCorrection.Length();
            bucket.PositionCorrectionSum += result.PositionCorrection.Length();
            bucket.FrictionSum += result.Friction;
            bucket.CorrectionSum += correction;
            if (result.Occluded) bucket.OccludedSamples++;
            if (result.Saturated) bucket.SaturatedSamples++;

            if (result.TargetSlot >= 0)
            {
                bucket.TargetSamples++;
                bucket.BodyRegionErrorSum += AimAssistMath.BodyError(target).Length();
                bucket.SecondsOnTarget += 1d / 60;
                bucket.ErrorSum += target.BodyError.Length();
                bucket.VelocitySum += velocity;
                bucket.InputAlignmentSum += result.InputAlignment;
                bucket.HeadPredictionSum += result.HeadPrediction;
                if (result.TargetSlot != _lastTarget)
                {
                    bucket.Switches++;
                    if (result.InputAlignment > .45f) bucket.InputAlignedSwitches++;
                }
            }

            if (result.RotationStrength > 0) bucket.AssistSeconds += 1d / 60;
            if (result.HeadBlend > 0)
            {
                bucket.HeadSeconds += 1d / 60;
                if (_lastHeadTarget != result.TargetSlot)
                {
                    bucket.HeadAcquisitions++;
                    bucket.HeadAcquireSeconds += result.TargetAge;
                }
                _lastHeadTarget = result.TargetSlot;
            }
            else
            {
                _lastHeadTarget = -1;
            }
            _lastTarget = result.TargetSlot;
        }

        private static void Save()
        {
            try
            {
                var output = new System.Collections.Generic.List<Bucket>();
                foreach (var bucket in Buckets)
                    if (bucket != null) output.Add(bucket);
                File.WriteAllText(_path!,
                    JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Aim diagnostics could not be saved: " + ex.Message);
            }
        }
    }
}
