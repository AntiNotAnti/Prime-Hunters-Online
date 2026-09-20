# Known gaps — claims not yet verified

What's below is unproven or partially proven, not broken. Say so rather than
claiming coverage that isn't there.

- **The tap rule has never met a finger.** *Written 2026-09-15.*
  `Mods/Launcher/Gui/Tap.cs` is the answer to "scrolling the settings on
  Android activates the buttons": no row acts on its press any more, and a
  gesture that travels more than eight points is given up. `-tapcheck` proves
  the rule on written-down coordinates and the desktop's `-uishot`/`-shellshot`
  prove the screens still take a click, but nothing here has dragged a real
  settings page on a real phone -- the emulator available cannot load a match,
  and touch is the one input this box does not have. What could still be wrong
  is the *interaction* with Avalonia's `ScrollGestureRecognizer` (it is assumed
  to take the pointer at thirty points, which is its default and was not
  measured on a device), not the rule.

- **A client's own beam is spawned before its puppets are placed, and turning
  snapshot-owned puppets on made that visible.** *Found and fixed
  2026-09-14; the fix is not yet re-measured.* `ProcessInput` runs before the
  movement step, so with the placement happening only in
  `NetHooks.AfterRemoteMovement` a client's shot was tested against the
  position the previous frame's step left behind while the intent it travelled
  with acked *this* frame's. One frame of a target's motion, against a headshot
  band 0.30 units tall and a runner measured at 0.377 units a frame: the whole
  band. `TryApplyRemoteInput` now writes the same read point the restore does.

  **What the measurement actually says, which is less than it first looked.**
  It was noticed as headshot agreement falling from 75% to 30% between the
  arena's protocol-6 and protocol-7 arms -- and on the *pads* map the same pair
  moved the other way, 63.6% to 81.8%. Nine to thirteen headshots per arm is
  exactly the sample size this file's own advice says not to read a percentage
  from, and two arms disagreeing in direction is what that looks like. The
  consistent signal is `local%`, which fell on **both** maps (72.7 to 64.0, and
  85.0 to 56.5): the shooter's own machine resolved a smaller share of the hits
  the authority credited it with, which is the shape a one-frame gap between
  the drawn world and the shot world makes.

  **Both arms were re-run on the fixed build and the disagreement did not go
  away.** The four pairings, 100 s each at 320 ms +- 80 with 2% loss:

  | arm | ceiling | clamped | local% | headshots agreed |
  |---|---|---|---|---|
  | arena-p6 | 24 | 80.0% | 72.7 | 6/8 (75.0%) |
  | arena-p7b | 45 | **0%** | 59.1 | 4/8 (50.0%) |
  | pads-p6 | 24 | 87.5% | 85.0 | 7/11 (63.6%) |
  | pads-p7b | 45 | **1.4%** | 74.1 | 13/16 (**81.2%**) |

  The pads map says protocol 7 is better on both headshots (63.6 to 81.2) and
  confirmation (85.0 to 100.0); the arena says it is worse on both. Eight to
  sixteen headshots an arm cannot separate those, and `local%` is lower on
  protocol 7 in **both** pairings -- and those historical A/B arms also included the then-current lethal-prediction
  behavior. Remote lethal prediction is no longer enabled, so those percentages
  must not be treated as current defaults.

  So: **the ceiling and the claim counts are measured; the percentages are
  not.** What is solid is `clamped` (80.0/87.5% down to 0/1.4%), every
  protocol-7 arm rescuing real kills and headshots, and the duel's 14 predicted
  headshots agreed 12/12 with none downgraded. Anything derived from `hs%` or
  `local%` at this sample size is noise, and settling it needs either much
  longer arms or the Pi rather than a contended WSL box. The one-frame gap is
  still a real fault and the fix is still right; it is simply not what this
  instrument can see.
- **The strict-earlier-world refusal branch of kill arbitration is still
  weakly exercised.** Hit claims have now run on a real intercontinental line
  (the Japan measurements in `NETWORK-HITCLAIMS.md` include claim/arbitration
  traffic), so "claims have never met a real line" is no longer true. What
  remains unresolved is the rare `ResultDeadShooter` refusal case and whether
  the **current dynamic grace window (RTT + margin, bounded 24–72 frames)**
  remains well tuned under real jitter/loss rather than synthetic shaping.
