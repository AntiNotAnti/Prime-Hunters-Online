using System;
using System.Buffers.Binary;

namespace MphRead.Mods.Network;

public enum LagCompPlausibility { Off, Shadow, Enforce }
public enum ShadowOutcome
{
    SameOutcome, ExistingHit_ShadowMiss, ExistingHead_ShadowBody,
    ExistingBody_ShadowHead, ExistingMiss_ShadowHit, DifferentVictim, HistoricalDataUnavailable
}
public readonly record struct LagTiming(double? RttMilliseconds, double? JitterMilliseconds,
    double? MinimumRecentRttMilliseconds, double? PresentationDelayFrames);
public readonly record struct LagCompensationDecision(double RequestedFrame, double RequestedDepth,
    double GlobalServedDepth, double? PlausibleDepth, bool WouldClamp, double EffectiveFrameShadow, LagDecision Timing);
public readonly record struct LagDecision(double RequestedFrames, double HardAppliedFrames,
    double? ShadowAllowedFrames, bool WouldClamp, double FramesShadowRefused);
public readonly record struct LagShadowSnapshot(long Shots, long WouldClamp, double RequestedFrames,
    double HardAppliedFrames, double ShadowRefusedFrames, double WorstRequestedFrames, long OutcomeCount, long TimedShots, double ShadowAllowedFrames);

/// <summary>Connection plausibility is diagnostic by default. ACK time remains
/// the source of shot time. No defender state participates in this policy.</summary>
public static class LagCompensationPolicy
{
    public static LagCompPlausibility Plausibility { get; private set; } = LagCompPlausibility.Shadow;
    public static bool Configure(string? value)
    {
        if (!Enum.TryParse(value, true, out LagCompPlausibility mode) || !Enum.IsDefined(mode)) return false;
        if (mode == LagCompPlausibility.Enforce) return false;
        Plausibility = mode; return true;
    }
    public static LagDecision Evaluate(double requested, in LagTiming timing, int pressAge,
        double schedulerAllowance = 2, int ceiling = NetUnlagged.DefaultMaxRewindFrames)
    {
        requested = double.IsFinite(requested) ? Math.Max(0, requested) : 0;
        double hard = Math.Min(requested, Math.Clamp(ceiling, 0, NetUnlagged.MaxRewindCeiling));
        if (timing.RttMilliseconds is not double rtt || !double.IsFinite(rtt) || rtt < 0
            || timing.JitterMilliseconds is not double jitter || !double.IsFinite(jitter) || jitter < 0
            || timing.PresentationDelayFrames is not double delay || !double.IsFinite(delay) || delay < 0 || delay > 8)
            return new(requested, hard, null, false, 0);
        // A displayed server frame travels downstream, then its ACK travels
        // upstream. This is a full RTT, plus presentation age, NOT half an RTT.
        // Recent minimum + bounded variance keeps an isolated late ACK/pong
        // from expanding the bound by an arbitrary amount.
        double baseline = timing.MinimumRecentRttMilliseconds is double minimum && double.IsFinite(minimum) && minimum >= 0
            ? Math.Min(rtt, minimum + Math.Min(jitter * 2, 100)) : rtt;
        double allowed = Math.Min(ceiling, (baseline + Math.Min(jitter * 2, 100)) * .06
            + delay + Math.Clamp(pressAge, 0, IntentPacket.PressHistory - 1) + Math.Clamp(schedulerAllowance, 0, 4));
        double refused = Math.Max(0, hard - allowed);
        return new(requested, hard, allowed, refused > 0, refused);
    }
    public static LagCompensationDecision Evaluate(int shooterSlot, uint now, uint ackFrame, byte ackSubFrame, int recoveredPressAge)
    {
        double requested = ackFrame == 0 || ackFrame >= now ? 0
            : Math.Max(0, now - ackFrame - ackSubFrame / 256.0 + Math.Clamp(recoveredPressAge, 0, IntentPacket.PressHistory - 1));
        var decision = Evaluate(requested, Timing(shooterSlot), recoveredPressAge, ceiling: NetUnlagged.MaxRewindFrames);
        return new(now - requested, requested, decision.HardAppliedFrames, decision.ShadowAllowedFrames,
            decision.WouldClamp, now - Math.Min(decision.HardAppliedFrames, decision.ShadowAllowedFrames ?? decision.HardAppliedFrames), decision);
    }

