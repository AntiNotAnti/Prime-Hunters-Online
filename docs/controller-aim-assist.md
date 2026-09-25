# Controller aim and head tracking

Aim assistance is intentionally curated internally. There are no user-facing aim-assist strength or snapping settings.

Assistance runs once per fixed 60 Hz simulation step for the local player. Render
frames project the accepted assisted camera turn and, above 60 Hz, may preview only
the raw difference from a newer aim-stick hardware sample. They never advance
selection, filtering, flick state, motion history, button edges, firing or telemetry.
The next simulation step consumes the exact previewed aim axes before accepting a
newer hardware sample, preventing double-turn or presentation/gameplay disagreement.
Mouse/touch, menus, spectating, death, alternate form, replay actors and remote
players bypass assistance. Device, input source, weapon, zoom and context changes
clear history.

## Geometry and intent

`AimAssistWorld` projects the presented player's collision cylinder into angular
body and headshot regions. Biped bounds use MinPickupHeight/MaxPickupHeight and
the collision radius. The head band is MaxPickupHeight minus 0.3 through
MaxPickupHeight. The projected rectangle encloses the cylinder silhouette. Visibility
belongs to the hittable region rather than its center: nearest, center and deterministic
inset-edge rays are tested against the actual cylinder/band and room geometry, so a
legitimate head/torso slice peeking around cover can remain assistable without allowing
tracking through solid cover. Region error measures the nearest valid boundary and is
zero inside the real hit region. Alt forms never receive head refinement.

Power Beam and Volt Driver refine heads only through 15 world units. Imperialist
allows head refinement throughout the existing 60-unit assist range. Shock Coil,
Battlehammer, Judicator, missiles, Magmaul and Omega remain body-oriented.
Imperialist has zero positional lead: velocity is feed-forward tracking, never a
predicted impact point. Standard profiles also currently use zero lead.

Physical stick intent is sampled after calibration and radial deadzone, before
filtering, response curves, sensitivity and FOV. Direction includes inversion,
so intent shares the camera's angular signs but not its sensitivity scaling.
Selection, opposition and flick detection use this physical vector. Camera delta
is separately used for rotation, velocity compensation and overshoot limits.

## Acquisition, retention and precision

Acquisition requires right-stick intent. Selection favors region overlap,
proximity and input alignment. A retained firing/charging target receives an
extra bonus. Challengers must clear both a score ratio and an absolute margin;
strong aligned input reduces the switching penalty.

Position correction and motion tracking have separate gains and independent speed
limits. Their sum is not re-clamped through the old MaxSpeed ceiling, so a precision
profile can keep conservative positional magnetism while matching a fast retained
target. Vertical head gains exceed horizontal gains. Target motion uses a bounded
velocity/acceleration servo: measured angular velocity is filtered, reversals accelerate
the filter, bounded angular acceleration supplies a tiny camera-tracking lookahead, and
none of it leads the projectile impact point.

Retention is continuous rather than a timer switch. Deliberate aligned tracking builds
0..1 confidence; neutral right stick never acquires. While the player strafes, confidence
decays slowly and scales retained motion tracking from 18% to 35%. Position attraction
is disabled during neutral-stick strafe tracking. Meaningful opposing input, target loss,
release-cone/lifecycle changes or prolonged neutrality clear the state immediately or
decay it to zero. Brief occlusion retains identity for 60 ms but applies no friction or
rotation and resets motion history.

Flick detection recognizes both a rapid magnitude rise and a fast change in stick vector,
so a player already holding substantial horizontal input can flick vertically toward a
head. At flick start there is one trajectory-weighted head-target selection pass; after
that the chosen target is locked for the 90 ms capture window. Capture still requires
visible, mechanically eligible head geometry, strong flick alignment and proximity within
the 0.35–0.8 degree range scaled by apparent band height. Scoped capture uses 65% of that
radius. Neutral, opposing and occluded input cancel it.

The real headshot band remains the outer validity region. Inside it, an inset safe region
adds only a weak positional nudge when the crosshair approaches an edge, leaving the
middle of the band free of center pull. Friction is also edge-aware: it primarily damps
the component about to overshoot the edge being approached, while strong deliberate
stick input rapidly releases the guardrail. Assistance never adds a push beyond the
remaining valid correction.

## Stick response

The existing Linear, Classic, Precision and Dynamic serialized values remain
unchanged. Linear remains linear; other presets use micro-aim (0–35%), tracking
(35–80%) and fast-turn segments. A small exponential filter smooths low-speed
noise, bypassing reversals and large flicks. Outer-stick acceleration smoothly
approaches a magnitude-dependent maximum rather than using a hard delay.

## Diagnostics and validation

Run `dotnet run --project src/MphRead -- -gamepadcheck` and
`dotnet run --project src/MphRead -- -frametimingcheck`. Checks cover region
boundaries, safe head interiors, mechanical range, physical intent, direction-change
flicks and flick target selection, confidence-based strafe retention, edge-friction
escape, independent tracking caps, bounded acceleration, opposition, camera units,
tracking reversals, high-refresh late-latch isolation, timing and zero-allocation core
processing. The aim changes do not alter damage, hitboxes, projectile behavior, lag
compensation or server authority; protocol 20 in this branch is solely the analogue
movement-axis extension.

`-gamepadassistdebug` displays regions, state, physical stick, camera delta,
position/tracking corrections, flick age/alignment, velocity and strafe retention.
`-gamepadassisttelemetry <path>` saves per-input/weapon/range buckets at exit.
Metrics include head-region entries/exits and errors, nearby headshot attempts,
flick attempts/captures, firing switches, opposition breaks and correction means.
Confirmed headshots come only from authoritative damage flags; speculative client
hits are excluded. Thus confirmed-headshot metrics require host/offline play.
Hit events are attributed to the latest shot bucket for that weapon, as in the
existing telemetry; they are not per-projectile conversion probabilities.
`-gamepadassistbaseline` bypasses camera assistance for comparison.

Hardware feel and balance still require playtesting every weapon at close/mid/far
range against strafes, jumps, cover and crossing opponents, with 30/60/120/144/240+
FPS presentation. Automated checks establish behavior, not measured headshot-rate
improvement or subjective input latency.
