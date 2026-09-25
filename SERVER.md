# Running a Project Prime server

You do not need any of this to play online. **Host → Where: Online** in the launcher asks a public
machine to run the match and joins you to it, with nothing to open on your router. This page is for
running a machine of your own that is always up.

A server **runs the match itself**, so it needs the game files. It also records
canonical match replays by default, with bounded retention, and a Raspberry Pi
is still enough.

```bash
# Linux
./ProjectPrime -server -port 27888 -players 8 -servername "My server"
# Windows -- the console binary, not ProjectPrime.exe
ProjectPrimeServer.exe -server -port 27888 -players 8 -servername "My server"
```

## Game files are required

**This changed, and it is a breaking change.** A server used to be a relay: the
first client to connect ran the match, and the server only forwarded packets,
so it needed nothing. Now the server runs the match, which means it runs the
engine, which means it needs the files.

Put a `paths.txt` beside the binary pointing at them, the same file a client
uses:

```
0.35.1.0
AMHP1=/home/you/projectprime-server/files/AMHP1
```

A server without them **will not start**. It says so and exits:

```
[server] cannot run the match: game files could not be located
[server] a dedicated server runs the match itself now, so this one will not
         start. Put the game files on this machine and paths.txt beside the
         binary -- see SERVER.md
```

That is deliberate. The old alternative was to fall back to client-authority relaying: it put the match on a player's machine, where that
player's own shots resolved instantly while everybody else's took a round trip,
and where a disconnection took the match with it.

`-simulate` and `-authority` are still accepted and now do nothing -- an
existing systemd unit or launch script keeps working unchanged.

| Flag | |
|---|---|
| `-port N` | UDP port. Default 27888 |
| `-players N` | slots. Default 4, use 8 |
| `-servername "NAME"` | the name shown in the browser |
| `-rotation FILE` | default `maprotation.txt`, written beside the binary on first run |
| `-friendlyfire` | team damage on |
| `-nomaster` | stay off every server list |
| `-master HOST` `-masterport N` | use a server list other than `51.161.113.128:27889` |
| `-serverreplays on\|off` | canonical authoritative replay recording. Default on |
| `-noserverreplays` | shorthand to disable canonical server replay recording |
| `-serverreplaystoragegb N` | replay storage soft cap in GiB. Default 25; 0 = unlimited |
| `-serverreplayretentiondays N` | delete eligible recordings older than N days. Default 14; 0 = forever |
| `-serverreplaykeeplast N` | always protect the newest N finalized recordings. Default 100 |

## Canonical server replays

A standalone authoritative server records the stream it treats as canonical:
accepted player intents, its authoritative snapshots, roster/match state and
replay events. Recording starts when a player is present and the server produces
its first authoritative snapshot. Map rotation finalizes the previous replay.

The default policy is:

```
server replays:   on
storage cap:      25 GiB
retention:        14 days
keep newest:      100
```

Cleanup runs at server startup and after each finalized match. The newest
`-serverreplaykeeplast` files and any replay with a `.favorite` sidecar are
protected first. Age pruning runs next, then the storage cap removes the oldest
remaining eligible files. If protected files alone exceed the cap, they are
kept and the server logs the remaining overage.

Canonical files live under the server export tree in `_demos/server/`; the
server logs the exact replay path when recording starts. Interrupted `.part`
files are not automatically deleted by retention.

The compact flags above also accept the configuration-style aliases
`-server_replays`, `-server_replay_storage_gb`,
`-server_replay_retention_days`, and `-server_replay_keep_last`.

Example for a smaller public server:

```bash
./ProjectPrime -server -players 8 \
  -serverreplaystoragegb 10 \
  -serverreplayretentiondays 7 \
  -serverreplaykeeplast 50
```

## Ports

UDP only. Forward **27888** to the machine. The server list uses **27889**.

Your server is listed on `51.161.113.128` automatically, so people find it in **Join → Find a
server**. Check it arrived with `ProjectPrime -servers`, which prints the list the browser shows.
`-nomaster` keeps it private.

## Map rotation

`maprotation.txt`, one match per line, `#` for comments:

