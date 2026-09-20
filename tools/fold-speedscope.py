"""Fold a speedscope profile into self and inclusive time per frame.

dotnet-trace writes the sampled stacks as an *evented* profile: a stream of
open/close records with a timestamp rather than a list of stacks. A question
like "where do the thirty milliseconds go" needs those folded, and no viewer
is installed on a build box.

    python tools/fold-speedscope.py ui.speedscope.json [-n 30] [-m Skia]
"""
import argparse
import json
import sys
from collections import defaultdict


def fold(path):
    with open(path, "r", encoding="utf-8") as handle:
        doc = json.load(handle)
    names = [f.get("name") or "?" for f in doc["shared"]["frames"]]
    own = defaultdict(float)
    inclusive = defaultdict(float)
    grand = 0.0
    for profile in doc["profiles"]:
        if profile.get("type") == "sampled":
            for stack, weight in zip(profile["samples"], profile["weights"]):
                if not stack:
                    continue
                grand += weight
                own[names[stack[-1]]] += weight
                for frame in set(stack):
                    inclusive[names[frame]] += weight
            continue
        stack = []
        for event in profile.get("events", []):
            at = float(event["at"])
            if event["type"] == "O":
                stack.append([names[event["frame"]], at, 0.0])
                continue
            if not stack:
                continue
            name, opened, children = stack.pop()
            span = at - opened
            own[name] += span - children
            inclusive[name] += span
            if stack:
                stack[-1][2] += span
            else:
                grand += span
    return own, inclusive, grand


def show(title, table, grand, top, match):
    print()
    print(f"--- {title} ---")
    rows = sorted(table.items(), key=lambda kv: -kv[1])
    shown = 0
    for name, value in rows:
        if match and match.lower() not in name.lower():
            continue
        print(f"{value:9.1f} ms  {value / grand * 100:5.1f}%  {name}")
        shown += 1
        if shown >= top:
            break


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("path")
    parser.add_argument("-n", "--top", type=int, default=30)
    parser.add_argument("-m", "--match", default="")
    args = parser.parse_args()
    own, inclusive, grand = fold(args.path)
    if grand <= 0:
        grand = sum(own.values()) or 1.0
    print()
    print(f"total sampled: {grand:,.0f} ms")
    show("self time", own, grand, args.top, args.match)
    show("inclusive time", inclusive, grand, args.top, args.match)
    print()


if __name__ == "__main__":
    sys.exit(main())
