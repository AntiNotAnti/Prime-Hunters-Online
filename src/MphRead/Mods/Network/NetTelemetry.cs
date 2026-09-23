using System;
using System.Diagnostics;
using System.Threading;
using MphRead.Entities;

namespace MphRead.Mods.Network;

public readonly record struct NetTransportSnapshot(long PacketsReceived, long PacketsSent, long BytesReceived,
    long BytesSent, long Invalid, long Oversized, int QueueCurrent, int QueueHighWater, long QueueDrops,
    long Coalesced, long Processed, double ProcessingMilliseconds, long SocketErrors);

/// <summary>Transport owns this instance. Worker/simulation counters use atomics; reporting never resets them.</summary>
public sealed class NetTransportTelemetry
{
    private long _rx, _tx, _rxBytes, _txBytes, _invalid, _oversized, _drops, _coalesced, _processed, _elapsed, _errors;
    private int _queue, _high;
    public void Received(int bytes) { Interlocked.Increment(ref _rx); Interlocked.Add(ref _rxBytes, bytes); }
    public void Sent(int bytes) { Interlocked.Increment(ref _tx); Interlocked.Add(ref _txBytes, bytes); }
    public void Invalid(bool oversized = false) { Interlocked.Increment(ref _invalid); if (oversized) Interlocked.Increment(ref _oversized); }
    public void Drop() => Interlocked.Increment(ref _drops);
    public void Coalesce() => Interlocked.Increment(ref _coalesced);
    public void Error() => Interlocked.Increment(ref _errors);
    public void Processed(long elapsed) { Interlocked.Increment(ref _processed); Interlocked.Add(ref _elapsed, elapsed); }
    public void Queue(int count)
    {
        Volatile.Write(ref _queue, count);
        int old;
        while (count > (old = Volatile.Read(ref _high)) && Interlocked.CompareExchange(ref _high, count, old) != old) { }
    }
    public NetTransportSnapshot Capture() => new(Interlocked.Read(ref _rx), Interlocked.Read(ref _tx),
        Interlocked.Read(ref _rxBytes), Interlocked.Read(ref _txBytes), Interlocked.Read(ref _invalid),
        Interlocked.Read(ref _oversized), Volatile.Read(ref _queue), Volatile.Read(ref _high), Interlocked.Read(ref _drops),
        Interlocked.Read(ref _coalesced), Interlocked.Read(ref _processed), Interlocked.Read(ref _elapsed) * 1000.0 / Stopwatch.Frequency,
        Interlocked.Read(ref _errors));
}

public enum NetIntentRejection { None, Duplicate, Reordered, WrongMatch, WrongEpoch, WrongGeneration, WrongLife, Invalid }
public readonly record struct NetIntentSnapshot(long Received, long Accepted, long Duplicate, long Reordered,
    long WrongMatch, long WrongEpoch, long WrongGeneration, long WrongLife, long Invalid, long FrameGaps,
    uint LargestGap, double? SilenceMilliseconds, double MaximumSilenceMilliseconds);

/// <summary>One simulation-thread owner. Counters describe decisions, never make them.</summary>
public sealed class NetPeerTelemetry
{
    private long _received, _accepted, _duplicate, _reordered, _match, _epoch, _generation, _life, _invalid, _gaps;
    private uint _lastFrame, _largestGap;
    private long _lastArrival;
    private double _maxSilence;
    private bool _seen;
    public void Intent(uint frame, NetIntentRejection rejection, long now = 0)
    {
        _received++;
        switch (rejection)
        {
            case NetIntentRejection.Duplicate: _duplicate++; return;
            case NetIntentRejection.Reordered: _reordered++; return;
            case NetIntentRejection.WrongMatch: _match++; return;
            case NetIntentRejection.WrongEpoch: _epoch++; return;
            case NetIntentRejection.WrongGeneration: _generation++; return;
            case NetIntentRejection.WrongLife: _life++; return;
            case NetIntentRejection.Invalid: _invalid++; return;
        }
        now = now == 0 ? Stopwatch.GetTimestamp() : now;
        if (_seen)
        {
            uint gap = NetLifecycleTracker.Newer(frame, _lastFrame) ? unchecked(frame - _lastFrame - 1) : 0;
            _gaps += gap; _largestGap = Math.Max(_largestGap, gap);
            _maxSilence = Math.Max(_maxSilence, (now - _lastArrival) * 1000.0 / Stopwatch.Frequency);
        }
        _accepted++; _seen = true; _lastFrame = frame; _lastArrival = now;
    }
    public NetIntentSnapshot Capture(long now = 0) => new(_received, _accepted, _duplicate, _reordered,
        _match, _epoch, _generation, _life, _invalid, _gaps, _largestGap,
        _seen ? ((now == 0 ? Stopwatch.GetTimestamp() : now) - _lastArrival) * 1000.0 / Stopwatch.Frequency : null, _maxSilence);
    public void NewLife() { _seen = false; _lastArrival = 0; }
    public void Reset()
    {
        _received = _accepted = _duplicate = _reordered = _match = _epoch = _generation = _life = _invalid = _gaps = 0;
        _largestGap = 0; _maxSilence = 0; NewLife();
    }
}

