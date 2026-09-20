# P0 render stability and respawn diagnostic

`Mods/Render/RenderPassState.cs` is a Scene partial holding explicit world,
postprocess, composite and HUD state boundaries. World setup restores write
masks before clearing; screen passes establish depth, culling, scissor,
stencil, blend equation/factors, texture units and shader defaults. RTT vertices
are clip-space already and use no inherited projection matrix. World material
matrices remain owned by each RenderItem.

HUD masks have a `try/finally` cleanup. A HUD sprite only enables masking
inside a valid mask scope. The frame wrapper restores the default framebuffer,
window viewport, texture unit 0, unbound mask texture, zero mask/fade uniforms
and writable depth even on early exits. DEBUG builds assert these invariants
and check GL errors at pass boundaries; Release removes those checkpoint calls.

Framebuffer completeness is required at creation, depth attachment changes,
cel color attachment changes and resize. The existing logged depth-texture
fallback remains, but the restored renderbuffer must also validate. Resize
publishes `_targetSize` only after every attachment validates, and restores
the incoming read/draw framebuffer bindings. Nonpositive window dimensions
skip drawing/resizing; scaled allocation dimensions are at least one pixel.
The cel copy path remains for P0; the later P1 rewrite is outside this PR.

Scene teardown also deletes the cel framebuffer and clears its attachment
cache before deleting scene textures. The shell retains its GL context between
matches: leaving an unbound cel framebuffer alive leaks the object and its
reference to the scene color storage. This omission predates the stability PR.
`dotnet run --project tools/render-resource-check/render-resource-check.csproj -c Release`
checks real cel allocation/reuse and repeated scene teardown over three cycles
in one hidden desktop GL context, using a synthetic 32x32 attachment and no
game assets. It does not exercise Android GL or gameplay/respawn rendering.

Main-player Spawn clears temporary disruption, whiteout, damage indicators,
weapon-wheel visuals and cached HUD layers via `PlayerRespawnVisuals.cs`.
Existing spawn resets still handle ice, damage flash and model alpha. Scene
fades and scripted-camera transitions are preserved. Simulation lifecycle hooks
record CPU state without GL calls; the next rendered frame records GPU state.
Debug logs include death, respawn request, spawn begin/end, and rendered offsets
0, 1, 2, 5 and 10 after a spawn, rather than querying/logging every frame.

## Rendered stress diagnostic

`Mods/Render/RespawnRenderCheck.cs` supplies a standalone real desktop GL
diagnostic. It follows MapAudit's Scene/GameWindow setup and fixed simulation
steps. It does not launch the shell, apply updates, or run updater cleanup.
`paths.txt` and extracted assets must already be configured beside the DLL.
The command does not generate missing custom-map binaries.

Run from the configured output directory, first 100 cycles, then 500:

```powershell
dotnet .\FruityPrime.dll -respawnrendercheck "MP3 PROVING GROUND" -cycles 100 -timeout 1800
dotnet .\FruityPrime.dll -respawnrendercheck "MP3 PROVING GROUND" -cycles 500 -timeout 3600
```

The timeout is wall seconds. Default cycles/timeout are 100/1800; bounds are
1..5000 cycles and 1..86400 seconds. Exit 0 means pass, 1 means a failed,
interrupted or incomplete run, and 2 means invalid arguments or unsupported
Android execution. A small `-cycles 9` run covers every scenario for initial
runtime validation; it is not the 100/500-cycle acceptance run.

The real window starts visible and unfocused, with a free cursor. It never
requests focus or grabs the mouse. Frozen keyboard/mouse snapshots isolate
the scene from subsequent desktop input. Keep the window unminimized; a
hidden window cannot reliably provide a default backbuffer. VSync is off;
one 1/60-second simulation step is followed by one actual rendered frame.
The report separates simulated time, wall time, and extra frozen-view draws.
This accelerates wall-clock execution without skipping simulation or rendered
settling frames. The internal timeout is checked between frames, so an
external process timeout is still needed to bound a hung native GL call.
The console prompt worker is disabled for this unattended invocation. Cleanup
stops the scene, waits up to ten seconds for its asynchronous OpenAL device
shutdown, and releases the one-shot music backend before native teardown.
Normal gameplay keeps its existing asynchronous audio shutdown behavior.

Every ordinary cycle kills the main player through `TakeDamage`, verifies
the real death counter, waits for the normal respawn timer, and uses the
existing MapAudit/NetHooks force-spawn hook to request respawn without a
button press. It then renders 300 settling steps (five simulated seconds).
The nine scenarios rotate deterministically:

- Opponent damage, self damage, and environmental `DamageFlags.Death`.
- Beam-source damage using a Power Beam entity as the source. This tests
  beam death attribution and visuals, not projectile launch/collision.
- Rapid re-kill one rendered frame after respawn, followed by another
  normal respawn and the full five-second settling period. This adds one
  real death/respawn per rapid cycle; the summary checks the exact total.
