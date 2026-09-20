using System;
using System.Text;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    public enum ShotAttemptResult
    {
        Spawned, AttachedEnemy, Cooldown, AutofireCooldown, GunLowered, NoAmmo,
        NoProjectileSlot, DeadOrNotInPlay, StaleLife, OtherNoSpawn
    }

    // Diagnostic identity only: no extra wire fields or gameplay authority.
    public readonly record struct ShotKey(ulong AuthorityEpoch, ushort MatchId, int ShooterSlot,
        ushort Generation, ushort LifeId, uint LaunchFrame)
    {
        public static ShotKey For(int slot, uint frame) => new(NetSession.AuthorityEpoch,
            NetSession.CurrentMatchId, slot, NetPlayerLifecycle.Generation(slot), NetPlayerLifecycle.Get(slot), frame);
        public override string ToString() => $"{AuthorityEpoch}/{MatchId}/{ShooterSlot}/{Generation}/{LifeId}/{LaunchFrame}";
    }

    public static class NetShotDiagnostics
    {
        public const int WeaponCount = (int)BeamType.Enemy + 2;
        public static readonly long[,] Outcomes = new long[WeaponCount, Enum.GetValues<ShotAttemptResult>().Length];
        public static readonly long[] LocalHits = new long[WeaponCount], AuthorityHits = new long[WeaponCount],
            Predictions = new long[WeaponCount], Claims = new long[WeaponCount], Rescues = new long[WeaponCount],
            Refusals = new long[WeaponCount], PredictedDamage = new long[WeaponCount], AuthorityDamage = new long[WeaponCount],
            LocalHeadshots = new long[WeaponCount], AuthorityHeadshots = new long[WeaponCount],
            RewindSamples = new long[WeaponCount], RewindFrames = new long[WeaponCount], RewindClamps = new long[WeaponCount];

        public static readonly long[] ContinuousTicks = new long[WeaponCount], ContinuousDamageTicks = new long[WeaponCount],
            ContinuousAcquired = new long[WeaponCount], ContinuousAmmo = new long[WeaponCount], DrainCredit = new long[WeaponCount];
        public static void Continuous(BeamProjectileEntity beam, int ammo)
        {
            int b = Bucket(beam.Beam);
            ContinuousTicks[b]++;
            if (beam.Damage > 0) ContinuousDamageTicks[b]++;
            if (beam.Target != null) ContinuousAcquired[b]++;
            ContinuousAmmo[b] += ammo;
            if (NetLog.Enabled) Trace("continuous", beam.ModLaunchKey, beam.Beam,
                $"phase={beam.ModContinuousPhase} damage={beam.Damage} ammo={ammo} acquired={beam.Target != null}");
        }

        public static int Bucket(BeamType weapon) => (int)weapon >= 0 && (int)weapon < WeaponCount - 1
            ? (int)weapon : WeaponCount - 1;

        public static void Trace(string stage, in ShotKey key, BeamType weapon, string detail = "")
        {
            if (NetLog.Enabled) NetLog.Event($"[shot] key={key} stage={stage} weapon={weapon} authorityFrame={NetSession.NetFrame} {detail}");
        }

        // Exactly one outcome for every TryFireWeapon invocation, including early returns.
        public static bool Finish(PlayerEntity shooter, ShotAttemptResult result, Vector3 shot = default, Vector3 aim = default)
        {
            if (NetSession.Active)
            {
                Outcomes[Bucket(shooter.CurrentWeapon), (int)result]++;
                if (result == ShotAttemptResult.Spawned) NetDamage.NoteFired(shooter, shot, aim);
                if (NetLog.Enabled) Trace("attempt", ShotKey.For(shooter.SlotIndex, NetUnlagged.LaunchFrameFor(shooter)),
                    shooter.CurrentWeapon, $"result={result} health={shooter.Health} shoot={shooter.Controls.Shoot.IsDown} press={shooter.Controls.Shoot.IsPressed} pressAge={NetPlayerBridge.ShootPressAge[shooter.SlotIndex]}");
            }
            return result == ShotAttemptResult.Spawned;
        }

        public static void Reset()
        {
            Array.Clear(Outcomes);
            foreach (long[] values in new[] { LocalHits, AuthorityHits, Predictions, Claims, Rescues, Refusals,
                PredictedDamage, AuthorityDamage, LocalHeadshots, AuthorityHeadshots, RewindSamples, RewindFrames, RewindClamps, ContinuousTicks, ContinuousDamageTicks, ContinuousAcquired, ContinuousAmmo, DrainCredit })
                Array.Clear(values);
        }

        public static string Describe()
        {
            var text = new StringBuilder("weapon attempted spawned localHits authorityHits predictions claims rescues refusals predictedDamage authorityDamage localHeadshots authorityHeadshots meanRewind clamp%\n");
            for (int b = 0; b < WeaponCount; b++)
            {
                long attempted = 0;
                for (int r = 0; r < Outcomes.GetLength(1); r++) attempted += Outcomes[b, r];
                if (attempted + LocalHits[b] + AuthorityHits[b] + Claims[b] == 0) continue;
                text.AppendLine($"{(b == WeaponCount - 1 ? "alt/bomb" : ((BeamType)b).ToString())} {attempted} {Outcomes[b, 0]} {LocalHits[b]} {AuthorityHits[b]} {Predictions[b]} {Claims[b]} {Rescues[b]} {Refusals[b]} {PredictedDamage[b]} {AuthorityDamage[b]} {LocalHeadshots[b]} {AuthorityHeadshots[b]} {(RewindSamples[b] == 0 ? 0 : (double)RewindFrames[b] / RewindSamples[b]):F2} {(RewindSamples[b] == 0 ? 0 : 100.0 * RewindClamps[b] / RewindSamples[b]):F1}");
                if (ContinuousTicks[b] > 0) text.AppendLine($"  continuous ticks={ContinuousTicks[b]} nonzero={ContinuousDamageTicks[b]} acquired={ContinuousAcquired[b]} ammo={ContinuousAmmo[b]} drainCredit={DrainCredit[b]}");
                for (int r = 1; r < Outcomes.GetLength(1); r++)
                    if (Outcomes[b, r] != 0) text.AppendLine($"  {(ShotAttemptResult)r}={Outcomes[b, r]}");
            }
            return text.ToString();
        }
    }
}
