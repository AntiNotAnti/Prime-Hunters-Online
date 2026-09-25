# Multiplayer — server browser, directory, and hosting without a port

## Asking a server what it's running

`PacketType.StatusQuery`/`StatusReply` (`NetStatus`) answers "what map, what
mode, how many players" **without claiming a slot** — what lets the front
screen poll a server every few seconds while somebody reads the screen. A
server built before that packet ignores it, so the launcher falls back to a
Hello/Bye join probe: that one *does* take a slot, is refused outright by a
full server, and counts itself among the players (`NetStatus` subtracts it) —
so it's used only until the cheap path answers once, then rarely.
**Redeploy the server** after taking a build with `StatusQuery` to get the
cheap path.

## The server browser

**Play online opens the list, not a form.** The address is the one thing a
new player can't invent, so the list comes first (with a field for an address
someone was handed directly), and only then the card asking for a name and
hunter.

It's a **card on the front screen**, not a window over it — same shape as
Play online/Host a game/Game files, its own scrolling list sized by the same
spacer arithmetic. It began as a separate `Form`, which was wrong twice over:
a popup over a launcher that is itself custom-painted reads as a different
program, and `Form` is the one thing in this codebase that can't be exercised
headless.

Rows come from the directory and are then **confirmed by this machine**: one
`StatusQuery` each, answering map/mode/head count/round trip in one exchange,
failing for exactly the servers this player couldn't have joined anyway. Rows
appear as they answer, sorted by players then latency.

**A hosted game can be listed too.** Local hosting starts an isolated dedicated
server process; online hosting asks a reachable directory/server host to start
one. The requesting player then joins as an ordinary client. Listing a server
publishes the address players must dial, so local dedicated hosting keeps an
explicit listing choice.

## Hosting without opening a port

Being listed isn't being reachable — most people can't or won't forward UDP
from their router, which made *Host a game* work on a LAN and nowhere else.

The fix is not NAT traversal: **the authoritative match runs somewhere
reachable and the requester joins it by connecting out**, like everybody else.
The game already uses one server endpoint, so this needs no peer-to-peer
punching or alternate gameplay transport.

| Piece | What |
|---|---|
| `HostRequest`/`HostReply` | launcher → host service: requested lobby/match rules and rotation. The host starts an isolated authoritative server process on a port from its range and returns the port plus owner token when applicable |
| `-hostports 27900-27919` on the directory | the range it may use, one port per game. Default on — a feature that has to be configured to work is a feature nobody has. `-hostports none` disables it |
| **Create server** (launcher, Online face) | the screen this is reached from now: name, game type, hunter, map rotation, **Host on**, and Hosted vs Dedicated. See the launcher table in CLAUDE.md |
| `MphRead -hostgame "ROOM" [-mode M] [-maprotation "A,B,C"]` | same thing from a command line — the only way to host with no launcher |
| `HostedIdleSeconds` (180) | an unjoined game is shut down and its port returned. Generous, since the usual reason one's empty is that the requester is still loading the map |

### The two additive fields, and why the directory has to be redeployed

Both were appended **past the fixed block** rather than inserted into it, so
`NetConfig.ProtocolVersion` did not move and no deployed client or server was
invalidated. Both are also therefore inert until a directory is redeployed,
which is the thing to check first when either looks broken.

| Field | Where | Old peer does what |
|---|---|---|
| The asker's whole map cycle — `[count][count × (40-byte room key + 1-byte mode)]` after `HostRequestPacket.Size` | `HostRequest`, launcher → directory | length-checks against `Size`, reads exactly that, and plays `RoomKey` on a loop — the behaviour it always had. Entry 0 **is** `RoomKey`, so the two halves can never disagree about what starts |
| One flags byte after the entries, bit 0 = "this directory starts games" (`MasterFlags.CanHost`) | `MasterList`, directory → launcher | a launcher from before stops reading once it has taken `count` entries and never sees it. A launcher that reads it and finds **nothing** treats that as a *third* state, not as a no — see below |

`NetMasterConfig.EntriesPerPacket` is derived from
`NetConfig.MaxPacketSize` and `MasterEntryPacket.Size`; do not copy a packet
budget into this document. `HostRequestPacket.MaxRotation` is likewise bounded
so its fixed block, rotation entries and session-policy tail fit one datagram.

**Silence is not a no**, and getting that backwards made the whole feature
dead on arrival. `MasterListResult.CanHost` is `bool?`: true or false when the
directory said, null when it is too old to have said. The first version folded
null into false as the "conservative" reading, and against the live directory
that produced **Host on: nobody** — because hosting is on by default and has
to be turned *off* with `-hostports none`, so every directory deployed in the
world hosts and none of them could say so. `WillHost` is `Answered &&
CanHost != false`: only an explicit no is a no. The cost of guessing wrong is
one clear refusal from `HostReply` at the moment the player presses the
button; the cost of the conservative reading was a row that said nothing and
explained less.

