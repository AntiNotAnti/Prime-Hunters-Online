#!/usr/bin/env python3
import json
import pathlib
import sys

if len(sys.argv) < 3:
    raise SystemExit("usage: compare-perf.py BASELINE.json CURRENT.json [--no-fail]")

baseline_path = pathlib.Path(sys.argv[1])
current_path = pathlib.Path(sys.argv[2])
no_fail = "--no-fail" in sys.argv[3:]

baseline = json.loads(baseline_path.read_text(encoding="utf-8"))
current = json.loads(current_path.read_text(encoding="utf-8"))

identity = ("Room", "Players", "Width", "Height", "RenderScale", "PresentationHz")
mismatch = [key for key in identity if baseline.get(key) != current.get(key)]
if mismatch:
    print("PERF COMPARE REFUSED: workload identity differs: " + ", ".join(mismatch))
    for key in mismatch:
        print(f"  {key}: baseline={baseline.get(key)!r}, current={current.get(key)!r}")
    raise SystemExit(2)

checks = [
    ("DrawAverageMs", 1.10, "lower"),
    ("DrawP95Ms", 1.12, "lower"),
    ("DrawP99Ms", 1.15, "lower"),
    ("DrawP999Ms", 1.20, "lower"),
    ("SimulationP99Ms", 1.15, "lower"),
    ("AllocatedBytesPerDraw", 1.25, "lower"),
    ("OnePercentLowFps", 0.85, "higher"),
    ("PointOnePercentLowFps", 0.80, "higher"),
]

failed = []
print(f"Performance comparison: {baseline_path.name} -> {current_path.name}")
print(f"Workload: {current.get('Room')} | {current.get('Players')} players | "
      f"{current.get('Width')}x{current.get('Height')} | "
      f"{current.get('PresentationHz')} Hz presentation")
print()
for key, threshold, direction in checks:
    before = float(baseline.get(key, 0) or 0)
    after = float(current.get(key, 0) or 0)
    change = (after / before) if before else (0.0 if after == 0 else float("inf"))
    if direction == "lower":
        bad = before > 0 and change > threshold
        limit = f"+{(threshold - 1) * 100:.0f}%"
    else:
        bad = before > 0 and change < threshold
        limit = f"-{(1 - threshold) * 100:.0f}%"
    delta = "n/a" if before == 0 else f"{(change - 1) * 100:+.1f}%"
    print(f"{key:24} {before:11.3f} -> {after:11.3f}  {delta:>9}  "
          + ("REGRESSION" if bad else "ok"))
    if bad:
        failed.append((key, delta, limit))

gc_before = sum(int(baseline.get(k, 0) or 0) for k in
                ("Gen0Collections", "Gen1Collections", "Gen2Collections"))
gc_after = sum(int(current.get(k, 0) or 0) for k in
               ("Gen0Collections", "Gen1Collections", "Gen2Collections"))
print(f"{'GC collections':24} {gc_before:11d} -> {gc_after:11d}")

if failed:
    print()
    print("Regression thresholds exceeded:")
    for key, delta, limit in failed:
        print(f"  {key}: {delta} (limit {limit})")
    if not no_fail:
        raise SystemExit(1)

print()
print("PASS" if not failed else "WARN")