- **The geometric gate on a claim has never refused anything, so its tolerance
  is untested from the wrong side.** `ClaimRadius` is 2.0 units and every run
  so far reads `0 refused`. That is the right outcome and it is also no
  evidence about where the gate actually sits: nothing has yet produced a claim
  the authority disagrees with, so it is not known whether 2.0 is generous, mean
  or irrelevant. A run with `-relayedpuppets` on one client and the default on
  another -- two clients deliberately holding different copies of the same
  puppet -- is the experiment.
- **The duel stages crossing lethal shots, but the strict refusal outcome is
  still rare.** `HitRig.Duel` deliberately keys both shooters to the server
  clock so lethal Imperialist shots cross in flight. Trades have therefore been
  staged intentionally. The measured runs still reported
  `VoidedDeadShooter == 0`, so the *strictly earlier world* refusal branch
  needs a deterministic test that guarantees one shooter fired after the world
  in which they were already killed.
- **The playout clock's snap counter is contaminated by this box.** 96 clock
  snaps in a 60 s loopback run is mostly the *client* failing to hold 60 Hz
  while three clients and a server share a WSL CPU, not the line. The same
  number on a real machine would mean something quite different, and the two
  cannot be told apart from the report as it stands.
- **`% of frames still` in the smoothing line is not a stutter measurement on
  its own.** A player standing still contributes still frames honestly -- the
  scripted tour has whole phases of it -- and a respawn contributes a huge
  step to `worst`. Both are only meaningful compared between two arms of the
  same scenario.

- **The authority spawns beams for a dead player that the player's own machine
  never spawned, and the reason is not established.** Fell out of the arm that
  closed the gap above: in the same 70 s run, slot 1 -- the runner, which
  presses fire only while dead, since holding fire is what asks for an early
  respawn -- read **0** beams on its own machine, **15** on the observer and
  **29** on the authority. Three machines, three answers, for a slot that
  fired nothing it meant to fire.

  The shape that fits is the revive boundary: the trigger is still held when
  the authority's copy comes back, and it empties into the floor there, while
  the owner's copy comes back later with the trigger already released. That is
  a guess. What is measured is the disagreement.

  It does not touch the headshot numbers -- those shots are aimed at the floor
  by design (`HitRig.Drive`) and land on nobody -- but it does inflate the
  authority's shot count, so quote per-slot counts rather than a total. Follow
  it with `NetDamage.Fired` around a death, on all three machines.


- **Live validation of snapshot-based form reconciliation remains.** The
  authority now reconciles from the owner's intent and each client from the
  authority's snapshot, with a transition-aware guard for stale state. The
  prior Pi run with 150 ms injected on two of three clients found a remote
  puppet wrong for 78 consecutive frames; that predates this change. The
  deterministic form test covers morph and unmorph at 150 and 300 ms, but a
  restart-free `run-remote-lag.sh` run is still needed to measure real packet
  timing and whether any visual bounce remains. See
  `.claude/multiplayer/NETWORK-DIAGNOSTICS.md`.
- **The damage pipeline's `Replayed` count reads zero for one slot on one
  client, and the reason is not established.** Measured against Japan,
  2026-09-09, four two-client runs: the first client's slot carries the same
  `Replayed` count on both machines every time (33/32, 25/25), and the second
  client's slot carries a non-zero count on its own machine and **exactly
  zero** on the observer's (0/3, 0/9). No `damage sequence jumped` was logged
  in any run.

  **What makes it an open question rather than a finding**: in the
  Weavel/Guardian run, with *no* rotation followed, the observer reports
  `2 confirmed` non-self predictions on that slot -- and `NetDamage.Replay`
  increments `Replayed[slot]` *before* it calls `NetHitPrediction.Confirm`, so
  the count cannot be zero if the confirmation happened. Either the array is
  being cleared by something other than a followed rotation
  (`ResetForRoomChange` on a match boundary is the candidate), or the
  reporting is reading it after that clear. Settle which before treating the
  zero as a lost-damage bug: **the two readings cannot both be describing the
  same counter.** A fifth run, 40 s with no rotation, then put the zero on the
  **other** side -- the observer read 12 for that slot and the slot's own
  machine read 0 -- so "the observer loses the other player's damage" is not
  the shape of it either. It is not hit prediction either way -- the same shape is in
  the reports from before this work, and it is the long-standing
  `damage-taken` mismatch in `NETWORK-DIAGNOSTICS.md` (17 against 7) seen from
  another angle.
