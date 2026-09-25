#!/usr/bin/env python3
"""Benchmark isolated authoritative Project Prime server processes on Linux.

Requires a published dedicated-server binary and configured game files in the
working directory (paths.txt). No clients are needed: each continuous server
runs its real 60 Hz authoritative simulation. The harness never downloads data.
"""
import argparse
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import tempfile
import time

STEP_RE = re.compile(r"(\d+) step\(s\), ([\d.]+) ms mean, ([\d.]+) ms worst, "
                     r"(\d+) overrun, (\d+) dropped, (\d+) stall\(s\)")


def read_proc(pid):
    try:
        stat = Path(f"/proc/{pid}/stat").read_text().split()
        status = Path(f"/proc/{pid}/status").read_text()
        rss = re.search(r"^VmRSS:\s+(\d+)\s+kB", status, re.M)
        return int(stat[13]) + int(stat[14]), int(rss.group(1)) if rss else 0
    except (OSError, ValueError, IndexError):
        return None


def summarize(values):
    if not values:
        return dict(count=0, mean=None, maximum=None)
    return dict(count=len(values), mean=sum(values) / len(values), maximum=max(values))


def arm(args, count, root):
    folder = root / f"{count}-servers"
    folder.mkdir(parents=True, exist_ok=True)
    rotation = folder / "rotation.txt"
    rotation.write_text(f"{args.room} | Battle | 0 | 999\n")
    env = dict(os.environ)
    processes, logs = [], []
    try:
        for i in range(count):
            log = open(folder / f"server-{i}.log", "w", encoding="utf-8")
            logs.append(log)
            cmd = [str(args.binary), "-server", "-port", str(args.base_port + i),
                   "-players", "8", "-rotation", str(rotation), "-nomaster",
                   "-noupdate", "-noautoupdate", "-noserverreplays", "-netdebug"]
            processes.append(subprocess.Popen(cmd, cwd=args.workdir, env=env,
                stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT))
        deadline = time.monotonic() + args.startup_timeout
        while time.monotonic() < deadline:
            if any(p.poll() is not None for p in processes):
                raise RuntimeError("a server exited during startup; inspect arm logs")
            ready = 0
            for path in folder.glob("server-*.log"):
                if "listening on UDP" in path.read_text(errors="replace"):
                    ready += 1
            if ready == count:
                break
            time.sleep(.1)
        else:
            raise RuntimeError("server startup deadline exceeded")

        ticks = os.sysconf(os.sysconf_names["SC_CLK_TCK"])
        samples = [[] for _ in processes]
        rss = [[] for _ in processes]
        previous = [read_proc(p.pid) for p in processes]
        previous_at = time.monotonic()
        end = previous_at + args.seconds
        while time.monotonic() < end:
            time.sleep(args.sample_interval)
            now = time.monotonic()
            elapsed = max(.001, now - previous_at)
            for i, p in enumerate(processes):
                current = read_proc(p.pid)
                if current is None or previous[i] is None:
                    continue
                cpu = max(0, current[0] - previous[i][0]) / ticks / elapsed * 100
                samples[i].append(cpu)
                rss[i].append(current[1] / 1024.0)
                previous[i] = current
            previous_at = now

        per_process = []
        for i, p in enumerate(processes):
            log_text = (folder / f"server-{i}.log").read_text(errors="replace")
            steps = STEP_RE.findall(log_text)
            per_process.append(dict(index=i, pid=p.pid, cpuPercent=summarize(samples[i]),
                rssMiB=summarize(rss[i]), lastServerStep=(dict(
                    steps=int(steps[-1][0]), meanMilliseconds=float(steps[-1][1]),
                    worstMilliseconds=float(steps[-1][2]), overruns=int(steps[-1][3]),
                    droppedTicks=int(steps[-1][4]), stalls=int(steps[-1][5])) if steps else None)))
        aggregate_cpu = [sum(row) for row in zip(*samples)] if samples and all(samples) else []
        aggregate_rss = [sum(row) for row in zip(*rss)] if rss and all(rss) else []
        result = dict(instances=count, seconds=args.seconds,
            aggregateCpuPercent=summarize(aggregate_cpu),
            aggregateRssMiB=summarize(aggregate_rss), processes=per_process,
            host=dict(cpuCount=os.cpu_count(), platform=os.uname().sysname + " " + os.uname().release))
        (folder / "summary.json").write_text(json.dumps(result, indent=2) + "\n")
        print(json.dumps(result), flush=True)
        return result
    finally:
        for p in processes:
            if p.poll() is None:
                p.send_signal(signal.SIGINT)
        for p in processes:
            try: p.wait(timeout=8)
            except subprocess.TimeoutExpired:
                p.kill(); p.wait()
        for log in logs:
            log.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--workdir", type=Path, required=True,
                        help="server directory containing paths.txt and game configuration")
    parser.add_argument("--room", default="MP1 SANCTORUS")
    parser.add_argument("--instances", default="1,2,4,8")
    parser.add_argument("--seconds", type=int, default=120)
    parser.add_argument("--sample-interval", type=float, default=1)
    parser.add_argument("--startup-timeout", type=int, default=60)
    parser.add_argument("--base-port", type=int, default=29000)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.binary = args.binary.resolve(); args.workdir = args.workdir.resolve(); args.output = args.output.resolve()
    if not Path("/proc").is_dir():
        parser.error("this benchmark currently requires Linux /proc")
    if not args.binary.is_file() or not os.access(args.binary, os.X_OK):
        parser.error("binary must be an executable published dedicated server")
    if not (args.workdir / "paths.txt").is_file():
        parser.error("workdir must contain configured paths.txt")
    counts = sorted({int(v) for v in args.instances.split(",") if v.strip()})
    if not counts or counts[0] < 1 or args.seconds < 1 or args.sample_interval <= 0:
        parser.error("instances/seconds/sample-interval must be positive")
    args.output.mkdir(parents=True, exist_ok=True)
    results = [arm(args, count, args.output) for count in counts]
    (args.output / "capacity-summary.json").write_text(json.dumps(
        dict(room=args.room, arms=results), indent=2) + "\n")


if __name__ == "__main__":
    main()