```
MP1 SANCTORUS      | Battle | 7 | 7
MP3 PROVING GROUND | Battle | 7 | 7
```

`ROOM KEY | mode | minutes | points`. Only the key is required.

`ProjectPrime -rooms` lists every key — the 27 cartridge rooms and any custom map. It reads the game
files to do that, so run it on a machine that has them, not necessarily on the server.

## As a service

systemd units are in `tools/systemd/`:

```bash
sed -e 's|__USER__|youruser|' -e 's|__DIR__|/home/youruser/projectprime-server|' \
    tools/systemd/mphread-server.service | sudo tee /etc/systemd/system/mphread-server.service
sudo systemctl enable --now mphread-server
```

Stop the service before replacing the binary — systemd holds the file open, and .NET maps it into
memory, so copying over a running one takes the process down in a way nothing explains.

`deploy-server.sh` does build, upload, units and restart against a remote box in one go.

## Your own server list

The list players' browsers ask is the same binary:

```bash
ProjectPrime -masterserver -port 27889
```

Add `-public HOST` if a game server shares the box (its heartbeats arrive over the loopback, and the
address published for it has to be the one the internet can reach), and `-hostports A-B` for the
port range it may run matches on for players who cannot open one.

Point servers at it with `-master HOST`, and players in **Settings → Servers**.

## Versions must match

A server refuses a client built against a different protocol, at the first packet, with a line in
its log. That is deliberate: the wire format does not move between versions, so an old client would
read every byte correctly and then play a different game. Update the server before handing out a
client built from a newer release.

## Hunter License career reporting

Career stats are written only by an **authoritative dedicated server**. The
client never submits its own kills, wins, damage, standings, or rating.

A provisioned server receives a reporter key out of band and stores it in
`career.env` beside the server binary:

```ini
PROJECT_PRIME_CAREER_SERVER_KEY=ppsrv_...
```

The systemd templates load that file with `EnvironmentFile=-...`. It must
remain private and should be mode 600. Do not put this value in a unit's
`ExecStart`, a shell history, a release archive, or the repository.

`deploy-server.sh` can install/update the file without exposing it in source:

```bash
PROJECT_PRIME_CAREER_SERVER_KEY='ppsrv_...' ./deploy-server.sh
```

When no reporter key is configured, gameplay is unchanged and the server logs
that career reporting is disabled. A provisioned server writes each completed
report to a durable local `career-outbox` before making the HTTPS request.
Transient Supabase/network failures therefore delay stats rather than losing
them; accepted files are deleted and permanently rejected reports are retained
with a `.rejected` suffix for diagnosis.

Each player sends only a short-lived, career-only ticket over the game socket.
The Supabase access/refresh tokens never travel through the UDP protocol. The
ingestion service verifies the server credential and every participant ticket
before it can touch the existing `prime` career/rating tables.

Continuous provisioned servers may apply the existing pairwise rating policy.
Player-created persistent lobbies are career-history only and cannot modify
rating points.

### Verify career reporting

After restarting a dedicated server, the log should contain both:

```text
[career] reporting enabled: https://hwcjaygoistufktorbmf.supabase.co/functions/v1/career-report
[career] reporter credential accepted
```

Check with:

```bash
systemctl cat mphread-server | grep EnvironmentFile
journalctl -u mphread-server -n 80 --no-pager | grep '\[career\]'
```

If the first command prints nothing, the installed systemd unit predates career reporting and is not loading `career.env`. Re-run `deploy-server.sh` or add `EnvironmentFile=-/home/<user>/mphread-server/career.env` under `[Service]`, then run `sudo systemctl daemon-reload && sudo systemctl restart mphread-server`.

## Anonymous networking telemetry

Dedicated servers can record anonymous network/combat/performance measurements for multiplayer quality studies. No player names, IP addresses, account IDs or chat are stored by this subsystem. Aggregate recording is enabled by default; uploads are disabled. Use `-notelemetry` to disable, or `PRIME_TELEMETRY_CONFIG` to select a configuration file. See [Protocol 19 combat and telemetry](docs/network/combat-telemetry.md) for retention, study modes and optional aggregate upload.
