namespace MphRead.Mods.Network.Telemetry;

public enum TelemetryEventType { Connection, Shot, AuthorityResult, Claim, CombatAck, ContinuousTarget, Form, Lifecycle, ServerStep, LagStudy, ConnectionDetail, TransportContention }

[System.Flags]
public enum CombatCorrectionReason : byte
{
    None = 0, Rejected = 1, Damage = 2, Health = 4, Headshot = 8
}

/// <summary>Fixed value payload: no references, player names, addresses, or account IDs.
/// Meaning of numeric fields is defined by schema version and event type.</summary>
public readonly record struct NetTelemetryEvent(
    TelemetryEventType Type, uint Frame, byte Player = 255, byte Victim = 255,
    byte Weapon = 255, ushort Generation = 0, ushort Life = 0, uint Id = 0,
    int Result = 0, int Flags = 0, double A = 0, double B = 0, double C = 0,
    double D = 0, double E = 0, double F = 0, double G = 0, double H = 0,
    double TimestampMilliseconds = 0);

public sealed record TelemetryHeader(int Schema, int Protocol, string MatchSessionId,
    string BuildCommit, string ServerVersion, string ServerPlatform, string MatchMode, string Map, int PlayerCount);
public readonly record struct TelemetryCounters(long EventsQueued, long EventsWritten, long EventsDropped,
    long QueueHighWater, long WriterFailures, long UploadFailures);
