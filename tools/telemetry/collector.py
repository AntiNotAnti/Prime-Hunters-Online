#!/usr/bin/env python3
"""Optional bounded aggregate collector. No raw events or player credentials."""
import argparse
import hmac
from http.server import BaseHTTPRequestHandler, HTTPServer
import json
import os
from pathlib import Path
import uuid

MAX_BODY = 1024 * 1024
SUMMARY_KEYS = {"header", "durationSeconds", "counters", "network", "combat", "claims", "lifecycle", "combatAckLatency", "formDuration", "forcedForms", "serverStepMilliseconds", "droppedTicks", "lagComp"}
HEADER_KEYS = {"schema", "protocol", "matchSessionId", "buildCommit", "serverVersion", "serverPlatform", "matchMode", "map", "playerCount"}
DISTRIBUTION_KEYS = {"count", "mean", "p50", "p95", "p99", "maximum"}
COUNTER_KEYS = {"eventsQueued", "eventsWritten", "eventsDropped", "queueHighWater", "writerFailures", "uploadFailures"}
LAG_KEYS = {"weapon", "rttBucket", "jitterBucket", "requested", "plausible", "displacement", "globalClamps", "shadowClamps", "hitsOutside", "rescuesOutside", "missesOutside"}


def valid(summary):
    if not isinstance(summary, dict) or set(summary) != SUMMARY_KEYS:
        return False
    header = summary["header"]
    if not isinstance(header, dict) or set(header) != HEADER_KEYS or header["schema"] != 1 or header["protocol"] != 19:
        return False
    if not isinstance(header["playerCount"], int) or not 0 <= header["playerCount"] <= 8:
        return False
    if any(not isinstance(header[k], str) or len(header[k]) > 256 for k in HEADER_KEYS - {"schema", "protocol", "playerCount"}):
        return False
    def numbers(value, keys):
        return isinstance(value, dict) and set(value) == keys and all(type(v) in (int, float) for v in value.values())
    if not numbers(summary["counters"], COUNTER_KEYS):
        return False
    for key in ("combatAckLatency", "formDuration", "serverStepMilliseconds"):
        if not numbers(summary[key], DISTRIBUTION_KEYS):
            return False
    for key in ("network", "combat", "claims", "lifecycle"):
        if not isinstance(summary[key], list) or len(summary[key]) > 256 or any(type(n) is not int for n in summary[key]):
            return False
    if not isinstance(summary["lagComp"], list) or len(summary["lagComp"]) > 594:
        return False
    for bucket in summary["lagComp"]:
        if not isinstance(bucket, dict) or set(bucket) != LAG_KEYS:
            return False
        if any(not numbers(bucket[k], DISTRIBUTION_KEYS) for k in ("requested", "plausible", "displacement")):
            return False
        if any(type(bucket[k]) not in (int, float) for k in LAG_KEYS - {"requested", "plausible", "displacement"}):
            return False
    return all(type(summary[k]) in (int, float) for k in ("durationSeconds", "forcedForms", "droppedTicks"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--listen", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8099)
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--max-bytes", type=int, default=256 * 1024 * 1024)
    parser.add_argument("--max-files", type=int, default=10000)
    args = parser.parse_args()
    token = os.environ.get("PRIME_TELEMETRY_COLLECTOR_TOKEN", "")
    if not token:
        parser.error("PRIME_TELEMETRY_COLLECTOR_TOKEN must be set")
    args.directory.mkdir(parents=True, exist_ok=True)

    class Handler(BaseHTTPRequestHandler):
        def setup(self):
            super().setup()
            self.connection.settimeout(5)

        def log_message(self, *_):
            pass  # Do not persist request addresses or headers.

        def reply(self, status):
            self.send_response(status)
            self.send_header("Content-Length", "0")
            self.end_headers()

        def do_POST(self):
            if self.path != "/api/net-telemetry/v1/matches":
                return self.reply(404)
            if not hmac.compare_digest(self.headers.get("Authorization", ""), "Bearer " + token):
                return self.reply(401)
            try:
                length = int(self.headers.get("Content-Length", "0"))
                if not 0 < length <= MAX_BODY:
                    return self.reply(413)
                payload = self.rfile.read(length)
                if len(payload) != length:
                    return self.reply(400)
                value = json.loads(payload, parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
                if not valid(value):
                    return self.reply(400)
                files = list(args.directory.glob("match-*.json"))
                if len(files) >= args.max_files or sum(p.stat().st_size for p in files) + length > args.max_bytes:
                    return self.reply(507)
                target = args.directory / ("match-" + uuid.uuid4().hex + ".json")
                with target.open("xb") as output:
                    output.write(payload)
                self.reply(201)
            except (OSError, ValueError, TypeError):
                self.reply(400)

    with HTTPServer((args.listen, args.port), Handler) as server:
        print(f"Aggregate collector listening on {args.listen}:{args.port}", flush=True)
        server.serve_forever()


if __name__ == "__main__":
    main()
