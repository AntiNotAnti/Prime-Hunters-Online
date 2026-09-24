using System;
using MphRead.Mods.Network;

int failures = 0;
void Check(bool passed, string name)
{
    Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}");
    if (!passed) failures++;
}

var clocks = new ContinuousWeaponPhase(2);
ulong Remote(ulong scene, uint frame, uint age, int slot = 0)
    => clocks.Resolve(slot, scene, true, false, 9000, true, frame, age, out _);

// A late accepted intent re-anchors the old formula: 100,101,102,102.
// This clock must keep moving through the missing Spawn and packet jitter.
clocks.Observe(0, 1000, true, true);
ulong p100 = Remote(1000, 100, 0);
clocks.Observe(0, 1001, true, true);
ulong p101 = Remote(1001, 100, 1);
clocks.Observe(0, 1002, true, true);
ulong p102 = Remote(1002, 100, 2);
clocks.Observe(0, 1003, true, true);
ulong p103 = Remote(1003, 102, 1); // packet arrived late; never re-anchor
clocks.Observe(0, 1004, true, true);
clocks.Observe(0, 1005, true, true); // no Spawn on these steps
clocks.Observe(0, 1006, true, true);
ulong p106 = Remote(1006, 104, 0);
Check(p100 == 100 && p101 == 101 && p102 == 102 && p103 == 103 && p106 == 106,
    "late and missing intents cannot repeat or skip a held firing phase");

clocks.Observe(0, 1007, false, true); // released trigger
clocks.Observe(0, 1010, true, true);
Check(Remote(1010, 300, 0) == 300, "new stream seeds from its first fresh intent");
clocks.Observe(0, 1011, true, false); // stale intent, even without a Spawn
ulong stale = clocks.Resolve(0, 1011, true, false, 9000, true, 300,
    ContinuousWeaponPhase.MaxIntentAge + 1, out bool staleShared);
clocks.Observe(0, 1012, true, true);
Check(stale == 1011 && !staleShared && Remote(1012, 310, 0) == 310,
    "stale fallback and freshness recovery start a new stream");

clocks.ResetSlot(0);
Check(Remote(1020, 500, 0) == 500, "slot reuse forgets the previous occupant's phase");
clocks.Reset();
Check(Remote(1030, 700, 0) == 700, "room/session reset forgets every phase");

clocks.Observe(0, 1031, false, true);
ulong offline = clocks.Resolve(0, 91, false, true, 9000, true, 400, 0,
    out bool offlineShared);
ulong invalid = clocks.Resolve(0, 92, true, false, 9000, false, 400, 0,
    out bool invalidShared);
Check(offline == 91 && !offlineShared && invalid == 92 && !invalidShared,
    "offline and invalid intent paths retain scene timing");

clocks.Reset();
ulong nearWrap = Remote(2000, uint.MaxValue, 0);
clocks.Observe(0, 2001, true, true);
clocks.Observe(0, 2002, true, true);
ulong pastWrap = Remote(2002, 1, 0);
Check(nearWrap == uint.MaxValue && pastWrap == (ulong)uint.MaxValue + 2,
    "held phase advances monotonically through intent frame rollover");

clocks.Reset();
ulong dedicated = clocks.Resolve(0, 2500, true, false, 9000, true, 401, 1, out _, receivedBeforeStep: true);
Check(dedicated == 401, "dedicated pre-step arrival does not shift source phase by one tick");
clocks.Reset();
int[] positions = new int[32];
for (uint step = 0; step < 64; step++)
{
    clocks.Observe(0, 3000 + step, true, true);
    ulong phase = Remote(3000 + step, 401 + step - step % 2, step % 2);
    if (phase % 2 == 0) positions[(int)((phase / 2) & 31)]++;
}
bool completeCycle = true;
foreach (int visits in positions) completeCycle &= visits == 1;
Check(completeCycle, "long-run dither visits every 30 Hz position once per 64 steps");
return failures == 0 ? 0 : 1;