    public static void Study(int shooter, int victim, int weapon, in LagCompensationDecision decision, int outcome, bool timingSample = false)
    {
        var timing = Timing(shooter);
        double horizontal = -1, vertical = -1, total = -1;
        if ((uint)victim < 8 && MphRead.Entities.PlayerEntity._players[victim] is { } player
            && NetUnlagged.TryHistoricalPose(player, decision.RequestedFrame, out var oldPose)
            && NetUnlagged.TryHistoricalPose(player, decision.EffectiveFrameShadow, out var proposed))
        {
            var delta = oldPose.Position - proposed.Position;
            horizontal = Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z); vertical = Math.Abs(delta.Y); total = delta.Length;
        }
        Telemetry.ProductionTelemetry.Emit(new(Telemetry.TelemetryEventType.LagStudy, NetSession.NetFrame,
            Player: (byte)shooter, Victim: (byte)victim, Weapon: (byte)weapon, Result: outcome, Flags: (decision.WouldClamp ? 1 : 0) | (timingSample ? 0 : 2),
            A: decision.RequestedDepth, B: decision.GlobalServedDepth, C: decision.PlausibleDepth ?? -1,
            D: total, E: timing.RttMilliseconds ?? -1, F: timing.JitterMilliseconds ?? -1, G: horizontal, H: vertical));
    }

    private readonly record struct ShotStudy(ShotKey Key, LagCompensationDecision Decision, int Weapon);
    private static readonly ShotStudy[,] _shotStudies = new ShotStudy[8, 256];
    private static readonly int[] _studyHead = new int[8];
    public static void RecordShotContext(int slot, uint launch, in LagCompensationDecision decision, int weapon)
    {
        if ((uint)slot >= 8 || !Telemetry.ProductionTelemetry.Enabled) return;
        _shotStudies[slot, _studyHead[slot]] = new(ShotKey.For(slot, launch), decision, weapon);
        _studyHead[slot] = (_studyHead[slot] + 1) & 255;
        Study(slot, 255, weapon, decision, -1, timingSample: true);
    }
    public static void RecordImpact(int slot, int victim, uint launch)
    {
        if ((uint)slot >= 8 || launch == 0 || !Telemetry.ProductionTelemetry.Enabled) return;
        var key = ShotKey.For(slot, launch);
        for (int n = 1; n <= 256; n++)
        {
            var sample = _shotStudies[slot, (_studyHead[slot] - n + 256) & 255];
            if (sample.Key != key) continue;
            Study(slot, victim, sample.Weapon, sample.Decision, 1); return;
        }
    }

    public static double Applied(in LagDecision decision) => Plausibility == LagCompPlausibility.Enforce
        && decision.ShadowAllowedFrames.HasValue ? Math.Min(decision.HardAppliedFrames, decision.ShadowAllowedFrames.Value)
        : decision.HardAppliedFrames;

    // Server-thread-owned, bounded, match-scoped. Connection observations are
    // refreshed on accepted intent; captures never mutate gameplay or counters.
    private static readonly LagTiming[] _timing = new LagTiming[8];
    private struct Cell { public long Shots, Clamps, Timed; public double Requested, Hard, Refused, Worst, Allowed; }
    private const int Weapons = NetShotDiagnostics.WeaponCount, Rtts = 9, Jitters = 6, Delays = 4, Outcomes = 7;
    private static readonly Cell[] _cells = new Cell[8 * Weapons * Rtts * Jitters * Delays];
    private static readonly long[] _outcomes = new long[_cells.Length * Outcomes];
    public static void SetTiming(int slot, in LagTiming timing) { if ((uint)slot < 8) _timing[slot] = timing; }
    public static LagTiming Timing(int slot) => (uint)slot < 8 ? _timing[slot] : default;
    public static void Reset() { Array.Clear(_timing); Array.Clear(_cells); Array.Clear(_outcomes); Array.Clear(_shotStudies); Array.Clear(_studyHead); }
    public static int RttBucket(double? rtt) => rtt is null ? 8 : rtt < 50 ? 0 : rtt < 100 ? 1 : rtt < 150 ? 2 : rtt < 200 ? 3 : rtt < 250 ? 4 : rtt < 300 ? 5 : rtt < 400 ? 6 : 7;
    public static int JitterBucket(double? jitter) => jitter is null ? 5 : jitter < 10 ? 0 : jitter < 25 ? 1 : jitter < 50 ? 2 : jitter < 80 ? 3 : 4;
    public static int DelayBucket(double? delay) => delay is null ? 3 : delay < 3 ? 0 : delay < 6 ? 1 : 2;
    private static int Index(int slot, int weapon, int rtt, int jitter, int delay)
    {
        if ((uint)slot >= 8 || (uint)weapon >= Weapons || (uint)rtt >= Rtts || (uint)jitter >= Jitters || (uint)delay >= Delays)
            throw new ArgumentOutOfRangeException(nameof(slot));
        return ((((slot * Weapons) + weapon) * Rtts + rtt) * Jitters + jitter) * Delays + delay;
    }
    public static void Record(int slot, int weapon, in LagDecision decision, ShadowOutcome outcome)
    {
        if (Plausibility == LagCompPlausibility.Off) return;
        var timing = Timing(slot);
        int at = Index(slot, weapon, RttBucket(timing.RttMilliseconds), JitterBucket(timing.JitterMilliseconds), DelayBucket(timing.PresentationDelayFrames));
        ref Cell cell = ref _cells[at]; cell.Shots++; if (decision.WouldClamp) cell.Clamps++;
        cell.Requested += decision.RequestedFrames; cell.Hard += decision.HardAppliedFrames;
        if (decision.ShadowAllowedFrames.HasValue) { cell.Timed++; cell.Allowed += decision.ShadowAllowedFrames.Value; }
        cell.Refused += decision.FramesShadowRefused; cell.Worst = Math.Max(cell.Worst, decision.RequestedFrames);
        _outcomes[at * Outcomes + (int)outcome]++;
    }
    public static LagShadowSnapshot CaptureTotal(ShadowOutcome outcome = ShadowOutcome.HistoricalDataUnavailable)
    {
        long shots = 0, clamps = 0, outcomes = 0, timed = 0; double requested = 0, hard = 0, refused = 0, worst = 0, allowed = 0;
        for (int i = 0; i < _cells.Length; i++)
        {
            ref Cell c = ref _cells[i]; shots += c.Shots; clamps += c.Clamps;
            requested += c.Requested; hard += c.Hard; refused += c.Refused; worst = Math.Max(worst, c.Worst);
            outcomes += _outcomes[i * Outcomes + (int)outcome]; timed += c.Timed; allowed += c.Allowed;
        }
        return new(shots, clamps, requested, hard, refused, worst, outcomes, timed, allowed);
    }
    public static LagShadowSnapshot Capture(int slot, int weapon, int rtt, int jitter, int delay, ShadowOutcome outcome)
    {
        int at = Index(slot, weapon, rtt, jitter, delay); ref Cell c = ref _cells[at];
        return new(c.Shots, c.Clamps, c.Requested, c.Hard, c.Refused, c.Worst, _outcomes[at * Outcomes + (int)outcome], c.Timed, c.Allowed);
    }
}

/// <summary>Unreliable diagnostic report. The server authorizes only the current
/// match/epoch and the actual smoothing range. It never changes shot authority.</summary>
public readonly record struct PeerTimingPacket(ushort MatchId, ulong AuthorityEpoch, float DelayFrames)
{
    public const int Size = 14;
    public void Write(Span<byte> bytes)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, MatchId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[2..], AuthorityEpoch);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[10..], DelayFrames);
    }
    public static bool TryRead(ReadOnlySpan<byte> bytes, out PeerTimingPacket packet)
    {
        packet = default;
        if (bytes.Length != Size) return false;
        float delay = BinaryPrimitives.ReadSingleLittleEndian(bytes[10..]);
        if (!float.IsFinite(delay) || delay < 0 || delay > 8) return false;
        packet = new(BinaryPrimitives.ReadUInt16LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes[2..]), delay);
        return true;
    }
}
