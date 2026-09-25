# Protocol 19 VPS study

The study runs as an isolated canary alongside the existing public server. It
requires matching Protocol 19 clients. The main server and directory continue
using their existing release. Lag compensation stays Shadow with the 45-frame
production ceiling; study results do not enable enforcement.

## Install

Publish a self-contained Linux x64 server from the tested commit:

```sh
dotnet publish src/MphRead/MphRead.csproj -c Release -r linux-x64 \
  -p:MphReadServer=true --self-contained true -p:PublishSingleFile=true \
  -p:PublishAot=false -o /tmp/prime-study-runtime
```

Stage that directory on the VPS, plus `install-study.py`, `collector.py` and
`report.py` from `tools/telemetry`. Run the installer as the service account with
passwordless sudo, using an existing lawful game-asset `paths.txt`:

```sh
python3 install-study.py --root /home/ubuntu/projectprime-study \
  --runtime /home/ubuntu/projectprime-study/runtime \
  --paths /home/ubuntu/fruityprime-server/current/paths.txt --open-firewall
```

Defaults: gameplay UDP 27921; collector HTTP 127.0.0.1:8099. The collector is not
exposed publicly. The installer creates a random server token in a mode-600
local environment file. No player credentials are used. Do not commit or copy
that token into logs or reports.

Units:

- `projectprime-study.service`: eight-player study match, anonymous telemetry.
- `projectprime-telemetry.service`: authenticated aggregate collector.
- `projectprime-study-report.timer`: regenerate the offline HTML/JSON report each
  minute from completed uploaded matches.

State, raw telemetry, collected aggregates and reports are below the study root.
Raw telemetry has 14-day retention; local output and uploads have configured
caps. Collector defaults to at most 10,000 files / 256 MiB. Archive aggregates
before reaching that cap; a full collector returns backpressure and the game
keeps its bounded local retry files. The report is an operator artifact, not a
public HTTP service. Retrieve it over SSH to review.

## Verify and admit study players

Check all units, bound ports, a complete match/upload cycle, authenticated upload
rejection, and report validation. Verify the main public server and master remain
active. Use matching clients to connect directly to the study host on UDP 27921.
The server name discloses anonymous networking telemetry; further details and the
Off setting are in [combat telemetry](combat-telemetry.md).

Keep all scripted deployment smoke data outside the collector's human-study input
directory. An empty human report is correct before real players participate.
A successful scripted WAN session proves connectivity and pipeline operation; it
is not a real-player study, or evidence sufficient to change lag policy.

The report keeps build/schema cohorts separate and exposes unknown historical
outcomes. Review weapon/map/RTT/jitter coverage as well as thousands of real
matches before proposing enforcement. General alternate-policy projectile,
homing, continuous-damage and secondary-body simulation is still a separate
coverage gap; current window membership alone does not answer those outcomes.

## Rollback

```sh
sudo systemctl disable --now projectprime-study projectprime-telemetry projectprime-study-report.timer
sudo ufw delete allow 27921/udp
```

This stops the canary and collector. It retains study evidence and does not change
the existing public server or master. For a later upgrade, stop only the canary
between matches, archive its runtime with its commit/hash, replace it with a
validated matching build, then restart the canary and verify collection again.
