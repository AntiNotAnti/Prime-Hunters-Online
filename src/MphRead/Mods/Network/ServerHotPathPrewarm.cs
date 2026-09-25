using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using MphRead.Entities;

namespace MphRead.Mods.Network;

/// <summary>
/// Best-effort JIT prewarm for dedicated-server combat paths. It executes no
/// gameplay code and mutates no scene state: PrepareMethod only asks the runtime
/// to compile methods before the first live shot pays that cost.
/// </summary>
internal static class ServerHotPathPrewarm
{
    private static int _done;
    public static double Run()
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return 0;
        long started = Stopwatch.GetTimestamp();
        foreach (Type type in new[]
        {
            typeof(BeamProjectileEntity), typeof(PlayerEntity), typeof(NetUnlagged),
            typeof(NetHitClaims), typeof(NetHitPrediction), typeof(NetContinuousTargeting),
            typeof(NetDynamicGeometryHistory), typeof(LagCompensationPolicy)
        })
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.ContainsGenericParameters || method.IsAbstract) continue;
                try { RuntimeHelpers.PrepareMethod(method.MethodHandle); }
                catch (Exception) { /* ReadyToRun/AOT/runtime-specific: prewarm is optional. */ }
            }
        }
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
}
