using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.NetTest
{
    internal static partial class LifecycleTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static object? MovementCall(Type type, string name, object? instance, params object[] args)
            => type.GetMethod(name, instance == null ? PrivateStatic : PrivateInstance)!.Invoke(instance, args);

        private static T MovementField<T>(Type type, string name)
            => (T)type.GetField(name, PrivateStatic)!.GetValue(null)!;

        private static void MovementFrame(uint frame)
            => typeof(NetSession).GetProperty(nameof(NetSession.NetFrame))!.SetValue(null, frame);

        private static PlayerEntity MovementPlayer()
        {
            var player = Player(0, 50);
            foreach (string field in new[] { "_networkPositionHistory", "_networkSpeedHistory" })
                typeof(PlayerEntity).GetField(field, PrivateInstance)!.SetValue(player, new Vector3[120]);
            typeof(PlayerEntity).GetField("_networkAltHistory", PrivateInstance)!.SetValue(player, new bool[120]);
            typeof(PlayerEntity).GetField("_networkPositionFrames", PrivateInstance)!.SetValue(player, new uint[120]);
            typeof(EntityBase).GetField("_scene", PrivateInstance)!.SetValue(player,
                RuntimeHelpers.GetUninitializedObject(typeof(Scene)));
            player.NodeRef = MphRead.Formats.Culling.NodeRef.None;
            return player;
        }

        private static void RecordMovement(PlayerEntity player, uint frame)
            => MovementCall(typeof(PlayerEntity), "ModRecordNetworkPosition", player, frame);

        private static bool PredictionAt(PlayerEntity player, uint frame, out Vector3 position, out Vector3 speed)
        {
            object[] args = { frame, Vector3.Zero, Vector3.Zero, false };
            bool found = (bool)MovementCall(typeof(PlayerEntity), "ModGetNetworkPrediction", player, args)!;
            position = (Vector3)args[1]; speed = (Vector3)args[2];
            return found;
        }

        private static void Movement()
        {
            MovementHistory();
            MovementCorrections();
            MovementAcknowledgements();
            MovementCommandTransport();
            MovementStateWire();
            MovementInputValidation();
            MovementControls();
        }

        private static void MovementHistory()
        {
            var player = MovementPlayer();
            for (uint frame = 1; frame <= 150; frame++)
            {
                player.Position = new Vector3(frame, 0, 0);
                player.Speed = new Vector3(frame / 100f, 0, 0);
                RecordMovement(player, frame);
            }
            Check(!PredictionAt(player, 30, out _, out _)
                && PredictionAt(player, 31, out var position, out _) && position.X == 31,
                "movement history evicts only entries outside its capacity");
            player.Position = new Vector3(999, 0, 0);
            RecordMovement(player, 150);
            Check(PredictionAt(player, 150, out position, out var speed) && position.X == 999
                && speed.X == 1.5f && PredictionAt(player, 31, out _, out _),
                "recording a corrected frame replaces it without evicting another input");
            MovementCall(typeof(PlayerEntity), "ModResetNetworkHistory", player);
            Check(!PredictionAt(player, 150, out _, out _), "respawn discards prediction history");
            foreach (uint frame in new[] { uint.MaxValue - 1, uint.MaxValue, 0u, 2u })
            {
                player.Position = new Vector3(frame == uint.MaxValue ? 10 : 20, 0, 0);
                RecordMovement(player, frame);
            }
            object[] args = { uint.MaxValue, Vector3.Zero };
            Check((bool)MovementCall(typeof(PlayerEntity), "ModGetNetworkPosition", player, args)!
                && ((Vector3)args[1]).X == 10, "historical position lookup handles frame wrap");
            Check(!PredictionAt(player, 1, out _, out _)
                && PredictionAt(player, 0, out _, out _), "prediction lookup requires the exact input frame");
        }

        private static void MovementCorrections()
        {
            Session(local: true);
            NetPlayerBridge.Reset();
            var player = MovementPlayer();
            var state = State(1); state.SlotIndex = 0;
            player.Position = state.Position;
            RecordMovement(player, 10);
            RecordMovement(player, 11);
            MovementFrame(20);
            NetSession.RemoteInputFrames[0] = 10;
            NetSession.RemoteInputPositions[0] = state.Position;
            NetSession.RemoteInputSpeeds[0] = Vector3.UnitX;
            void Reconcile() => MovementCall(typeof(NetPlayerBridge), "ReconcileLocalMovement",
                null, player, state, 0, player.Speed);
            Reconcile();
            Check(player.Speed == Vector3.UnitX, "matched movement error corrects velocity once");
            NetSession.RemoteInputFrames[0] = 11;
            Reconcile();
            Check(player.Speed == Vector3.UnitX && NetPlayerBridge.PredictionSnaps == 0,
                "successive pre-correction acks cannot multiply the same impulse");

            NetPlayerBridge.Reset();
            player.Speed = Vector3.Zero;
            RecordMovement(player, 30);
            MovementFrame(40);
            NetSession.RemoteInputFrames[0] = 30;
            NetSession.RemoteInputPositions[0] = state.Position + new Vector3(5, 0, 0);
            NetSession.RemoteInputSpeeds[0] = Vector3.Zero;
            state.Position = new Vector3(15, 4, 3);
            state.Speed = new Vector3(.2f, .3f, .4f);
            state.Flags |= PlayerState.FlagAltForm;
            Vector3 before = player.Position;
            Reconcile(); Reconcile();
            Check(player.Position == before && NetPlayerBridge.PredictionSnaps == 1,
                "hard correction stays queued until the next collision pass and duplicate ack is ignored");
            MovementCall(typeof(NetPlayerBridge), "ApplyPendingLocalCorrection", null, player);
            Check(player.Position == NetPlayerBridge.InFormFor(player, state.Position, true)
                && player.Speed == state.Speed && player.PrevPosition == player.Position,
                "queued correction converts form coordinates and adopts matching velocity and sweep origin");
            Check(!PredictionAt(player, 30, out _, out _), "hard correction invalidates outstanding predictions");

            NetPlayerBridge.Reset();
            NetSession.RemoteInputFrames[0] = 35;
            Reconcile(); Reconcile();
            Check(NetPlayerBridge.PredictionHistoryMisses == 1 && NetPlayerBridge.PredictionSnaps == 1,
                "expired prediction history queues one authority recovery");
            player.Health = 0;
            MovementCall(typeof(NetPlayerBridge), "ApplyPendingLocalCorrection", null, player);
            Check(!MovementField<bool[]>(typeof(NetPlayerBridge), "_pendingLocalCorrection")[0],
                "death discards a queued movement correction");

            NetPlayerBridge.Reset();
            NetSession.RemoteInputFrames[0] = 1000;
            Reconcile();
            Check(NetPlayerBridge.PredictionSnaps == 0 && NetPlayerBridge.PredictionHistoryMisses == 0,
                "future input acknowledgement cannot force a correction");
        }

        private static void MovementAcknowledgements()
        {
            Session(local: true);
            var player = MovementPlayer();
            MovementCall(typeof(NetSession), "MarkMovementSimulated", null,
                0, 10u, Vector3.Zero, Vector3.Zero, false);
            player.Position = new Vector3(15, 2, 3); player.Speed = Vector3.UnitY;
            MovementCall(typeof(NetSession), "CompleteMovementSimulation", null, player);
            Check(MovementField<Vector3[]>(typeof(NetSession), "_simulatedInputPositions")[0] == player.Position
                && MovementField<Vector3[]>(typeof(NetSession), "_simulatedInputSpeeds")[0] == player.Speed,
                "authority ack includes impulses and teleports after the movement pass");
            MovementCall(typeof(NetSession), "MarkMovementSimulated", null,
                0, 10u, Vector3.One, Vector3.One, true);
            player.Position = Vector3.One;
            MovementCall(typeof(NetSession), "CompleteMovementSimulation", null, player);
            Check(MovementField<Vector3[]>(typeof(NetSession), "_simulatedInputPositions")[0].X == 15,
                "held input retains the result of its first authoritative tick");
            MovementCall(typeof(NetSession), "MarkMovementSimulated", null,
                0, 11u, Vector3.Zero, Vector3.Zero, false);
            NetSession.ForgetSlot(0);
            MovementCall(typeof(NetSession), "CompleteMovementSimulation", null, player);
            Check(MovementField<uint[]>(typeof(NetSession), "_simulatedInputFrames")[0] == 0
                && MovementField<Vector3[]>(typeof(NetSession), "_simulatedInputPositions")[0] == Vector3.Zero,
                "slot reuse discards an unfinished movement capture");

            typeof(NetHooks).GetField("_localIntentPending", PrivateStatic)!.SetValue(null, true);
            typeof(NetHooks).GetField("_sampledLocalIntent", PrivateStatic)!.SetValue(null,
                new IntentPacket { Aim = Vector3.UnitZ, Buttons = IntentButtons.MoveUp, ChargeLevel = 12 });
            typeof(PlayerEntity).GetField("_gunVec1", PrivateInstance)!.SetValue(player, Vector3.UnitX);
            NetHooks.AfterRemoteMovement(player);
            var intent = MovementField<IntentPacket>(typeof(NetHooks), "_sampledLocalIntent");
            Check(intent.Aim == Vector3.UnitX && intent.Buttons == IntentButtons.MoveUp && intent.ChargeLevel == 12,
                "outgoing input uses the simulated aim while preserving pre-simulation controls and charge");
            typeof(NetHooks).GetField("_localIntentPending", PrivateStatic)!.SetValue(null, false);
        }

        private static void MovementInputValidation()
        {
            Session(); Deliver(Packet(1, State(1)));
            var intent = new IntentPacket { MatchId = 51, AuthorityEpoch = 4,
                SlotGeneration = 10, LifeId = 1, Frame = 100, Aim = new Vector3(float.PositiveInfinity, 0, 0) };
            NetSession.AcceptSlotIntent(1, intent);
            Check(!NetSession.RemoteIntentValid[1], "non-finite aim cannot reach authoritative movement");
            intent.Frame = 2; intent.Aim = Vector3.UnitZ;
            NetSession.AcceptSlotIntent(1, intent);
            Check(NetSession.RemoteIntentValid[1] && NetSession.RemoteIntents[1].Frame == 2,
                "invalid aim cannot poison input sequence ordering");
        }

        private static void MovementControls()
        {
            Session(local: true);
            var movement = typeof(NetHooks).Assembly.GetType("MphRead.Mods.Network.NetMovementInput")!;
            MovementCall(movement, "Reset", null);
            MovementFrame(10);
            MovementCall(movement, "RecordBoost", null, MovementPlayer(), Vector2.UnitY);
            MovementFrame(11);
            object[] capture = { new IntentPacket { Frame = 11, Buttons = IntentButtons.InPlayState,
                RollForward = Vector2.UnitX, Aim = Vector3.UnitZ } };
            MovementCall(movement, "CaptureBoost", null, capture);
            var intent = (IntentPacket)capture[0];
            Check(intent.BoostFrame == 10 && intent.BoostDirection == Vector2.UnitY,
                "flick boost survives a lost initial input packet");
            byte[] bytes = new byte[IntentPacket.FullSize]; intent.Write(bytes);
            var read = IntentPacket.Read(bytes);
            Check(read.RollForward == intent.RollForward && read.BoostFrame == 10
                && read.BoostDirection == Vector2.UnitY, "movement controls survive authority input encoding");
            bytes = new byte[ObserverIntentState.Size]; ObserverIntentState.FromIntent(intent).Write(bytes);
            read = ObserverIntentState.Read(bytes).ToIntent(51, 4, default);
            Check(read.RollForward == intent.RollForward && read.BoostFrame == 10
                && read.BoostDirection == Vector2.UnitY, "movement controls survive observer bundle encoding");
            bool Consume(IntentPacket input) => (bool)MovementCall(movement, "ConsumeBoost", null, 1, input)!;
            Check(Consume(intent) && !Consume(intent), "held or duplicate packets cannot repeat a flick boost");
            intent.BoostFrame = 12;
            Check(!Consume(intent), "future boost event is rejected");
            intent.BoostFrame = 10; intent.Frame = 18;
            MovementCall(movement, "ResetSlot", null, 1);
            Check(!Consume(intent), "expired boost event is rejected");
            intent.Frame = 11; intent.RollForward = new Vector2(float.NaN, 0);
            Check(!Consume(intent), "non-finite roll basis is rejected");

            Session(); Deliver(Packet(1, State(1)));
            var player = MovementPlayer();
            typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.SlotIndex))!.SetValue(player, 1);
            var abilities = typeof(PlayerEntity).GetField("_abilities", PrivateInstance)!;
            abilities.SetValue(player, Enum.Parse(abilities.FieldType, "Boost"));
            intent.Frame = 11; intent.RollForward = Vector2.UnitX;
            intent.MatchId = 51; intent.AuthorityEpoch = 4; intent.SlotGeneration = 10; intent.LifeId = 1;
            NetSession.AcceptSlotIntent(1, intent);
            MovementCall(typeof(PlayerEntity), "ModNetworkRollInput", player);
            Check((float)typeof(PlayerEntity).GetField("_altRollFbX", PrivateInstance)!.GetValue(player)! == 1
                && player.SwipeBoostRequested && player.SwipeBoostX == 1 && player.SwipeBoostY == 0,
                "remote roll uses input basis and maps world-space flick to the same direction");
            player.SwipeBoostRequested = false;
            MovementCall(typeof(PlayerEntity), "ModNetworkRollInput", player);
            Check(!player.SwipeBoostRequested, "reusing an intent on another server tick cannot boost twice");
            MovementFrame(NetSession.NetFrame + 31);
            intent.Frame = 12; intent.BoostFrame = 12;
            NetSession.RemoteIntents[1] = intent;
            MovementCall(typeof(PlayerEntity), "ModNetworkRollInput", player);
            Check(!player.SwipeBoostRequested, "stale connection cannot initiate a flick boost");
            intent.Frame = 13; intent.BoostFrame = 13;
            NetSession.AcceptSlotIntent(1, intent);
            typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.Flags1))!.SetValue(player,
                player.Flags1 | PlayerFlags1.Morphing);
            MovementCall(typeof(PlayerEntity), "ModNetworkRollInput", player);
            Check(!player.SwipeBoostRequested, "a morph transition cannot queue a boost for a later tick");
            typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.Flags1))!.SetValue(player,
                player.Flags1 & ~PlayerFlags1.Morphing);
            MovementCall(typeof(PlayerEntity), "ModNetworkRollInput", player);
            Check(player.SwipeBoostRequested, "a still-current boost can run when movement permits it");

            Session();
            NetMatchSync.Apply();
            Check(!NetMatchSync.Synced, "minimal headless match state safely waits for a room name");
        }
    }
}
