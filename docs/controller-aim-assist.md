# Controller aim and head tracking

Aim assistance is intentionally curated internally. There are no user-facing aim-assist strength or snapping settings.

Assistance runs once per fixed 60 Hz simulation step for the local player. Render
frames project the accepted assisted camera turn and, above 60 Hz, may preview only
the raw difference from a newer aim-stick hardware sample. They never advance
selection, filtering, flick state, motion history, button edges, firing or telemetry.
The next simulation step consumes the exact previewed aim axes before accepting a
newer hardware sample, preventing double-turn or presentation/gameplay disagreement.
Mouse/touch, menus, spectating, death, alternate form, replay actors and remote
players bypass assistance. Device, input source, weapon and room/context changes
clear history. A zoom transition does not: camera FOV is already the common angular
unit, so the retained target, body/head confidence and target-motion history survive
scope-in/out while transient flick and shot-commit state is cleared.

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

Acquisition requires right-stick intent. The broad degree cone remains a safety
fence, but primary acquisition/release distance is normalized by the target's
projected half-width and half-height. One target radius therefore means the same
thing at close, medium and long range instead of a fixed number of degrees changing
meaning with distance. Selection favors normalized surface proximity, trajectory
intersection and input alignment. Challengers must clear both a score ratio and an
absolute margin; strong aligned input reduces the switching penalty.

Production tuning is per `BeamType`, not only a generic weapon category. Power Beam,
Volt Driver, Imperialist, Shock Coil, Missile, Magmaul, Judicator, Battlehammer and
Omega each have internal acquisition, friction, body-height, tracking, servo and
correction-budget behavior. These are curated constants, not player-facing options.
Imperialist additionally blends hip and scoped profiles continuously from the actual
animated camera FOV, so a quick scope does not abruptly replace the controller model.

Position correction and motion tracking still have independent caps during acquisition.
After a target has been deliberately retained for roughly 75 ms, the follower changes
to a critically damped second-order servo in target-normalized coordinates. The
proportional term is normalized surface error and the derivative term is only the
target angular velocity the player's camera is not already supplying. This removes
range-dependent tuning and reduces ringing on AD strafes without increasing initial
snap. Target yaw/pitch motion still shares a persistent direction estimate to suppress
minor-axis corkscrew noise on diagonal strafe+jump motion.

The control law classifies each sample as approaching, braking, matched, overshooting or
escaping from the target surface. Approach receives little resistance; braking and
overshoot receive precision damping; matched tracking stays light; deliberate escape
releases immediately.

Retention is continuous rather than a timer switch. Body and head tracking have separate
0..1 confidence values, so torso engagement cannot instantly grant full head retention.
Neutral right stick never acquires. While the player strafes, blended body/head confidence
scales retained motion tracking from 18% to 35%; positional attraction remains disabled.

Visibility coverage is temporally filtered: loss decays quickly while newly exposed
surface rises more slowly. A fresh target must expose a meaningful surface slice before
it can acquire, while an already-retained target keeps the short occlusion grace.
Brief occlusion still applies zero friction and zero rotation, but the last visible
velocity/acceleration estimate decays internally instead of being erased. Hidden positions
are never used to update it. A target that reappears within grace therefore resumes from
remembered motion rather than a dead stop.

Flick detection recognizes both rapid magnitude rise and fast vector changes, and keeps
short physical-stick and camera-velocity history. Landing prediction fits the four most
recent camera-velocity samples instead of trusting one frame, then evaluates the miss in
projected head radii. Capture is allowed only when that natural fitted trajectory already
reaches or nearly reaches the mechanically valid head region. Flick speed changes the
finishing envelope: fast intentional flicks get a modestly larger normalized radius and
shorter landing horizon; slow micro-aim stays narrow; very fast misses shrink again. The
selected head is locked for the short capture window.

The real headshot band remains the outer validity region. Inside it, a weak inset safe
pocket shifts by at most 12% with target angular motion, always clamped back inside the
mechanical band. This is retention room, not projectile lead. Edge friction primarily
damps the component about to overshoot the approached edge, and the next frame's stick
filter becomes more transparent near precision boundaries so tiny corrective reversals
are preserved.

Shot commitment is weapon-state aware. Immediate weapons may commit on the firing press;
charge weapons commit around release/actual shot rather than the beginning of a long
charge; continuous fire is identified separately. A short commit locks retained identity
for roughly 50 ms near a valid surface and may strengthen edge protection, but never
increases positional snap. Abrupt target-motion transitions such as a strafe reversal,
jump apex/landing or impulse clear stale acceleration and temporarily speed convergence.
Normal target selection remains trajectory-aware outside flicks.

## Correction budget and stick response

Each weapon/state has a small leaky correction budget measured in assisted camera
degrees. Tiny rescue corrections can therefore be sharp, but sustained automatic
pull spends the budget and must recover before more assist can be supplied. Deliberate
player input always remains outside this budget and escapes immediately. Projected
head aspect also redistributes horizontal/vertical precision gain: a narrow vertical
band receives more vertical precision without inventing a stronger horizontal pull.

The existing Linear, Classic, Precision and Dynamic serialized curve values remain
unchanged. Linear remains linear; other presets use micro-aim, tracking and fast-turn
segments. Near a precision boundary, the low-speed noise filter becomes more transparent
and outer-stick acceleration is actively driven back toward 1x during braking,
overshoot, head refinement and shot commitment. This prevents the controller's own
turn acceleration from fighting the precision controller.

Explicit calibration now also measures eight radial gate sectors so a square/elliptical
or worn stick produces uniform circular travel after normal axis calibration. At runtime
each connected device keeps a tiny unsaved centre offset learned only while the stick is
well inside its deadzone with buttons/triggers at rest. It is clamped to +/-0.03 and
never becomes a player-visible setting.

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
flick attempts/captures, firing switches, opposition breaks and correction means,
plus player-vs-assist angular contribution, assist share, correction in the final
frames before a shot, pre-shot head dwell, overshoots, motion transitions, player-
versus-target-caused head exits, scope-transition target loss and reacquire time.
Confirmed headshots come only from authoritative damage flags; speculative client
hits are excluded. Thus confirmed-headshot metrics require host/offline play.
Hit events are attributed to the latest shot bucket for that weapon, as in the
existing telemetry; they are not per-projectile conversion probabilities.
`-gamepadassistbaseline` bypasses camera assistance for comparison.

Hardware feel and balance still require playtesting every weapon at close/mid/far
range against strafes, jumps, cover and crossing opponents, with 30/60/120/144/240+
FPS presentation. Automated checks establish behavior, not measured headshot-rate
improvement or subjective input latency.
