#!/usr/bin/env python3
"""Real 60 Hz continuous-target client/server matrix, with isolated user-data/logs.

Requires a staged runtime and the operator's paths.txt. This is headless evidence,
not a rendering test. Only child processes created by this runner are terminated.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import time

PROFILES = [(f"rtt{rtt}-loss{loss}", f"{rtt}:{jitter}", f"{loss}%", "3%")
            for rtt, jitter in [(0, 0), (100, 20), (250, 40), (320, 80), (400, 80)]
            for loss in [0, 1, 2, 5]]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--dotnet", default=str(Path.home() / ".dotnet/dotnet"))
    parser.add_argument("--seconds", type=int, default=40)
    parser.add_argument("--modes", default="shockcoil")
    parser.add_argument("--profiles", default="all")
    parser.add_argument("--players", type=int, default=2, choices=[2, 4, 8])
    parser.add_argument("--hunter", default="Samus")
    parser.add_argument("--telemetry", choices=["off", "aggregate", "study"], default="off")
    parser.add_argument("--map", default="MP1 SANCTORUS")
    parser.add_argument("--mapdir", type=Path, help="Optional prebuilt custom map catalog; stock rooms use an empty catalog")
    args = parser.parse_args()
    runtime, data, output = args.runtime.resolve(), args.data.resolve(), args.output.resolve()
    if not (runtime / "ProjectPrime.dll").is_file() or not (data / "paths.txt").is_file():
        parser.error("runtime must contain ProjectPrime.dll and data must contain paths.txt")
    output.mkdir(parents=True, exist_ok=True)
    mapdir = args.mapdir.resolve() if args.mapdir else output / "empty-maps"
    mapdir.mkdir(parents=True, exist_ok=True)
    results = []
    for mode in args.modes.split(","):
        for name, lag, loss, reorder in PROFILES:
            if args.profiles != "all" and name not in args.profiles.split(","):
                continue
            folder = output / f"{mode}-{args.hunter}-{args.players}p-{name}"
            folder.mkdir(parents=True, exist_ok=True)
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
                probe.bind(("127.0.0.1", 0))
                port = str(probe.getsockname()[1])
            children, logs = [], []
            def launch(role, options):
                user = folder / role
                user.mkdir(exist_ok=True)
                shutil.copy2(data / "paths.txt", user / "paths.txt")
                (user / "maprotation.txt").write_text(f"{args.map} | Battle | 15 | 99\n")
                env = dict(os.environ, PROJECT_PRIME_USER_DATA=str(user), ALSOFT_DRIVERS="null", PRIME_CONTINUOUS_TRACE=str(folder / f"{role}-targets.jsonl"))
                if role == "server":
                    config = user / "telemetry-config.json"
                    config.write_text(json.dumps({"enabled": args.telemetry != "off", "detail": args.telemetry.title(), "directory": str(user / "telemetry")}))
                    env["PRIME_TELEMETRY_CONFIG"] = str(config)
                log = open(folder / f"{role}.log", "w")
                logs.append(log)
                child = subprocess.Popen([args.dotnet, str(runtime / "ProjectPrime.dll"), "-mapdir", str(mapdir), *options],
                                         cwd=runtime, env=env, stdout=log, stderr=subprocess.STDOUT)
                children.append(child)
                return child
            try:
                server = launch("server", ["-server", "-port", port, "-players", str(args.players), "-nomaster",
                                            "-serverreplays", "off", "-debuglog"])
                deadline = time.monotonic() + 45
                while "this server runs the match itself" not in (folder / "server.log").read_text():
                    if server.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError(f"server failed to start; see {folder / 'server.log'}")
                    time.sleep(.1)
                peers = []
                for role in [f"peer{i}" for i in range(args.players)]:
                    peers.append(launch(role, ["-netcheck", "127.0.0.1", "-port", port, "-name", role,
                        "-hunter", args.hunter, "-seconds", str(args.seconds), "-nographics", "-hitrig", mode,
                        "-netlag", lag, "-netloss", loss, "-netreorder", reorder, "-netduplicate", "1%", "-netseed", "431", "-debuglog"]))
                    time.sleep(.4)
                codes = [peer.wait(timeout=args.seconds + 90) for peer in peers]
                def last_line(path, prefix):
                    if not path.is_file():
                        return None
                    return next((line for line in reversed(path.read_text().splitlines()) if line.startswith(prefix)), None)
                result = dict(mode=mode, telemetry=args.telemetry, players=args.players, hunter=args.hunter, profile=name, latency=lag, loss=loss,
                              reorder=reorder, client_exit_codes=codes,
                              authority_target=last_line(folder / "server/netlog-server.txt", "continuous target:"),
                              client_reports={role: {
                                  "simulation": last_line(folder / f"{role}.log", "[netchecksim] steps="),
                                  "scenario": last_line(folder / f"{role}.log", "[netchecksim] altScenarioExercised="),
                                  "rig": last_line(folder / f"{role}.log", "hit rig:"),
                                  "target": last_line(folder / f"{role}.log", "continuous target:"),
                                  "hits": last_line(folder / f"{role}.log", "hit prediction:"),
                                  "hitreg": last_line(folder / role / f"netlog-{role}.txt", "           hitreg ")}
                                  for role in [f"peer{i}" for i in range(args.players)]})
                results.append(result)
                (output / "summary.json").write_text(json.dumps(results, indent=2) + "\n")
                print(json.dumps(result), flush=True)
            finally:
                for child in reversed(children):
                    if child.poll() is None:
                        child.terminate()
                for child in children:
                    try:
                        child.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        child.kill()
                        child.wait()
                for log in logs:
                    log.close()
    (output / "summary.json").write_text(json.dumps(results, indent=2) + "\n")
    return int(any(any(code != 0 for code in row["client_exit_codes"]) for row in results))


if __name__ == "__main__":
    raise SystemExit(main())
