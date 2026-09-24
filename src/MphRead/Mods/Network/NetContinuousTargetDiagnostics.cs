using System;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

public enum ContinuousTargetRejection : byte
{
    None, ExplicitNone, MissingDecision, Stale, Stream, WrongGeneration, WrongLife,
    UnavailableHistory, Team, Range, Angle, Form, Eligibility
}
public enum ContinuousTargetDivergence : byte
{
    None, OwnerTarget_None_AuthorityTarget_Player, OwnerTarget_Player_AuthorityTarget_None,
    DifferentPlayer, WrongGeneration, WrongLife, EligibilityDisagreement, AngleDisagreement,
    DistanceDisagreement, FormDisagreement, CollisionDisagreement, PhaseDisagreement,
    DamageGateDisagreement, Unknown
}

/// <summary>Bounded value traces; recording and comparison do not allocate. Formatting is on demand.</summary>
public static class NetContinuousTargetDiagnostics
{
    public struct Evaluation
    {
        public bool Authority;
        public uint Frame, ReportFrame;
        public int Owner;
        public ushort Generation, Life, Timer;
        public ShotKey Launch;
        public ulong Phase;
        public NetTargetIdentity Reported, Selected, Previous, CollisionTarget;
        public bool AltForm, Morphing, TeamEligible, Targetable, Eligible, Acquired, Changed, FirstDivergence;
        public float Distance, Dot, Threshold, Tolerance;
        public ContinuousTargetRejection Rejection;
        public bool CollisionTest, Overlap, DamageGate;
        public uint Damage;
        public Vector3 Back, Position, Center, Origin, Direction, TargetPosition;
        public float Radius;
    }
    public const int Capacity = 4096;
    private static readonly Evaluation[] _ring = new Evaluation[Capacity];
    private static readonly BeamProjectileEntity?[] _beams = new BeamProjectileEntity?[Capacity];
    private static readonly bool[] _first = new bool[PlayerEntity.SlotCapacity];
    private static int _next, _count;
    public static long Evaluations, Reports, Accepted, Rejected, ExplicitNone, Same, Changed;
    public static long Agreement, LocalOnly, AuthorityOnly, Different, Lifecycle, Collision;
    public static readonly long[] Rejections = new long[Enum.GetValues<ContinuousTargetRejection>().Length];
    private static readonly Evaluation[] _firstDivergence = new Evaluation[PlayerEntity.SlotCapacity];

