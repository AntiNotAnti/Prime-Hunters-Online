# Weapon timing policies

`WeaponLagPolicies.Resolve` reads the actual selected `EquipInfo.Weapon`, its
CanCharge/PartialCharge flags and charge level. It covers both normal and affinity
multiplayer mechanics. Timing centralization preserves the existing spawn →
rewind → bounded projectile Process loop; it does not retune damage, headshot
bands, speed, random seeds, turn acceleration, prediction or hit-claim fallback.

| Weapon | Mode | Catch-up cap | Homing | Historical target | Press age |
|---|---|---:|---|---|---|
| Power Beam | ProjectileCatchUp; charged affinity HomingProjectileCatchUp | 45 | charged affinity, mechanics value 81 at full charge | players | yes |
| Missile | ProjectileCatchUp; charged affinity HomingProjectileCatchUp | 45 | charged affinity, 81 | players | yes |
| Imperialist | HistoricalTrace | 45 | no | players, unchanged head/body band | yes |
| Judicator | ProjectileCatchUp; charged affinity AreaHistorical | 45; area has no projectile steps | no | players at launch; charged affinity ice wave spawns inside rewind | yes |
| Magmaul | ProjectileCatchUp | 45 | no | players | yes |
| Volt Driver | ProjectileCatchUp; charged affinity HomingProjectileCatchUp | 45 | charged affinity, 40 | players; owner target independently checked | yes |
| Battlehammer | ProjectileCatchUp | 45 | no | players | yes |
| Shock Coil | Continuous | 45 upper bound for newly allocated beam only | continuous selector, 409 | reevaluated each firing call | yes, preserved |
| Omega Cannon | ProjectileCatchUp | 45 | charged fields exist but CanCharge is off in MP | players | yes |

Values are sourced from Weapons.WeaponsMP and the Spawn charge rules. The cap is
in 60 Hz frames, defaults to 45 and follows the existing explicit `-maxrewind`
diagnostic override (maximum 120, below the 128-frame history). No stale input can
cause work beyond that ceiling. Instant area modes have ProjectileCatchUp=false.
Alt-form player contact uses the separate read-only `NetContactLagComp` path,
with ACK-timed victim geometry and accepted attacker movement sweeps. It remains
outside `BeginShot`. Kanden/Sylux bombs and detached turret contact retain their
entity/live paths; there is no new Kanden or Sylux body-contact attack.

Imperialist is implemented as a fast **projectile**, even though its mode is
HistoricalTrace. Setting its cap to zero would change current gameplay. Shock
Coil similarly calls the existing rewind path: replacement beams add no catch-up
work, and a new continuous beam stops processing once aged. The category's cap
is an upper bound, not a request to simulate every beam for 45 frames. Early
collision/lifespan termination and missing-history stops remain in place.

Historical geometry includes player bodies and Protocol 18's supported door,
force-field and platform/object collision history, plus constant static room
geometry. Player rewind restores historical collision form, canonical volumes
and Kanden segments without gameplay form transitions. Read-only shadow traces
also support alt bodies, but still decline scenes with unmodeled secondary bodies
or occluders; they do not silently treat unavailable geometry as a miss.

Homing uses existing selector eligibility and per-frame mechanics acceleration.
The production Process rejects a target behind its velocity (negative dot
product) instead of making a 180-degree turn. Catch-up runs the same Process,
once per owed frame, then resumes live updates. It never holds the live scene
rewound for the projectile's remaining lifespan.

`--weapon-policy` covers 1,620 timing profiles across all 18 MP mechanics entries,
both charge states, RTT 0/50/150/250/320, jitter 0/40/80 and loss 0/2/5. It checks
policy equivalence and bounds. `--health-shots` covers all nine weapons through
production intent/lifecycle and prediction/claim regression paths. These
asset-free tests do not claim to simulate every weapon's collision physics;
asset-backed combat and rendered eight-player validation remain release gates.

A separately tested lifecycle fix binds a player homing target to SlotGeneration
and LifeId at acquisition. Every Process step releases a target whose identity
changed, including during catch-up. Launches inside a rewind additionally require
history for that current target incarnation. Replay scenes retain their own target
references and do not consult the live network lifecycle. The encoded owner slot
remains a suggestion checked against the authoritative selector, not an identity
that may follow a respawn or slot replacement.