`MphRead -servers` prints all three states, which is what tells a directory
that is down from one that is up and does not host from one that is simply
old — the first needs looking at, the second is a setting, the third is a
deploy.

**Historical compatibility measurement (2026-09-14):** an older directory
accepted the fixed HostRequest block but ignored the then-new rotation tail.
That result is useful evidence for additive parsing, not a description of the
current hosting topology.

### Who can host: the servers themselves

**Any `DedicatedServer` started with `-hostports A-B` will open extra matches
on ports of its own** (`Mods/Network/HostPool.cs`), and says so in a flags byte
appended to its status reply. The create-server screen asks every server the
directory names, on the port the browser already pings it on.

That is the second shape of this. The first put a **directory on every box**,
and it was wrong in a way worth recording: those directories listed nothing —
every relay reports to the one real directory — and existed purely to be asked
"will you host". A component invented to satisfy a layering mistake. Starting a
match on a machine is a property of *being that machine*, not of being a
directory, and the machine is already listed, already pinged and already
reachable.

So: **one directory in the world.** It answers "who is up". Each server answers
"can you open me a game". The directory is a host candidate too, since it has a
port range and will open one — `HostPool` was lifted out of `MasterServer`, so
both run the same code.

| Piece | What |
|---|---|
| `-hostports A-B` on a **server** | the range it may open extra matches on. **Off** by default, unlike the directory's: it is an admin's bandwidth and their ports, and a game server has a match of its own to protect |
| `ServerStatusPacket.Flags` bit 0 | "I will open new games". Appended past `Size`, so an older server is read exactly as before and an older launcher never looks — no protocol bump |
| Silence = **no** here | the opposite of the directory's flag, and right both times: hosting on a server is off unless asked for, so not saying and saying no are the same answer; hosting on a directory is on unless turned off, so not saying means "too old to ask" |
| `NetMasterClient.Merge` | one row per machine rather than one per port; a host-capable endpoint wins when the same machine appears through multiple services |
| Hosted matches | each gets an isolated `HostedServerProcess`; the child always owns simulation authority and no player can be promoted |
| `Hosts.Count` counts toward auto-update | hosted child processes are active sessions; the parent must not update/restart while one is in use |

Verified 2026-09-14 on loopback: a `-server ... -hostports 28900-28903` was
asked for a 3-map rotation on its own port and opened it on 28900, which was
then joined.

**Deployment note:** specific VM counts, NSG/firewall state and currently deployed host-port ranges are operational state, not architecture. Verify them live instead of preserving them here.

## A server on the player's own machine

The other half of "I want to run a server", and the half nothing in the
launcher could reach before: **Server type → Dedicated server** starts an
ordinary `DedicatedServer` as its own process on this box and joins it over
the loopback (`Mods/Network/LocalServer.cs`). What it buys over a hosted game
is that it is the player's — their rotation, their uptime, nobody else's port
range and no reaping when it empties. What it costs is the thing hosting was
invented to avoid: **UDP 27888 has to reach this PC** before anybody outside
can join, which the screen says before the choice rather than after.

Three things in there are worth keeping in view:

- **It gets its own window and its own life.** A server the player started
  has to outlive the client that started it — somebody quitting to the front
  screen has not asked to end the match everybody else is in. On Windows that
  is `UseShellExecute = true`, which starts the process independently *and*
  gives a console binary a console of its own; elsewhere a child already
  outlives its parent. `LocalServer.Stop` is not called on shutdown, and
  starting a second server does not stop the first (`FreePort` has already
  moved past its port). Stop exists for `-hostlocal`, which starts one to
  measure it.
- **Which binary, and why Windows is the exception.** Everywhere but Windows
  the game build is a console program that already accepts `-server`, so
  `LocalServer.Available()` takes `Environment.ProcessPath` (with the `.dll`
  as a prefix argument when that path is `dotnet`, since a framework-dependent
  apphost cannot find a runtime here). That is also the `ProtocolVersion`
  argument. When Windows needs a separate server package, a tagged client now
  downloads the package from **its own release tag**, not whatever release is
  newest; local/development builds retain the latest-package fallback. The
  child still gets `-noautoupdate` so it cannot drift after launch.
  **On Windows the game exe is not a candidate at all.** It accepts `-server`
  and it is a `WinExe`, so the server it starts has no console: nothing it
  logs is ever seen and there is no window to close. `ProjectPrimeServer.exe`
  is looked for beside the game first and in `<base>/server/` second, and its
  absence is the *only* case where the screen's install mark appears.
  `MphRead -installserver` is that download on its own, for when it is the
  button that has to be diagnosed; it prints the tag it fetched. Tagged builds
  fetch their matching release, while local/development builds fetch latest.