- Active disruption, active whiteout, recent scoreboard, and their combination.
  Whiteout is rearmed just before the natural respawn so it crosses Spawn.
  Disruption is injected through the existing player hook before death;
  ordinary death cleanup may end it before Spawn.

Each final respawn checks that disruption and whiteout were reset. Settling
changes the window 640x480 -> 800x450 -> 640x480, render scale 100 -> 50 -> 100,
and cel off -> on -> off. Transition counters must meet the requested count.
Match score/time limits are disabled for the diagnostic only.

Every rendered simulation step checks `Scene.CheckRenderInvariants()` before
readback and polls GL errors. `RenderGlErrorCount` also catches errors already
consumed by renderer DEBUG checkpoints. Living frames outside black fades
read the complete final RGB backbuffer before swap. A full frame with every
channel <= 3 fails. This deliberately does not implement near-black regions,
HUD bar detection, or scene/postprocess captures. Black fade types are excluded
and counted; each completed settling interval must supply at least 120 eligible
frames and must end outside a black fade, preventing an indefinitely fading
run from passing without pixel checks.

## Deterministic pass-boundary control

After initial settling, before the first death, simulation is frozen. In each
of cel off/on modes the check warms the target, takes a clean baseline, and
compares another clean draw byte-for-byte. FPS text is disabled because it
depends on wall time. Any clean-repeat difference fails instead of weakening
the comparison tolerance.

Seven subsequent draws independently poison scissor, depth writes/function,
color writes, blend equation/factors, raster state (culling, polygon mode,
stencil and viewport), HUD `use_mask` plus texture unit 1, and all of these
together. Each must restore the exact baseline RGB picture and end-frame
invariants: 14 poisoned comparisons across the two cel modes. The old final
backbuffer is cleared before poisoning so suppressed writes cannot retain a
good picture and pass accidentally. The final HUD program supplies the
`use_mask` location; unavailable coverage is a failure. Frame count and
simulation time must remain unchanged during this control.
The opaque poison mask uses a reserved desktop texture name, avoiding Scene's
implicit allocation counter. The check forces a positive cel-edge setting and
requires the actual outline-pass counter to advance; a depth-texture fallback
is reported as unavailable coverage, never silently counted as a cel pass.

For a regression proof, the renderer owner can temporarily disable the new
pass-boundary setup in a separate test build and run the same command. The
control must report pixel/invariant failures and a nonzero exit; restore the
boundaries and require the positive run to pass. The harness never edits or
disables production boundaries itself. Both arms still require execution;
the existence of the control is not evidence that either arm passed.

## Failure artifacts and limits

Debug logging is forced for this invocation. Up to three offending final-frame
PNGs are written under `logs/respawn-render-<UTC timestamp>-<pid>/` beside the
DLL; output identifies the exact paths. The existing ScreenCapture encoder
seam is reused directly because SaveWindow intentionally refuses black images,
which are essential evidence here. Image-write errors are reported and do not
turn a failed rendering check into a pass.

This harness does not claim fullscreen/alt-tab coverage, HUD-region visual
correctness, normal gameplay/performance equivalence, or arbitrary driver
acceptance. Mesa/XWayland remains mandatory to close issue #34; it is not
available on this Windows host. The broader P1/P2 work is deferred.

## Validation on 2026-09-16

Windows, NVIDIA RTX 5080, OpenGL 4.6 / driver 610.88, default audio environment:

- Debug 100-cycle run: exit 0, 111 real deaths/respawns (rapid cases add one),
  51,780 rendered simulation steps, 31,872 checked backbuffers, zero full-black
  frames, invariant failures or GL errors. Resize, scale and cel each changed
  200 times. Simulated time 863 seconds; wall time 71.8 seconds.
- Final Debug 500-cycle run: exit 0, 556 deaths/respawns, 257,880 rendered
  simulation steps and 158,317 checked backbuffers. Zero full-black frames,
  invariant failures or GL errors; resize, scale and cel each changed 1,000
  times. Simulated time 4,298 seconds; wall time 334.5 seconds.
- All 14 poisoned-state comparisons restored the exact clean RGB image,
  including actual cel outline execution, without advancing simulation.
- Negative control with the world baseline omitted: depth/blend poisoning
  changed 141,721–271,876 of 307,200 pixels in four comparisons. Restored after
  building a separate local diagnostic binary.
- Separate negative control omitting the HUD shader mask reset: four failed
  comparisons, 298,886–298,922 pixels changed, black world with HUD text still
  visible; exit 1. Restored production code passes those comparisons.
- Desktop Debug, Windows Release publish, server Release and Android Debug
  builds pass. Android has 14 existing documentation warnings.
- Headless simulation: both players spawned, 120 steps, exit 0. Frame timing
  regression: all cases passed, exit 0.

Earlier diagnostic versions printed successful render metrics but failed native
process teardown. Those exits are not counted as successful acceptance runs.
The harness now waits for OpenAL shutdown and releases its one-shot music
engine; the final 100- and 500-cycle runs above exited normally. The updater
canary survived all diagnostic runs, confirming that they skip updater cleanup.