- **Continuous-weapon hit agreement is measured now, and the 131/28 figure it
  was justified by does not reproduce.** Shock Coil's old `scene.FrameCount`
  dither differed on each machine. Spawn now derives one phase from the owner's
  `NetFrame` or a per-slot clock seeded from `IntentPacket.Frame +
  RemoteIntentAge` on a remote machine. The remote clock advances through a held
  stream without re-anchoring to packets that arrive late, and the phase is used
  for ammo, damage and the homing ramp. The beam retains that phase for its
  enemy hit gate. Invalid or stale intents and offline play use scene timing.
  Measured against the Japan box at 275 ms -- 13 arms of 180 s, TEST ARENA, a
  simulating server, `-hitrig shockcoil` -- hit-count agreement went 70.7% to
  74.2% and unpredicted hits 31.5% to 27.1%. Three things came out of that
  campaign. **The 131-against-28 is wrong**: the shortfall on the same weapon
  against the same box is 70%, not 21%. **The dither was never the larger
  half** -- damage *per hit* already agreed to 97.8% before the change and moved
  0.4 points, so the disagreement is entirely a hit *count*, and the ramp's
  unreplicated `ShockCoilTimer` is therefore not contributing a measurable
  error either. And **the means are not significant** at n=13: all four metrics
  move the right way, none reaches |t| = 2. What is robust is the spread, which
  is the signature the mechanism predicts -- the old dither drew an arbitrary
  clock offset per session, so the base arm ranges 60.6-78.6% where this one
  ranges 70.3-78.4% (F = 3.8; unpredicted hits, 20.8-43.2% against 23.7-30.3%,
  F = 6.9). The residual quarter is untouched and unexplained by either arm:
  both simulations still have to acquire the same target and process every hit,
  and that is not shown here. The shown-health floor in `HealthFor` still covers
  the visible rebound (`.claude/multiplayer/NETWORK-PREDICTION.md`).
- **The scoreboard crash reported in bot matches is not reproduced here, and
  is therefore not fixed.** Reported from a phone, 2026-09-06: *"in bot matches
  the game still sometimes crashes when trying to view the scoreboard."* The
  scoreboard is now drawn on every `-maptest` run (`ModForceScoreboard`, two
  windows per run, a `MAPFAIL` if it never drew) and it was swept over all
  twelve game modes, 2 to 8 players, bots on, pro HUD on and off, and forced
  into `MatchState.Ending` — no crash anywhere, on the desktop. Two real
  defects **were** found on that path and fixed, and either could plausibly be
  it, but neither is confirmed as the cause: `GameState.Reset` cleared every
  per-slot array except `Nicknames` and `Stars`, so an offline match drew the
  previous *networked* match's roster; and `DrawText2D` indexed the font's
  width and offset tables with `ch - MinCharacter` unchecked in all four
  alignment branches, which is a crash for any character the loaded font does
  not cover — on a screen that draws eight names at once. What is needed to
  close this is a debug log from the phone it happens on: the render thread
  already catches and prints the whole exception (`GameView.Run`), so the
  stack is one switch away.
- **The raw gamepad fallback has never been held against a real unmapped
  pad.** `GamepadLayout`'s two shapes are written from the layouts SDL's own
  database uses for them, and the mapping-file path
  (`gamecontrollerdb.txt`, `SDL_GAMECONTROLLERCONFIG`) is exercised only by
  code inspection: this box has no `/dev/uinput` to fake a third pad with, and
  the virtual-pad recipe in `GAMEPAD.md` needs root. What is proven is that a
  mapped pad still takes the mapped path, since that code is unchanged. When a
  player reports buttons in the wrong places, `-gamepad` prints the mapping
  line to correct rather than a shrug.
