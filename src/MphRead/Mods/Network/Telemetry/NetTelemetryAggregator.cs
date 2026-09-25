using System;
using System.Collections.Generic;

namespace MphRead.Mods.Network.Telemetry;

public sealed record TelemetryDistribution(long Count, double Mean, double P50, double P95, double P99, double Maximum);
public sealed record TelemetryLagBucket(int Weapon, int RttBucket, int JitterBucket,
    TelemetryDistribution Requested, TelemetryDistribution Plausible, TelemetryDistribution Displacement,
    long GlobalClamps, long ShadowClamps, long HitsOutside, long RescuesOutside, long MissesOutside,
    long HitsInside, long RescuesInside, long MissesInside, long UnknownOutcomes);
public sealed record TelemetryCombatAckBucket(int Weapon, int Result,
    TelemetryDistribution SettlementMilliseconds, long ExactDamage, long DamageCorrections,
    long HealthCorrections, long HeadshotCorrections, long Rejected, long[] CorrectionReasons);
public sealed record TelemetryTransportContentionDetails(long Acquisitions, long Contended,
    TelemetryDistribution WaitPerAcquisitionMilliseconds, TelemetryDistribution HoldPerAcquisitionMilliseconds,
    double MaximumWaitMilliseconds, double MaximumHoldMilliseconds);
public sealed record TelemetrySummary(TelemetryHeader Header, double DurationSeconds, TelemetryCounters Counters,
    long[] Network, long[] Combat, long[] Claims, long[] Lifecycle,
    TelemetryDistribution CombatAckLatency, TelemetryDistribution FormDuration, long ForcedForms,
    TelemetryDistribution ServerStepMilliseconds, long DroppedTicks, TelemetryLagBucket[] LagComp,
    TelemetryNetworkDetails NetworkDetails, TelemetryLifecycleDetails LifecycleDetails, TelemetryCombatDetails CombatDetails,
    long[] ShadowOutcomes, long[] FormCorrectionReasons, TelemetryCombatAckBucket[] CombatAcks,
    TelemetryTransportContentionDetails TransportContention);
public sealed record TelemetryNetworkDetails(TelemetryDistribution RttMilliseconds, TelemetryDistribution JitterMilliseconds,
    TelemetryDistribution RecentMinimumRttMilliseconds, TelemetryDistribution RttVariationMilliseconds,
    long[] RttBuckets, long[] JitterBuckets, long Retransmissions, long EstimatedLost, long QueueHighWater);
public sealed record TelemetryLifecycleDetails(TelemetryDistribution JoinMilliseconds, TelemetryDistribution LoadMilliseconds,
    TelemetryDistribution BootstrapMilliseconds, TelemetryDistribution RejoinMilliseconds, long Ready, long LateJoins, long Disconnects);
public sealed record TelemetryCombatDetails(long SettledPredictions, long ExactDamagePredictions, long DamageCorrections,
    long HeadshotCorrections, long HealthCorrections, long RejectedPredictions);

