using MphRead.Mods.Network;

namespace MphRead.Entities
{
    public partial class PlayerEntity
    {
        // The authority's answer is deliberately separate from the local timer.
        // A client can first learn about a life long after it began (join in
        // progress), so starting a fresh 180-frame timer when Spawn() is replayed
        // would advertise protection that the server no longer grants.
        private bool _modSpawnProtectionStateKnown;
        private bool _modSpawnProtectedByAuthority;
        private bool _modSpawnProtectionReleasedThisLife;
        private bool _modSpawnProtectionOwnerShotReleasedThisLife;
        private ushort _modSpawnProtectionVisualTicks;

        /// <summary>
        /// The custom multiplayer spawn-protection rule as it should be presented
        /// and used for client prediction. The authority owns the timer; clients
        /// use the latest authoritative state once they have one.
        /// </summary>
        internal bool ModMatchSpawnProtectionActive
        {
            get
            {
                if (!_scene.GameState.Multiplayer || !_scene.GameState.SpawnProtection || _health <= 0
                    || _modSpawnProtectionReleasedThisLife)
                {
                    return false;
                }
                return _modSpawnProtectionStateKnown
                    ? _modSpawnProtectedByAuthority
                    : _matchSpawnProtectionTimer > 0;
            }
        }

        internal ushort ModSpawnProtectionVisualTicks => _modSpawnProtectionVisualTicks;

        /// <summary>New life: no authoritative answer from the wire belongs to it yet.</summary>
        internal void ModResetSpawnProtectionReplication()
        {
            _modSpawnProtectionStateKnown = false;
            _modSpawnProtectedByAuthority = false;
            _modSpawnProtectionReleasedThisLife = false;
            _modSpawnProtectionOwnerShotReleasedThisLife = false;
            _modSpawnProtectionVisualTicks = 0;
        }

        /// <summary>
        /// Apply the server's current protection state. False is monotonic for a
        /// life: once protection has expired or a real shot has released it, an
        /// older snapshot cannot turn it back on.
        /// </summary>
        internal void ModSetSpawnProtectionFromAuthority(bool active)
        {
            _modSpawnProtectionStateKnown = true;
            if (!active)
            {
                _modSpawnProtectedByAuthority = false;
                _modSpawnProtectionReleasedThisLife = true;
                _matchSpawnProtectionTimer = 0;
            }
            else if (!_modSpawnProtectionReleasedThisLife)
            {
                _modSpawnProtectedByAuthority = true;
            }
        }

        /// <summary>
        /// A real local/authoritative weapon shot was successfully spawned.
        /// Cancel match protection immediately and repeat that fact over several
        /// intents so one lost UDP packet cannot leave the authority protecting a
        /// player who has already fired.
        /// </summary>
        internal void ModReleaseSpawnProtection()
        {
            // Every peer replays remote input for animation/projectiles, but only
            // the player's owner and the authority are entitled to decide this
            // gameplay state. An observer that happens to reproduce a shot one
            // frame early must not hide the shield before the server does.
            if (_scene.GameState.Multiplayer && NetSession.Active
                && !NetSession.IsAuthority && !NetSession.IsHost
                && SlotIndex != NetHooks.LocalSlot)
            {
                return;
            }

            bool wasMatchProtected = ModMatchSpawnProtectionActive || _matchSpawnProtectionTimer > 0;
            _matchSpawnProtectionTimer = 0;

            // Preserve the cartridge/native scripted invulnerability channel in
            // multiplayer. In single-player this is the same firing behavior the
            // original code had before the two timers were separated.
            if (!_scene.GameState.Multiplayer)
            {
                _spawnInvulnTimer = 0;
            }

            _modSpawnProtectionReleasedThisLife = true;
            if (_modSpawnProtectionStateKnown)
            {
                _modSpawnProtectedByAuthority = false;
            }
            if (wasMatchProtected && _scene.GameState.Multiplayer && _scene.GameState.SpawnProtection)
            {
                // Keep the surrender bit set for the rest of this life. It can
                // only remove protection, never grant it, so there is no reason
                // to make correctness depend on an arbitrary packet-loss window.
                _modSpawnProtectionOwnerShotReleasedThisLife = true;
            }
        }

        /// <summary>
        /// Owner-reported successful shot. This can only remove protection, never
        /// grant it, so trusting it cannot give the sender a combat advantage.
        /// Life/generation fencing in IntentPacket prevents a late report from
        /// affecting the next respawn.
        /// </summary>
        internal void ModReleaseSpawnProtectionFromNetwork()
        {
            _matchSpawnProtectionTimer = 0;
            _modSpawnProtectionStateKnown = true;
            _modSpawnProtectedByAuthority = false;
            _modSpawnProtectionReleasedThisLife = true;
        }

        /// <summary>
        /// Whether this life has already spawned the real shot that surrendered
        /// its protection. Persistent until the next Spawn() so any later intent
        /// can repair a lost release report.
        /// </summary>
        internal bool ModReportSpawnProtectionReleased()
            => _modSpawnProtectionOwnerShotReleasedThisLife;

        /// <summary>Presentation-only pulse clock; gameplay never reads it.</summary>
        internal void ModTickSpawnProtectionPresentation()
        {
            if (ModMatchSpawnProtectionActive)
            {
                if (_modSpawnProtectionVisualTicks < ushort.MaxValue)
                {
                    _modSpawnProtectionVisualTicks++;
                }
            }
            else
            {
                _modSpawnProtectionVisualTicks = 0;
            }
        }
    }
}
