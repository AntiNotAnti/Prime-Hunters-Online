using MphRead.Formats;
using MphRead.Formats.Culling;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    public class ItemSpawnEntity : EntityBase
    {
        private readonly ItemSpawnEntityData _data;
        private bool _playKeySfx = false;
        private ushort _spawnCount = 0;
        private ushort _spawnCooldown = 0;
        private sbyte _lastPickerSlot = -1;
        private bool _linkDone = false;
        private EntityBase? _parent = null;
        private Vector3 _invPos;
        private EntityBase? _pickupNotifyEntity = null;

        public ItemSpawnEntityData Data => _data;
        public new bool Active { get; set; }
        public bool AlwaysActive { get; set; }
        public ItemInstanceEntity? Item { get; set; }
        public NodeData3? ClosestNode { get; set; } = null;

        // used if there is no base model
        protected override Vector4? OverrideColor { get; } = new ColorRgb(0xC8, 0x00, 0xC8).AsVector4();

        public ItemSpawnEntity(ItemSpawnEntityData data, string nodeName, Scene scene)
            : base(EntityType.ItemSpawn, nodeName, scene)
        {
            _data = data;
            Id = data.Header.EntityId;
            Position = data.Header.Position.ToFloatVector(); // vecs from header are not used
            AlwaysActive = data.AlwaysActive != 0;
            if (_scene.GameState.Mode == GameMode.SinglePlayer)
            {
                int state = _scene.GameState.StorySave.InitRoomState(_scene.RoomId, Id, active: data.Enabled != 0);
                if (AlwaysActive)
                {
                    Active = data.Enabled != 0;
                }
                else
                {
                    Active = state != 0;
                }
            }
            else
            {
                Active = data.Enabled != 0;
            }
            if (scene.Services.DisablePowerups
                && Mods.Multiplayer.MapResourceRules.IsPowerup(data.ItemType))
            {
                Active = false;
            }
            _spawnCooldown = (ushort)(data.SpawnDelay * 2); // todo: FPS stuff
            if (data.HasBase != 0)
            {
                SetUpModel("items_base");
            }
            else
            {
                AddPlaceholderModel();
            }
        }

        public override void Initialize()
        {
            base.Initialize();
            if (!_scene.Services.IsReplica) Mods.Network.NetHealthSync.Register(this);
            _scene.TryGetEntity(_data.NotifyEntityId, out _pickupNotifyEntity);
        }

        public Mods.Network.HealthSpawnState ModHealthState => new(
            Item != null && Item.DespawnTimer != 0, Active, _spawnCooldown, _spawnCount, _lastPickerSlot);

        internal void ModApplyNetworkHealthState(in Mods.Network.HealthSpawnState state, bool feedback)
        {
            Active = state.Active;
            _spawnCooldown = state.Cooldown;
            _spawnCount = state.SpawnCount;
            if (!state.Available && Item != null)
            {
                int localSlot = _scene.Services.PlayerReplication.LocalSlot;
                if (feedback && Item.DespawnTimer != 0 && state.PickerSlot == localSlot
                    && localSlot >= 0 && localSlot < _scene.Players.Items.Count)
                {
                    _scene.Players.Items[localSlot].PlayHealthPickupSfx(Item.ItemType);
                }
                Item.DespawnTimer = 0;
            }
            else if (state.Available && Item == null)
            {
                Item = SpawnItem(_data.ItemType, Position.AddY(0.65f), NodeRef, _scene);
                if (Item != null) { Item.Owner = this; Item.ParentId = _data.ParentId; }
            }
        }

        public override bool Process()
        {
            if (!_linkDone && _data.ParentId != -1)
            {
                if (_scene.TryGetEntity(_data.ParentId, out EntityBase? parent))
                {
                    _parent = parent;
                }
                if (_parent != null)
                {
                    _invPos = Matrix.Vec3MultMtx4(Position, _parent.CollisionTransform.Inverted());
                }
                _linkDone = true;
            }
            if (_linkDone && _parent != null)
            {
                Position = Matrix.Vec3MultMtx4(_invPos, _parent.CollisionTransform);
            }
            if (_scene.Services.ReplicatesHealthSpawns && (Mods.Multiplayer.MapResourceRules.IsHealth(_data.ItemType)
                || _scene.Services is Mods.Network.ReplaySceneServices { HasAuthorityWorld: true }))
            {
                if (_scene.Services.TryGetHealthSpawn((short)Id, out var state))
                {
                    ModApplyNetworkHealthState(state, feedback: true);
                }
                return base.Process();
            }
            if (!Active)
            {
                return true;
            }
            if (Item == null && _spawnCooldown > 0)
            {
                _spawnCooldown--;
            }
            if (Item == null && _spawnCooldown == 0 && (_data.MaxSpawnCount == 0 || _spawnCount < _data.MaxSpawnCount))
            {
                Item = SpawnItem(_data.ItemType, Position.AddY(0.65f), NodeRef, _scene);
                if (Item != null)
                {
                    _spawnCooldown = (ushort)(_data.SpawnInterval * 2); // todo: FPS stuff
                    _spawnCount++;
                    if (Mods.Multiplayer.MapResourceRules.IsHealth(_data.ItemType))
                    {
                        _lastPickerSlot = -1;
                    }
                    Item.Owner = this;
                    Item.ParentId = _data.ParentId;
                    if (_data.ItemType != ItemType.ArtifactKey)
                    {
                        _soundSource.Update(Position, rangeIndex: 7);
                        UpdateNodeRefVolume();
                        _soundSource.PlaySfx(SfxId.ITEM_SPAWN1);
                    }
                    else if (_playKeySfx)
                    {
                        _soundSource.PlayFreeSfx(SfxId.KEY_APPEAR);
                    }
                    _playKeySfx = false;
                }
            }
            return base.Process();
        }

        public void OnItemPickedUp(PlayerEntity? picker = null)
        {
            if (Mods.Multiplayer.MapResourceRules.IsHealth(_data.ItemType))
            {
                _lastPickerSlot = picker == null ? (sbyte)-1 : (sbyte)picker.SlotIndex;
            }
            if (_data.CollectedMessage != Message.None)
            {
                _scene.SendMessage(_data.CollectedMessage, this, _pickupNotifyEntity, _data.CollectedMsgParam1, _data.CollectedMsgParam2);
            }
        }

        public override void HandleMessage(MessageInfo info)
        {
            if (info.Message == Message.Activate || (info.Message == Message.SetActive && (int)info.Param1 != 0))
            {
                Active = true;
                _playKeySfx = true;
                if (_scene.GameState.Mode == GameMode.SinglePlayer)
                {
                    _scene.GameState.StorySave.SetRoomState(_scene.RoomId, Id, state: 3);
                }
            }
            else if (info.Message == Message.SetActive && (int)info.Param1 == 0)
            {
                Active = false;
                if (_scene.GameState.Mode == GameMode.SinglePlayer)
                {
                    _scene.GameState.StorySave.SetRoomState(_scene.RoomId, Id, state: 1);
                }
                if (Item != null)
                {
                    Item.DespawnTimer = 0;
                    _spawnCount--;
                }
            }
            else if (info.Message == Message.MoveItemSpawner && info.Sender != null)
            {
                if (info.Sender.Type == EntityType.EnemySpawn && ((EnemySpawnEntity)info.Sender).Data.EnemyType == EnemyType.Hunter)
                {
                    foreach (PlayerEntity player in _scene.GetPlayerEntities())
                    {
                        if (player.EnemySpawner == info.Sender)
                        {
                            player.GetPosition(out Vector3 position);
                            Position = position;
                            break;
                        }
                    }
                }
                else
                {
                    info.Sender.GetPosition(out Vector3 position);
                    Position = position;
                }
                if (Item != null)
                {
                    Item.Position = Position.AddY(1);
                }
            }
        }

        public override void GetDrawInfo()
        {
            if (IsVisible(NodeRef))
            {
                base.GetDrawInfo();
            }
        }

        public static ItemInstanceEntity? SpawnItemDrop(ItemType type, Vector3 position,
            NodeRef nodeRef, uint chance, Scene scene)
        {
            return SpawnItem(type, position, nodeRef, scene, chance, despawnTime: 450 * 2); // todo: FPS stuff
        }

        public static ItemInstanceEntity? SpawnItem(ItemType type, Vector3 position,
            NodeRef nodeRef, int despawnTime, Scene scene)
        {
            return SpawnItem(type, position, nodeRef, scene, chance: null, despawnTime);
        }

        private static ItemInstanceEntity? SpawnItem(ItemType type, Vector3 position, NodeRef nodeRef,
            Scene scene, uint? chance = null, int despawnTime = 0)
        {
            if (scene.Services.DisablePowerups
                && Mods.Multiplayer.MapResourceRules.IsPowerup(type))
            {
                return null;
            }
            ItemInstanceEntity? item = null;
            if (type != ItemType.None && (!chance.HasValue || scene.Random.GetRandomInt2(100) < chance.Value))
            {
                item = new ItemInstanceEntity(new ItemInstanceEntityData(position, type, despawnTime), nodeRef, scene);
                scene.AddEntity(item);
            }
            return item;
        }
    }

    public class FhItemSpawnEntity : EntityBase
    {
        private readonly FhItemSpawnEntityData _data;
        private bool _spawn = true;

        protected override Vector4? OverrideColor { get; } = new ColorRgb(0xC8, 0x00, 0xC8).AsVector4();

        public FhItemSpawnEntity(FhItemSpawnEntityData data, Scene scene) : base(EntityType.FhItemSpawn, scene)
        {
            _data = data;
            Id = data.Header.EntityId;
            SetTransform(data.Header.FacingVector, data.Header.UpVector, data.Header.Position);
            AddPlaceholderModel();
        }

        public override bool Process()
        {
            // todo: FH item spawning logic
            if (_spawn)
            {
                FhItemEntity item = SpawnItem(Position, _data.ItemType, _scene);
                _scene.AddEntity(item);
                _spawn = false;
            }
            return base.Process();
        }

        // todo: FH entity node ref
        public static FhItemEntity SpawnItem(Vector3 position, FhItemType itemType, Scene scene)
        {
            return new FhItemEntity(new FhItemInstanceEntityData(position, itemType), scene);
        }
    }
}
