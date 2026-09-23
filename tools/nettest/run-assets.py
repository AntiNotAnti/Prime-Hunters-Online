#!/usr/bin/env python3
"""Run real, hidden rendered clients against a local authoritative server.
Requires extracted game files and native OpenGL. Never downloads or copies assets.
Build first. Binaries are staged so subsequent builds cannot change a running arm.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import signal
import subprocess
import tempfile
import time

PROFILES = {
    "lan": [(0, 0, 0, 0)] * 8,
    "moderate": [(100, 20, 1, 0)] * 8,
    "poor": [(250, 40, 2, 0)] * 8,
    "severe": [(320, 80, 2, 1)] * 8,
    "mixed": [(0, 0, 0, 0)] * 2 + [(80, 20, 1, 0)] * 2
        + [(150, 20, 1, 0)] * 2 + [(250, 40, 2, 0), (320, 80, 2, 1)],
}
HUNTERS = ["Samus", "Kanden", "Trace", "Sylux", "Noxus", "Spire", "Weavel", "Samus"]


def observations(out):
    server = (out / "server.log").read_text(errors="replace")
    samples = re.findall(r"(\d+) step\(s\), ([\d.]+) ms mean, ([\d.]+) ms worst, "
        r"(\d+) overrun, (\d+) dropped, (\d+) stall\(s\)", server)
    queue = re.findall(r"queue=\d+/(\d+) drops=(\d+)", server)
    return {
        "lastServerSample": dict(zip(["steps", "meanMilliseconds", "worstMilliseconds", "overruns", "droppedTicks", "stalls"],
            map(float, samples[-1]))) if samples else None,
        "serverQueueHighWater": max((int(q[0]) for q in queue), default=None),
        "serverQueueDrops": max((int(q[1]) for q in queue), default=None),
        "simulationFailureLogged": "[sim] step failed:" in server,
        "peerReportsPassed": sum("RESULT: PASS" in path.read_text(errors="replace") for path in out.glob("peer-*.log")),
    }


def run_arm(args, stage, name):
    out = args.out / name
    out.mkdir(parents=True, exist_ok=True)
    seconds = args.seconds or (300 if name == "lan" else 120)
    command = [args.dotnet, str(stage / "ProjectPrime.dll")]
    environment = dict(os.environ, ALSOFT_DRIVERS="null")
    handles, clients = [], []
    server = None
    started = time.monotonic()
    try:
        server_log = open(out / "server.log", "w", encoding="utf-8")
        handles.append(server_log)
        server = subprocess.Popen(command + ["-server", "-port", str(args.port), "-players", "8",
            "-simulate", "-nomaster", "-noautoupdate", "-noupdate", "-noserverreplays",
            "-rotation", str(stage / "validation-rotation.txt"), "-netdebug"],
            cwd=stage, env=environment, stdin=subprocess.DEVNULL, stdout=server_log, stderr=subprocess.STDOUT)
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            if server.poll() is not None:
                raise RuntimeError(f"{name}: server exited; see {out / 'server.log'}")
            if "listening on UDP" in (out / "server.log").read_text(errors="replace"):
                break
            time.sleep(.1)
        else:
            raise RuntimeError(f"{name}: server startup exceeded 60 seconds")
        time.sleep(2)
        for slot, (rtt, jitter, loss, reorder) in enumerate(PROFILES[name]):
            log = open(out / f"peer-{slot}.log", "w", encoding="utf-8")
            handles.append(log)
            clients.append(subprocess.Popen(command + ["-netcheck", "127.0.0.1", "-port", str(args.port),
                "-name", f"CHECK{slot}", "-hunter", HUNTERS[slot], "-seconds", str(seconds), "-size", "320x180",
                "-noupdate", "-noautoupdate", "-netlag", f"{rtt}:{jitter}", "-netloss", f"{loss}%",
                "-netreorder", f"{reorder}%", "-netseed", str(8128 + slot)],
                cwd=stage, env=environment, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT))
            time.sleep(.25)
        deadline = time.monotonic() + seconds + 90
        while time.monotonic() < deadline and any(p.poll() is None for p in clients):
            time.sleep(.2)
        results = []
        for slot, client in enumerate(clients):
            timed_out = client.poll() is None
            if timed_out:
                client.terminate()
            try:
                code = client.wait(timeout=5)
            except subprocess.TimeoutExpired:
                client.kill()
                code = client.wait()
            results.append({"peer": slot, "exit": code, "timedOut": timed_out})
        summary = {"scenario": name, "seconds": seconds, "elapsedSeconds": time.monotonic() - started,
            "profiles": PROFILES[name], "results": results, "host": platform.platform(),
            "assemblySha256": hashlib.sha256((stage / "ProjectPrime.dll").read_bytes()).hexdigest(),
            "observations": observations(out)}
        (out / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
        print(json.dumps(summary), flush=True)
        return (all(r["exit"] == 0 and not r["timedOut"] for r in results)
            and not summary["observations"]["simulationFailureLogged"]
            and summary["observations"]["peerReportsPassed"] == 8
            and server.poll() is None)
    finally:
        for process in clients:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
        if server is not None and server.poll() is None:
            server.send_signal(signal.SIGINT)
            try:
                server.wait(timeout=10)
            except subprocess.TimeoutExpired:
                server.kill()
                server.wait()
        for handle in handles:
            handle.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-data", type=Path, required=True, help="directory containing configured paths.txt")
    parser.add_argument("--build", type=Path, default=Path(__file__).resolve().parents[2] / "src/MphRead/bin/Release/net10.0")
    parser.add_argument("--dotnet", default=os.environ.get("DOTNET", "dotnet"))
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--scenario", choices=["all", *PROFILES], default="all")
    parser.add_argument("--seconds", type=int, help="override duration; default LAN 300, other arms 120")
    parser.add_argument("--port", type=int, default=27991)
    parser.add_argument("--room", default="MP1 SANCTORUS")
    args = parser.parse_args()
    args.out = args.out.resolve()
    if not (args.game_data / "paths.txt").is_file() or not (args.build / "ProjectPrime.dll").is_file():
        parser.error("configured paths.txt and a built ProjectPrime.dll are required")
    if args.seconds is not None and args.seconds <= 0:
        parser.error("seconds must be positive")
    with tempfile.TemporaryDirectory(prefix="prime-network-assets-") as temporary:
        stage = Path(temporary)
        shutil.copytree(args.build, stage, dirs_exist_ok=True)
        entries = []
        for line in (args.game_data / "paths.txt").read_text().splitlines():
            if "=" in line:
                key, value = line.split("=", 1)
                if value.strip():
                    path = Path(value.strip())
                    if not path.is_absolute():
                        path = (args.game_data / path).resolve()
                    line = key + "=" + str(path)
            entries.append(line)
        (stage / "paths.txt").write_text("\n".join(entries) + "\n")
        (stage / "validation-rotation.txt").write_text(f"{args.room} | Battle | 30 | 999\n")
        results = [run_arm(args, stage, name) for name in (PROFILES if args.scenario == "all" else [args.scenario])]
    return 0 if all(results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