public readonly record struct NetPresentationSnapshot(double Delay, double JitterFrames, long Interpolated,
    long Held, long Starved, long Snaps, long StalledFrames, int LongestStall);
public readonly record struct NetLagCompSnapshot(long Shots, double? MeanApplied, int WorstApplied,
    int WorstRequested, int? RequestedP50, int? RequestedP95, int? RequestedP99, long Clamped,
    long FramesRefused, long PressAgeShots, long PressAgeFrames, long HistoryMisses, long CatchUpSteps, long CatchUpHits);
public readonly record struct NetCombatSnapshot(long Predicted, long Confirmed, long Denied, long ClaimsReceived,
    long ClaimsApplied, long ClaimsDuplicate, long ClaimsRejected, long ClaimsTooOld, long DeadShooter, long DeadVictim);
public readonly record struct NetTelemetrySnapshot(NetTransportSnapshot? Transport, long SnapshotsAccepted,
    long SnapshotsOutOfOrder, long IntentFallbackFrames, NetPresentationSnapshot Presentation,
    NetLagCompSnapshot LagComp, NetCombatSnapshot Combat, LagShadowSnapshot Shadow);

public static class NetTelemetry
{
    // Match and session are separate event counters. Session survives rematches;
    // a reconnect/Stop replaces connection identity and resets both.
    public static readonly NetPeerTelemetry[] Match = CreatePeers(), Session = CreatePeers();
    private static NetPeerTelemetry[] CreatePeers()
    { var peers = new NetPeerTelemetry[PlayerEntity.SlotCapacity]; for (int i = 0; i < peers.Length; i++) peers[i] = new(); return peers; }
    public static void Intent(int slot, uint frame, NetIntentRejection reason)
    { if ((uint)slot < Match.Length) { Match[slot].Intent(frame, reason); Session[slot].Intent(frame, reason); } }
    public static void NewLife(int slot) { if ((uint)slot < Match.Length) { Match[slot].NewLife(); Session[slot].NewLife(); } }
    public static void ForgetSlot(int slot) { if ((uint)slot < Match.Length) { Match[slot].Reset(); Session[slot].Reset(); } }
    public static void NewMatch() { foreach (var peer in Match) peer.Reset(); foreach (var peer in Session) peer.NewLife(); }
    public static void FullSessionReset() { foreach (var peer in Match) peer.Reset(); foreach (var peer in Session) peer.Reset(); }
    private static int? Percentile(double percentile)
    {
        long total = 0; foreach (long n in NetUnlagged.DepthHistogram) total += n;
        if (total == 0) return null;
        long target = (long)Math.Ceiling(total * percentile), sum = 0;
        for (int i = 0; i < NetUnlagged.DepthHistogram.Length; i++)
        { sum += NetUnlagged.DepthHistogram[i]; if (sum >= target) return i; }
        return null;
    }
    // Bridge existing counters rather than maintaining second copies. These
    // presentation/lag/combat counters have the reset scope of their producer.
    public static NetTelemetrySnapshot Capture(NetTransport? transport = null) => new(transport?.Telemetry.Capture(),
        NetSession.SnapshotsReceived, NetSession.SnapshotsOutOfOrder, NetTimingDiagnostics.IntentFallbackFrames,
        new(NetSmoothing.Delay, NetSmoothing.JitterFrames, NetSmoothing.Interpolated, NetSmoothing.Held,
            NetSmoothing.Starved, NetSmoothing.Snaps, NetSmoothing.StalledFrames, NetSmoothing.WorstStall),
        new(NetUnlagged.ShotsCompensated, NetUnlagged.ShotsCompensated == 0 ? null : (double)NetUnlagged.FramesRewound / NetUnlagged.ShotsCompensated,
            NetUnlagged.WorstRewind, NetUnlagged.WorstRequested, Percentile(.5), Percentile(.95), Percentile(.99),
            NetUnlagged.ShotsClamped, NetUnlagged.FramesRefused, NetUnlagged.StalePresses, NetUnlagged.StalePressFrames,
            NetUnlagged.HistoryMisses, NetUnlagged.CatchUpSteps, NetUnlagged.CatchUpHits),
        new(NetHitPrediction.Predicted, NetHitPrediction.Confirmed, NetHitPrediction.Denied, NetHitClaims.Received,
            NetHitClaims.AppliedHere, NetHitClaims.DuplicateHere, NetHitClaims.RefusedHere, NetHitClaims.TooOldHere,
            NetHitClaims.VoidedDeadShooter, NetHitClaims.VoidedDeadVictim), LagCompensationPolicy.CaptureTotal());
}
