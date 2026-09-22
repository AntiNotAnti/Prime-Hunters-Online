using System;
using MphRead.Effects;
using MphRead.Formats;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    public partial class PlayerEntity
    {
        private ushort _replicaLastDamage;
        internal void ModBeginReplicaLife(in PlayerState state) => _replicaLastDamage = state.DamageEventId;
        internal void ModAcceptReplicaDamage(in PlayerState state)
        {
            for (int i = 0; i < PlayerState.DamageHistory; i++)
            {
                DamageEvent hit = state.EventAt(i);
                if (hit.EventId == 0 || NetLifecycleTracker.Newer(hit.EventId, state.DamageEventId)
                    || _replicaLastDamage != 0 && !NetLifecycleTracker.Newer(hit.EventId, _replicaLastDamage)) continue;
                _replicaLastDamage = hit.EventId;
                if (_health == 0) continue;
                _timeSinceDamage = 0;
                if (state.Health == 0 && hit.EventId == state.DamageEventId) { ModAcceptReplicaDeath(); continue; }
                if (((DamageFlags)hit.Flags & DamageFlags.NoSfx) == 0) PlayHunterSfx(HunterSfx.Damage);
                if (IsAltForm || _frozenTimer != 0) continue;
                float side = Vector3.Dot(hit.Direction, Vector3.Cross(_facingVector, Vector3.UnitY));
                float forward = Vector3.Dot(hit.Direction, _facingVector);
                PlayerAnimation animation = Math.Abs(side) > Math.Abs(forward)
                    ? side > 0 ? PlayerAnimation.DamageLeft : PlayerAnimation.DamageRight
                    : forward > 0 ? PlayerAnimation.DamageBack : PlayerAnimation.DamageFront;
                SetBipedAnimation(animation, AnimFlags.NoLoop, setBiped1: false, setBiped2: true, setIfMorphing: false);
                CameraInfo.SetShake(Math.Clamp(hit.Damage * .01f, .05f, 1));
            }
        }
        /// <summary>Enter the recorded death presentation without resolving damage,
        /// emitting a kill, changing scores or granting a new live life.</summary>
        internal void ModAcceptReplicaDeath()
        {
            if (!_scene.Services.IsReplica || _health == 0) return;
            _soundSource.StopAllSfx(force: true);
            PlayHunterSfx(HunterSfx.Death);
            if (IsAltForm || IsMorphing) _scene.SpawnEffect(216, Vector3.UnitX, Vector3.UnitY, Position);
            ClearReplicaEffect(ref _furlEffect); ClearReplicaEffect(ref _boostEffect);
            ClearReplicaEffect(ref _burnEffect); ClearReplicaEffect(ref _chargeEffect);
            ClearReplicaEffect(ref _muzzleEffect); ClearReplicaEffect(ref _doubleDmgEffect);
            ClearReplicaEffect(ref _deathaltEffect);
            _health = 0;
            _healthRecovery = _ammoRecovery[0] = _ammoRecovery[1] = 0;
            EquipInfo.ChargeLevel = 0;
            CameraInfo.Shake = 0;
            _doubleDmgTimer = _deathaltTimer = _cloakTimer = 0;
            _frozenTimer = _frozenGfxTimer = _disruptedTimer = _burnTimer = 0;
            Flags2 &= ~PlayerFlags2.Cloaking;
            Speed = Vector3.Zero;
            _respawnTimer = RespawnTime;
            _timeSinceDead = 0;
            _boostCharge = 0;
            UpdateZoom(false);
            WeaponSelection = CurrentWeapon;
            Flags1 &= ~PlayerFlags1.WeaponMenuOpen;
            Controls.ClearAll();
        }
        private void ClearReplicaEffect(ref EffectEntry? effect)
        {
            if (effect == null) return;
            _scene.UnlinkEffectEntry(effect); effect = null;
        }
    }
}
