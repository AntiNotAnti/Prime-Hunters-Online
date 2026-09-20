using System;
using MphRead.Mods.Network;

static class Program
{
    private static int _checks;

    private static void Expect(FormCorrection actual, FormCorrection wanted, string scenario, uint frame)
    {
        _checks++;
        if (actual != wanted)
        {
            throw new Exception($"{scenario} frame {frame}: expected {wanted}, got {actual}");
        }
    }

    private static int Main()
    {
        // A relayed press can lead the authority's snapshot for an entire
        // 300 ms round trip. Exercise both transition directions at 60 Hz.
        foreach (int ping in new[] { 150, 300 })
        {
            var morph = new FormReconciliation();
            for (uint frame = 0; frame <= 50; frame++)
            {
                bool animating = frame < 20;
                bool actualAlt = !animating;
                bool stale = frame < 20 + (uint)(ping * 60 / 1000);
                Expect(morph.Step(frame, !stale, actualAlt, animating, false, ping),
                    FormCorrection.None, $"morph at {ping} ms", frame);
            }

            var unmorph = new FormReconciliation();
            for (uint frame = 0; frame <= 50; frame++)
            {
                bool animating = frame < 14;
                bool stale = frame < 14 + (uint)(ping * 60 / 1000);
                Expect(unmorph.Step(frame, stale, false, false, animating, ping),
                    FormCorrection.None, $"unmorph at {ping} ms", frame);
            }
        }

        var lostPress = new FormReconciliation();
        for (uint frame = 0; frame <= 20; frame++)
        {
            FormCorrection wanted = frame == 8 ? FormCorrection.Start
                : frame == 20 ? FormCorrection.Force : FormCorrection.None;
            Expect(lostPress.Step(frame, true, false, false, false, 0),
                wanted, "lost press", frame);
            if (frame == 4)
            {
                Expect(lostPress.Step(frame, true, false, false, false, 0),
                    FormCorrection.None, "repeated call", frame);
            }
        }

        var recovering = new FormReconciliation();
        for (uint frame = 0; frame < 30; frame++)
        {
            bool started = frame >= 9;
            bool finished = frame >= 25;
            Expect(recovering.Step(frame, true, finished, started && !finished, false, 0),
                frame == 8 ? FormCorrection.Start : FormCorrection.None,
                "successful recovery", frame);
        }

        var stalled = new FormReconciliation();
        for (uint frame = 0; frame <= 90; frame++)
        {
            Expect(stalled.Step(frame, true, false, true, false, 300),
                frame == 90 ? FormCorrection.Force : FormCorrection.None,
                "stalled transition", frame);
        }

        // ExitAltForm already changes IsAltForm to false. The stuck
        // Unmorphing flag must still reach ModForceForm at the ceiling.
        var stalledUnmorph = new FormReconciliation();
        for (uint frame = 0; frame <= 90; frame++)
        {
            Expect(stalledUnmorph.Step(frame, false, false, false, true, 300),
                frame == 90 ? FormCorrection.Force : FormCorrection.None,
                "stalled unmorph with matching form bit", frame);
        }
        Expect(stalledUnmorph.Step(91, false, false, false, false, 300),
            FormCorrection.None, "completed stalled unmorph", 91);

        var longAnimation = new FormReconciliation();
        for (uint frame = 0; frame <= 90; frame++)
        {
            bool animating = frame < 70;
            Expect(longAnimation.Step(frame, true, !animating, animating, false, 300),
                FormCorrection.None, "70-frame morph animation", frame);
        }

        var staleAgain = new FormReconciliation();
        for (uint frame = 0; frame <= 66; frame++)
        {
            bool animating = frame < 20;
            bool actualAlt = !animating;
            bool desiredAlt = frame >= 38 && frame <= 40;
            FormCorrection wanted = frame == 54 ? FormCorrection.Start
                : frame == 66 ? FormCorrection.Force : FormCorrection.None;
            Expect(staleAgain.Step(frame, desiredAlt, actualAlt, animating, false, 300),
                wanted, "stale form after agreement", frame);
        }

        // A death/cancel can return a player to biped without an observed
        // unmorph animation. The next morph is a NEW animation, not the old
        // transition continuing through all the intervening settled frames.
        var restartedMorph = new FormReconciliation();
        Expect(restartedMorph.Step(0, true, false, true, false, 0),
            FormCorrection.None, "first morph", 0);
        Expect(restartedMorph.Step(20, false, false, false, false, 0),
            FormCorrection.None, "settled after cancelled morph", 20);
        for (uint frame = 200; frame <= 290; frame++)
        {
            Expect(restartedMorph.Step(frame, true, false, true, false, 0),
                frame == 290 ? FormCorrection.Force : FormCorrection.None,
                "new morph gets its own transition timeout", frame);
        }

        var reset = new FormReconciliation();
        for (uint frame = 0; frame < 7; frame++)
        {
            reset.Step(frame, true, false, false, false, 0);
        }
        reset.Reset(); // Reset, ForgetSlot and room change all clear this state.
        for (uint frame = 7; frame < 15; frame++)
        {
            Expect(reset.Step(frame, true, false, false, false, 0),
                FormCorrection.None, "reset", frame);
        }
        Expect(reset.Step(15, true, false, false, false, 0),
            FormCorrection.Start, "reset", 15);

        Console.WriteLine($"form reconciliation: {_checks} checks passed");
        return 0;
    }
}
