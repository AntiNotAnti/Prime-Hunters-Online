using System;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// Drives a real Spire through morph and slam inputs on the headless server.
    /// No draw call is made. Requires extracted game files, like -simcheck.
    /// </summary>
    public static class SpireAltPoseCheck
    {
        public static int Run(string room)
        {
            if (!ServerSim.Available(out string reason))
            {
                Console.WriteLine($"SPIREPOSE FAIL {reason}");
                return 1;
            }
            var sim = new ServerSim();
            if (!sim.Start(room, GameMode.Battle, maxPlayers: 2, _ => { }, () => { }))
            {
                return 1;
            }
            try
            {
                RosterPacket roster = RosterPacket.Create();
                roster.Slots[0] = 0;
                roster.Hunters[0] = (byte)Hunter.Spire;
                roster.Colors[0] = 0;
                roster.Pings[0] = 0;
                roster.Names[0] = "SPIREPOSE";
                roster.Count = 1;
                NetSession.ApplyRoster(roster);

                bool morphSent = false;
                bool attackSent = false;
                int activeFrames = 0;
                int movingFramesLeft = 0;
                int movingFramesRight = 0;
                float maxLeftOffset = 0;
                float maxRightOffset = 0;
                float maxLeftChange = 0;
                float maxRightChange = 0;
                Vector3 firstLeft = Vector3.Zero;
                Vector3 firstRight = Vector3.Zero;
                Vector3 previousLeft = Vector3.Zero;
                Vector3 previousRight = Vector3.Zero;
                var presses = new uint[IntentPacket.PressHistory];
                PlayerEntity player = PlayerEntity.Players[0];
                for (uint frame = 1; frame <= 240; frame++)
                {
                    IntentButtons buttons = player.LoadFlags.TestFlag(LoadFlags.Spawned) && player.Health > 0
                        ? IntentButtons.InPlayState : IntentButtons.None;
                    if (morphSent || player.IsAltForm)
                    {
                        buttons |= IntentButtons.AltFormState;
                    }
                    if (!morphSent && frame >= 30 && player.LoadFlags.TestFlag(LoadFlags.Spawned))
                    {
                        buttons |= IntentButtons.Morph;
                        morphSent = true;
                    }
                    else if (morphSent && !attackSent && player.IsAltForm && !player.IsMorphing)
                    {
                        buttons |= IntentButtons.AltAttack;
                        attackSent = true;
                    }
                    Array.Clear(presses);
                    presses[0] = (uint)(buttons & (IntentButtons.Morph | IntentButtons.AltAttack));
                    NetSession.AcceptSlotIntent(0, new IntentPacket
                    {
                        Frame = frame,
                        Buttons = buttons,
                        Presses = presses,
                        Aim = Vector3.UnitZ,
                        Position = player.Position,
                        WeaponSelect = 0xFF,
                        AmmoUa = 400,
                        AmmoMissiles = 50
                    });
                    sim.Step();
                    if (sim.StepFailures != 0)
                    {
                        Console.WriteLine("SPIREPOSE FAIL simulation step threw");
                        return 1;
                    }
                    if (!player.Flags2.TestFlag(PlayerFlags2.AltAttack))
                    {
                        continue;
                    }
                    (Vector3 left, Vector3 right) = player.ModSpireAltCollisionPose();
                    Vector3 localLeft = left - player.Position;
                    Vector3 localRight = right - player.Position;
                    if (activeFrames == 0)
                    {
                        firstLeft = localLeft;
                        firstRight = localRight;
                    }
                    else
                    {
                        if ((localLeft - previousLeft).Length > 0.001f)
                        {
                            movingFramesLeft++;
                        }
                        if ((localRight - previousRight).Length > 0.001f)
                        {
                            movingFramesRight++;
                        }
                    }
                    previousLeft = localLeft;
                    previousRight = localRight;
                    activeFrames++;
                    maxLeftOffset = Math.Max(maxLeftOffset, localLeft.Length);
                    maxRightOffset = Math.Max(maxRightOffset, localRight.Length);
                    maxLeftChange = Math.Max(maxLeftChange, (localLeft - firstLeft).Length);
                    maxRightChange = Math.Max(maxRightChange, (localRight - firstRight).Length);
                }
                bool ok = Headless.Active && morphSent && player.IsAltForm && attackSent
                    && activeFrames >= 3 && movingFramesLeft >= 2 && movingFramesRight >= 2
                    && maxLeftOffset > 0.1f && maxRightOffset > 0.1f
                    && maxLeftChange > 0.05f && maxRightChange > 0.05f;
                Console.WriteLine($"SPIREPOSE {(ok ? "ok" : "FAIL")} {room}"
                    + $" | headless {Headless.Active} | spawned {player.LoadFlags.TestFlag(LoadFlags.Spawned)}"
                    + $" | morph sent {morphSent} | morphed {player.IsAltForm}"
                    + $" | attack sent {attackSent} | active frames {activeFrames}"
                    + $" | moving L/R {movingFramesLeft}/{movingFramesRight}"
                    + $" | offset L/R {maxLeftOffset:0.000}/{maxRightOffset:0.000}"
                    + $" | change L/R {maxLeftChange:0.000}/{maxRightChange:0.000}");
                return ok ? 0 : 1;
            }
            finally
            {
                sim.Stop();
            }
        }
    }
}
