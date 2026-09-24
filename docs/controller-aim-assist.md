# Controller aim and head tracking

Aim assistance is intentionally curated internally. There are no user-facing aim-assist strength or snapping settings.

Assistance runs once per fixed 60 Hz simulation step for the local player. Render
frames project the accepted camera turn; they never advance selection, filtering,
flick state, or motion history. Mouse/touch, menus, spectating, death, alternate
form, replay actors and remote players bypass assistance. Device, input source,
weapon, zoom and context changes clear history.

## Geometry and intent

`AimAssistWorld` projects the presented player's collision cylinder into angular
body and headshot regions. Biped bounds use MinPickupHeight/MaxPickupHeight and
the collision radius. The head band is MaxPickupHeight minus 0.3 through
MaxPickupHeight. The projected rectangle encloses the cylinder silhouette; destination rays
are checked against the actual vertical impact band and geometry visibility. Region error measures the nearest valid boundary;
it is zero inside the region, with no horizontal center pull. Visibility gates
body and head independently. Alt forms never receive head refinement.

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

Position correction and motion tracking have separate gains and speed limits.
Tracking is strongest after acquisition; vertical head gains exceed horizontal
gains. States distinguish acquiring/tracking body, refining/tracking head, flick
capture and occluded retention. Brief occlusion retains identity for 60 ms but
applies no friction or rotation and resets velocity history.

Neutral right stick never acquires. After at least 100 ms of deliberate
acquisition, movement magnitude of 0.15 or more can retain 28% motion tracking
for at most 300 ms after the last look input. Positional attraction is disabled
in this interval. Opposition, release-cone exit and lifecycle resets break it.

A quick physical stick rise above 0.65 opens a 90 ms head-capture window. Capture
requires strong alignment (0.80), visible mechanically eligible head geometry,
and proximity within 0.35–0.8 degrees scaled by apparent band height. Scoped
capture uses 65% of that radius. Exponential correction approaches a 15% inset
and is bounded by remaining error and the profile speed cap. Capture stays with
its initial target; neutral, opposing and occluded input cancel it.

Vector friction separates radial and tangential camera motion. Approaching the
region receives light friction; inside the head band vertical precision friction
helps retain height. Assistance never adds a push beyond the remaining error.

## Stick response

The existing Linear, Classic, Precision and Dynamic serialized values remain
unchanged. Linear remains linear; other presets use micro-aim (0–35%), tracking
(35–80%) and fast-turn segments. A small exponential filter smooths low-speed
noise, bypassing reversals and large flicks. Outer-stick acceleration smoothly
approaches a magnitude-dependent maximum rather than using a hard delay.

## Diagnostics and validation

Run `dotnet run --project src/MphRead -- -gamepadcheck` and
`dotnet run --project src/MphRead -- -frametimingcheck`. Checks cover region
boundaries, mechanical range, physical intent, flicks, strafe retention,
opposition, camera units, tracking reversals, timing and zero-allocation core
processing. No damage, hitbox, projectile, lag compensation or protocol changes
are involved; only the ordinary local camera direction reaches shot processing.

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
