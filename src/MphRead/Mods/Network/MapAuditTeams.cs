using System;
using System.Collections.Generic;
using MphRead.Entities;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    public sealed partial class MapAudit
    {
        public static bool TeamProbe { get; set; }
        private readonly List<string> _teamProbeProblems = new();

        private void CaptureTeamResults()
        {
            if (_shotDirectory == null || !ShowWindow || GameState.Mode != GameMode.BattleTeams) return;
            GameState.MatchState = MatchState.Ending;
            PlayerEntity.ModForceScoreboard = true;
            foreach (bool tie in new[] { false, true })
            {
                for (int slot = 0; slot < 8; slot++)
                {
                    GameState.Points[slot] = tie ? 2 : PlayerEntity.Players[slot].TeamIndex == 3 ? 4 : 2;
                    GameState.Kills[slot] = GameState.Deaths[slot] = 0;
                }
                GameState.UpdateState();
                Scene.OnDrawFrame();
                Scene.OnRenderFrame();
                ScreenCapture.SaveWindow(Scene, System.IO.Path.Combine(_shotDirectory,
                    tie ? "teams-tie.png" : "teams-winner.png"));
            }
        }

        // Run after the rendered tour, before cleanup, using the actual initialized entities.
        private List<string> RunTeamProbe()
        {
            var failures = new List<string>();
            int checks = 0;
            void Check(bool result, string message)
            {
                checks++;
                if (!result)
                {
                    failures.Add(message);
                    Console.WriteLine($"MAPFAIL {_room} | teamprobe: {message}");
                }
            }
            try
            {
                int[] expected = { 0, 1, 2, 3, 3, 2, 1, 0 };
                Check(GameState.TeamCount == 4 && GameState.Teams, "four-team match configured");
                for (int slot = 0; slot < 8; slot++)
                {
                    PlayerEntity player = PlayerEntity.Players[slot];
                    Check(player.TeamIndex == expected[slot] && _everSpawned[slot], $"slot {slot} team/spawn");
                    player.Health = 99;
                    player.IsBot = false;
                    player.ModForceForm(false);
                    GameState.Points[slot] = GameState.Deaths[slot] = GameState.Kills[slot] = 0;
                    GameState.TeamDeaths[slot] = 0;
                }
                GameState.PointGoal = 999;
                GameState.FriendlyFire = false;
                PlayerEntity target = PlayerEntity.Players[7];
                target.TakeDamage(5, DamageFlags.IgnoreInvuln, null, PlayerEntity.Players[0]);
                Check(target.Health == 99, "slot 0 cannot damage allied slot 7");
                target.TakeDamage(5, DamageFlags.IgnoreInvuln, null, PlayerEntity.Players[3]);
                Check(target.Health < 99, "team D can damage team A");
                int beforeFriendly = target.Health;
                GameState.FriendlyFire = true;
                target.TakeDamage(5, DamageFlags.IgnoreInvuln, null, PlayerEntity.Players[0]);
                Check(target.Health < beforeFriendly, "friendly fire rule enables allied damage");
                GameState.FriendlyFire = false;

                GameState.Points[7] = 4;
                GameState.Points[4] = 5;
                GameState.UpdateState();
                Check(GameState.TeamPoints[0] == 4 && GameState.TeamPoints[3] == 5,
                    "upper slots contribute to assigned team totals");
                if (GameState.Mode == GameMode.BattleTeams || GameState.Mode == GameMode.BountyTeams
                    || GameState.Mode == GameMode.NodesTeams)
                {
                    Check(GameState.Standings[4] == 0 && GameState.Standings[7] == 1,
                        "team D wins and team A is second");
                }
                if (GameState.Mode == GameMode.SurvivalTeams)
                {
                    GameState.PointGoal = 2;
                    GameState.MatchTime = 60;
                    for (int slot = 0; slot < 8; slot++)
                    {
                        PlayerEntity.Players[slot].Health = expected[slot] == 0 ? 0 : 99;
                        GameState.TeamDeaths[slot] = 3;
                    }
                    GameState.UpdateSurvival(Scene.FrameTime);
                    Check(GameState.MatchTime == 60, "eliminating A leaves three competing teams");
                    for (int slot = 0; slot < 8; slot++)
                        PlayerEntity.Players[slot].Health = expected[slot] == 3 ? 99 : 0;
                    GameState.UpdateSurvival(Scene.FrameTime);
                    GameState.UpdateState();
                    Check(GameState.MatchTime == 0 && GameState.Standings[4] == 0 && GameState.TeamTime[3] == -1,
                        "surviving team D wins with MAX time");
                }
                else if (GameState.Mode == GameMode.NodesTeams || GameState.Mode == GameMode.DefenderTeams)
                {
                    NodeDefenseEntity? node = null;
                    foreach (NodeDefenseEntity candidate in Scene.GetNodeDefenseEntities()) { node = candidate; break; }
                    Check(node != null, "objective exists in selected map layer");
                    if (node != null)
                    {
                        void Occupy(int slot)
                        {
                            foreach (PlayerEntity player in PlayerEntity.Players) player.Health = 0;
                            PlayerEntity occupant = PlayerEntity.Players[slot];
                            occupant.Health = 99;
                            Vector3 center = VolumeCenter(node.Volume);
                            occupant.Reposition(center - occupant.Volume.SpherePosition, occupant.NodeRef);
                        }
                        if (GameState.Mode == GameMode.DefenderTeams)
                        {
                            Occupy(7);
                            float before = GameState.TeamTime[0];
                            node.Process();
                            Check(node.CurrentTeam == 0 && GameState.TeamTime[0] > before,
                                "slot 7 contributes Defender time to A");
                            Occupy(4);
                            before = GameState.TeamTime[3];
                            node.Process();
                            Check(node.CurrentTeam == 3 && GameState.TeamTime[3] > before,
                                "slot 4 contributes Defender time to D");
                            PlayerEntity other = PlayerEntity.Players[7];
                            other.Health = 99;
                            other.Reposition(VolumeCenter(node.Volume) - other.Volume.SpherePosition, other.NodeRef);
                            node.Process();
                            Check(node.Contested && node.CurrentTeam == NodeDefenseEntity.NoTeam,
                                "different teams contest Defender without a false owner");
                        }
                        else
                        {
                            int steps = (int)Math.Ceiling(11 / Math.Max(Scene.FrameTime, 1 / 60f));
                            foreach (int slot in new[] { 4, 7, 4 })
                            {
                                Occupy(slot);
                                for (int frame = 0; frame < steps; frame++) node.Process();
                                Check(node.CurrentTeam == expected[slot], $"slot {slot} captures Nodes for assigned team");
                            }
                            Check(GameState.NodesCaptured[7] > 0 && GameState.NodesCaptured[4] > 0,
                                "upper-slot Node capture accounting");
                            PlayerEntity.Players[4].Health = 0;
                            node.Process();
                            Check(!node.IsOccupied && node.OccupyingTeam == NodeDefenseEntity.NoTeam,
                                "upper-slot occupancy clears after departure");
                        }
                    }
                }
                else if (GameState.Mode == GameMode.BountyTeams)
                {
                    OctolithFlagEntity? flag = null;
                    FlagBaseEntity? flagBase = null;
                    foreach (OctolithFlagEntity candidate in Scene.GetOctolithFlagEntities()) { flag = candidate; break; }
                    foreach (FlagBaseEntity candidate in Scene.GetFlagBaseEntities()) { flagBase = candidate; break; }
                    Check(flag != null && flagBase != null, "Bounty flag and base exist");
                    if (flag != null && flagBase != null)
                    {
                        if (flag.Carrier != null) flag.OnCaptured();
                        foreach (PlayerEntity player in PlayerEntity.Players) player.Health = 0;
                        PlayerEntity carrier = PlayerEntity.Players[4];
                        carrier.Health = 99;
                        float maxPickup = Fixed.ToFloat(carrier.Values.MaxPickupHeight);
                        float minPickup = Fixed.ToFloat(carrier.Values.MinPickupHeight);
                        Vector3 pickupCenter = flag.Position.AddY(-maxPickup - 1.25f + (maxPickup - minPickup + 0.5f) / 2);
                        carrier.ModForceForm(true);
                        carrier.ModForceForm(false);
                        carrier.Reposition(pickupCenter - carrier.Position, carrier.NodeRef);
                        carrier.PrevPosition = carrier.Position - Vector3.UnitX * 0.1f;
                        flag.Process();
                        Check(flag.Carrier == carrier, "slot 4 picks up Bounty flag");
                        if (flag.Carrier == carrier)
                        {
                            int before = GameState.Points[4];
                            Vector3 center = VolumeCenter(CollisionVolume.Move(flagBase.Data.Volume, flagBase.Position));
                            carrier.Reposition(center - carrier.Position, carrier.NodeRef);
                            flagBase.Process();
                            GameState.UpdateState();
                            Check(GameState.Points[4] == before + 1 && GameState.TeamPoints[3] == before + 1,
                                "Bounty delivery scores for team D");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex.Message);
                Console.WriteLine($"MAPFAIL {_room} | teamprobe: {ex}");
            }
            Console.WriteLine($"TEAMPROBE {_room} | {GameState.Mode} | {checks} checks | {failures.Count} failures");
            return failures;
        }
    }
}
