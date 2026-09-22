using System;
using MphRead.Formats;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    /// <summary>
    /// Network aim injection for remote players.
    ///
    /// Lives here rather than in PlayerInput.cs because PlayerEntity is
    /// already a partial class: the aim helpers (UpdateAimX/UpdateAimY) are
    /// private, and reaching them from a partial declaration costs upstream
    /// exactly one call site instead of four visibility changes.
    ///
    /// The reason remote aim needs its own path at all: the mouse branch in
    /// PlayerInput is gated on "!IsBot", and a networked player is driven the
    /// same way a bot is. Without this, remote players would move correctly
    /// but always face straight ahead.
    /// </summary>
    public partial class PlayerEntity
    {
        // Do not put the locally controlled hunter one simulation frame
        // behind itself just to smooth its model. Remote/spectated hunters use
        // the generic draw interpolation; the local view uses render-time
        // late latching below.
        protected override bool InterpolateDrawTransform
            => DemoPlayback.IsActive || (!IsMainPlayer && !NetSession.Active);

        /// <summary>
        /// The generic entity interpolation point for this picture. Replay
        /// cameras use it only as the baseline from which the replay-specific
        /// authority-frame smoother offsets the watched hunter.
        /// </summary>
        internal Vector3 ModPresentationPosition => ModDrawTransform().Row3.Xyz;

        internal bool ModReplayPresentationCamera(double alpha, out Matrix4 view,
            out Vector3 position, out float fov)
        {
            view = CameraInfo.ViewMatrix;
            position = CameraInfo.Position;
            fov = CameraInfo.Fov;
            if (!DemoPlayback.IsActive
                || !NetSmoothing.SampleReplayPresentation(SlotIndex,
                    out Vector3 replayPosition, out Vector3 replayFacing,
                    out bool replayAlt)
                || !CameraInfo.ModGetDrawPose(alpha, out Vector3 cameraPosition,
                    out Vector3 cameraTarget, out Vector3 cameraUp, out float cameraFov))
            {
                return false;
            }

            Vector3 presented = NetPlayerBridge.InFormFor(
                this, replayPosition, replayAlt);
            Vector3 delta = presented - ModPresentationPosition;
            cameraPosition += delta;

            float lookDistance = (cameraTarget - CameraInfo.ModGetDrawPosition(alpha)).Length;
            if (!Single.IsFinite(lookDistance) || lookDistance < 0.1f)
                lookDistance = 1;
            cameraTarget = cameraPosition + replayFacing * lookDistance;
            if ((cameraTarget - cameraPosition).LengthSquared < 0.000001f)
                return false;

            Vector3 look = (cameraTarget - cameraPosition).Normalized();
            Vector3 up = cameraUp;
            if (!Single.IsFinite(up.X) || !Single.IsFinite(up.Y)
                || !Single.IsFinite(up.Z) || up.LengthSquared < 0.000001f
                || MathF.Abs(Vector3.Dot(look, up.Normalized())) > 0.999f)
            {
                up = MathF.Abs(look.Y) < 0.999f ? Vector3.UnitY : Vector3.UnitZ;
            }
            Vector3 right = Vector3.Cross(look, up);
            if (right.LengthSquared < 0.000001f)
                return false;
            up = Vector3.Cross(right.Normalized(), look).Normalized();

            position = cameraPosition;
            fov = cameraFov;
            view = Matrix4.LookAt(cameraPosition, cameraTarget, up);
            return true;
        }

        protected override Matrix4 GetModelTransform(ModelInstance inst, int index)
        {
            Matrix4 transform = base.GetModelTransform(inst, index);
            if (NetSession.Active && SlotIndex == NetHooks.LocalSlot) transform.Row3.Xyz += ModMovementDrawOffset;
            if (NetSession.Active && SlotIndex != NetHooks.LocalSlot
                && NetSmoothing.SamplePresentation(SlotIndex,
                    out Vector3 presented, out bool presentedAlt))
            {
                Vector3 drawPosition = NetPlayerBridge.InFormFor(this, presented, presentedAlt);
                transform.Row3.Xyz += drawPosition - Position;
            }
            return transform;
        }

        private const int NetworkHistoryLength = 120;
        private readonly Vector3[] _networkPositionHistory = new Vector3[NetworkHistoryLength];
        private readonly Vector3[] _networkSpeedHistory = new Vector3[NetworkHistoryLength];
        private readonly bool[] _networkAltHistory = new bool[NetworkHistoryLength];
        private readonly uint[] _networkPositionFrames = new uint[NetworkHistoryLength];
        private int _networkPositionHistoryCount;
        private int _networkPositionHistoryHead;

        /// <summary>Where this player's next beam will be born. Diagnostics only.</summary>
        internal OpenTK.Mathematics.Vector3 ModMuzzlePos => _muzzlePos;

        internal void ModResetNetworkHistory()
        {
            _networkPositionHistoryCount = 0;
            _networkPositionHistoryHead = 0;
        }

        internal void ModRecordNetworkPosition(uint frame)
        {
            // Append in constant time. Re-recording a frame replaces it rather
            // than consuming another entry (e.g. after a correction).
            if (_networkPositionHistoryCount == 0
                || _networkPositionFrames[_networkPositionHistoryHead] != frame)
            {
                _networkPositionHistoryHead = (_networkPositionHistoryHead + 1) % NetworkHistoryLength;
                _networkPositionHistoryCount = Math.Min(_networkPositionHistoryCount + 1, NetworkHistoryLength);
            }
            int head = _networkPositionHistoryHead;
            _networkPositionHistory[head] = Position;
            _networkSpeedHistory[head] = Speed;
            _networkAltHistory[head] = IsAltForm;
            _networkPositionFrames[head] = frame;
        }

        internal bool ModGetNetworkPosition(uint frame, out Vector3 position)
        {
            for (int i = 0; i < _networkPositionHistoryCount; i++)
            {
                int at = (_networkPositionHistoryHead - i + NetworkHistoryLength) % NetworkHistoryLength;
                if (!NetLifecycleTracker.Newer(_networkPositionFrames[at], frame))
                {
                    position = _networkPositionHistory[at];
                    return true;
                }
            }
            position = default;
            return false;
        }

        internal bool ModGetNetworkPrediction(uint frame, out Vector3 position,
            out Vector3 speed, out bool altForm)
        {
            for (int i = 0; i < _networkPositionHistoryCount; i++)
            {
                int at = (_networkPositionHistoryHead - i + NetworkHistoryLength) % NetworkHistoryLength;
                if (_networkPositionFrames[at] == frame)
                {
                    position = _networkPositionHistory[at];
                    speed = _networkSpeedHistory[at];
                    altForm = _networkAltHistory[at];
                    return true;
                }
            }
            position = default;
            speed = default;
            altForm = false;
            return false;
        }

        /// <summary>Where this player's gun points. Sent as the aim in every intent.</summary>
        internal Vector3 ModGunVector => _gunVec1;

        /// <summary>
        /// Build a render-only first-person view from input that arrived after
        /// the last 60 Hz simulation step. No gameplay field is changed: the
        /// next simulation step will consume the same input normally and its
        /// intent/shot remains authoritative.
        /// </summary>
        internal Matrix4 ModLateLatchedView(float pointerX, float pointerY,
            float controllerX, float controllerY)
        {
            if (CameraType != CameraType.First || Flags1.TestFlag(PlayerFlags1.NoAimInput)
                || CameraSequence.Current != null)
            {
                return CameraInfo.ViewMatrix;
            }

            float mouseX = -pointerX / 4f * Mods.InputSettings.MouseSensitivity
                * (Mods.InputSettings.InvertMouseX ? -1 : 1);
            float mouseY = -pointerY / 4f * Mods.InputSettings.MouseSensitivity
                * (Mods.InputSettings.InvertMouseY ? -1 : 1);
            if (EquipInfo.Zoomed)
            {
                controllerX *= Mods.Input.GamepadOptions.ScopedX;
                controllerY *= Mods.Input.GamepadOptions.ScopedY;
            }

            float x = mouseX + controllerX;
            float y = mouseY + controllerY;
            float normalFov = Fixed.ToFloat(Values.NormalFov) * 2;
            if (EquipInfo.Zoomed && normalFov != 0)
            {
                float zoomScale = CameraInfo.Fov / normalFov;
                x *= zoomScale;
                y *= zoomScale;
            }
            if (x == 0 && y == 0)
            {
                return CameraInfo.ViewMatrix;
            }

            Vector3 aim = _gunVec1;
            float targetAimY = Math.Clamp(_aimY + y,
                IsAltForm ? -25f : -85f, IsAltForm ? 5f : 85f);
            float pitch = MathHelper.DegreesToRadians(targetAimY - _aimY);
            if (pitch != 0)
            {
                Matrix4 pitchBasis = GetTransformMatrix(aim, Vector3.UnitY);
                Vector3 local = pitch <= 0
                    ? new Vector3(0, MathF.Sin(pitch), MathF.Cos(pitch))
                    : new Vector3(0, -MathF.Sin(-pitch), MathF.Cos(-pitch));
                aim = Matrix.Vec3MultMtx3(local, pitchBasis).Normalized();
            }
            if (x != 0)
            {
                float angle = MathHelper.DegreesToRadians(x);
                float sin = angle <= 0 ? MathF.Sin(angle) : -MathF.Sin(-angle);
                float cos = angle <= 0 ? MathF.Cos(angle) : MathF.Cos(-angle);
                float oldX = aim.X;
                float oldZ = aim.Z;
                aim.X = oldX * cos + oldZ * sin;
                aim.Z = oldX * -sin + oldZ * cos;
                aim = aim.Normalized();
            }
            Vector3 up = CameraInfo.UpVector.LengthSquared > 0.000001f
                ? CameraInfo.UpVector.Normalized() : Vector3.UnitY;
            return Matrix4.LookAt(CameraInfo.Position, CameraInfo.Position + aim, up);
        }


        internal void ModRefreshNetworkAim()
        {
            if (NetSession.Active && SlotIndex != NetHooks.LocalSlot
                && NetSession.RemoteIntentValid[SlotIndex]
                && NetPlayerBridge.AimTrusted(SlotIndex))
            {
                ModSetAim(NetSession.RemoteIntents[SlotIndex].Aim);
            }
        }

        /// <summary>
        /// Point a remote player's gun where its owner is pointing it.
        ///
        /// Absolute, not a rotation: see IntentPacket.Aim. Everything derived
        /// from the aim ray is recomputed here, because the authority decides
        /// what a shot hits from _aimPosition and would otherwise fire along
        /// last frame's ray.
        /// </summary>
        internal void ModSetAim(Vector3 aim)
        {
            if (!(aim.LengthSquared > 0.0001f))
            {
                return;
            }
            if (NetSession.Active && SlotIndex != NetHooks.LocalSlot)
            {
                // A remote player's camera is not the authoritative state.
                // Repositioning the player without moving this cached camera
                // made its transmitted aim originate from the previous room
                // position, so every remote beam missed its target.
                //
                // At eye height, which this line used to leave out. Every
                // shot in the game starts from CameraInfo.Position -- it is
                // what _gunDrawPos and then _muzzlePos are built from in
                // UpdateAimVecs -- and UpdateCameraFirst puts it AimYOffset
                // above the feet, nine tenths of a unit. Assigning the bare
                // position therefore fired every remote player's beam from
                // its ankles, along a ray parallel to the one its owner was
                // looking down and nine tenths of a unit under it. Aimed at
                // somebody's chest that goes into the floor in front of
                // them.
                //
                // The tell was that it looked exactly like latency and was
                // not: on the authority the shots that landed were its own,
                // the one player whose camera the engine still owned. With
                // eleven milliseconds of ping and three clients on one map,
                // slot 1's own machine counted 69 of its shots overlapping
                // slot 2 while the authority counted none at all.
                CameraInfo.Position = _field6D0
                    ? Position
                    : Position.AddY(Fixed.ToFloat(Values.AimYOffset));
            }
            _gunVec1 = aim.Normalized();
            float flat = MathF.Sqrt(_gunVec1.X * _gunVec1.X + _gunVec1.Z * _gunVec1.Z);
            _aimY = Math.Clamp(MathHelper.RadiansToDegrees(MathF.Atan2(_gunVec1.Y, flat)), -85, 85);
            _aimPosition = CameraInfo.Position + _gunVec1 * Fixed.ToFloat(Values.AimDistance);
            UpdateAimFacing();
        }

        /// <summary>
        /// Recompute this player's room node after its position was written
        /// in from the network.
        ///
        /// NodeRef is what the renderer culls against (PlayerDraw:
        /// `IsMainPlayer || IsVisible(NodeRef)`), and the engine normally
        /// advances it during simulation. A remote player whose position is
        /// assigned directly skips that step, keeps the node it spawned in,
        /// and disappears -- or shows only a shadow -- once the viewer is
        /// somewhere else in the room.
        /// </summary>
        /// <summary>
        /// The furthest one position update may move a player and still be
        /// walked across portals rather than looked up. A player crosses a
        /// couple of tenths of a unit in a frame; anything past this is a
        /// respawn, a teleporter or the authority correcting a divergence,
        /// and the straight line to it means nothing.
        /// </summary>
        private const float _nodeWalkStepMax = 4;

        /// <summary>
        /// The room part a step from one position to the next walks into, or
        /// none when this is not a step a walk can describe.
        ///
        /// Refused for a jump no walk could make -- a respawn, a teleporter,
        /// the authority correcting a divergence -- since the straight line
        /// between those two points crosses whatever it happens to cross, and
        /// for a result the part's own bounding box says is impossible, which
        /// is what keeps one bad hop from becoming permanent.
        ///
        /// Static so that the map harness can run the same rule alongside the
        /// engine's own walk and report how far the two drift apart; a copy
        /// of it there would measure the copy.
        /// </summary>
        internal static Formats.Culling.NodeRef ModWalkNodeRef(Scene scene,
            Formats.Culling.NodeRef current, Vector3 previous, Vector3 position)
        {
            if (current == Formats.Culling.NodeRef.None
                || (position - previous).LengthSquared >= _nodeWalkStepMax * _nodeWalkStepMax)
            {
                return Formats.Culling.NodeRef.None;
            }
            Formats.Culling.NodeRef walked = scene.UpdateNodeRef(current, previous, position);
            if (walked.PartIndex == -1 || !scene.PartCouldContain(walked.PartIndex, position))
            {
                return Formats.Culling.NodeRef.None;
            }
            return walked;
        }

        /// <summary>
        /// Put this player at a position, hitbox and room node included.
        ///
        /// The same three lines NetPlayerBridge.Move does, exposed because
        /// <see cref="Mods.Network.NetUnlagged"/> needs them in the middle of
        /// a frame rather than around the edges of one. That timing is the
        /// whole reason it cannot just assign Position: _volume is a cached
        /// copy of the collision sphere that PlayerProcess recomputes once a
        /// frame, so a rewind applied after that has run would move the model
        /// and leave the hitbox behind -- and a rewind whose hitbox does not
        /// move is a rewind that does nothing at all.
        /// </summary>
        internal void ModPlaceAt(OpenTK.Mathematics.Vector3 position)
        {
            OpenTK.Mathematics.Vector3 previous = Position;
            Position = position;
            PrevPosition = position;
            ModRefreshNodeRef(previous);
        }

        /// <summary>
        /// Whether a position the authority sent is somewhere this room could
        /// actually have put a player.
        ///
        /// Every placement the authority makes is a spawn point: it runs
        /// GetRespawnPoint and publishes where that put the player. So a
        /// placement nowhere near any spawn point in the room this client has
        /// loaded did not come from this room at all -- it is the authority
        /// still standing in the map before the rotation, describing
        /// coordinates that mean something there and nothing here.
        ///
        /// Taking one is the report about spawning outside the level: the
        /// player is teleported to wherever those coordinates land, which is
        /// usually nowhere, and then falls out of the world in the dark until
        /// the kill plane catches them. Refusing it costs nothing -- the
        /// local spawn that is already in place is a real spawn point of this
        /// room, chosen a moment earlier by the same rule.
        /// </summary>
        internal bool ModPlacementBelongsHere(OpenTK.Mathematics.Vector3 position)
        {
            bool any = false;
            return ModNearestSpawn(position, ref any) != null
                // A room with no spawn points at all is not a room this rule
                // can say anything about, so it says nothing.
                || !any;
        }

        /// <summary>
        /// The spawn point a placement was made from, or null if it was made
        /// from none of this room's.
        ///
        /// <paramref name="any"/> comes back true when the room has spawn
        /// points at all, which is what tells "the authority meant a point
        /// somewhere else" apart from "there is nothing here to mean".
        /// </summary>
        private PlayerSpawnEntity? ModNearestSpawn(OpenTK.Mathematics.Vector3 position,
            ref bool any)
        {
            // Generous: the authority may have published a frame or two after
            // the placement, by which time the player has begun to fall to
            // the floor, and a spawn point sits above it.
            const float reach = 12;
            PlayerSpawnEntity? nearest = null;
            float nearestDist = reach * reach;
            foreach (PlayerSpawnEntity spawn in _scene.GetPlayerSpawnEntities())
            {
                any = true;
                OpenTK.Mathematics.Vector3 between = spawn.Position - position;
                float dist = between.LengthSquared;
                if (dist <= nearestDist)
                {
                    nearestDist = dist;
                    nearest = spawn;
                }
            }
            return nearest;
        }

        /// <summary>
        /// Which way the level author meant a player standing at a placement
        /// the authority made to be looking.
        ///
        /// Read off the spawn point rather than off the snapshot because the
        /// snapshot's facing is already stale by the time it arrives: the
        /// authority spawns this player looking down the point's own vector
        /// and then eases that facing 10% a frame towards whatever aim the
        /// owner's intents are still carrying from the life that just ended
        /// (<see cref="ModSetAim"/>, UpdateAimFacing). The point itself does
        /// not drift, and it is the exact vector the engine's own respawn
        /// passes to Spawn().
        ///
        /// Null when the placement came from no point in this room, which is
        /// the case <see cref="ModPlacementBelongsHere"/> refuses outright.
        /// </summary>
        internal OpenTK.Mathematics.Vector3? ModSpawnFacingAt(OpenTK.Mathematics.Vector3 position)
        {
            bool any = false;
            PlayerSpawnEntity? spawn = ModNearestSpawn(position, ref any);
            return spawn?.FacingVector;
        }

        /// <summary>
        /// Turn this machine's own player to face the way the spawn point the
        /// authority placed them on faces.
        ///
        /// A respawn happens twice: once here the moment the player asks for
        /// it, and once on the authority, which owns where they end up. The
        /// two run GetRespawnPoint a few frames apart against different sets
        /// of living players and rotate their choice with the frame counter,
        /// so they routinely pick *different* points -- which is why the
        /// placement is handed over at all. Handing over the position alone
        /// left the player standing on the authority's point while still
        /// looking down the local one's vector: the report about facing the
        /// wrong way after respawning, and it was only ever "sometimes"
        /// because it needs the two choices to disagree.
        ///
        /// Both halves have to move. <see cref="_facingVector"/> is what the
        /// first-person camera aims (UpdateCameraFirst) and what the body's
        /// heading is rebuilt from every frame (UpdateAimVecs); _gunVec1 is
        /// where the gun points and what the next intent publishes. Setting
        /// only the first leaves the aim behind and UpdateAimFacing drags the
        /// facing straight back to it.
        /// </summary>
        internal void ModSetSpawnFacing(OpenTK.Mathematics.Vector3 facing)
        {
            ModSetFacing(facing);
            ModSetAim(facing);
        }

        internal void ModRefreshNodeRef(OpenTK.Mathematics.Vector3 previousPosition)
        {
            _volume = CollisionVolume.Move(_volumeUnxf, Position);
            // Walked from the last position first, looked up only when the
            // walk cannot be trusted.
            //
            // This used to be the other way round, on the grounds that a
            // puppet's position is written in rather than simulated and so
            // never crosses a portal "properly". That reasoning does not
            // hold: UpdateNodeRef tests the *segment* between two positions
            // against the portal planes, and it does not care how either
            // position was arrived at -- only that they are consecutive and
            // close, which two snapshots of the same player 1/60 s apart are
            // exactly as much as two simulation steps of one are.
            //
            // What the lookup costs, measured with `-maptest ROOM -players 8`
            // against the node the engine itself walked: on MP4 HIGHGROUND it
            // named the wrong part for 73% of the positions players actually
            // stood in, on MP7 PROCESSOR CORE 84%, and on MP6 HEADSHOT it had
            // no answer at all for 86%. In more than half of those the part
            // it named was one the viewer could not see while the real one
            // was -- which is a hunter standing in front of you, shootable,
            // with a shadow, and not drawn. That is the report this method
            // was written for in the first place; it was the cure that was
            // wrong, not the diagnosis.
            //
            // The walk is refused for a jump no walk could make (a respawn, a
            // teleporter, a correction from the authority) and for a result
            // the part's own bounding box says is impossible, which is what
            // stops one bad hop becoming permanent -- the failure the lookup
            // was reached for.
            Formats.Culling.NodeRef walked = ModWalkNodeRef(
                _scene, NodeRef, previousPosition, Position);
            if (walked != Formats.Culling.NodeRef.None)
            {
                NodeRef = walked;
                ModNodeUnresolved = false;
                return;
            }
            // None means the position is inside no part at all -- a fraction
            // outside the geometry, which happens -- and the node it had is a
            // better answer than nothing.
            Formats.Culling.NodeRef found = _scene.GetNodeRefByPosition(Position);
            if (found.PartIndex == -1)
            {
                // The body rather than the point it is anchored at, and then a
                // little either side of it. A lookup tests a position against
                // every portal plane of a part, so a player standing on top of
                // the level -- or a hair outside it, which is where a jump
                // lands -- can be outside all of them while the middle of the
                // same player is comfortably inside one. Cheap, and it is only
                // reached when the direct answer already failed.
                found = _scene.GetNodeRefByPosition(_volume.SpherePosition);
            }
            if (found.PartIndex == -1)
            {
                found = _scene.GetNodeRefByPosition(Position + Vector3.UnitY * 0.5f);
            }
            if (found.PartIndex == -1)
            {
                found = _scene.GetNodeRefByPosition(Position - Vector3.UnitY * 0.5f);
            }
            if (found.PartIndex != -1)
            {
                NodeRef = found;
                ModNodeUnresolved = false;
                return;
            }
            // Nothing placed this player at all. The node it had is still the
            // best guess for everything that reads one -- but it is a guess,
            // and a wrong one hides the player outright, so say so and let
            // PlayerDraw draw them anyway. Reported from AD2 ALINOS PERCH by
            // several players at once: hunters up at the top of the map who
            // could be shot and could not be seen, with their shadows still
            // moving about underneath them. Same shape as the MP4 HIGHGROUND
            // EXPANDED report that this method was written for, except that
            // there the position resolved and here it does not resolve at all.
            ModNodeUnresolved = true;
            NetPlayerBridge.NodeLookupsUnresolved++;
            if (NodeRef != Formats.Culling.NodeRef.None)
            {
                NodeRef = _scene.UpdateNodeRef(NodeRef, previousPosition, Position);
            }
        }

        /// <summary>
        /// Whether this player's last node lookup failed outright, so the node
        /// it is holding is stale rather than merely old.
        ///
        /// Read by <c>PlayerDraw</c>, which culls on the node: a puppet whose
        /// node cannot be worked out is drawn rather than hidden. Drawing one
        /// that should have been culled costs a model the depth buffer mostly
        /// swallows; hiding one that should have been drawn costs a player who
        /// can shoot you from somewhere you cannot see them, which is the
        /// report this exists for.
        /// </summary>
        internal bool ModNodeUnresolved { get; private set; }

        /// <summary>
        /// Record the positions a collision query is about to be built from.
        /// The candidate pool drains when limitMin/limitMax span a huge grid
        /// range, and those come straight from PrevPosition and Position, so
        /// logging both says whether a bad position is the cause.
        /// </summary>
        internal void ModLogCollisionRange()
        {
            if (!NetLog.Enabled || !NetSession.Active)
            {
                return;
            }
            NetLog.CollisionRange(SlotIndex, "pre-check", PrevPosition, Position);
        }

        /// <summary>
        /// Point a remote player where the authority says it is pointing.
        ///
        /// Only the authority runs a remote player's aim (it has the relayed
        /// deltas); everyone else receives the resulting facing in the
        /// snapshot. Without applying it, a puppet slid around the map still
        /// facing whatever direction it spawned in.
        /// </summary>
        internal void ModSetFacing(OpenTK.Mathematics.Vector3 facing)
        {
            // Not-a-number fails every comparison, including this one, so the
            // length test has to be written as a test for a good value rather
            // than a test for a bad one.
            if (!(facing.LengthSquared > 0.0001f))
            {
                return;
            }
            _facingVector = facing.Normalized();
            SetTransform(_facingVector, _upVector, Position);
        }

        /// <summary>
        /// Hidden and non-solid on this machine, whether this is the real
        /// local player (set directly by <see cref="Mods.SpectatorMode"/>) or a
        /// puppet (set from the FlagSpectating bit in its snapshot).
        /// </summary>
        internal void ModSetSpectating(bool value)
        {
            if (value)
            {
                Flags2 |= PlayerFlags2.Spectating;
            }
            else
            {
                Flags2 &= ~PlayerFlags2.Spectating;
            }
        }

        /// <summary>
        /// In the match, and so worth aiming at: alive, and not sitting this
        /// one out in the spectator.
        ///
        /// What <c>PlayerAi</c>'s opponent and teammate searches ask, in place
        /// of <c>Health > 0</c> alone. A spectator is already hidden and
        /// non-solid, so the bots that kept picking one as their target were
        /// walking towards nobody and shooting through them -- a hunter
        /// nobody can hit is not an opponent, and one nobody can be hit by is
        /// not a teammate worth covering either.
        /// </summary>
        internal bool ModInPlay => Health > 0 && !Flags2.TestFlag(PlayerFlags2.Spectating);

        /// <summary>
        /// Whether this player is standing in the map rather than waiting at
        /// the origin to be placed. Spawned is set by Spawn() and never
        /// cleared, so health is what distinguishes "in the match" from
        /// "dead, waiting for a respawn point".
        /// </summary>
        internal bool ModIsInPlay => LoadFlags.TestFlag(LoadFlags.Spawned) && _health > 0;

        /// <summary>
        /// Put a puppet on the map where the authority put the real player.
        ///
        /// Spawn() is what clears HideModel, gives the player its health and
        /// marks the slot Spawned; a puppet that only ever had a position
        /// written into it stayed hidden, which is why a correctly tracked
        /// remote player could still be invisible. Only the transition needs
        /// this -- once spawned, plain position writes are enough.
        /// </summary>
        internal void ModNetSpawn(OpenTK.Mathematics.Vector3 position,
            OpenTK.Mathematics.Vector3 facing, bool respawn = true)
        {
            OpenTK.Mathematics.Vector3 forward = facing.LengthSquared > 0.0001f
                ? facing.Normalized()
                : -OpenTK.Mathematics.Vector3.UnitZ;
            Spawn(position, forward, OpenTK.Mathematics.Vector3.UnitY,
                ModSpawnNodeRef(_scene, position), respawn);
        }

        /// <summary>
        /// The room part to spawn into, from the map's own data where the map
        /// has any to give.
        ///
        /// A spawn position is a spawn point, and a spawn point is an entity
        /// carrying the node ref the level author put on it -- which is what
        /// the engine's own respawn passes and is not a guess.
        /// GetNodeRefByPosition is: on MP6 HEADSHOT it answers none for 86%
        /// of the positions a player actually stands in. That mattered
        /// because nothing recovers a node ref of none afterwards -- the
        /// one-hop walk that maintains it needs a part to start from -- so a
        /// client that joined a match there spent all of it with no node at
        /// all. See RoomEntity.IsNodeRefVisible for what that cost.
        ///
        /// Static so the harness can seed a puppet the way the game does:
        /// MapAudit's remote-node probe carries a node ref alongside the real
        /// one, and seeding that from the lookup measured a rule this code
        /// stopped using -- one bad seed walks wrong for the rest of the run
        /// and the probe reported it as the game's own behaviour.
        /// </summary>
        internal static Formats.Culling.NodeRef ModSpawnNodeRef(Scene scene,
            OpenTK.Mathematics.Vector3 position)
        {
            EntityBase? closest = null;
            float closestDist = 2 * 2;
            foreach (EntityBase entity in scene.Entities)
            {
                if (entity.Type != EntityType.PlayerSpawn
                    || entity.NodeRef == Formats.Culling.NodeRef.None)
                {
                    continue;
                }
                float dist = (entity.Position - position).LengthSquared;
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = entity;
                }
            }
            if (closest != null)
            {
                return closest.NodeRef;
            }
            return scene.GetNodeRefByPosition(position);
        }

        /// <summary>
        /// How far this player would have to turn, in the degrees
        /// UpdateAimX/UpdateAimY take, to be pointing at a position.
        ///
        /// Derived from _gunVec1 rather than the facing vector because that
        /// is what the aim helpers rotate and what the gun actually fires
        /// along; the biped's facing lags it while turning.
        /// </summary>
        internal (float X, float Y) ModAimDeltaTowards(Vector3 target)
        {
            Vector3 desired = ModAimVectorTowards(target);
            float flatLength = MathF.Sqrt(desired.X * desired.X + desired.Z * desired.Z);
            float aimFlat = MathF.Sqrt(_gunVec1.X * _gunVec1.X + _gunVec1.Z * _gunVec1.Z);
            if (flatLength < 0.001f || aimFlat < 0.001f)
            {
                return (0, 0);
            }
            float tx = desired.X / flatLength;
            float tz = desired.Z / flatLength;
            float ax = _gunVec1.X / aimFlat;
            float az = _gunVec1.Z / aimFlat;
            // UpdateAimX rotates (x, z) by the negative of its argument
            // (x' = x cos + z sin), so the turn to make is the negated
            // signed angle from the current aim to where it should be.
            float turn = -MathHelper.RadiansToDegrees(
                MathF.Atan2(ax * tz - az * tx, ax * tx + az * tz));
            float targetPitch = MathHelper.RadiansToDegrees(MathF.Atan2(desired.Y, flatLength));
            float currentPitch = MathHelper.RadiansToDegrees(MathF.Atan2(_gunVec1.Y, aimFlat));
            return (turn, targetPitch - currentPitch);
        }

        /// <summary>
        /// Where _gunVec1 has to point for a shot to hit a position.
        ///
        /// Not simply "at the target". A shot travels from the muzzle towards
        /// _aimPosition, which sits a fixed distance down the aim ray from the
        /// eye, so the gun and the crosshair only agree at that one distance --
        /// the game's convergence point, which a person compensates for by
        /// watching where the shots land. Pointing the aim ray straight at a
        /// target further away therefore missed low and to the side, every
        /// time. This solves for the aim direction whose convergence point
        /// lies on the muzzle-to-target line instead.
        /// </summary>
        private Vector3 ModAimVectorTowards(Vector3 target)
        {
            Vector3 eye = CameraInfo.Position;
            float aimDistance = Fixed.ToFloat(Values.AimDistance);
            Vector3 fromMuzzle = target - _muzzlePos;
            if (fromMuzzle.LengthSquared < 0.0001f || aimDistance <= 0)
            {
                return target - eye;
            }
            Vector3 direction = fromMuzzle.Normalized();
            Vector3 offset = _muzzlePos - eye;
            float b = Vector3.Dot(offset, direction);
            float c = Vector3.Dot(offset, offset) - aimDistance * aimDistance;
            float discriminant = b * b - c;
            if (discriminant < 0)
            {
                // The target line never reaches the convergence sphere -- at
                // point-blank range the two are close enough that aiming
                // straight at it is right anyway.
                return target - eye;
            }
            float t = -b + MathF.Sqrt(discriminant);
            return _muzzlePos + direction * t - eye;
        }

        /// <summary>Where a shot aimed at this player should be pointed.</summary>
        internal Vector3 ModAimTarget => Position + PlayerVolumes[(int)Hunter, 0].SpherePosition;

        /// <summary>
        /// Change which hunter this slot is playing. Initialize() rebuilds the
        /// models and values from it, so callers must run that afterwards --
        /// NetSlotManager does, as part of activating the slot.
        /// </summary>
        internal void ModSetHunter(Hunter hunter)
        {
            if (hunter != Hunter)
            {
                // Sylux's three bomb slots and the count that indexes them
                // belong to the hunter, not to the player, and this is the one
                // place a player stops being that hunter. Spawn clears them
                // when somebody comes back *as* Sylux; leaving here is the
                // other direction, and the one the respawn hunter picker made
                // ordinary -- lay a bomb, come back as somebody else, and the
                // count stayed behind describing bombs that are now nobody's.
                // BombEntity.Destroy checks that it is still the bomb
                // registered at its own index, so the ones left in the world
                // pass over this cleanly rather than decrementing it.
                SyluxBombs[0] = null;
                SyluxBombs[1] = null;
                SyluxBombs[2] = null;
                SyluxBombCount = 0;
            }
            Hunter = hunter;
        }

        /// <summary>
        /// Whether the HUD's directional damage indicator is currently lit.
        ///
        /// The check that says the damage feedback actually ran: these timers
        /// are set inside TakeDamage and nowhere else, so a client that had
        /// its health assigned from a snapshot never lit one.
        /// </summary>
        internal bool ModDamageIndicatorActive
        {
            get
            {
                for (int i = 0; i < _damageIndicatorTimers.Length; i++)
                {
                    if (_damageIndicatorTimers[i] > 0)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Put a remote player into or out of alt form to match the authority.
        ///
        /// Morphing is an input the owner performs, and only the authority
        /// receives that input: everyone else has a puppet with no controls,
        /// so the snapshot's alt-form bit was written every frame and read by
        /// nobody. A player who morphed stayed a biped on every other screen.
        ///
        /// Routed through the engine's own switch rather than the flag it
        /// sets, because the form change also swaps the collision volume, the
        /// model and the transform -- setting the flag alone would leave a
        /// morph ball standing upright with a hunter's hitbox.
        /// </summary>
        /// <summary>
        /// Begin the transition into or out of alt form, animation and all.
        ///
        /// The owner's relayed input normally does this by itself; this is
        /// for the case where the two machines have ended up disagreeing.
        /// </summary>
        internal void ModStartFormSwitch()
        {
            bool switched = TrySwitchForms(force: true);
            NetLog.Event($"slot {SlotIndex} form switch requested -> {switched}, now {ModFormState()}");
        }

        /// <summary>
        /// Put this player in a form outright, skipping the transition.
        ///
        /// A last resort, and deliberately not the first thing tried.
        /// EnterAltForm only sets Morphing and leaves UpdateForm to run when
        /// the morph animation ends, while ExitAltForm applies the change
        /// immediately -- so forcing the form as soon as a difference
        /// appeared removed the morph-in animation and left the morph-out one
        /// intact, which is exactly the asymmetry that showed up in play.
        /// </summary>
        internal void ModForceForm(bool altForm)
        {
            if (altForm == IsAltForm)
            {
                // Unmorph changes the form bit before its animation ends. If
                // that animation stalls, UpdateForm must not run a second time:
                // it would shift the position by the collision-centre offset.
                // A stalled morph being cancelled back to biped also needs
                // ExitAltForm to remove Weavel's halfturret and other effects.
                if (IsMorphing && !altForm)
                {
                    ExitAltForm();
                }
                if (IsUnmorphing && !altForm)
                {
                    NetLog.Event($"slot {SlotIndex} stalled unmorph completed from {ModFormState()}");
                    if (IsMainPlayer && CameraSequence.Current != null)
                    {
                        CameraSequence.Current.InitialCamInfo.NodeRef = NodeRef;
                    }
                    else
                    {
                        CameraInfo.NodeRef = NodeRef;
                    }
                    Flags1 &= ~PlayerFlags1.Unmorphing;
                    SetBipedAnimation(PlayerAnimation.Idle, AnimFlags.None);
                    if (_burnTimer > 0)
                    {
                        CreateBurnEffect();
                    }
                    if (CameraType != CameraType.First)
                    {
                        SwitchCamera(CameraType.First, _facingVector);
                    }
                }
                else if (IsMorphing)
                {
                    // The form was already applied; only the stale animation
                    // flag remains. Preserve the alt model and camera.
                    Flags1 &= ~PlayerFlags1.Morphing;
                }
                return;
            }
            NetLog.Event($"slot {SlotIndex} form forced to {(altForm ? "alt" : "biped")} "
                + $"from {ModFormState()}");
            Flags1 &= ~PlayerFlags1.Morphing;
            Flags1 &= ~PlayerFlags1.Unmorphing;
            UpdateForm(altForm);
            // And the camera, which UpdateForm does not touch.
            //
            // The camera type is switched by EnterAltForm and ExitAltForm, and
            // this path is neither: it skips the transition, so it skipped the
            // switch too. A player put into alt form this way kept the
            // first-person camera, and the moment anybody looked through it --
            // spectating them, or watching a demo, where every player on
            // screen is a puppet -- the morph ball was filmed from inside its
            // own head instead of from the third-person camera the game uses
            // for it. That is the "alt form does not reproduce the original
            // view" half of the replay report.
            //
            // Exactly the switch the two real transitions make, so a forced
            // form and a walked one end in the same state.
            if (altForm)
            {
                SwitchCamera(Values.AltFormStrafe != 0 ? CameraType.Third2 : CameraType.Third1,
                    new Vector3(_field70, 0, _field74));
            }
            else
            {
                SwitchCamera(CameraType.First, _facingVector);
            }
        }

        /// <summary>
        /// Put a remote player on the weapon the authority says it is
        /// holding.
        ///
        /// The weapon travels in every snapshot and was never applied. Input
        /// alone does not settle it: a NextWeapon press cycles through
        /// whatever that machine believes is available, and availability
        /// depends on pickups, which are not replicated -- so two clients
        /// pressing the same button landed on different beams and drew the
        /// wrong gun firing the wrong colour.
        /// </summary>
        internal void ModSetWeapon(BeamType weapon)
        {
            if (weapon == CurrentWeapon || weapon < BeamType.PowerBeam
                || weapon > BeamType.OmegaCannon)
            {
                return;
            }
            // Availability is a local notion built from local pickups; the
            // authority saying this player holds the weapon is what settles
            // it. Charging comes with it: a puppet that could hold a weapon
            // but not charge it fired uncharged shots for its owner's charged
            // ones, which is every affliction in the game.
            _availableWeapons[weapon] = true;
            _availableCharges[weapon] = true;
            TryEquipWeapon(weapon, silent: true);
        }

        /// <summary>
        /// Universal ammo and missiles, as the owner's own machine counts
        /// them.
        /// </summary>
        internal (int Ua, int Missiles) ModAmmo => (_ammo[UA], _ammo[Missiles]);

        /// <summary>
        /// Put a remote player's ammo where its owner says it is.
        ///
        /// Not cosmetic, and not a nicety: BeamProjectileEntity refuses to
        /// spawn a beam whose cost exceeds the shooter's ammo, so a puppet
        /// that has run dry produces no projectile at all -- on the very
        /// machine that decides what is hit. The shooter watches their own
        /// beam leave the gun and connect, the authority never creates one,
        /// and the target is untouchable. It reads exactly like an invincible
        /// player, and switching to a weapon on the other ammo pool "fixes"
        /// it, because that pool still has something in it.
        ///
        /// The two drift apart within a round for a reason that cannot be
        /// closed any other way: every machine simulates the shot and spends
        /// the ammo, but pickups are collected locally and are not
        /// replicated, so only the owner's count is ever right. Sending it is
        /// cheaper and more honest than trying to replicate item state.
        /// </summary>
        /// <summary>
        /// The most universal ammo this player can hold, for the harness's
        /// top-up. Read rather than assumed: the cap is a per-hunter value and
        /// the single-player save has a different one again.
        /// </summary>
        internal int ModAmmoCap => _ammoMax[UA];

        /// <summary>
        /// What this player's alt-form ram is worth: the boost charge scaled
        /// by the hunter's own alt-attack damage, latched when the boost
        /// starts. Sent in the intent for the same reason the charge is.
        /// </summary>
        internal int ModBoostDamage => _boostDamage;

        /// <summary>
        /// Put a remote player's shot strength where its owner says it is:
        /// the charge on the gun, the strength of the ram, and whether double
        /// damage is running.
        ///
        /// <b>All three decide the damage of a shot, and all three were
        /// re-derived here rather than received.</b> The charge was a count of
        /// frames the relayed trigger had been held -- which is the owner's
        /// count give or take the send interval, the jitter and whatever was
        /// dropped -- and on a partial-charge weapon the damage is a
        /// continuous function of that count, so the two machines put
        /// different numbers on the same shot every time one was fired. Double
        /// damage is worse, because it is not a drift but a factor of two: the
        /// powerup is collected by each machine's own copy of the pickups, on
        /// its own respawn timer, so the authority's copy of a shooter can
        /// simply not have one the owner is holding.
        ///
        /// Either way the shooter's own machine resolves its hits now
        /// (<see cref="Mods.Network.NetHitPrediction"/>) and the authority
        /// resolves them again a round trip later; where the numbers differ
        /// the client runs a victim's health down faster than the authority
        /// does, and the shot after that predicts a kill on somebody who is
        /// still standing.
        ///
        /// The same shape as <see cref="ModSetAmmo"/> and the alt-form state:
        /// whoever is playing a character is the one who knows.
        /// </summary>
        private byte _modPendingHomingTarget;

        internal void ModSetPendingHomingTarget(byte encodedTarget)
        {
            _modPendingHomingTarget = encodedTarget;
        }

        internal byte ModConsumePendingHomingTarget()
        {
            byte target = _modPendingHomingTarget;
            _modPendingHomingTarget = 0;
            return target;
        }

        /// <summary>
        /// Pick the same non-continuous homing target the owner's shot will use
        /// before the release is sent. Only the charged affinity Volt Driver
        /// needs this: different peers independently choosing from slightly
        /// different snapshot positions made the visible bolt and authority
        /// resolution bend toward different players.
        /// </summary>
        internal byte ModPickNetworkHomingTarget()
        {
            WeaponInfo weapon = EquipInfo.Weapon;
            if (!NetSession.Active || weapon.Beam != BeamType.VoltDriver || !ModChargeReady
                || !weapon.Afflictions[1].TestFlag(Affliction.Disrupt) || _disruptedTimer > 0)
            {
                return 0;
            }
            Vector3 direction = _aimPosition - _muzzlePos;
            if (!Single.IsFinite(direction.X) || !Single.IsFinite(direction.Y) || !Single.IsFinite(direction.Z)
                || direction.LengthSquared < 0.000001f)
            {
                return 0;
            }
            EntityBase? target = BeamProjectileEntity.ModFindNonContinuousHomingTarget(
                this, EquipInfo, _muzzlePos, direction.Normalized(), _scene);
            if (target == null)
            {
                return IntentPacket.HomingTargetValid;
            }
            if (target is PlayerEntity player && player.SlotIndex >= 0
                && player.SlotIndex < IntentPacket.HomingTargetMask)
            {
                return (byte)(IntentPacket.HomingTargetValid | (player.SlotIndex + 1));
            }
            // The compact field intentionally represents player slots only.
            // Let non-player targets use the engine's normal local selection.
            return 0;
        }

        internal void ModSetShotState(int chargeLevel, bool doubleDamage)
        {
            if (SlotIndex == NetHooks.LocalSlot)
            {
                // Never the machine's own player: this is its own state coming
                // back to it a round trip later.
                return;
            }
            EquipInfo.ChargeLevel = (ushort)Math.Clamp(chargeLevel, 0, UInt16.MaxValue);
            // Boost charge, speed and ram damage now belong to the same
            // authoritative movement step. A delayed owner report must not
            // replace the damage the server just derived for its boost.
            if (doubleDamage)
            {
                // Held up rather than counted down: the owner says so again
                // every couple of frames for as long as it lasts, and the
                // moment they stop saying so it is gone. A puppet's own
                // countdown is not the clock that matters, and the engine only
                // ever asks whether this is above zero.
                if (_doubleDmgTimer < 8)
                {
                    _doubleDmgTimer = 8;
                }
            }
            else
            {
                _doubleDmgTimer = 0;
            }
        }

        internal void ModSetAmmo(int ua, int missiles)
        {
            // -1 is the engine's "infinite" marker; a puppet must not be
            // handed one by a malformed packet.
            _ammo[UA] = Math.Clamp(ua, 0, _ammoMax[UA]);
            _ammo[Missiles] = Math.Clamp(missiles, 0, _ammoMax[Missiles]);
        }

        /// <summary>
        /// Put this hunter's own weapon in its hands, with ammo.
        ///
        /// The three affliction states -- frozen, burning, disrupted -- exist
        /// only on the affinity version of a weapon: TryEquipWeapon swaps in
        /// table entry beam + 9 when the beam is the hunter's own, and only
        /// those entries carry an Affliction flag. In a match that weapon is
        /// picked up, not issued, so a scripted tour that never happened to
        /// walk over the right pickup could not reach three of the states a
        /// player can be put into. The tour issues it instead.
        ///
        /// Test-only: nothing in a normal session calls this.
        /// </summary>
        /// <summary>
        /// Put a remote player's zoom where its owner says it is.
        ///
        /// Only for a weapon that can zoom: the engine's own toggle is gated
        /// the same way, and a player flagged zoomed while holding a weapon
        /// with no sight would draw a laser it does not have and take the
        /// Imperialist's unzoomed halving on a beam that has none.
        /// </summary>
        internal void ModSetZoom(bool zoomed)
        {
            bool canZoom = EquipInfo.Weapon != null
                && EquipInfo.Weapon.Flags.TestFlag(WeaponFlags.CanZoom);
            bool wanted = zoomed && canZoom;
            if (EquipInfo.Zoomed != wanted)
            {
                UpdateZoom(wanted);
            }
        }

        internal void ModArmAffinityWeapon()
        {
            ModArmWeapon(Weapons.GetAffinityBeam(Hunter));
        }

        /// <summary>
        /// Issue a weapon outright: available, chargeable, full ammo, equipped.
        ///
        /// Everything a probe needs and nothing a match should ever do. In a
        /// match a weapon is picked up, so a test that waits to walk over the
        /// right pickup never reaches the thing it was written to measure --
        /// which is how the affliction states and the zoom phase both went
        /// untested for months.
        /// </summary>
        internal void ModArmWeapon(BeamType beam)
        {
            if (beam < BeamType.PowerBeam || beam > BeamType.OmegaCannon)
            {
                return;
            }
            _availableWeapons[beam] = true;
            _availableCharges[beam] = true;
            WeaponInfo info = Weapons.Current[(int)beam];
            _ammo[info.AmmoType] = _ammoMax[info.AmmoType];
            if (CurrentWeapon != beam)
            {
                TryEquipWeapon(beam, silent: true);
            }
        }

        /// <summary>
        /// Issue a weapon that can zoom, for the tour's zoom phase.
        ///
        /// The phase used to press the Imperialist's own key and hope. In a
        /// match it is picked up, not issued, so a fresh roster has nobody
        /// holding one, the key press did nothing, and the phase reported
        /// `untested` run after run -- which reads as "not proven" and was
        /// really "never attempted". The same reasoning as
        /// <see cref="ModArmAffinityWeapon"/>: a probe that waits to walk over
        /// the right pickup never gets there.
        ///
        /// Judicator as the fallback because it is the other weapon in the
        /// table carrying <see cref="WeaponFlags.CanZoom"/>.
        /// </summary>
        internal void ModArmZoomWeapon()
        {
            BeamType beam = BeamType.Imperialist;
            if (!Weapons.Current[(int)beam].Flags.TestFlag(WeaponFlags.CanZoom))
            {
                beam = BeamType.Judicator;
            }
            ModArmWeapon(beam);
        }

        /// <summary>
        /// Turn a scripted player without a session behind it.
        ///
        /// The map audit runs the tour with no network at all, and the hook
        /// that normally applies a scripted player's aim (ModApplyRemoteAim)
        /// returns early when the session is offline. Without this the audit's
        /// eight players walked, jumped and fired, but faced wherever they
        /// spawned -- so they rarely hit each other, and everything that only
        /// happens on a hit went untested.
        /// </summary>
        internal void ModApplyScriptAim(float deltaX, float deltaY)
        {
            UpdateAimY(deltaY);
            UpdateAimX(deltaX);
        }

        /// <summary>
        /// What this player is holding and how charged it is, for the audit's
        /// affliction probe -- "the shot landed and nothing happened" has
        /// several causes and they are not distinguishable from outside.
        /// </summary>
        internal string ModWeaponState =>
            $"{CurrentWeapon} equip={EquipInfo.Weapon.Beam} charge={EquipInfo.ChargeLevel}"
            + $"/{EquipInfo.Weapon.MinCharge * 2} chargeable={_availableCharges[CurrentWeapon]}"
            + $" affliction={EquipInfo.Weapon.Afflictions[1]}"
            + $" shooting={Flags2.TestFlag(PlayerFlags2.Shooting)}";

        /// <summary>Charge built up on the current weapon, for the audit.</summary>
        internal int ModChargeLevel => EquipInfo.ChargeLevel;

        /// <summary>
        /// Whether letting go now would fire a *charged* shot.
        ///
        /// Not a fixed number of frames: a weapon without the PartialCharge
        /// flag counts as charged only at FullCharge (60 frames doubled), and
        /// one with it from MinCharge (15 doubled). A probe that held the
        /// trigger for what looked like plenty -- 64 frames -- fired shot
        /// after uncharged shot, and every affliction in the game lives on the
        /// charged entry of its weapon.
        /// </summary>
        internal bool ModChargeReady
        {
            get
            {
                WeaponInfo weapon = EquipInfo.Weapon;
                if (!weapon.Flags.TestFlag(WeaponFlags.CanCharge))
                {
                    return false;
                }
                int needed = weapon.Flags.TestFlag(WeaponFlags.PartialCharge)
                    ? weapon.MinCharge * 2
                    : weapon.FullCharge * 2;
                return EquipInfo.ChargeLevel >= needed;
            }
        }

        /// <summary>Frozen by an affinity Judicator, for the audit.</summary>
        internal bool ModFrozen => _frozenTimer > 0;

        /// <summary>
        /// Freeze or thaw this player because the authority says so.
        ///
        /// The affliction cannot travel with the hit that caused it: the
        /// freeze is applied inside <c>TakeDamage</c> from the beam entity,
        /// and <c>NetDamage.Replay</c> has no beam to hand it -- so before
        /// this, a player frozen on the authority was frozen *nowhere else*.
        /// They walked around normally on their own screen, and the machine
        /// running the simulation, which pins a puppet wherever its owner last
        /// said it was, drew a block of ice gliding across the room. Both
        /// halves of "either they are frozen and cannot move, or they are
        /// not".
        ///
        /// The countdown still runs locally rather than being carried frame by
        /// frame: it starts a fraction of a second late, which nobody can see,
        /// and it is topped up for as long as the flag is still set so it
        /// cannot expire early. Ending is handed back to the engine -- one
        /// frame left, and the ordinary tick plays the sound and the ice
        /// breaking, exactly as it does for the machine that resolved the hit.
        /// </summary>
        internal void ModSetFrozen(bool frozen)
        {
            if (_health <= 0)
            {
                return;
            }
            if (frozen)
            {
                if (_frozenTimer == 0)
                {
                    // The engine's own numbers and the engine's own re-freeze
                    // rule: a second freeze within two seconds of the last is
                    // a short one. A relayed freeze always has an attacker, so
                    // the multiplayer duration is the one that applies.
                    _soundSource.PlaySfx(SfxId.SHOTGUN_FREEZE);
                    if (IsMainPlayer)
                    {
                        ResetCombatVisor();
                        _drawIceLayer = true;
                    }
                    _frozenTimer = _timeSinceFrozen > 60 * 2 // todo: FPS stuff
                        ? (ushort)(75 * 2)
                        : (ushort)(15 * 2);
                    _frozenGfxTimer = (ushort)(_frozenTimer + 5 * 2); // todo: FPS stuff
                    EndAltAttack();
                }
                else if (_frozenTimer < 2)
                {
                    // Still frozen where it matters; do not let this copy thaw
                    // a round trip early and start moving again.
                    _frozenTimer = 2;
                }

                // The authoritative Frozen bit can remain set longer than this
                // copy's graphics countdown (especially on a quick re-freeze).
                // Keep the visible ice alive for as long as gameplay says the
                // player is frozen, then let the engine's normal break/thaw path
                // run once the authoritative bit clears.
                ushort gfxFloor = (ushort)Math.Min(UInt16.MaxValue, _frozenTimer + 5 * 2);
                if (_frozenGfxTimer < gfxFloor)
                {
                    _frozenGfxTimer = gfxFloor;
                }
                if (IsMainPlayer)
                {
                    _drawIceLayer = true;
                }
            }
            else if (_frozenTimer > 1)
            {
                _frozenTimer = 1;
            }
        }

        /// <summary>
        /// Put the collision volume back where the player now is.
        ///
        /// The engine recomputes it once a frame, at the end of the movement
        /// step -- and a puppet is moved *after* that, when its owner's
        /// reported position arrives (see NetPlayerBridge.Move). So for every
        /// remote player the volume described where this machine's own
        /// simulation had guessed they were, and the correction never reached
        /// it.
        ///
        /// Two things are drawn from that volume rather than from the
        /// position: the blob shadow under a hunter and the flames of a burn.
        /// Both therefore came out at the guess while the model was drawn at
        /// the correction -- which for anybody moving is a shadow trailing a
        /// step behind them, on the ground they have just left. Reported as
        /// "the shadow is behind the character instead of under them".
        ///
        /// It is also what shots are tested against, so a volume a correction
        /// behind is a hitbox a correction behind.
        /// </summary>
        internal void ModRefreshVolume()
        {
            _volume = CollisionVolume.Move(_volumeUnxf, Position);
        }

        /// <summary>Burning from an affinity Magmaul.</summary>
        internal bool ModBurning => _burnTimer > 0;

        /// <summary>Aim disrupted by an affinity Volt Driver.</summary>
        internal bool ModDisrupted => _disruptedTimer > 0;

        /// <summary>
        /// Disrupt or un-disrupt this player because the authority says so.
        ///
        /// The same shape as <see cref="ModSetFrozen"/>, and there for the
        /// same reason: the affliction is applied inside <c>TakeDamage</c>
        /// from the beam that landed the hit, and the replay that reaches
        /// every other machine has no beam to hand it. Before this, a charged
        /// Volt Driver disrupted its victim on the authority's copy of them
        /// and nowhere else -- so the one player who should have seen the
        /// screen tear apart was the one player who did not.
        ///
        /// Two things happen here and both matter. The timer is what the aim
        /// path reads, so the disruption is *felt* -- and it is felt on the
        /// machine whose aim it is, which is the only place that can apply it
        /// to a real player's input rather than to a puppet's relayed
        /// direction. The HUD state is the distortion itself, which is the
        /// main player's alone.
        ///
        /// The countdown runs locally, like the freeze's: it starts a fraction
        /// of a second late, which nobody can see, and it is topped up while
        /// the flag holds so it cannot end early. Ending is the engine's --
        /// <c>UpdateDisruptedState</c> runs the recovery out through its own
        /// four states, and cutting it short would leave the screen mid-shift.
        /// </summary>
        internal void ModSetDisrupted(bool disrupted)
        {
            if (_health <= 0)
            {
                return;
            }
            if (disrupted)
            {
                if (_disruptedTimer == 0)
                {
                    _disruptedTimer = 60 * 2; // todo: FPS stuff
                    if (IsMainPlayer)
                    {
                        HudOnDisrupted();
                        _soundSource.PlaySfx(SfxId.LOB_DISRUPT);
                    }
                }
                else if (_disruptedTimer < 2)
                {
                    _disruptedTimer = 2;
                }
            }
            else if (_disruptedTimer > 1)
            {
                // One frame left rather than none, so the ordinary tick is
                // what ends it: the recovery animation is driven from there.
                _disruptedTimer = 1;
            }
        }

        /// <summary>
        /// Set this player on fire, or put them out, because the authority
        /// says so.
        ///
        /// Cosmetic here and nothing more, which is the point: the burn's own
        /// tick calls <c>TakeDamage</c> once every eight frames, and
        /// <see cref="NetDamage.Suppress"/> drops that everywhere but on the
        /// machine resolving the match -- so the fire burns on every screen
        /// while the damage still travels as damage, counted once, by the one
        /// machine entitled to count it. <c>_burnedBy</c> is deliberately left
        /// alone for the same reason: attribution belongs to the kill the
        /// authority resolves, not to a copy of the flames.
        /// </summary>
        internal void ModSetBurning(bool burning)
        {
            if (_health <= 0)
            {
                return;
            }
            if (burning)
            {
                if (_burnTimer == 0)
                {
                    _burnTimer = 150 * 2; // todo: FPS stuff
                    CreateBurnEffect();
                }
                else if (_burnTimer < 2)
                {
                    _burnTimer = 2;
                }
            }
            else if (_burnTimer > 1)
            {
                // The tick clears the effect when the timer runs out, which is
                // where the flames should stop being drawn from.
                _burnTimer = 1;
            }
        }

        /// <summary>Whether this player can currently be zoomed, for the test tour.</summary>
        internal bool ModCanZoom => EquipInfo.Weapon != null
            && EquipInfo.Weapon.Flags.TestFlag(WeaponFlags.CanZoom);

        /// <summary>
        /// The form and animation state, for the per-client log.
        ///
        /// A puppet stuck between forms and one that simply has not been told
        /// yet look identical in the alt-form flag alone; the animation it is
        /// playing, and whether that animation has ended, is what separates
        /// them.
        /// </summary>
        internal string ModFormState()
        {
            string form = IsAltForm ? "alt" : "biped";
            if (IsMorphing)
            {
                form += "+morphing";
            }
            if (IsUnmorphing)
            {
                form += "+unmorphing";
            }
            // The frozen timer belongs here because it is the one thing that
            // stops a form transition without stopping anything else:
            // ProcessPlayer only advances the biped model's animation while
            // `_frozenTimer == 0`, and a morph completes when that animation
            // ends. A puppet frozen mid-morph therefore sits in
            // "biped+morphing" until the ice runs out, which reads exactly
            // like a stuck puppet and is not one.
            return $"{form}/{Biped2Anim}"
                + (Biped2Flags.TestFlag(AnimFlags.Ended) ? "/ended" : "")
                + (_frozenTimer > 0 ? $"/frozen:{_frozenTimer}" : "")
                + (_health == 0 ? "/dead" : "");
        }

        private OpenTK.Mathematics.Vector3 _modLastGoodFacing = -OpenTK.Mathematics.Vector3.UnitZ;
        private OpenTK.Mathematics.Vector3 _modLastGoodGunVec = -OpenTK.Mathematics.Vector3.UnitZ;
        private OpenTK.Mathematics.Vector3 _modLastGoodPosition;

        /// <summary>
        /// Put back the last usable vectors if this player's have stopped
        /// being numbers.
        ///
        /// UpdateAimFacing normalises (facing - gun * dot), which is the zero
        /// vector exactly when the aim and the body line up -- and
        /// normalising zero gives NaN. In a networked match that does not stay
        /// local: the NaN facing is published, every client applies it, anyone
        /// aiming at that player computes a NaN aim of their own, and within
        /// seconds several players are frozen, dying repeatedly, with every
        /// measurement reading NaN. One frame of stale facing is a far smaller
        /// price than that.
        /// </summary>
        internal void ModRepairVectors()
        {
            if (Finite(_facingVector))
            {
                _modLastGoodFacing = _facingVector;
            }
            else
            {
                _facingVector = _modLastGoodFacing;
                NetLog.Event($"slot {SlotIndex} facing repaired, {ModFormState()}, gun={_gunVec1}");
            }
            if (Finite(_gunVec1))
            {
                _modLastGoodGunVec = _gunVec1;
            }
            else
            {
                _gunVec1 = _modLastGoodGunVec;
                NetLog.Event($"slot {SlotIndex} aim repaired");
            }
            if (Finite(Position))
            {
                _modLastGoodPosition = Position;
            }
            else
            {
                Position = _modLastGoodPosition;
                NetLog.Event($"slot {SlotIndex} position repaired");
            }
            if (!Finite(Speed))
            {
                Speed = OpenTK.Mathematics.Vector3.Zero;
            }
            if (!Finite(_aimPosition))
            {
                _aimPosition = Position + _gunVec1;
            }
        }

        private static bool Finite(OpenTK.Mathematics.Vector3 value)
        {
            return Single.IsFinite(value.X) && Single.IsFinite(value.Y) && Single.IsFinite(value.Z);
        }

        /// <summary>
        /// Run the engine's own death sequence for a player the authority has
        /// killed by something that was not a hit -- a fall, a kill plane.
        /// Those never produce a damage record, so the victim only saw its
        /// health become zero and never counted a death or played any of it.
        /// </summary>
        internal void ModNetDie()
        {
            NetDamage.ReplayDeath(this);
        }

        /// <summary>
        /// The scoreboard's row count and the height it would need, for the
        /// test report.
        ///
        /// The list is drawn from a fixed centre on a 192-pixel screen, so
        /// past a certain number of players the first and last rows simply
        /// leave it. Checking the height is how a headless run can tell that
        /// a six-player scoreboard still fits, since the panel is drawn
        /// straight to the window and never appears in a captured frame.
        /// </summary>
        internal (int Rows, float Height) ModScoreboardSize()
        {
            return (GameState.ActivePlayers, GetScoreboardHeight());
        }

        /// <summary>
        /// Whether any beam can hurt this player at all.
        ///
        /// BeamEffectiveness is an array of nine multipliers and it defaults
        /// to Zero for every beam; only Spawn() fills it in. TakeDamage
        /// returns immediately on a Zero entry, so a player that reached the
        /// map without going through Spawn is not hard to hit -- it is
        /// literally invulnerable, and nothing about it looks wrong.
        /// </summary>
        internal bool ModCanBeHurt()
        {
            for (int i = 0; i < BeamEffectiveness.Length; i++)
            {
                if (BeamEffectiveness[i] != Effectiveness.Zero)
                {
                    return true;
                }
            }
            return false;
        }

        private void ApplyModAim()
        {
            ApplyGamepadAim();
            if (!NetSession.Active)
            {
                return;
            }
            if (SlotIndex == NetHooks.LocalSlot)
            {
                // A scripted local player has no mouse, so its rotation has
                // to enter the same way a remote player's does. Same call
                // site, same point in the frame -- an autopilot that turned
                // at a different moment from the engine would be a test of
                // something the game never does.
                if (NetTestScript.Enabled)
                {
                    UpdateAimY(NetTestScript.AimDeltaY);
                    UpdateAimX(NetTestScript.AimDeltaX);
                }
                return;
            }
            if (!NetSession.RemoteIntentValid[SlotIndex])
            {
                return;
            }
            // Current-life intents have already passed lifecycle validation.
            // Movement authority needs this aim immediately; waiting for a
            // spawn acknowledgement made forward movement use the spawn-point
            // heading for part of a round trip.
            ModSetAim(NetSession.RemoteIntents[SlotIndex].Aim);
        }

        /// <summary>
        /// The right stick's turn, in the same place and the same units the
        /// mouse's goes in.
        ///
        /// Here rather than anywhere more obvious because this is the one
        /// point upstream already offers inside the player's own input step:
        /// aim applied a few lines earlier or later than the mouse's would
        /// feel different from the mouse for reasons nobody could name, and
        /// the alternative is a call site in <c>PlayerInput.cs</c>.
        ///
        /// Only the hunter this machine is driving. Every player runs this
        /// method, including bots and the puppets standing in for other
        /// people, and a pad that turned all of them would be a very strange
        /// bug to read about.
        /// </summary>
        private void ApplyGamepadAim()
        {
            if (IsBot || SlotIndex != PlayerEntity.MainPlayerIndex
                || Mods.SpectatorMode.IsSpectating
                || Flags1.TestFlag(PlayerFlags1.NoAimInput))
            {
                _controllerAssist.Reset();
                return;
            }
            float x = Mods.Input.GamepadInput.AimDeltaX * (EquipInfo.Zoomed ? Mods.Input.GamepadOptions.ScopedX : 1);
            float y = Mods.Input.GamepadInput.AimDeltaY * (EquipInfo.Zoomed ? Mods.Input.GamepadOptions.ScopedY : 1);
            var assisted = ApplyControllerAssist(x, y);
            x = assisted.X; y = assisted.Y;
            if (x == 0 && y == 0)
            {
                return;
            }
            ModNoteInput();
            UpdateHudShiftY(y);
            UpdateHudShiftX(x);
            UpdateAimY(y);
            UpdateAimX(x);
        }

        /// <summary>
        /// Say that this player is being played, when the thing playing it is
        /// not this machine's keyboard or mouse.
        ///
        /// <c>Input.HasInput</c> is written in one place -- the pass in
        /// <c>ProcessAllInput</c> that turns a <see cref="OpenTK.Windowing.GraphicsLibraryFramework.KeyboardState"/>
        /// into binds -- and the pad's contribution is *ored* onto those binds
        /// after that pass has run (see <c>GamepadInput.Apply</c>). So a player
        /// holding nothing but a controller looked idle to everything that
        /// asks "has this person touched anything lately", and two of those
        /// answers are visible from the first minute of a match:
        ///
        /// - <c>UpdateGunAnimation</c> lowers the gun after <c>GunIdleTime</c>
        ///   and only raises it again on a frame where <c>_timeSinceInput</c>
        ///   is exactly zero. On a pad that frame never came: the gun sank off
        ///   the bottom of the screen and stayed there, and
        ///   <c>CanShoot</c>/<c>TryEquipWeapon</c> both refuse while
        ///   <c>GunAnimation.UpDown</c> is playing -- so the player could
        ///   neither fire nor change weapon for the rest of the life. Reported
        ///   from Android as "the weapon falls away and I cannot shoot".
        /// - the idle sway starts drifting the aim on its own, which is worst
        ///   with the one weapon whose player is deliberately holding still.
        ///
        /// The touch controls do not need this: they press the keys the player
        /// has bound into a keyboard of their own, so they arrive *inside* the
        /// pass that sets the flag.
        ///
        /// **Three writers, not one.** The pad is the visible case; the other
        /// two are worse and were found by following it:
        ///
        /// - a **remote player** is driven from relayed intents
        ///   (`NetPlayerBridge.ApplyIntent`), and `ProcessAllInput` skips the
        ///   flag entirely for them. So every puppet on every machine looked
        ///   idle from the moment it stopped respawning or changing weapon --
        ///   which lowers its gun, and `CanShoot` refuses to spawn a beam
        ///   while that animation is playing. The authority is a machine full
        ///   of puppets: a player holding still and firing, which is what a
        ///   sniper does, had their shots fail to spawn on the one machine
        ///   whose shots count. That is "my Imperialist shot went through
        ///   them and did nothing".
        /// - a **scripted client** writes into the same binds and is not a
        ///   bot, so the harness had the fault too, which is why its shot
        ///   counts were a fraction of what the tour asks for and why the
        ///   affliction probe could never fire the charged shot it waits to
        ///   fire.
        /// </summary>
        internal void ModNoteInput()
        {
            Input.HasInput = true;
        }
    }
}
