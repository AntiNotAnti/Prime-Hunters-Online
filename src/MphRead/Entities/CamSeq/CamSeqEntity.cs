using System.Collections.Generic;
using System.Diagnostics;
using MphRead.Formats;
using MphRead.Sound;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    public class CamSeqEntity : EntityBase
    {
        public CameraSequenceEntityData Data { get; }
        protected override Vector4? OverrideColor { get; } = new ColorRgb(0xFF, 0x69, 0xB4).AsVector4();
        public CameraSequence Sequence { get; }
        public string Name => Sequence.Name;

        private bool _active = false;
        private bool _handoff = false;
        private byte _handoffTimer = 0;
        private byte _delayTimer = 0;
        private EntityBase? _endMessageTarget = null;

        public static CamSeqEntity? Current { get => global::MphRead.GameState.Current.CameraSequences.Entity; set => global::MphRead.GameState.Current.CameraSequences.Entity = value; }
        // todo: clear when changing rooms
        private CameraSequence?[] _sequenceData => _scene.CameraSequences.Sequences;

        public CamSeqEntity(CameraSequenceEntityData data, Scene scene) : base(EntityType.CameraSequence, scene)
        {
            Data = data;
            Id = data.Header.EntityId;
            SetTransform(data.Header.FacingVector, data.Header.UpVector, data.Header.Position);
            AddPlaceholderModel();
            byte seqId = data.SequenceId;
            CameraSequence? sequence = _sequenceData[seqId];
            if (sequence == null)
            {
                sequence = CameraSequence.Load(seqId, scene);
                _sequenceData[seqId] = sequence;
            }
            Sequence = sequence;
        }

        public static void ClearData(Scene? scene = null)
        {
            System.Array.Clear((scene?.CameraSequences ?? global::MphRead.GameState.Current.CameraSequences).Sequences);
        }

        public override void Initialize()
        {
            base.Initialize();
            // these would override the keyframe refs if they were used, but they aren't
            Debug.Assert(Data.PlayerId1 == 0);
            Debug.Assert(Data.PlayerId2 == 0);
            Debug.Assert(Data.Entity1 == -1);
            Debug.Assert(Data.Entity2 == -1);
            if (Data.EndMessageTargetId != -1)
            {
                _scene.TryGetEntity(Data.EndMessageTargetId, out _endMessageTarget);
            }
            Sequence.Initialize();
        }

        public override bool Process()
        {
            if (!_active)
            {
                return base.Process();
            }
            if (_handoffTimer > 0)
            {
                _handoffTimer--;
                if (_handoffTimer == 0)
                {
                    Cancel();
                    return base.Process();
                }
            }
            if (_delayTimer <= Data.DelayFrames * 2) // todo: FPS stuff
            {
                TryStart();
            }
            if (_delayTimer > Data.DelayFrames * 2) // todo: FPS stuff
            {
                Sequence.Process();
                if (Sequence.Flags.TestFlag(CamSeqFlags.CanEnd))
                {
                    if (Data.Loop != 0)
                    {
                        if (Bugfixes.SmoothCamSeqHandoff)
                        {
                            Sequence.Restart(Sequence.TransitionTimer, Sequence.TransitionTime);
                        }
                        else
                        {
                            // setting back the timer doesn't do anything, since the time value it compares against is lost,
                            // and Restart will update the frame values while both are set to zero anyway
                            ushort transitionTimer = Sequence.TransitionTimer;
                            Sequence.Restart();
                            Sequence.TransitionTimer = transitionTimer;
                        }
                    }
                    else
                    {
                        int sfxData = CameraSequence.SfxData[Data.SequenceId];
                        // the game stops free SFX scripts here, but we don't have the kind of
                        // "detach" action we need to do that without cutting off ending sounds
                        if ((sfxData & 0x4000) != 0)
                        {
                            if (Sfx.ForceFieldSfxMute > 0)
                            {
                                Sfx.ForceFieldSfxMute--;
                            }
                        }
                        if ((sfxData & 0x8000) != 0)
                        {
                            _scene.Players.Main.RestartLongSfx();
                        }
                        else
                        {
                            _scene.Players.Main.RestartTimedSfx();
                        }
                        int musicValue = CameraSequence.MusicData[Data.SequenceId];
                        if (musicValue != 0
                            && ((musicValue & 0x2000) == 0 || (((int)_scene.GameState.StorySave.BossFlags >> (2 * _scene.AreaId)) & 3) == 0)
                            && ((musicValue & 0x400) == 0 || _scene.GameState.EscapeTimer == -1 || _scene.GameState.EscapeState != EscapeState.Escape)
                            && (musicValue & 0x4000) == 0
                            && (musicValue & 0x8000) == 0)
                        {
                            Music.PlayPausedMusic();
                        }
                        _active = false;
                        Sequence.End();
                        _scene.CameraSequences.Entity = null;
                        _scene.Players.Main.RefreshExternalCamera();
                        SendEndMessage();
                    }
                }
            }
            return base.Process();
        }

        private void TryStart()
        {
            PlayerEntity player = _scene.Players.Main;
            if (player.Health == 0 && player.DeathCountdown > 0 && Data.BlockInput != 0 || _scene.GameState.DialogPause)
            {
                return;
            }
            int musicValue = CameraSequence.MusicData[Data.SequenceId];
            bool hasMusic = musicValue != 0
                && ((musicValue & 0x2000) == 0 || (((int)_scene.GameState.StorySave.BossFlags >> (2 * _scene.AreaId)) & 3) == 0)
                && ((musicValue & 0x400) == 0 || _scene.GameState.EscapeTimer == -1 || _scene.GameState.EscapeState != EscapeState.Escape);
            int sfxData = CameraSequence.SfxData[Data.SequenceId];
            if (_delayTimer == 0)
            {
                if (Data.Loop == 0)
                {
                    if ((sfxData & 0x2000) != 0)
                    {
                        Sfx.Instance.StopSoundById((int)SfxId.CHIME1);
                    }
                    if ((sfxData & 0x4000) != 0)
                    {
                        Sfx.ForceFieldSfxMute++;
                    }
                    if ((sfxData & 0x8000) != 0)
                    {
                        _scene.Players.Main.StopLongSfx();
                    }
                    else
                    {
                        _scene.Players.Main.StopTimedSfx();
                    }
                }
                if (hasMusic && (musicValue & 0x4000) == 0)
                {
                    Music.Pause();
                }
            }
            _delayTimer++;
            if (_delayTimer > Data.DelayFrames * 2) // todo: FPS stuff
            {
                if (hasMusic)
                {
                    int musicOrSeqId = musicValue & 0x3FF;
                    if ((musicValue & 0x800) != 0)
                    {
                        Music.MusicToResume = (MusicId)musicOrSeqId;
                    }
                    else if ((musicValue & 0x1000) != 0 && _scene.GameState.EscapeTimer != -1 && _scene.GameState.EscapeState == EscapeState.Escape)
                    {
                        Music.PlayMusic(MusicId.SEQ_OREGANO_M55);
                        Music.UpdateEscapeMusic();
                    }
                    else if ((musicValue & 0x4000) != 0)
                    {
                        Music.PlayMusic((MusicId)musicOrSeqId);
                    }
                    else
                    {
                        Music.PlaySeq((SeqId)musicOrSeqId);
                    }
                }
                int scriptId = sfxData & 0x1FFF;
                if (scriptId != 0)
                {
                    Sfx.Instance.StopFreeSfxScripts();
                    Sfx.Instance.PlayScript(scriptId | 0x4000, source: null,
                        noUpdate: false, recency: -1, sourceOnly: false, cancellable: false);
                }
                Start();
            }
        }

        private void Start()
        {
            // the game overrides keyframe values with ent1/ent2/player1/player2, but none of those are ever set
            Sequence.Flags &= ~CamSeqFlags.BlockInput;
            Sequence.Flags &= ~CamSeqFlags.ForceAlt;
            Sequence.Flags &= ~CamSeqFlags.ForceBiped;
            if (Data.BlockInput != 0)
            {
                Sequence.Flags |= CamSeqFlags.BlockInput;
            }
            if (Data.ForceAltForm != 0)
            {
                Sequence.Flags |= CamSeqFlags.ForceAlt;
            }
            else if (Data.ForceBipedForm != 0 && Data.BlockInput != 0)
            {
                Sequence.Flags |= CamSeqFlags.ForceBiped;
            }
            ushort transitionTime = (ushort)(_handoff ? 60 * 2 : 0); // todo: FPS stuff
            Sequence.SetUp(_scene.Players.Main.CameraInfo, transitionTime);
            _scene.Players.Main.RefreshExternalCamera();
        }

        private void Cancel()
        {
            PlayerEntity player = _scene.Players.Main;
            player.RestartLongSfx();
            bool currentSeq = _scene.CameraSequences.Current == Sequence;
            bool playerCam = Sequence.CamInfoRef == player.CameraInfo;
            SendEndMessage();
            Sequence.End();
            _active = false;
            if (currentSeq)
            {
                player.RefreshExternalCamera();
                if (playerCam && (player.IsAltForm || player.IsMorphing || player.IsUnmorphing))
                {
                    player.ResumeOwnCamera();
                }
            }
            if (_scene.CameraSequences.Entity == this)
            {
                _scene.CameraSequences.Entity = null;
            }
        }

        private void SendEndMessage()
        {
            if (Data.EndMessage != Message.None)
            {
                _scene.SendMessage(Data.EndMessage, this, _endMessageTarget, Data.EndMessageParam, 0);
            }
        }

        public override void HandleMessage(MessageInfo info)
        {
            if (info.Message == Message.Activate || (info.Message == Message.SetActive && (int)info.Param1 != 0))
            {
                PlayerEntity player = _scene.Players.Main;
                if (player.Health == 0 && player.DeathCountdown > 0 && Data.BlockInput != 0)
                {
                    return;
                }
                bool activate = true;
                bool handoff = false;
                if (_scene.CameraSequences.Entity != null)
                {
                    if (_scene.CameraSequences.Entity.Data.BlockInput != 0)
                    {
                        activate = false;
                    }
                    if (_scene.CameraSequences.Entity.Data.Handoff != 0 && Data.Handoff != 0)
                    {
                        handoff = true;
                        if (_scene.CameraSequences.Entity._handoffTimer == 0)
                        {
                            activate = false;
                        }
                    }
                }
                if (_scene.CameraSequences.Current != null && _scene.CameraSequences.Current.Flags.TestFlag(CamSeqFlags.BlockInput))
                {
                    activate = false;
                }
                if (activate)
                {
                    if (Cheats.SkipPlanetIntros && (Name == "unit2_land_intro" || Name == "unit1_land_intro"
                        || Name == "unit3_land_intro" || Name == "unit4_land_intro" || Name == "gorea_land_intro"))
                    {
                        return;
                    }
                    if (_scene.CameraSequences.Entity != null && _scene.CameraSequences.Entity != this)
                    {
                        if (handoff)
                        {
                            _scene.CameraSequences.Entity.Sequence.CamInfoRef = null;
                        }
                        _scene.CameraSequences.Entity.Cancel();
                    }
                    if (_scene.CameraSequences.Current != null && _scene.CameraSequences.Current != Sequence)
                    {
                        _scene.CameraSequences.Current.End();
                    }
                    if (!_active)
                    {
                        // hack to avoid delay when Octolith intro seqs start at room reload (player view should not be visible in between)
                        bool quickStart = Name == "unit2_b1_octolith_intro" || Name == "bigeye_octolith_intro";
                        _active = true;
                        _delayTimer = (byte)(quickStart ? 7 : 0);
                        _handoffTimer = 0;
                        _handoff = handoff;
                        _scene.CameraSequences.Entity = this;
                        if (Data.DelayFrames == 0 || quickStart)
                        {
                            TryStart();
                        }
                    }
                }
                else
                {
                    IReadOnlyList<CameraSequenceKeyframe> keyframes = Sequence.Keyframes;
                    for (int i = 0; i < keyframes.Count; i++)
                    {
                        CameraSequenceKeyframe keyframe = keyframes[i];
                        var message = (Message)keyframe.MessageId;
                        if (message != Message.None)
                        {
                            // game sets the keyframe as the sender
                            _scene.SendMessage(message, null!, keyframe.MessageTarget, (int)keyframe.MessageParam, 0);
                        }
                    }
                }
            }
            else if (info.Message == Message.SetActive && (int)info.Param1 == 0)
            {
                if (_handoffTimer == 0)
                {
                    _handoffTimer = 2 * 2;
                }
            }
        }

        public static void CancelCurrent(Scene? scene = null)
        {
            (scene?.CameraSequences ?? global::MphRead.GameState.Current.CameraSequences).Entity?.Cancel();
        }
    }
}
