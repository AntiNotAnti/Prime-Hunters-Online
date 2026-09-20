# CLAUDE Index

Read `../ARCHITECTURE-INVARIANTS.md` first. It is the short machine-oriented contract for current architecture. `CLAUDE.md` is the larger developer guide: identity, paths, environment, commands, historical context and pointers into the topic files below. The topic files hold
the depth — read the one you need for the area you're touching rather than
loading everything.

- KNOWN-GAPS.md — unresolved or unverified claims only; fixed items must be removed
- android/ANDROID-PORT.md — the GL ES renderer, the touch controls, building the APK
- launcher/LAUNCHER-OVERVIEW.md — entries, platforms (incl. macOS/Android), threading
- launcher/LAUNCHER-WINDOW.md — one window: the launcher and the pause menu drawn inside the game window
- launcher/LAUNCHER-DESIGN.md — UI components, logo/assets, pitfalls
- launcher/LAUNCHER-SETTINGS.md — settings window layout and toggles
- launcher/LAUNCHER-FIRSTRUN.md — extraction flow and progress bar
- DEBUG-LOGS.md — the launcher's corner switch: what it writes, where, and why it exists
- GAMEPAD.md — controllers on the desktop and Android: the layout, the feel, and how to test one without owning one
- multiplayer/NETWORK-BROWSER.md — server discovery, directory, hosting
- multiplayer/NETWORK-CHAT.md — the in-game chat line: the packet, the relay's rules, the input traps
- multiplayer/NETWORK-DEMOS.md — recording and replaying a match: format, clocking, the gaps
- multiplayer/NETWORK-MATCHEND.md — match end, rotation, the double-counted-kill bug
- multiplayer/NETWORK-DIAGNOSTICS.md — the full damage-bug postmortem, traps, diagnostics
- multiplayer/NETWORK-SERVERAUTH.md — the server as the simulation authority: the headless engine, what moved, what did not, and what a room costs a server
- multiplayer/NETWORK-UNLAGGED.md — lag compensation: the rewind, the projectile catch-up, what was not ported from Q-Zandronum, how it is measured
- multiplayer/NETWORK-PREDICTION.md — current local outgoing-hit prediction/reconciliation rules, plus clearly labeled historical lethal-prediction experiments
- multiplayer/NETWORK-HITCLAIMS.md — a client declaring which of its own shots landed and the authority arbitrating them: the five checks, the grace window, and the rule that decides who dies when two people kill each other
- multiplayer/NETWORK-SMOOTHING.md — remote players read off a playout clock instead of snapped to whichever snapshot arrived last, and the sub-frame ack that keeps hit registration exact through it
- render/CEL-SHADING.md — flat colours in place of textures, and the depth-kink ink pass
- render/RENDER-STABILITY.md — explicit GL pass boundaries, respawn diagnostics and the rendered stress check
- render/FRAME-PACING.md — 60 Hz of simulation under a picture drawn at the display's rate: the split, why interpolation was taken back out, and how both halves are tested without a 144 Hz monitor
- mapgen/MAP-PIPELINE.md — custom maps: the generator, the Quake 3 importer, the format traps
- testing/HITRIG.md — the headshot rig: the geometry a headshot turns on, why the feature tour cannot measure it, and how an A/B arm is run
- testing/TEST-HARNESS.md — netcheck/maptest, map sweeps, the world and affliction probes
- testing/TEST-HARD-CASES.md — disconnects, blackouts, latency, loss, capacity, spectators and legacy authority regressions
- testing/TEST-METRICS.md — reading results, common traps, last verified status
- build-deploy/BUILD-WORKFLOW.md — CI workflows, tagging and the bump, release notes, binaries, asset guard
- build-deploy/MACOS.md — native builds, signing, bundles, smoke tests and user-data paths
- build-deploy/DEPLOY-SERVERS.md — deploy script and publish commands

Usage: these are the token-optimised detail store for CLAUDE.md. Current code and tests outrank prose. When behavior changes, update ARCHITECTURE-INVARIANTS.md, CLAUDE.md and the affected topic file in the same change. Keep dated measurements as historical evidence, not as statements of current architecture.
