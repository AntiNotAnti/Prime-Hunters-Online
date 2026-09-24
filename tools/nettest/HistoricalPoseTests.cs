using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.NetTest;
internal static class HistoricalPoseTests
{
    internal static void Check(bool value, string label) => NetArchitectureTests.Check(value, label);
    internal static object? Call(object instance, string name, params object[] args) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    public static void Run()
    {
        PlayerEntity.GeneratePlayerVolumes();
        var lower = new HistoricalPlayerPose(new(1, 2, 3), true, new(new(2, 2, 3), new(3, 2, 3), new(4, 2, 3)));
        var upper = lower with { Position = new(3, 2, 3), AltPose = new(new(4, 2, 3), new(5, 2, 3), new(6, 2, 3)) };
        var halfway = NetUnlagged.InterpolatePose(lower, upper, .5f);
        Check(halfway.Position.X == 2 && halfway.AltPose.Seg3.X == 5, "position and chain interpolate together");
        foreach (bool form in new[] { false, true })
        {
            var a = lower with { AltForm = form };
            Check(NetUnlagged.InterpolatePose(a, upper with { AltForm = !form }, .99f) == a,
                "morph/unmorph retains lower complete pose");
        }
        Check(NetUnlagged.InterpolatePose(lower, upper with { Position = new(100, 0, 0) }, .5f) == lower,
            "teleports do not create invented geometry");
        foreach (Hunter hunter in new[] { Hunter.Samus, Hunter.Kanden, Hunter.Spire, Hunter.Noxus, Hunter.Trace, Hunter.Sylux, Hunter.Weavel })
        foreach (bool currentAlt in new[] { false, true })
        {
            var player = (PlayerEntity)RuntimeHelpers.GetUninitializedObject(typeof(PlayerEntity));
            typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.Hunter))!.SetValue(player, hunter);
            typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.Flags1))!.SetValue(player, currentAlt ? PlayerFlags1.AltForm : 0);
            player.Position = new(90, 80, 70); player.PrevPosition = new(89, 80, 70);
            var segments = new[] { new Vector3(90), new Vector3(91), new Vector3(92), new Vector3(93), new Vector3(94) };
            HealthShotTests.Field(player, "_kandenSegPos", segments);
            var volume = CollisionVolume.Move(PlayerEntity.PlayerVolumes[(int)hunter, currentAlt ? 2 : 0], player.Position);
            HealthShotTests.Field(player, "_volume", volume);
            HealthShotTests.Field(player, "_volumeUnxf", PlayerEntity.PlayerVolumes[(int)hunter, currentAlt ? 2 : 0]);
            object before = Call(player, "ModCaptureCollisionState")!;
            var flags = player.Flags1;
            var pose = lower with { AltForm = !currentAlt };
            try
            {
                Call(player, "ModApplyHistoricalCollisionPose", pose);
                Check(player.Position == pose.Position && player.Flags1 == flags, "rewind does not mutate gameplay flags");
                Check((bool)typeof(PlayerEntity).GetProperty("ModCollisionIsAltForm", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(player)! == pose.AltForm,
                    "collision form overrides gameplay form");
                if (hunter == Hunter.Kanden) Check(segments[1] == pose.AltPose.Seg1 && segments[3] == pose.AltPose.Seg3, "historical chain applied");
                Check(player.Volume.SphereRadius == PlayerEntity.PlayerVolumes[(int)hunter, pose.AltForm ? 2 : 0].SphereRadius,
                    "historical radius selected for every hunter");
            }
            finally { Call(player, "ModRestoreCollisionState", before); }
            Check(before.Equals(Call(player, "ModCaptureCollisionState")), "capture/apply/restore lossless including previous position and cached volumes");
            Check(segments[0] == new Vector3(90) && segments[4] == new Vector3(94), "unused chain endpoints untouched");
        }
    }
}
