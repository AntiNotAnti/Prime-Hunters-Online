using System;
using MphRead.Mods.Network;
namespace MphRead.NetTest;
internal static class InputEdgeTests
{
    public static int Run()
    {
        try
        {
            foreach (int gap in new[] { 1, 2, 3, 4 })
            foreach (int lost in new[] { 0, 1, 2, 3 })
            {
                var sender = new NetInputEdgeSender(); var receiver = new NetInputEdgeReceiver();
                InputEdgeHistory previous = default; int source = 0, observed = 0;
                for (uint frame = 1; frame <= 1200; frame++)
                {
                    bool press = frame < 1180 && frame % gap == 0;
                    var history = sender.Record(frame, press ? IntentButtons.AltAttack : 0);
                    if (press) source++;
                    if (frame % (lost + 1) == 0)
                    {
                        receiver.Receive(history, frame); receiver.Receive(history, frame);
                        if (frame > 1) receiver.Receive(previous, frame - 1);
                    }
                    if ((receiver.Consume(frame, out _) & IntentButtons.AltAttack) != 0) observed++;
                    previous = history;
                }
                NetArchitectureTests.Check(source == observed, $"same-action multiplicity/loss/reorder/duplicate/wrap gap={gap} lossBurst={lost}: {source}/{observed}");
                receiver.Reset();
                var next = sender.Record(1201, IntentButtons.AltAttack);
                receiver.Receive(next, 1201);
                NetArchitectureTests.Check(receiver.Consume(1201, out _) == IntentButtons.AltAttack, "fresh lifecycle receive window");
            }
            for (int age = 0; age < 8; age++)
            {
                var sender = new NetInputEdgeSender(); var receiver = new NetInputEdgeReceiver();
                var history = sender.Record(100, IntentButtons.Shoot | IntentButtons.Morph | IntentButtons.Jump);
                for (uint n = 1; n <= age; n++) history = sender.Record(100 + n, 0);
                receiver.Receive(history, (uint)(100 + age));
                var actual = receiver.Consume((uint)(100 + age), out int recoveredAge);
                NetArchitectureTests.Check(actual == (IntentButtons.Shoot | IntentButtons.Morph | IntentButtons.Jump)
                    && recoveredAge == age, "simultaneous actions and exact recovered Shoot age");
            }
            NetArchitectureTests.Check(NetInputEdgeTelemetry.Overflow == 0, "ordinary scripted input has no overflow");
            Console.WriteLine("PASS: input sequence wrap, lifecycle reset, rapid identical actions, 0-3 lost packets, duplication, reorder and ages 0-7"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
