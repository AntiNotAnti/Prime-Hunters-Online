# Build & Deploy — servers and deployment

Deployment notes and commands.

Deploy script (server and directory)

```bash
# server and directory (rebuilds ARM64, installs both units, restarts them)
MPH_SERVER_HOST=net.livetek.fr MPH_SERVER_USER=livetek \
  MPH_SERVER_PASS="$(read -rsp 'pi password: ' p; echo "$p")" ./deploy-server.sh
# MPH_DEPLOY_MASTER=0 to leave the directory alone
```

Publish commands (Windows client and server)

```bash
# Windows client
dotnet publish src/MphRead/MphRead.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -o publish/win-x64

# Windows dedicated server
dotnet publish src/MphRead/MphRead.csproj -c Release -r win-x64 \
  -p:MphReadServer=true --self-contained true -p:PublishSingleFile=true \
  -o publish/win-x64-server
```

Notes

- A running binary may be locked. Stage a new file and atomically rename/swap it; use the actual current binary name (`FruityPrime` / `FruityPrimeServer.exe`).
- Any incompatible protocol change requires server and clients to match. Do **not** copy the protocol number into this runbook; read `NetConfig.ProtocolVersion` from `NetProtocol.cs` (15 at this audit). A mismatch is refused during Hello. Deploy authoritative servers before distributing a client that requires a new protocol.

Standalone authoritative servers record canonical replays by default. Retention defaults to **25 GiB / 14 days / keep newest 100**. Override with `-serverreplays on|off`, `-serverreplaystoragegb N`, `-serverreplayretentiondays N`, and `-serverreplaykeeplast N`; `0` disables the corresponding size/age limit.

## Live fleet and deployment state

Do not keep IP addresses, VM counts, firewall snapshots, subscriptions or
current unit state in this repository as architectural truth. They age faster
than the code and have repeatedly turned this file into a map of yesterday.

Ask the directory for the current public game-server inventory:

```bash
./FruityPrime -servers -master <directory-host> -masterport 27889
```

Then verify the target host itself (service unit, binary SHA/version, UDP
firewall, `paths.txt`, extracted game-data path and free disk) before deploying.
A directory entry proves that a heartbeat arrived; the launcher's
`StatusQuery`/join probe is what proves that a player can actually reach the
server.

## Authoritative game servers

Server authority is no longer a test-side `-simulate` deployment. A normal
`-server` process runs the match itself, and `-simulate` / `-authority` are
accepted compatibility no-ops. Every game server therefore needs operator-
supplied extracted game files plus a valid `paths.txt`; it refuses startup
rather than falling back to first-client authority.

Hosted games created by the launcher, directory or a host pool are isolated
server child processes. Do not deploy an old relay beside a newer authoritative
server and describe one as production and one as the test authority. If a
legacy relay is intentionally kept for protocol testing, name and isolate it as
legacy compatibility infrastructure.

## Post-deploy checks

1. Confirm the service is running the intended binary/version and that
   `paths.txt` resolves on that host.
2. Query its status from another machine and confirm protocol/map/player cap.
3. Join with a current client and verify the client is **not** granted
   `PacketType.Authority`.
4. Cross at least one match boundary/rematch and confirm room rebuild plus
   `MatchLoaded`/spawn lifecycle.
5. Confirm canonical replay creation/retention when enabled.
6. For a renamed/older systemd install, inspect `systemctl cat` after the first
   migration instead of assuming `deploy-server.sh` changed the live unit.

See `SERVER.md`, `ARCHITECTURE-INVARIANTS.md` and
`.claude/multiplayer/NETWORK-SERVERAUTH.md` for the current contract.