    public static ContinuousTargetDivergence Compare(in Evaluation owner, in Evaluation authority)
    {
        if (!owner.Selected.HasPlayer && authority.Selected.HasPlayer) return ContinuousTargetDivergence.OwnerTarget_None_AuthorityTarget_Player;
        if (owner.Selected.HasPlayer && !authority.Selected.HasPlayer) return ContinuousTargetDivergence.OwnerTarget_Player_AuthorityTarget_None;
        if (owner.Selected.Slot != authority.Selected.Slot) return ContinuousTargetDivergence.DifferentPlayer;
        if (owner.Selected.Generation != authority.Selected.Generation) return ContinuousTargetDivergence.WrongGeneration;
        if (owner.Selected.LifeId != authority.Selected.LifeId) return ContinuousTargetDivergence.WrongLife;
        if (owner.AltForm != authority.AltForm || owner.Morphing != authority.Morphing) return ContinuousTargetDivergence.FormDisagreement;
        if (owner.Eligible != authority.Eligible || owner.TeamEligible != authority.TeamEligible || owner.Targetable != authority.Targetable)
            return ContinuousTargetDivergence.EligibilityDisagreement;
        if ((owner.Dot >= owner.Threshold) != (authority.Dot >= authority.Threshold)) return ContinuousTargetDivergence.AngleDisagreement;
        if (owner.Rejection == ContinuousTargetRejection.Range != (authority.Rejection == ContinuousTargetRejection.Range)) return ContinuousTargetDivergence.DistanceDisagreement;
        if (owner.Phase != authority.Phase) return ContinuousTargetDivergence.PhaseDisagreement;
        if (owner.CollisionTest != authority.CollisionTest || owner.Overlap != authority.Overlap
            || owner.CollisionTarget != authority.CollisionTarget) return ContinuousTargetDivergence.CollisionDisagreement;
        if (owner.DamageGate != authority.DamageGate || owner.Damage != authority.Damage) return ContinuousTargetDivergence.DamageGateDisagreement;
        return ContinuousTargetDivergence.None;
    }
    internal static void Record(BeamProjectileEntity beam, in Evaluation evaluation)
    {
        int index = _next;
        _ring[index] = evaluation; _beams[index] = beam;
        _next = (_next + 1) % Capacity; _count = Math.Min(_count + 1, Capacity); Evaluations++;
        if (!evaluation.Authority) return;
        Telemetry.ProductionTelemetry.Emit(new(Telemetry.TelemetryEventType.ContinuousTarget, evaluation.Frame,
            Player: (byte)evaluation.Owner, Victim: (byte)(evaluation.Selected.HasPlayer ? evaluation.Selected.Slot : 255),
            Result: (int)evaluation.Rejection, A: evaluation.Reported.EncodedSlot, B: evaluation.Selected.EncodedSlot,
            C: evaluation.Phase, D: evaluation.Dot, E: evaluation.Threshold));
        Reports++;
        if (evaluation.Rejection == ContinuousTargetRejection.ExplicitNone) ExplicitNone++;
        else if (evaluation.Rejection == ContinuousTargetRejection.None) Accepted++;
        else { Rejected++; Rejections[(int)evaluation.Rejection]++; }
        // No owner decision exists to compare for a missing/malformed report.
        if (!evaluation.Reported.IsWellFormed) return;
        if (evaluation.Changed) Changed++; else Same++;
        if (evaluation.Reported == evaluation.Selected) Agreement++;
        else
        {
            if (!evaluation.Reported.HasPlayer) AuthorityOnly++;
            else if (!evaluation.Selected.HasPlayer) LocalOnly++;
            else Different++;
            if (evaluation.Rejection is ContinuousTargetRejection.WrongGeneration or ContinuousTargetRejection.WrongLife) Lifecycle++;
            if (!_first[evaluation.Owner]) { _first[evaluation.Owner] = true; _firstDivergence[evaluation.Owner] = evaluation; _ring[index].FirstDivergence = true; }
        }
    }
    internal static void CollisionResult(BeamProjectileEntity beam, PlayerEntity target, bool overlap, uint damage = 0)
    {
        if (beam.Beam != BeamType.ShockCoil) return;
        for (int n = 1; n <= Math.Min(_count, 128); n++)
        {
            int i = (_next - n + Capacity) % Capacity;
            if (_beams[i] != beam) continue;
            ref var item = ref _ring[i];
            if (item.Selected.Slot != target.SlotIndex && !overlap) return;
            if (!item.CollisionTest) Collision++;
            item.CollisionTarget = NetTargetIdentity.ForSlot(target.SlotIndex);
            item.CollisionTest = true; item.Overlap |= overlap; item.DamageGate |= damage > 0; item.Damage += damage;
            item.Back = beam.BackPosition; item.Position = beam.Position;
            item.Center = target.Volume.SpherePosition; item.Radius = target.Volume.SphereRadius;
            if (item.Authority) Telemetry.ProductionTelemetry.Emit(new(Telemetry.TelemetryEventType.ContinuousTarget, item.Frame,
                Player: (byte)item.Owner, Victim: (byte)target.SlotIndex, Result: (int)item.Rejection,
                Flags: (overlap ? 1 : 0) | (damage > 0 ? 2 : 0), A: item.Reported.EncodedSlot,
                B: item.Selected.EncodedSlot, C: item.Phase, D: damage));
            return;
        }
    }
    public static int CopyTo(Span<Evaluation> destination)
    {
        int count = Math.Min(destination.Length, _count);
        for (int i = 0; i < count; i++) destination[i] = _ring[(_next - count + i + Capacity) % Capacity];
        return count;
    }
    internal static void EndBurst(int slot) { if ((uint)slot < _first.Length) _first[slot] = false; }
    public static string Describe() => $"continuous target: eval={Evaluations} reports={Reports} accepted={Accepted} none={ExplicitNone} rejected={Rejected} agreed={Agreement} owner-only={LocalOnly} authority-only={AuthorityOnly} different={Different} lifecycle={Lifecycle} collision={Collision} stale={Rejections[(int)ContinuousTargetRejection.Stale]} history={Rejections[(int)ContinuousTargetRejection.UnavailableHistory]} angle={Rejections[(int)ContinuousTargetRejection.Angle]} form={Rejections[(int)ContinuousTargetRejection.Form]}";
    public static void WriteTraces(System.IO.TextWriter writer)
    {
        for (int n = 0; n < _count; n++)
        {
            var item = _ring[(_next - _count + n + Capacity) % Capacity];
            writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(item,
                TraceJsonOptions));
        }
    }
    private static readonly System.Text.Json.JsonSerializerOptions TraceJsonOptions = CreateTraceOptions();
    private static System.Text.Json.JsonSerializerOptions CreateTraceOptions()
    {
        var options = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
        options.Converters.Add(new VectorConverter()); return options;
    }
    private sealed class VectorConverter : System.Text.Json.Serialization.JsonConverter<Vector3>
    {
        public override Vector3 Read(ref System.Text.Json.Utf8JsonReader reader, Type type, System.Text.Json.JsonSerializerOptions options)
            => throw new NotSupportedException();
        public override void Write(System.Text.Json.Utf8JsonWriter writer, Vector3 value, System.Text.Json.JsonSerializerOptions options)
        { writer.WriteStartArray(); writer.WriteNumberValue(value.X); writer.WriteNumberValue(value.Y); writer.WriteNumberValue(value.Z); writer.WriteEndArray(); }
    }
    public static string FirstDivergences()
    {
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < _first.Length; i++)
            if (_first[i]) { var e = _firstDivergence[i]; text.Append($" first[{i}]={e.ReportFrame}/{e.Rejection} owner={e.Reported} authority={e.Selected}"); }
        return text.ToString();
    }
    public static void Reset()
    {
        Array.Clear(_ring); Array.Clear(_beams); Array.Clear(_first); Array.Clear(_firstDivergence); Array.Clear(Rejections);
        _next = _count = 0; Evaluations = Reports = Accepted = Rejected = ExplicitNone = Same = Changed = 0;
        Agreement = LocalOnly = AuthorityOnly = Different = Lifecycle = Collision = 0;
    }
}
