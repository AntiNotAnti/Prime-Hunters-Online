#!/usr/bin/env python3
"""Pull the numbers a headshot A/B turns on out of a run's logs.

One row per run directory. The columns are chosen so that a run which did not
happen cannot be read as a run that went well: `triggers` and `onTarget` say
whether the sniper ever had a shot, and every rate is printed with its
denominator rather than on its own.

    summarise.py <run-dir> [run-dir...]
"""
import re
import sys
from pathlib import Path

# The client's own report lines.
RE_PRED = re.compile(
    r"hit prediction: (\d+) predicted, (\d+) confirmed \(([\d.]+)%\), (\d+) denied, (\d+) unpredicted")
RE_HEAD = re.compile(
    r"headshots: (\d+) predicted(?:, (\d+) agreed by the authority \(([\d.]+)%\), (\d+) downgraded)?")
RE_RIG_SNIPER = re.compile(
    r"hit rig: (\w+) as sniper, (\d+) triggers, (\d+) frames on target, mean range ([\d.]+)")
RE_RIG_RUNNER = re.compile(
    r"hit rig: (\w+) as runner, (\d+) frames airborne, mean \|vertical speed\| ([\d.]+) units/frame, worst ([\d.]+)")
RE_PINGS = re.compile(r"pings: (.+)")
# The server's, which is the only place the rewind is measured at all.
RE_LAG = re.compile(
    r"lag compensation: (\d+) shots rewound, mean ([\d.]+) frames \((\d+) ms\), worst (\d+), "
    r"catch-up (\d+) steps / (\d+) hits, history misses (\d+); "
    r"ceiling (\d+) frames \(\d+ ms\), clamped (\d+)")
RE_CLAMP_PCT = re.compile(r"clamped \d+ \(([\d.]+)% of shots, mean ([\d.]+) frames refused\)")
RE_STALE = re.compile(r"stale presses (\d+)")
RE_DEPTHS = re.compile(r"rewind depths asked \(frames: shots\):(.*)")


def last(pattern, text):
    found = pattern.findall(text)
    return found[-1] if found else None


def read(path):
    try:
        return path.read_text(errors="replace")
    except OSError:
        return ""


def summarise(run):
    run = Path(run)
    out = {"run": run.name}
    server = read(run / "server.log")
    if not server:
        # A run against a remote server keeps its authority's log elsewhere;
        # the caller drops it in beside the clients under this name.
        server = read(run / "authority.log")
    lag = last(RE_LAG, server)
    if lag:
        (shots, mean_f, mean_ms, worst, steps, hits, misses, ceiling, clamped) = lag
        out.update(shots=int(shots), meanFrames=float(mean_f), meanMs=int(mean_ms),
                   worst=int(worst), catchUpHits=int(hits), historyMisses=int(misses),
                   ceiling=int(ceiling), clamped=int(clamped))
        pct = last(RE_CLAMP_PCT, server)
        out["clampedPct"] = float(pct[0]) if pct else 0.0
        out["framesRefused"] = float(pct[1]) if pct else 0.0
        stale = last(RE_STALE, server)
        out["stalePresses"] = int(stale) if stale else None
        depths = last(RE_DEPTHS, server)
        out["depths"] = depths.strip() if depths else ""

    for log in sorted(run.glob("*.log")):
        if log.name in ("server.log", "authority.log"):
            continue
        text = read(log)
        rig = last(RE_RIG_SNIPER, text)
        if rig:
            out.update(mode=rig[0], triggers=int(rig[1]),
                       onTarget=int(rig[2]), meanRange=float(rig[3]))
            pred = last(RE_PRED, text)
            if pred:
                out.update(predicted=int(pred[0]), confirmed=int(pred[1]),
                           confirmedPct=float(pred[2]), denied=int(pred[3]),
                           unpredicted=int(pred[4]))
                # What share of the hits the authority credited this client
                # with, it had already shown for itself. `conf%` cannot say
                # this: it is confirmed over predicted, so a client that
                # predicts one hit and gets it right reads 100% while missing
                # sixty-nine others.
                landed = int(pred[1]) + int(pred[4])
                out["localShare"] = int(pred[1]) * 100.0 / landed if landed else None
            head = last(RE_HEAD, text)
            if head:
                out["headPredicted"] = int(head[0])
                out["headAgreed"] = int(head[1]) if head[1] else 0
                out["headAgreedPct"] = float(head[2]) if head[2] else None
                out["headDowngraded"] = int(head[3]) if head[3] else 0
            pings = last(RE_PINGS, text)
            if pings:
                out["pings"] = pings.strip()
        runner = last(RE_RIG_RUNNER, text)
        if runner:
            out.update(airborne=int(runner[1]), meanRise=float(runner[2]),
                       worstRise=float(runner[3]))
    return out


COLUMNS = [
    ("run", "run", "{}"),
    ("mode", "mode", "{}"),
    ("ceiling", "ceil", "{}"),
    ("shots", "rewound", "{}"),
    ("meanMs", "meanRw", "{} ms"),
    ("worst", "worst", "{}"),
    ("clamped", "clamped", "{}"),
    ("clampedPct", "clamp%", "{:.1f}"),
    ("framesRefused", "refused", "{:.1f}f"),
    ("triggers", "trig", "{}"),
    ("meanRange", "range", "{:.1f}"),
    ("worstRise", "worstY", "{:.3f}"),
    ("predicted", "pred", "{}"),
    ("confirmedPct", "conf%", "{:.1f}"),
    # The opposite error, and the one the client pin is aimed at: a hit the
    # authority credited that this machine never resolved for itself. Every one
    # of these is a shot whose flinch, mark and kill waited a round trip.
    ("unpredicted", "unpred", "{}"),
    ("localShare", "local%", "{:.1f}"),
    ("headPredicted", "hsPred", "{}"),
    ("headAgreed", "hsOk", "{}"),
    ("headDowngraded", "hsDown", "{}"),
    ("headAgreedPct", "hs%", "{:.1f}"),
]


def main(argv):
    rows = [summarise(a) for a in argv]
    widths = {}
    cells = []
    for row in rows:
        cell = {}
        for key, head, fmt in COLUMNS:
            value = row.get(key)
            cell[key] = "-" if value is None else fmt.format(value)
        cells.append(cell)
    for key, head, _ in COLUMNS:
        widths[key] = max(len(head), *(len(c[key]) for c in cells)) if cells else len(head)
    print("  ".join(head.rjust(widths[key]) for key, head, _ in COLUMNS))
    for cell in cells:
        print("  ".join(cell[key].rjust(widths[key]) for key, _, _ in COLUMNS))
    for row, cell in zip(rows, cells):
        if row.get("depths"):
            print(f"\n{row['run']} depths asked: {row['depths']}")
        if row.get("pings"):
            print(f"{row['run']} pings: {row['pings']}")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        raise SystemExit(2)
    main(sys.argv[1:])
