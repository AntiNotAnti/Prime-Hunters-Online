using OpenTK.Mathematics;

namespace MphRead.Entities
{
    public partial class PlayerEntity
    {
        /// <summary>Enter the recorded death presentation without resolving damage,
        /// emitting a kill, changing scores or granting a new live life.</summary>
        internal void ModAcceptReplicaDeath()
        {
            if (!_scene.Services.IsReplica || _health == 0) return;
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
            Controls.ClearAll();
        }
    }
}