- **Disruption over the wire is implemented and unmeasured.** `FlagBurning`
  was measured crossing (255 frames on the victim's own machine against the
  authority's 299, Kanden vs Spire, 70 s); `FlagDisrupted` is the same
  mechanism, the same shape and the same call site, and the scripted tour
  simply never landed a charged Volt Driver -- 21 hits in that run and not one
  of them disrupted anybody. The feature check counts it now, so the next run
  that manages one will say so.
- **Changing hunter between lives is proven offline and not in a match.** The
  swap itself was measured in a `-maptest`: requested while alive at frame
  241, still Samus through 500 frames of damage, dead at 781, back at 961 as
  Sylux on 99 energy in suit 2. What that run cannot show is the other half --
  the re-Identify, the roster, and every other client's `NetSlotManager.Sync`
  picking up the new hunter -- which needs two real clients and somebody
  opening the pause menu.
- **The enemy portrait's size was fixed by measurement, not by a picture of
  the fault.** `DrawHudObject`'s mode 0 was measured stretching a 32-unit
  sprite to 20.7, 27.7 and 36.3 units tall at 4:3, 16:9 and 21:9 (the weapon
  icon, three captures), which is what puts the opponent portrait over the
  name below it; the portrait itself is drawn for two seconds after a hit and
  the sampler never caught one.
- **The Windows half of the server's self-update is unrun.** The rename-aside
  path is written to Windows' own documented behaviour -- a running image
  cannot be deleted but can be renamed, since the mapping follows the file --
  and there is no Windows machine here to watch it happen. What *is* measured
  on this box: the Unix path is unchanged (delete then rename, as before), and
  the startup sweep really does delete a `.fp-old` and a `.incoming` left in an
  installation. The first Windows server to take a release is the test.
- **The Windows graphical launcher still lacks a full end-to-end window smoke on a native Windows runner.**
  Windows CI exercises shared input/controller code and publishes the GUI binary,
  but the automated `-shellshot` navigation still runs on Linux. macOS native
  CI now runs `-windowcheck` when an accelerated graphics renderer is available;
  real interactive gameplay remains covered by the separate macOS gap below.

- **Nobody has played a match from the launcher window.** It starts one and
  the launcher window goes away when it does (checked), but this box can't
  show a GLFW window at all (`Scene.OnRenderFrame` never produces a frame
  under its GL), so "Escape opens the pause menu over a running match" is
  proven on the menu's side (flags, windows, the pump) and unproven on the
  game's.
- **macOS interactive gameplay and Finder/Gatekeeper launch need manual verification.** Native CI now checks signed startup on both architectures; see `.claude/build-deploy/MACOS.md`.
- **The Android match runs on an emulator; how it *looks* there proves
  nothing.** With the game files copied onto the device, an emulator (API 30,
  x86_64, software CPU and SwiftShader) has been driven front screen → offline
  match → first person with the HUD, from a cold start in portrait. So
  `Mods/Render/GlEs.cs` — immediate mode, display lists, the current colour,
  the alpha test — does load a room and draw it. But SwiftShader puts vertical
  streaks through every surface in that build, with cel shading on and off
  alike, so every picture from it is good for "it ran" and for nothing else.
  Rendering is judged on the desktop. `.claude/android/ANDROID-PORT.md` lists
  what to watch on a first run, in order.
- **The portrait freeze is reproduced and fixed; the fix is proven by
  measurement, not by playing.** A room load stretched to 12 s with a window
  resize injected into it held the UI thread for 16,921 ms under
  `GLSurfaceView` -- three times Android's ANR threshold, which is the white
  box over the black loading screen -- and for at most 1,092 ms, none of it
  during the load, once `GameView` owned its own EGL context and thread. What
  has *not* been shown is the same fix on a real phone under a real load, and
  the match has only been driven on this emulator afterwards: it starts from
  portrait, survives home-and-back, backs out and starts again.
- **The cel shading has only been judged on the desktop.** Flat colours in
  place of textures and the depth-kink ink pass were shot across five rooms
  and a live two-client match at 1600x900, and cel *off* is pixel-identical to
  before the change.

  On the emulator the mode is **unusable, and the reason is measured**: the
  ink pass reads a flat surface's kink at 0.004-0.009 under llvmpipe and at
  235-256 under SwiftShader, against a threshold of 1.1. Thirty thousand times
  the noise, on a depth field whose large-scale structure is correct -- the
  same per-pixel imprecision that streaks SwiftShader's colour, on its depth.
  Nothing in the shader survives that, and a threshold that did would draw no
  outline at all -- which is now what happens, on its own:
  `Renderer.CalibrateInk` measures a flat surface at **1998** there against
  **0.0159** here and lifts the ink floor to match, so the mode degrades to no
  outline rather than to a black screen. **What that floor does on a phone
  whose depth is merely mediocre rather than useless is untested**, and that is
  the case that matters. (An earlier claim here that the ES path had been seen
  drawing the mode was wrong: those runs had cel shading *off* -- see the
  settings-directory note in `android/ANDROID-PORT.md`.)
- **A phone is still a different machine** — the emulator is x86_64 with
  SwiftShader, a phone is arm64 with a real driver. That is the ABI and the GL
  implementation both differing from what is tested here.

- **Opening the external release/update page is not manually verified on every desktop.**
  The in-app server browser itself is exercised by launcher checks; this gap is
  specifically the OS handoff used by `OpenPage` (for example macOS update-page
  fallback), especially Windows `UseShellExecute`.
- **Legacy service-unit migration is deployment-specific, not assumed complete.**
  `deploy-server.sh` can rewrite older systemd units/binary names, but live host
  state must be checked on the host after deployment. Do not treat this file as
  evidence of what a particular Pi/VM is currently running.

- **The ARM64 server package has never been started by CI** — cross-compiled
  on an x64 runner, so `check-dedicated-server.sh` can't run it there.
  `linux-x64-server` (same build config, a processor the runner actually has)
  is the nearest CI gets; the Pi via `deploy-server.sh` is the real test.
- **The Windows dedicated-server binary gets its startup contract in CI, but
  not a real match.** CI cannot contain the operator's game files, so the
  Windows job proves the console binary, server parsing, actionable missing-data
  refusal and exit behavior. A long-running authoritative Windows match behind
  a real firewall still needs field validation.
- **Hosted-server capacity must be re-measured for isolated authoritative
  processes.** The old 20-game/160-player Pi ramp measured the former
  in-process/client-authority hosting architecture with synthetic players.
  Hosted games now spawn isolated authoritative server processes, so the old
  density number is historical and must not be used as a current capacity
  claim. A new ramp needs CPU, RSS, 60 Hz simulation health, delivery and
  joinability per child process.
- **An old client against the new server is untested.** The Pi has run the
  2026-09-01 server since that date, and both refusals were then proved on the
  wire: a full server answers a ninth Hello with `Refused` reason 1 in 11 ms,
  and a protocol-3 Hello with reason 2. What has not been tried is a client
  built *before* `RefusedPacket` meeting that server -- by design it drops an
  unknown packet type and falls back to the eight-second timeout it always
  had, but nobody has run it.
- **The rotation crash was found on the public server's own rotation and
  fixed there; no other pair of maps has been tried.** The mechanism -- a
  pooled player's NodeRef into the room just unloaded -- does not depend on
  which rooms they are, but only MP1 SANCTORUS -> MP3 PROVING GROUND has been
  run, three clients at a time.
- **Late joiners and bursty features skew the tour's numbers**, not the
  replication. Clients start ~3 s apart; a client that joins a bursty phase
  (bombing, unmorphing) late reports a fraction of what the subject did, and
  time-normalisation can't fix a burst it wasn't there for. Judge against
  clients that were present, not the raw tally.
- **Alt-attack presses read ~60% on every observer.** Not loss (loss would
  differ per observer) — two presses inside one intent window arrive as one,
  since the edge history is ORed into a single mask per packet. The bombs
  those presses would have laid still land 79-99%.
- Kanden and Spire show lower fidelity than other hunters on `unmorph` and
  projectile lifetime. Not explained.
- The scoreboard rows tighten to fit past four players, down to 19 px; beyond
  eight would need a second column.
- The First Hunt "biodefense chamber" rooms are listed as multiplayer but
  carry no player spawn points — survival rooms, kept out of the launcher's
  map list and out of any Battle rotation rather than "fixed".
- `zoom` and `double damage` are usually `untested`, since nothing in the
  tour reliably picks either up. `double damage` is probably fine — item
  pickup is simulated from replicated positions and three clients in a 90 s
  match agreed exactly (`12`, `12`, `12`) — but "probably fine" isn't
  "measured".

## A relative root in paths.txt used to break every read

`AMHE0=files\AMHE0` -- a path relative to the program -- made the game report
every room's spawns and every hunter model missing, from a folder with the
root repeated two or three times:

    ...\files\AMHE0\files\AMHE0\files\AMHE0\levels\entities\mp3_Ent.bin

The files were where they should be. Several read paths combine the root in
more than once -- `Read.GetEntities` combines it, calls `GetEntitiesFromPath`
which combines it again, which calls `ReadBytes` which combines it a third
time -- and that is invisible for an absolute root, since `Path.Combine(abs,
abs)` is `abs`. Only a relative one stacks, which is why nobody had seen it:
the launcher writes an absolute path and a hand-written relative one is rare.

`Paths.Absolute` makes every root from paths.txt absolute as it is read,
against the program's own directory. Reproduced and fixed on the same machine:
a relative root gave 30 spawn failures and 1 missing model before, 0 and 0
after.
