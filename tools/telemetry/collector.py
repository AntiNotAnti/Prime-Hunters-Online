#!/usr/bin/env python3
"""Optional bounded aggregate collector. No raw events or player credentials."""
import argparse
import hmac
from http.server import BaseHTTPRequestHandler, HTTPServer
import json
import math
import os
from pathlib import Path
import uuid

MAX_BODY = 1024 * 1024
SUMMARY_KEYS = {"header", "durationSeconds", "counters", "network", "combat", "claims", "lifecycle", "combatAckLatency", "formDuration", "forcedForms", "serverStepMilliseconds", "droppedTicks", "lagComp"}
V2_KEYS = {"networkDetails", "lifecycleDetails", "combatDetails", "shadowOutcomes", "formCorrectionReasons"}
V3_KEYS = {"combatAcks", "transportContention"}
HEADER_KEYS = {"schema", "protocol", "matchSessionId", "buildCommit", "serverVersion", "serverPlatform", "matchMode", "map", "playerCount"}
DISTRIBUTION_KEYS = {"count", "mean", "p50", "p95", "p99", "maximum"}
COUNTER_KEYS = {"eventsQueued", "eventsWritten", "eventsDropped", "queueHighWater", "writerFailures", "uploadFailures"}
LAG_KEYS = {"weapon", "rttBucket", "jitterBucket", "requested", "plausible", "displacement", "globalClamps", "shadowClamps", "hitsOutside", "rescuesOutside", "missesOutside"}
COMBAT_ACK_KEYS = {"weapon", "result", "settlementMilliseconds", "exactDamage", "damageCorrections",
                   "healthCorrections", "headshotCorrections", "rejected", "correctionReasons"}
TRANSPORT_CONTENTION_KEYS = {"acquisitions", "contended", "waitPerAcquisitionMilliseconds",
                             "holdPerAcquisitionMilliseconds", "maximumWaitMilliseconds", "maximumHoldMilliseconds"}


def valid(summary):
    if not isinstance(summary, dict):
        return False
    header = summary.get("header")
    if not isinstance(header, dict) or set(header) != HEADER_KEYS or header["schema"] not in (1, 2, 3) or header["protocol"] not in (19, 20, 21):
        return False
    expected = SUMMARY_KEYS | (V2_KEYS if header["schema"] >= 2 else set()) | (V3_KEYS if header["schema"] >= 3 else set())
    if set(summary) != expected:
        return False
    if type(header["playerCount"]) is not int or not 0 <= header["playerCount"] <= 8:
        return False
    if any(not isinstance(header[k], str) or len(header[k]) > 256 for k in HEADER_KEYS - {"schema", "protocol", "playerCount"}):
        return False
    def numbers(value, keys):
        return isinstance(value, dict) and set(value) == keys and all(type(v) in (int, float) and math.isfinite(v) and v >= 0 for v in value.values())
    if not numbers(summary["counters"], COUNTER_KEYS):
        return False
    for key in ("combatAckLatency", "formDuration", "serverStepMilliseconds"):
        if not numbers(summary[key], DISTRIBUTION_KEYS):
            return False
    for key, length in (("network", 8), ("combat", 8), ("claims", 16), ("lifecycle", 256)):
        if not isinstance(summary[key], list) or len(summary[key]) != length or any(type(n) is not int or n < 0 for n in summary[key]):
            return False
    if not isinstance(summary["lagComp"], list) or len(summary["lagComp"]) > (648 if header["schema"] == 2 else 594):
        return False
    for bucket in summary["lagComp"]:
        if not isinstance(bucket, dict) or set(bucket) != LAG_KEYS | ({"hitsInside", "rescuesInside", "missesInside", "unknownOutcomes"} if header["schema"] == 2 else set()):
            return False
        if any(not numbers(bucket[k], DISTRIBUTION_KEYS) for k in ("requested", "plausible", "displacement")):
            return False
        if any(type(bucket[k]) not in (int, float) or not math.isfinite(bucket[k]) or bucket[k] < 0 for k in set(bucket) - {"requested", "plausible", "displacement"}):
            return False
        if any(type(bucket[k]) is not int or not 0 <= bucket[k] < size
               for k, size in (("weapon", 12 if header["schema"] == 2 else 11), ("rttBucket", 9), ("jitterBucket", 6))):
            return False
    if header["schema"] >= 2:
        groups = {
            "networkDetails": ({"rttMilliseconds", "jitterMilliseconds", "recentMinimumRttMilliseconds", "rttVariationMilliseconds"}, {"retransmissions", "estimatedLost", "queueHighWater"}, {"rttBuckets": 9, "jitterBuckets": 6}),
            "lifecycleDetails": ({"joinMilliseconds", "loadMilliseconds", "bootstrapMilliseconds", "rejoinMilliseconds"}, {"ready", "lateJoins", "disconnects"}, {}),
            "combatDetails": (set(), {"settledPredictions", "exactDamagePredictions", "damageCorrections", "headshotCorrections", "healthCorrections", "rejectedPredictions"}, {})}
        for key, (distributions, scalars, arrays) in groups.items():
            item = summary[key]
            if not isinstance(item, dict) or set(item) != distributions | scalars | set(arrays): return False
            if any(not numbers(item[k], DISTRIBUTION_KEYS) for k in distributions): return False
            if any(type(item[k]) is not int or item[k] < 0 for k in scalars): return False
            if any(not isinstance(item[k], list) or len(item[k]) != length or any(type(v) is not int or v < 0 for v in item[k]) for k, length in arrays.items()): return False
        for key in ("shadowOutcomes", "formCorrectionReasons"):
            if not isinstance(summary[key], list) or len(summary[key]) != 7 or any(type(v) is not int or v < 0 for v in summary[key]): return False
    if header["schema"] >= 3:
        acks = summary["combatAcks"]
        if not isinstance(acks, list) or len(acks) > 12 * 14:
            return False
        for ack in acks:
            if not isinstance(ack, dict) or set(ack) != COMBAT_ACK_KEYS:
                return False
            if type(ack["weapon"]) is not int or not 0 <= ack["weapon"] < 12:
                return False
            if type(ack["result"]) is not int or not 0 <= ack["result"] < 14:
                return False
            if not numbers(ack["settlementMilliseconds"], DISTRIBUTION_KEYS):
                return False
            for key in ("exactDamage", "damageCorrections", "healthCorrections", "headshotCorrections", "rejected"):
                if type(ack[key]) is not int or ack[key] < 0:
                    return False
            if not isinstance(ack["correctionReasons"], list) or len(ack["correctionReasons"]) != 16 \
                    or any(type(v) is not int or v < 0 for v in ack["correctionReasons"]):
                return False
        contention = summary["transportContention"]
        if not isinstance(contention, dict) or set(contention) != TRANSPORT_CONTENTION_KEYS:
            return False
        for key in ("acquisitions", "contended"):
            if type(contention[key]) is not int or contention[key] < 0:
                return False
        for key in ("waitPerAcquisitionMilliseconds", "holdPerAcquisitionMilliseconds"):
            if not numbers(contention[key], DISTRIBUTION_KEYS):
                return False
        for key in ("maximumWaitMilliseconds", "maximumHoldMilliseconds"):
            if type(contention[key]) not in (int, float) or not math.isfinite(contention[key]) or contention[key] < 0:
                return False
    return all(type(summary[k]) in (int, float) and math.isfinite(summary[k]) and summary[k] >= 0 for k in ("durationSeconds", "forcedForms", "droppedTicks"))


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
