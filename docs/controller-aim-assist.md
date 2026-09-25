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
MaxPickupHeight. The rectangle remains only a broad scoring/debug envelope. Precision
correction now minimizes angular distance to rays that actually intersect the cylindrical
hit surface inside the requested vertical band, so an angular-envelope corner is no
longer treated as hittable merely because it lies inside the rectangle.

Visibility is sampled only at proven cylinder/band intersections and returns 0..1
coverage rather than a boolean. A legitimate peek can stay selectable, but friction and
retained tracking scale down with exposed surface area. No correction is emitted through
solid cover. Alt forms never receive head refinement.

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
target. The servo now supplies only target angular velocity the player's camera is not
already matching. Target yaw/pitch motion also shares a persistent direction estimate,
which damps minor-axis corkscrew noise during diagonal strafe+jump motion.

The control law classifies each sample as approaching, braking, matched, overshooting or
escaping from the target surface. Approach receives little resistance; braking and
overshoot receive precision damping; matched tracking stays light; deliberate escape
releases immediately.

Retention is continuous rather than a timer switch. Body and head tracking have separate
0..1 confidence values, so torso engagement cannot instantly grant full head retention.
Neutral right stick never acquires. While the player strafes, blended body/head confidence
scales retained motion tracking from 18% to 35%; positional attraction remains disabled.

Brief occlusion still applies zero friction and zero rotation, but the last visible
velocity/acceleration estimate decays internally instead of being erased. Hidden positions
are never used to update it. A target that reappears within grace therefore resumes from
remembered motion rather than a dead stop.

Flick detection recognizes both rapid magnitude rise and fast vector changes, and keeps
short physical-stick history. It detects the braking/settling half of a flick and predicts
the unassisted landing from current camera velocity/acceleration. Capture is allowed only
when the natural trajectory already reaches or nearly reaches the mechanically valid head
region. Flick speed changes the finishing envelope: fast intentional flicks get a modestly
larger radius and shorter landing horizon; slow micro-aim stays narrow; very fast misses
shrink again. The selected head is locked for the short capture window.

The real headshot band remains the outer validity region. Inside it, a weak inset safe
pocket shifts by at most 12% with target angular motion, always clamped back inside the
mechanical band. This is retention room, not projectile lead. Edge friction primarily
damps the component about to overshoot the approached edge, and the next frame's stick
filter becomes more transparent near precision boundaries so tiny corrective reversals
are preserved.

A short shot-commit state locks retained target identity for roughly 50 ms when fire is
pressed near a valid target/head surface. It can slightly strengthen edge protection but
never increases positional snap. Normal target selection is also trajectory-aware outside
flicks, preferring a target the current camera path will cross over a marginally closer
off-path candidate.

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
compensation or server authority. The current wire remains protocol 21; no aim-assist
state is added to networking.

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
