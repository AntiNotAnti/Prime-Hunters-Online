# Controller Analog Movement

## Goal

Replace the controller's eight-direction, thresholded movement with true analogue
movement magnitude while keeping keyboard, touch, bots, server authority, collision,
and maximum movement speed behavior stable.

## Design

- The controller stick is still calibrated and passed through the existing radial
  deadzone.
- Directional keybinds remain populated so animation, jump direction, alt-form
  controls, and legacy gameplay checks continue to work.
- `PlayerControls.AnalogMoveX/Y` carries the controller magnitude separately.
- Biped forward/back and strafe traction are multiplied by the matching signed
  analogue axis. Full deflection is exactly the former digital strength.
- Trace and Weavel alt-form locomotion uses the same scaling.
- Rolling alt forms scale their native roll traction by the corresponding axis.
- The existing horizontal speed cap remains unchanged and is still the final
  movement-speed guard.
- Keyboard/touch/bot input leaves the analogue override inactive and therefore
  retains full-strength digital movement.

## Mixed input

Controller input remains additive with keyboard/touch input. If a digital direction
was already held, that axis keeps full strength while the other axis may still use
partial controller magnitude. This preserves the pre-existing mixed-input semantics
rather than letting a partial stick weaken a held key.

## Networking

Protocol 20 appends two signed bytes to `IntentPacket`:

- `MoveX`: -127..127
- `MoveY`: -127..127

The original 88-byte intent body and the protocol-19 eight-byte shot-state tail keep
all existing offsets. `IntentPacket.FullSize` grows from 96 to 98 bytes.

Only a real analogue source writes nonzero movement-axis bytes. Digital movement
continues to be represented by the existing `IntentButtons` bits, so a keyboard
diagonal is not mistaken for a normalized controller diagonal.

Remote players decode the two bytes back to -1..1 and apply the same traction
scaling before their owner-reported position is restored. This keeps animation,
movement state, collision-side simulation, and replay behavior aligned with the
owner without changing the owner-reported-position architecture.

## Replay

The replay checkpoint schema includes the three analogue-control fields. Live replay
intent records use the protocol-20 98-byte intent size, and older protocol recordings
remain explicitly version-gated.

## Validation

Required checks:

- quarter-stick X produces quarter strafe traction;
- three-quarter reverse Y produces three-quarter backward traction;
- the opposite axis contributes zero;
- clearing controls also clears analogue state;
- protocol-20 intent round-trip preserves signed movement bytes;
- independent wire fixture remains byte-identical after encode/decode;
- keyboard movement with no analogue override remains full strength;
- full-stick controller movement matches former digital maximum;
- radial controller diagonals preserve their actual .707/.707 components;
- multiplayer intent/replay paths preserve the encoded axes;
- normal gamepad, network, replay, and architecture checks remain green.

## Non-goals

This change does not:

- add a movement setting or controller option;
- alter acceleration constants or hunter speed caps;
- change server authority or owner-reported position;
- add client movement prediction/reconciliation;
- modify aim assist;
- change keyboard movement behavior.