- **`paths.txt` is copied next to the server**, and only when the server is
  somewhere else. A server runs the match itself, so it needs the extracted
  game files, and it looks for `paths.txt` beside its own binary rather than
  in the working directory — `ConsoleSetup.Run` makes the binary's directory
  current before anything reads either. Without the copy the server refuses to
  start with a message nobody sees, because it is a process with no console.
  `LocalServer.Start` checks `GameFiles.Problem()` here rather than leaving
  the refusal to the child, for the same reason.
- **The join is `127.0.0.1`.** A router that hairpins badly is a thing that
  exists, and a player watching their own server fail to admit them has no way
  to tell that apart from a server that did not start.

`MphRead -hostlocal "A,B,C"` is that path with no launcher, which is how it is
checked from a box with no display: it spawns the process, writes the
rotation, copies `paths.txt`, waits for the socket and prints where it landed.
Measured here: three maps, listed on a local directory, answering
`StatusQuery` on 127.0.0.1:27888, and joined by a real `-netcheck` client.

macOS has **no** server package (`release.yml` publishes win-x64, linux-x64
and linux-arm64 servers and nothing else), so `UpdateCheck.ServerRid()` returns
`""` there and the screen says so rather than offering a download that 404s.
A Mac can still use Hosted, which needs nothing installed.

Hole punching was the other candidate and wasn't worth it: needs a rendezvous
protocol, needs a relay fallback anyway, and has a failure mode for every
symmetric NAT. This has none.

Current end-to-end expectation: `-hostgame` asks a host service for an isolated
server process, joins the returned port as an ordinary client, and never receives
simulation authority. A second client joins through the same ordinary path.

## The directory (master server)

| Piece | What |
|---|---|
| `MphRead -masterserver` (`Network/NetMaster.cs`) | the directory. Servers announce every 15 s, entries expire after 50 s of silence, `MasterQuery` returns the list in as many datagrams as it takes. Relays no gameplay, stores nothing, can share a box with the server it lists |
| `MphRead -servers [-master HOST]` | prints the same list the browser would show — exercises that data path on a machine with no display |
| `MasterReporter` | the server's end: one datagram every 15 s, every failure swallowed and retried (a directory outage must never touch a match), first failure logs a line |
| Server browser | rows confirmed by a `StatusQuery` each, as above |

Two decisions worth keeping:

- **The address in the list is the one the heartbeat arrived from**, not the
  one the server believes it has — a server behind a router only knows its
  private address, and a directory full of `192.168.x.x` is useless. Port
  comes from the heartbeat too, since a datagram's source port isn't
  necessarily the one it listens on.
- **Latency is measured by the launcher, not reported by the directory** —
  the master could only ever report its own round trip to each server, not
  the number the person reading the screen cares about.

`NetMasterConfig` owns the configured default directory endpoint. DNS,
firewall and whether that deployment is currently online are operational facts
and must be checked live; do not record a temporary outage or alternate host as
architecture.

`tools/systemd/mphread-master.service` is the unit; `deploy-server.sh`
installs both it and the game server's, filling in user/directory, and
**leaves an existing unit alone** on later deploys — so the two hand-added
options on the Pi (`-master 127.0.0.1` on the server, `-public
51.161.113.128` on the directory) survive a redeploy and aren't in the
templates.

Two things had to be true before anything appeared in a list, neither visible
from the code:

- **`-public` on the directory.** Right for a server behind a router, exactly
  wrong for a server sharing a box with the directory — that heartbeat
  arrives from `127.0.0.1`, and a list of loopback addresses sends every
  player to their own machine. `-public 51.161.113.128` tells the
  directory once what to publish for anything registering from loopback or a
  private range.
- **UDP 27889 through the firewall**, separately from the game port. `ufw`
  on the Pi allowed 27888 and nothing else, so the game server answered from
  outside while the directory timed out — which looks exactly like a
  directory that isn't running. Check `sudo ufw status | grep 2788` before
  believing anything else.
- **Being listed and being reachable are different things.** A server behind
  a home router registers fine (directory records the heartbeat's public
  source) and is then unjoinable because nothing forwards UDP to it. The
  browser shows this honestly as a red "did not answer" row, which is the
  signal to check the router, not the server.

`ServerStatus`/`MasterListing` return `""` rather than `null` through backing
fields — they're structs, so `default` is an ordinary value the browser holds
for every row it hasn't probed yet, and a plain auto-property would hand that
row a null to call `.Length` on, crashing the window on the first row of the
first list anyone opened. No headless check could have caught it; `-servers`
exists partly so this path is checkable without a Windows box.