/// <summary>Writer-thread only. Fixed-size histograms retain bounded memory for arbitrarily long matches.</summary>
public sealed class NetTelemetryAggregator
{
    private sealed class Distribution
    {
        private readonly long[] _bins = new long[2049];
        private readonly double _scale;
        private long _count; private double _sum, _max;
        public Distribution(double scale = 1) => _scale = scale;
        public void Add(double value)
        {
            if (!double.IsFinite(value) || value < 0) return;
            _bins[Math.Min(2048, (int)Math.Min(2048, value * _scale))]++; _count++; _sum += value; _max = Math.Max(_max, value);
        }
        private double Quantile(double q)
        {
            if (_count == 0) return 0;
            long n = (long)Math.Ceiling(_count * q), sum = 0;
            for (int i = 0; i < _bins.Length; i++) { sum += _bins[i]; if (sum >= n) return i / _scale; }
            return _max;
        }
        public TelemetryDistribution Capture() => new(_count, _count == 0 ? 0 : _sum / _count, Quantile(.5), Quantile(.95), Quantile(.99), _max);
    }
    private sealed class LagCell
    {
        public readonly Distribution Requested = new(4), Plausible = new(4), Displacement = new(100);
        public long Global, Shadow, Hits, Rescues, Misses, HitsInside, RescuesInside, MissesInside, Unknown;
    }
    private sealed class CombatAckCell
    {
        public readonly Distribution Latency = new();
        public long Exact, Damage, Health, Head, Rejected;
        public readonly long[] Reasons = new long[16];
    }
    private readonly LagCell?[] _lag = new LagCell[NetShotDiagnostics.WeaponCount * 9 * 6];
    private readonly CombatAckCell?[] _combatAcks = new CombatAckCell[NetShotDiagnostics.WeaponCount * ((int)CombatAckResult.Corrected + 1)];
    private readonly long[] _network = new long[8], _combat = new long[8], _claims = new long[16], _lifecycle = new long[256];
    private readonly Distribution _latency = new(), _forms = new(), _steps = new(100);
    private long _forced, _dropped, _ready, _lateJoins, _disconnects, _exact, _rejected, _retransmissions, _lost, _queueHigh;
    private readonly Distribution _rtt = new(), _jitter = new(), _minimum = new(), _variation = new();
    private readonly Distribution _join = new(.01), _load = new(.01), _bootstrap = new(.01), _rejoin = new(.01);
    private readonly long[] _rttBuckets = new long[9], _jitterBuckets = new long[6], _outcomes = new long[7], _formReasons = new long[7];
    private readonly long[] _lastRetransmissions = new long[8], _lastLost = new long[8];
    private readonly ushort[] _connectionGeneration = new ushort[8];
    private long _lastLockAcquisitions, _lastLockContended;
    private double _lastLockWait, _lastLockHold, _maxLockWait, _maxLockHold;
    private long _lockAcquisitions, _lockContended;
    private readonly Distribution _lockWait = new(1000), _lockHold = new(1000);
    public void Add(in NetTelemetryEvent e)
    {
        switch (e.Type)
        {
            case TelemetryEventType.Connection:
                _network[0]++; _network[1] = (long)e.D; _network[2] = (long)e.E; _network[3] = (long)e.F;
                _rtt.Add(e.A); _minimum.Add(e.B); _jitter.Add(e.C); _queueHigh = Math.Max(_queueHigh, (long)e.H);
                _rttBuckets[LagCompensationPolicy.RttBucket(e.A < 0 ? null : e.A)]++;
                _jitterBuckets[LagCompensationPolicy.JitterBucket(e.C < 0 ? null : e.C)]++; break;
            case TelemetryEventType.ConnectionDetail:
                _variation.Add(e.A);
                if (e.Player < 8)
                {
                    int slot = e.Player;
                    if (_connectionGeneration[slot] != e.Generation)
                    { _lastRetransmissions[slot] = _lastLost[slot] = 0; _connectionGeneration[slot] = e.Generation; }
                    _retransmissions += Math.Max(0, (long)e.B - _lastRetransmissions[slot]);
                    _lost += Math.Max(0, (long)e.C - _lastLost[slot]);
                    _lastRetransmissions[slot] = (long)e.B; _lastLost[slot] = (long)e.C;
                }
                break;
            case TelemetryEventType.Shot:
                if ((uint)e.Result < _outcomes.Length) _outcomes[e.Result]++; break;
            case TelemetryEventType.AuthorityResult: _combat[0]++; _combat[1] += (long)e.A; break;
            case TelemetryEventType.CombatAck:
                _combat[2]++; if (e.B != 0) _combat[3]++; if (e.C != 0) _combat[4]++; if (e.D != 0) _combat[5]++;
                bool accepted = new CombatAckEntry { Result = (byte)e.Result }.Accepted;
                if (e.B == 0) _exact++;
                if (!accepted) _rejected++;
                _latency.Add(e.A);
                if (e.Weapon < NetShotDiagnostics.WeaponCount && (uint)e.Result <= (uint)CombatAckResult.Corrected)
                {
                    int ackAt = e.Weapon * ((int)CombatAckResult.Corrected + 1) + e.Result;
                    var ack = _combatAcks[ackAt] ??= new();
                    ack.Latency.Add(e.A);
                    CombatCorrectionReason reason = CombatCorrectionReason.None;
                    if (!accepted) { ack.Rejected++; reason |= CombatCorrectionReason.Rejected; }
                    if (e.B != 0) { ack.Damage++; reason |= CombatCorrectionReason.Damage; } else ack.Exact++;
                    if (e.C != 0) { ack.Health++; reason |= CombatCorrectionReason.Health; }
                    if (e.D != 0) { ack.Head++; reason |= CombatCorrectionReason.Headshot; }
                    ack.Reasons[(int)reason]++;
                }
                break;
            case TelemetryEventType.Claim: if ((uint)e.Result < _claims.Length) _claims[e.Result]++; break;
            case TelemetryEventType.Lifecycle:
                if ((uint)e.Result < _lifecycle.Length) _lifecycle[e.Result]++;
                if (e.Result == 200)
                { _ready++; _join.Add(e.B); _load.Add(e.C); _bootstrap.Add(e.D);
                    if ((e.Flags & 1) != 0) _lateJoins++;
                    if ((e.Flags & 2) != 0) _rejoin.Add(e.B); }
                if (e.Result == 202) _disconnects++;
                break;
            case TelemetryEventType.Form:
                if ((e.Flags & 16) != 0) _forms.Add(e.A);
                if ((uint)e.Result < _formReasons.Length && e.Result != 0) _formReasons[e.Result]++;
                if (e.Result == (int)FormCorrectionReason.ForcedMaximumMismatch) _forced++;
                break;
            case TelemetryEventType.ServerStep: _steps.Add(e.A); _dropped = (long)e.B; break;
            case TelemetryEventType.TransportContention:
                {
                    long acquisitions = Math.Max(0, (long)e.A - _lastLockAcquisitions);
                    long contended = Math.Max(0, (long)e.B - _lastLockContended);
                    double wait = Math.Max(0, e.C - _lastLockWait);
                    double hold = Math.Max(0, e.E - _lastLockHold);
                    _lastLockAcquisitions = (long)e.A; _lastLockContended = (long)e.B;
                    _lastLockWait = e.C; _lastLockHold = e.E;
                    _lockAcquisitions += acquisitions; _lockContended += contended;
                    if (acquisitions > 0)
                    {
                        _lockWait.Add(wait / acquisitions);
                        _lockHold.Add(hold / acquisitions);
                    }
                    _maxLockWait = Math.Max(_maxLockWait, e.D);
                    _maxLockHold = Math.Max(_maxLockHold, e.F);
                    break;
                }
            case TelemetryEventType.LagStudy:
                int weapon = Math.Clamp(e.Weapon, (byte)0, (byte)(NetShotDiagnostics.WeaponCount - 1));
                int rtt = LagCompensationPolicy.RttBucket(e.E < 0 ? null : e.E);
                int jitter = LagCompensationPolicy.JitterBucket(e.F < 0 ? null : e.F);
                var cell = _lag[(weapon * 9 + rtt) * 6 + jitter] ??= new();
                if ((e.Flags & 2) == 0) { cell.Requested.Add(e.A); if (e.C >= 0) cell.Plausible.Add(e.C); if (e.A > e.B) cell.Global++; if ((e.Flags & 1) != 0) cell.Shadow++; }
                cell.Displacement.Add(e.D);
                if ((e.Flags & 1) != 0) { if (e.Result == 1) cell.Hits++; if (e.Result == 2) cell.Rescues++; if (e.Result == 0) cell.Misses++; }
                else { if (e.Result == 1) cell.HitsInside++; if (e.Result == 2) cell.RescuesInside++; if (e.Result == 0) cell.MissesInside++; }
                if ((e.Flags & 2) != 0 && e.Result < 0) cell.Unknown++;
                break;
        }
    }
    public TelemetrySummary Capture(TelemetryHeader header, double seconds, TelemetryCounters counters)
    {
        var buckets = new List<TelemetryLagBucket>();
        for (int i = 0; i < _lag.Length; i++) if (_lag[i] is { } cell)
            buckets.Add(new(i / 54, i / 6 % 9, i % 6, cell.Requested.Capture(), cell.Plausible.Capture(), cell.Displacement.Capture(), cell.Global, cell.Shadow, cell.Hits, cell.Rescues, cell.Misses, cell.HitsInside, cell.RescuesInside, cell.MissesInside, cell.Unknown));
        var combatAcks = new List<TelemetryCombatAckBucket>();
        int results = (int)CombatAckResult.Corrected + 1;
        for (int i = 0; i < _combatAcks.Length; i++) if (_combatAcks[i] is { } ack)
            combatAcks.Add(new(i / results, i % results, ack.Latency.Capture(), ack.Exact,
                ack.Damage, ack.Health, ack.Head, ack.Rejected, ack.Reasons));
        return new(header, seconds, counters, _network, _combat, _claims, _lifecycle, _latency.Capture(), _forms.Capture(), _forced, _steps.Capture(), _dropped, buckets.ToArray(),
            new(_rtt.Capture(), _jitter.Capture(), _minimum.Capture(), _variation.Capture(), _rttBuckets, _jitterBuckets, _retransmissions, _lost, _queueHigh),
            new(_join.Capture(), _load.Capture(), _bootstrap.Capture(), _rejoin.Capture(), _ready, _lateJoins, _disconnects),
            new(_combat[2], _exact, _combat[3], _combat[5], _combat[4], _rejected), _outcomes, _formReasons,
            combatAcks.ToArray(), new(_lockAcquisitions, _lockContended, _lockWait.Capture(), _lockHold.Capture(),
                _maxLockWait, _maxLockHold));
    }
}
