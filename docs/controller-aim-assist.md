# Controller aim and head tracking

Controller assistance runs once per 60 Hz simulation step for the local player.
The pure core lives in `Mods/Input/AimAssist`; `AimAssistWorld` supplies visible
enemy positions from the locally presented world. Mouse/touch input, menus,
spectating, death, alternate form, and replay/remote actors bypass assistance.
A neutral aim stick never rotates the camera, even while strafing.

## Camera units and presentation

Stick response curves, inversion, scoped sensitivity, and zoom scaling are applied
before assistance, so its errors, corrections, and velocity estimates all use
actual camera degrees. The camera applies that result without scaling it again.
Velocity history records the rotation actually consumed, including pitch limits.
Extra render frames project that accepted turn instead of projecting an unassisted
stick turn. They never advance target selection or motion history.

## Targeting and head refinement

- Selection measures the visible chest-to-head region and retains target identity
  through small scoring changes. An exposed head can be acquired above cover.
- Head position stays at the center of the existing 0.3-unit headshot band;
  this follows the game's hit rules, rather than cosmetic animation bones.
- Refinement requires continuous head eligibility. Settling on a head allows full
  head weighting; the chest no longer imposes a permanent downward bias.
- Prediction is bounded by both a global angular limit and the apparent size of
  the head. Motion filters respond faster when a strafe or jump reverses.
- Loss of visibility immediately removes correction toward that point. Brief
  complete occlusion preserves identity only; reappearance starts fresh motion
  history. Weapon/zoom, device, input source, and context changes reset history.
- Assistance ramps smoothly at low stick deflection, respects opposing input,
  caps total rotational speed, and cannot add to a deliberate stick overshoot.

Weapon-specific profiles remain in `AimAssistTuning.cs`. Tracking, projectile,
and splash profiles retain body targeting. No damage, hitboxes, projectile
trajectories, or networking rules are changed.

## Validation

Run `dotnet run --project src/MphRead -- -gamepadcheck` for deterministic controller,
assist, camera-unit, and UI checks. Tracking scenarios cover stationary targets,
strafing/jumping heads, 30/60/120 Hz integration, mixed-input handover, partial and
complete cover, invalid observations, pitch limits, and allocation-free operation.
Run `dotnet run --project src/MphRead -- -frametimingcheck` for presentation checks.

For controller playtesting, `-gamepadassistdebug` displays the selected target and
assist state. `-gamepadassistbaseline` bypasses assistance for comparison, and
`-gamepadassisttelemetry <path>` records diagnostics. Automated checks do not
replace hardware playtesting or multiplayer balance evaluation.
