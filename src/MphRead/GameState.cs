using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Sound;
using MphRead.Text;

namespace MphRead
{
    public enum MatchState
    {
        InProgress = 0,
        GameOver = 1,
        Ending = 2,
        Disconnected = 3
    }

    public enum TransitionState
    {
        None = 0,
        Start = 1,
        Process = 2,
        End = 3
    }

    public enum EscapeState
    {
        None = 0,
        Event = 1,
        Escape = 2
    }

    public partial class SceneGameState
    {
        private readonly ScenePlayerRegistry _players;
        public SceneCameraSequences CameraSequences { get; } = new();
        internal Scene? Owner { get; set; }
        internal SceneGameState(ScenePlayerRegistry players) { _players = players; ModeState = ModeStateAdventure; }

        // Bootstrap/rules can arrive before the foreground scene is constructed.
        // Carry only configuration into a new world, never its actors or progress.
        internal SceneGameState ForScene(ScenePlayerRegistry players)
        {
            var state = new SceneGameState(players)
            {
                Mode = Mode, Teams = Teams, TeamCount = TeamCount,
                FriendlyFire = FriendlyFire, PointGoal = PointGoal, TimeGoal = TimeGoal,
                OctolithReset = OctolithReset, RadarPlayers = RadarPlayers,
                AffinityWeapons = AffinityWeapons, ShadowFreeze = ShadowFreeze
            };
            Nicknames.CopyTo(state.Nicknames, 0);
            return state;
        }

        /// <summary>
        /// How long the results screen is left up, in seconds. Paired with
        /// <c>Mods.Network.DedicatedServer.EndSequenceSeconds</c>, which has
        /// to cover this and MatchFinalCameraSeconds before it.
        /// </summary>
        public const float MatchFinalCameraSeconds = 5;
        public const float MatchEndingSeconds = 10;
        public const float MatchKillcamSeconds = 5;

        public GameMode Mode { get; set; } = GameMode.SinglePlayer;
        public bool SinglePlayer => Mode == GameMode.SinglePlayer;
        public bool Multiplayer => Mode != GameMode.SinglePlayer;
        public bool IsOctolithMode => Mode == GameMode.Capture || Mode == GameMode.Bounty || Mode == GameMode.BountyTeams;
        public bool PausePrevented { get; set; }
        public bool MenuPause { get; private set; }
        public bool DialogPause { get; private set; }
        public MatchState MatchState { get; set; } = MatchState.InProgress;
        public TransitionState TransitionState { get; set; } = TransitionState.None;
        public bool InRoomTransition => TransitionState != TransitionState.None;
        public EscapeState EscapeState { get; set; } = EscapeState.None;
        public float EscapeTimer { get; set; } = -1;
        public bool EscapePaused { get; set; }
        private static string[] BuildDefaultNicknames()
        {
            string[] names = new string[PlayerEntity.SlotCapacity];
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = $"Player{i + 1}";
            }
            return names;
        }

        public int[] EncounterState { get; } = new int[PlayerEntity.SlotCapacity];
        public bool[] CompletedRandomEncounterRooms { get; } = new bool[66]; // only for the no repeat encounters feature
        public int TransitionRoomId { get; set; } = -1;
        public bool TransitionAltForm { get; set; }
        public int ActivePlayers { get; set; } = 0;
        public string[] Nicknames { get; } = BuildDefaultNicknames();
        public int[] Stars { get; } = new int[PlayerEntity.SlotCapacity];
        public int[] Standings { get; } = new int[PlayerEntity.SlotCapacity];
        public int[] TeamStandings { get; } = new int[PlayerEntity.SlotCapacity];
        public int[] ResultSlots { get; } = new int[PlayerEntity.SlotCapacity]; // ordered by team rank, then by player rank
        public int PrimeHunter { get; set; } = -1;

        public bool Teams { get; set; } = false;
        public int TeamCount { get; set; } = 2;
        public bool FriendlyFire { get; set; } = false;
        public int PointGoal { get; set; } = 0; // also used for starting extra lives
        public float TimeGoal { get; set; } = 0; // also used for starting extra lives
        /// <summary>
        /// The multiplier every hit is scaled by inside <c>TakeDamage</c>:
        /// index into <see cref="Metadata.DamageLevels"/>, 0.75 / 1 / 1.25.
        ///
        /// <b>Pinned to medium, which is x1, and nothing sets it.</b> It was a
        /// per-machine setting read out of each player's own settings file,
        /// and it multiplies the damage of *every* weapon -- so two machines
        /// that disagreed about it disagreed about every shot in the match by
        /// up to a third, in the one direction nothing corrects: a client
        /// resolves its own hits the instant it fires them
        /// (<c>NetHitPrediction</c>) and the authority resolves them again a
        /// round trip later, so a client scaling higher runs a victim's health
        /// down faster than the machine keeping score and eventually predicts
        /// a kill on somebody who is standing up.
        ///
        /// Nobody was asking for the other two answers and the cartridge's own
        /// default is the middle one, so there is one answer. <b>The setter
        /// accepts and discards</b>: upstream's console menu still has a
        /// Damage Level row and still assigns this, and the point is that the
        /// assignment does nothing rather than that the row is edited out of a
        /// file every pull from upstream has to fast-forward through.
        /// `GameSettings.ApplyMatchRules` -- which is this project's own --
        /// does not assign it at all.
        /// </summary>
        public int DamageLevel
        {
            get => 1;
            set { }
        }
        public bool OctolithReset { get; set; } = false;
        public bool RadarPlayers { get; set; } = false;
        public bool AffinityWeapons { get; set; } = false;

        /// <summary>
        /// Whether the Judicator's ice wave keeps the cartridge's own reach.
        ///
        /// On is the DS's behaviour, glitch and all -- see
        /// <c>BeamProjectileEntity.CheckIceWaveCollision</c>, where the beam's
        /// own up axis is divided out of the distance check and then used in
        /// the angle check anyway, so what is tested is not a 60-degree cone
        /// but a 60-degree wedge of a cylinder of infinite height. That is the
        /// shadow freeze: an affinity Judicator charge freezes people three
        /// floors up or down, through everything in between, and they never
        /// see what did it.
        ///
        /// Off makes it the cone it was meant to be. A rule and not a
        /// preference, for the reason FriendlyFire is one: the machine that
        /// resolves a shot decides who it hit, so two clients disagreeing
        /// about this would be two clients playing different games. The
        /// server holds it and broadcasts it in the match state.
        /// </summary>
        public bool ShadowFreeze { get; set; } = true;

        public float MatchTime { get; set; } = -1;
        public bool ForceEndGame { get; set; } = false;

        public int[] Points { get; } = new int[PlayerEntity.SlotCapacity];
        public int[] TeamPoints { get; } = new int[PlayerEntity.SlotCapacity];
        public int[] Kills { get; } = new int[PlayerEntity.SlotCapacity];
        public int[] TeamKills { get; } = new int[PlayerEntity.SlotCapacity];
        public int[] Deaths { get; } = new int[PlayerEntity.SlotCapacity];
        public int[] TeamDeaths { get; } = new int[PlayerEntity.SlotCapacity];
        public float[] Time { get; } = new float[PlayerEntity.SlotCapacity]; // used for prime hunter time, player survival time
        public float[] TeamTime { get; } = new float[PlayerEntity.SlotCapacity]; // used for defense time, max team survival time
        public int[] BeamDamageMax { get; } = new int[PlayerEntity.SlotCapacity];
        public int[] BeamDamageDealt { get; } = new int[PlayerEntity.SlotCapacity];
        public int[] DamageCount { get; } = new int[PlayerEntity.SlotCapacity];
        public int[] AltDamageCount { get; } = new int[PlayerEntity.SlotCapacity];
        public int[] KillStreak { get; } = new int[PlayerEntity.SlotCapacity];
        public int[] Suicides { get; } = new int[PlayerEntity.SlotCapacity];
        public int[] FriendlyKills { get; } = new int[PlayerEntity.SlotCapacity];
        public int[] HeadshotKills { get; } = new int[PlayerEntity.SlotCapacity];
        public int[,] BeamKills { get; } = new int[PlayerEntity.SlotCapacity, 9];

        public int[] OctolithScores { get; } = new int[PlayerEntity.SlotCapacity]; // field260 in-game
        public int[] OctolithDrops { get; } = new int[PlayerEntity.SlotCapacity]; // field268 in-game
        public int[] OctolithStops { get; } = new int[PlayerEntity.SlotCapacity]; // field270 in-game

        public int[] NodesCaptured { get; } = new int[PlayerEntity.SlotCapacity]; // field260 in-game
        public int[] NodesLost { get; } = new int[PlayerEntity.SlotCapacity]; // field268 in-game

        public int[] KillsAsPrime { get; } = new int[PlayerEntity.SlotCapacity]; // field260 in-game
        public int[] PrimesKilled { get; } = new int[PlayerEntity.SlotCapacity]; // field268 in-game

        public Action<Scene> ModeState { get; private set; } = null!;
        private bool _pausingDialog = false;
        private bool _unpausingDialog = false;

        public void PauseMenu()
        {
            MenuPause = true;
            Sfx.Instance.StopAllSound();
            Sfx.TimedSfxMute++;
        }

        public void UnpauseMenu()
        {
            MenuPause = false;
            Sfx.TimedSfxMute--;
        }

        public void PauseDialog()
        {
            _pausingDialog = true;
        }

        public void UnpauseDialog()
        {
            _unpausingDialog = true;
        }

        public void ApplyPause()
        {
            if (CameraSequences.Current?.Flags.TestFlag(CamSeqFlags.BlockInput) == true)
            {
                return;
            }
            if (_pausingDialog)
            {
                DialogPause = true;
            }
            if (_unpausingDialog)
            {
                DialogPause = false;
            }
            _pausingDialog = false;
            _unpausingDialog = false;
        }

        /// <summary>
        /// Whether this mode groups players into competitive teams.
        ///
        /// Capture is the one that catches callers out: it is a team mode
        /// whose name does not end in "Teams", so anything that tested the
        /// name alone -- as the launcher did -- decided Capture was a
        /// free-for-all, handed every player and bot the same "no team", and
        /// then left Setup below to sort them by a TeamIndex nobody had set.
        /// One list, asked by everyone, is what keeps that from happening
        /// again.
        /// </summary>
        public bool IsTeamMode(GameMode mode)
        {
            return mode == GameMode.BattleTeams || mode == GameMode.SurvivalTeams
                || mode == GameMode.Capture || mode == GameMode.BountyTeams
                || mode == GameMode.NodesTeams || mode == GameMode.DefenderTeams;
        }

        public void Setup(Scene scene)
        {
            if (IsTeamMode(Mode))
            {
                Teams = true;
                for (int i = 0; i < PlayerEntity.SlotCapacity; i++)
                {
                    PlayerEntity player = _players.Items[i];
                    if (player.LoadFlags.TestFlag(LoadFlags.Active))
                    {
                        Mods.Multiplayer.TeamVisuals.Apply(player);
                    }
                }
            }
            // Adventure is the default state machine. Multiplayer modes
            // replace it below. The per-scene state migration briefly set
            // this to null, which left SinglePlayer with no handler and made
            // the first gameplay frame crash at ModeState(scene).
            ModeState = ModeStateAdventure;
            if (Mode == GameMode.Battle || Mode == GameMode.BattleTeams)
            {
                PointGoal = 7;
                MatchTime = 7 * 60;
                ModeState = ModeStateBattle;
            }
            else if (Mode == GameMode.Survival || Mode == GameMode.SurvivalTeams)
            {
                PointGoal = 2; // spare lives
                MatchTime = 15 * 60;
                ModeState = ModeStateSurvival;
            }
            else if (Mode == GameMode.Bounty || Mode == GameMode.BountyTeams)
            {
                PointGoal = 3;
                MatchTime = 15 * 60;
                ModeState = ModeStateBounty;
            }
            else if (Mode == GameMode.Capture)
            {
                PointGoal = 5;
                MatchTime = 15 * 60;
                ModeState = ModeStateCapture;
            }
            else if (Mode == GameMode.Defender || Mode == GameMode.DefenderTeams)
            {
                TimeGoal = 1.5f * 60;
                MatchTime = 15 * 60;
                ModeState = ModeStateDefender;
            }
            else if (Mode == GameMode.Nodes || Mode == GameMode.NodesTeams)
            {
                PointGoal = 70;
                MatchTime = 15 * 60;
                ModeState = ModeStateNodes;
            }
            else if (Mode == GameMode.PrimeHunter)
            {
                TimeGoal = 1.5f * 60;
                MatchTime = 15 * 60;
                ModeState = ModeStatePrimeHunter;
            }
            if (CameraSequences.Intro != null)
            {
                CameraSequences.Intro.Initialize();
                CameraSequences.Intro.SetUp(_players.Main.CameraInfo, transitionTime: 0);
                CameraSequences.Intro.Flags |= CamSeqFlags.Loop;
                scene.SetFade(FadeType.FadeInBlack, 20 / 30f, overwrite: true);
            }
            ForceEndGame = false;
            _tempoChanged = false;
            _stateChanged = false;
            if (Owner?.Services.IsReplica != true) Mods.KillCam.Reset();
            _lastAlarmTime = 0;
            _nextAlarmIndex = 0;
        }

        /// <summary>
        /// Put the per-match bookkeeping back to the start of a round.
        ///
        /// Setup() does this at the end of its work, but Setup only runs when
        /// a room is added, not when one is loaded over another -- which is
        /// what a server rotation does. Without it the second map of a
        /// session kept the first one's "the tempo has already changed" and
        /// "the results camera is already set up" flags, so the last minute
        /// of every subsequent match was silent.
        /// </summary>
        public void ResetMatchProgress()
        {
            MatchState = MatchState.InProgress;
            ForceEndGame = false;
            _tempoChanged = false;
            _stateChanged = false;
            _matchEndTime = 0;
            if (Owner?.Services.IsReplica != true) Mods.KillCam.Reset();
            _lastAlarmTime = 0;
            _nextAlarmIndex = 0;
        }

        public void UpdateTime(Scene scene)
        {
            // todo: update license info etc.
            if (MatchTime > 0)
            {
                MatchTime = MathF.Max(MatchTime - scene.FrameTime, 0);
            }
        }

        private bool _tempoChanged = false;
        private bool _stateChanged = false;
        private float _matchEndTime = 0;
        private float _lastAlarmTime = 0;
        private int _nextAlarmIndex = 0;
        private readonly IReadOnlyList<float> _alarmIntervals = new float[4]
        {
            1 / 30f, 8 / 30f, 15 / 30f, 6 / 30f
        };

        public void ProcessFrame(Scene scene)
        {
            // Not on a headless simulation. The match intro is a camera
            // flying round the room for the person about to play in it, and a
            // server has neither -- _players.Main there is slot 0, which
            // is an arbitrary remote player, so running it would fly a camera
            // nobody looks through and, worse, hand that one slot the
            // BlockFormSwitch and blocked input the sequence applies to its
            // main player. Every client still runs its own.
            if (Multiplayer && CameraSequences.Current?.IsIntro == true
                && !Mods.Headless.Active)
            {
                Debug.Assert(CameraSequences.Current.CamInfoRef == _players.Main.CameraInfo);
                CameraSequences.Current.Process();
            }
            if (MatchState == MatchState.InProgress)
            {
                if (SinglePlayer && !PausePrevented && !scene.MoviePlaying)
                {
                    if (MenuPause && _players.Main.Controls.Pause.IsPressed)
                    {
                        Sfx.Instance.PlayFreeSfx(SfxId.MENU_CANCEL);
                        UnpauseMenu();
                        _players.Main.EndMenuPauseHud();
                        _players.Main.Controls.Pause.IsPressed = false;
                        return;
                    }
                    if (!MenuPause && CameraSequences.Current?.BlockInput != true && _players.Main.Controls.Pause.IsPressed)
                    {
                        _players.Main.Controls.Pause.IsPressed = false;
                        PauseMenu();
                        _players.Main.SetUpMenuPauseHud();
                        Sfx.Instance.PlayFreeSfx(SfxId.MENU_CONFIRM);
                    }
                    if (MenuPause)
                    {
                        _players.Main.ProcessPauseMenu();
                        return;
                    }
                }
                // todo: update SFX
                for (int i = 0; i < scene.MessageQueue.Count; i++)
                {
                    MessageInfo message = scene.MessageQueue[i];
                    if (message.Message == Message.Complete && message.ExecuteFrame == scene.FrameCount)
                    {
                        MatchTime = 0;
                    }
                }
                // todo: update MP playtime to license info
                if (Multiplayer && !Features.AllowInvalidTeams)
                {
                    bool invalid = _players.MaxPlayers < 2;
                    if (!invalid && Teams)
                    {
                        Span<bool> teams = stackalloc bool[4];
                        for (int i = 0; i < PlayerEntity.SlotCapacity; i++)
                        {
                            PlayerEntity player = _players.Items[i];
                            if (player.LoadFlags.TestFlag(LoadFlags.Active))
                            {
                                if ((uint)player.TeamIndex < (uint)TeamCount)
                                {
                                    teams[player.TeamIndex] = true;
                                }
                            }
                        }
                        int representedTeams = 0;
                        for (int team = 0; team < TeamCount; team++)
                        {
                            if (teams[team]) representedTeams++;
                        }
                        invalid = representedTeams < 2;
                    }
                    if (invalid && !MenuPause)
                    {
                        MatchTime = 0;
                        CameraSequences.Current?.End();
                        // todo: stop music/SFX, state bits/disconnect message?
                    }
                }
                ModeState(scene);
                if (SinglePlayer && EscapeTimer != -1)
                {
                    // bugfix?: this fade check seems to count things like the Omega Cannon flash
                    if (!EscapePaused && !MenuPause && !DialogPause && scene.FadeType == FadeType.None
                        && CameraSequences.Current?.Flags.TestFlag(CamSeqFlags.BlockInput) != true)
                    {
                        EscapeTimer -= scene.FrameTime;
                        if (EscapeState == EscapeState.Escape)
                        {
                            Music.UpdateEscapeMusic();
                        }
                        else
                        {
                            UpdateEventSounds(EscapeTimer);
                        }
                    }
                    if (EscapeTimer <= 0)
                    {
                        if (EscapeState == EscapeState.Escape && _players.Main.Health > 0)
                        {
                            scene.SendMessage(Message.Death, null!, _players.Main, 0, 0);
                        }
                        EscapeTimer = -1;
                    }
                }
                if (MatchTime != 0 && !ForceEndGame)
                {
                    if (Multiplayer && MatchTime > 0)
                    {
                        var time = TimeSpan.FromSeconds(MatchTime);
                        if (time.TotalMinutes < 1 && time.Seconds <= 59 && !_tempoChanged)
                        {
                            Music.UpdateTempo(307, 900 / 30f);
                            _tempoChanged = true;
                        }
                        if (time.TotalMinutes < 1 && time.Seconds <= 9)
                        {
                            float comparison = 1;
                            if (time.Seconds <= 5)
                            {
                                if (Features.HalfSecondAlarm)
                                {
                                    comparison = 0.5f;
                                }
                                else
                                {
                                    comparison = _alarmIntervals[_nextAlarmIndex];
                                }
                            }
                            if (_lastAlarmTime == 0 || scene.ElapsedTime - _lastAlarmTime >= comparison)
                            {
                                Sfx.Instance.PlaySample((int)SfxId.ALARM, source: null, loop: false,
                                    noUpdate: false, recency: -1, sourceOnly: false, cancellable: false);
                                _lastAlarmTime = scene.ElapsedTime;
                                _nextAlarmIndex++;
                                if (_nextAlarmIndex >= _alarmIntervals.Count)
                                {
                                    _nextAlarmIndex = 0;
                                }
                            }
                        }
                    }
                }
                else
                {
                    _players.Main.HudEndDisrupted();
                    if ((Mode == GameMode.Survival || Mode == GameMode.SurvivalTeams) && !ForceEndGame)
                    {
                        for (int i = 0; i < PlayerEntity.SlotCapacity; i++)
                        {
                            PlayerEntity player = _players.Items[i];
                            // the game also checks if the player's time is greater than or equal to the time goal,
                            // which in survival is always zero, so the check isn't needed
                            if (player.LoadFlags.TestFlag(LoadFlags.Active)
                                && (player.Health > 0 || TeamDeaths[player.TeamIndex] <= PointGoal))
                            {
                                Time[i] = -1;
                                TeamTime[player.TeamIndex] = -1;
                            }
                        }
                        UpdateState();
                    }
                    // todo: 1P time up? isn't that handled by death countdown etc.?
                    MatchState = MatchState.GameOver;
                    MatchTime = MatchFinalCameraSeconds;
                    scene.SetFade(FadeType.None, length: 0, overwrite: true);
                    _stateChanged = true;
                    _matchEndTime = scene.GlobalElapsedTime;
                    uint finalFrame = Mods.Network.NetSession.IsClient
                        && Mods.Network.NetSession.AppliedSnapshotFrame != 0
                            ? Mods.Network.NetSession.AppliedSnapshotFrame
                            : Mods.Network.NetSession.NetFrame;
                    if (!scene.Services.IsReplica) Mods.KillCam.BeginFinal(finalFrame);
                    Sfx.Instance.StopFreeSfxScripts();
                    Sfx.Instance.StopAllSound();
                    _players.Main.StopLongSfx();
                    // sfxtodo: stop more kinds of SFX? fade for 1P mode?
                    if (!this.SinglePlayer)
                    {
                        Music.PlaySeq(SeqId.TIMEOUT);
                    }
                }
            }
            else if (MatchState == MatchState.GameOver)
            {
                if (!scene.Services.IsReplica && Mods.KillCam.IsFinal)
                {
                    // The native winner scene has finished. The isolated final
                    // replay now owns presentation before results.
                    _stateChanged = false;
                }
                else
                {
                    PlayerEntity winner = _players.Items[ResultSlots[0]];
                    if (!IsResultTie && winner.Health > 0
                        && winner.LoadFlags.TestFlag(LoadFlags.Active)
                        && winner.LoadFlags.TestFlag(LoadFlags.Spawned))
                    {
                        if (_stateChanged)
                        {
                            _stateChanged = false;
                            winner.SetUpMatchEndCamera();
                        }
                        _players.Main.UpdateMatchEndCamera(
                            winner, scene.GlobalElapsedTime - _matchEndTime);
                    }
                    else
                    {
                        EnsureIntroCamSeq();
                    }
                }
                if (MatchTime == 0 && (scene.Services.IsReplica || !Mods.KillCam.FinalPresentationPending))
                {
                    if (!scene.Services.IsReplica) Mods.KillCam.EndFinal();
                    MatchState = MatchState.Ending;
                    // Ten seconds of results, where the DS gave five.
                    //
                    // Five was long enough to read a scoreboard and nothing
                    // else, and the screen now has something to do on it:
                    // the next hunter, the next suit and the next map all
                    // live here (see Mods.EndScreen), and a choice somebody
                    // has to make in five seconds is a choice they make by
                    // accident. DedicatedServer.EndSequenceSeconds is the
                    // other half of this number -- the server holds its
                    // intermission open for the whole sequence, and the two
                    // have to be changed together or the map changes out
                    // from under the screen.
                    MatchTime = MatchEndingSeconds;
                    // todo: update license info, stop SFX
                }
            }
            else if (MatchState == MatchState.Ending)
            {
                EnsureIntroCamSeq();
                // todo: more stuff?
                if (MatchTime == 0)
                {
                    MatchTime = -1;
                    if (Mods.Network.NetMatchEnd.ShouldLeaveAfterMatch && !PlayPickedMap())
                    {
                        scene.SetFade(FadeType.FadeOutBlack, 20 / 30f, overwrite: true, AfterFade.Exit);
                    }
                    // Connected, the match ending is not the session ending.
                    // The server is running its intermission and is about to
                    // say which map is next; NetRoomChange fades to black and
                    // loads it, which is the fade the player sees. Quitting
                    // here sent everybody back to their own launcher instead,
                    // which is how a match that somebody won broke up the
                    // group that was playing it.
                }
            }
        }

        // Both graphical heads queue the next local match without returning
        // control to the launcher. With no selection, repeat the current arena.
        private bool PlayPickedMap() => Mods.Launcher.OfflineRematch.Continue();

        private void EnsureIntroCamSeq()
        {
            if (Multiplayer && CameraSequences.Current == null && CameraSequences.Intro != null)
            {
                CameraSequences.Intro.SetUp(_players.Main.CameraInfo, transitionTime: 0);
                _players.Main.CameraInfo.Update();
                CameraSequences.Intro.Flags |= CamSeqFlags.Loop;
            }
        }

        public AreaState GetAreaState(int areaId, StorySave? save = null)
        {
            if (save == null)
            {
                save = StorySave;
            }
            if (save == null)
            {
                return AreaState.None;
            }
            return (AreaState)(((int)save.BossFlags >> (2 * areaId)) & 3);
        }

        public bool QueuedOublietteUnlockMessage { get; set; }

        public void ModeStateAdventure(Scene scene)
        {
            _players.Main.SaveStatus();
            if ((StorySave.Areas & 0x100) == 0)
            {
                if (QueuedOublietteUnlockMessage && scene.FadeType == FadeType.FadeInBlack && !scene.MoviePlaying)
                {
                    StorySave.Areas |= 0x100;
                    StorySave.CurrentOctoliths = 0;
                    // GUNSHIP TRANSMISSION severe timefield disruption detected in the vicinity of the ALIMBIC CLUSTER.
                    _players.Main.ShowDialog(DialogType.Okay, messageId: 43);
                    QueuedOublietteUnlockMessage = false;
                }
                for (int i = 0; i < scene.MessageQueue.Count; i++)
                {
                    MessageInfo message = scene.MessageQueue[i];
                    if (message.Message == Message.UnlockOubliette && message.ExecuteFrame == scene.FrameCount)
                    {
                        if (StorySave.CurrentOctoliths == 0xFF)
                        {
                            this.PausePrevented = true;
                            scene.StartMovie(Movie.OublietteUnlock, FadeType.FadeOutInWhite, 20 / 30f, FadeType.FadeOutInBlack, 5 / 30f);
                            this.QueuedOublietteUnlockMessage = true;
                        }
                        else
                        {
                            foreach (CamSeqEntity entity in scene.GetCamSeqEntities())
                            {
                                scene.SendMessage(Message.Activate, null!, entity, param1: 0, param2: 0);
                                break;
                            }
                        }
                        break;
                    }
                }
            }
            if (_players.Main.Health > 0)
            {
                for (int i = 0; i < scene.MessageQueue.Count; i++)
                {
                    MessageInfo message = scene.MessageQueue[i];
                    if (message.Message == Message.Checkpoint && message.ExecuteFrame == scene.FrameCount)
                    {
                        Debug.Assert(scene.Room != null);
                        scene.SendMessage(Message.SetActive, null!, message.Sender, param1: 0, param2: 0);
                        StorySave.CheckpointEntityId = message.Sender.Id;
                        StorySave.CheckpointRoomId = scene.Room.RoomId;
                        UpdateCleanSave(force: false);
                        break;
                    }
                }
                for (int i = 0; i < scene.MessageQueue.Count; i++)
                {
                    MessageInfo message = scene.MessageQueue[i];
                    if (message.Message == Message.LoadOubliette && message.ExecuteFrame == scene.FrameCount)
                    {
                        TransitionRoomId = 91; // Gorea_b1
                        scene.StartMovie(Movie.Gorea1Intro, FadeType.FadeOutInWhite, 10 / 30f, FadeType.FadeOutInWhite, 10 / 30f);
                        break;
                    }
                }
            }
            // todo: game timer/boss record stuff
        }

        private void EndIfPointGoalReached()
        {
            // Connected, the machine that keeps the score is the only one
            // allowed to decide the score has been reached; everybody else
            // learns it from the server. See NetMatchEnd.MayEndOnScore.
            if (PointGoal <= 0 || !Mods.Network.NetMatchEnd.MayEndOnScore)
            {
                return;
            }
            for (int i = 0; i < PlayerEntity.SlotCapacity; i++)
            {
                PlayerEntity player = _players.Items[i];
                if (player.LoadFlags.TestFlag(LoadFlags.Active) && TeamPoints[player.TeamIndex] >= PointGoal)
                {
                    // deal with multiple nodes points on the same frame
                    TeamPoints[player.TeamIndex] = PointGoal;
                    MatchTime = 0;
                    break;
                }
            }
        }

        public void ModeStateBattle(Scene scene)
        {
            EndIfPointGoalReached();
        }

        public void ModeStateSurvival(Scene scene)
        {
            UpdateSurvival(scene.FrameTime);
        }

        internal void UpdateSurvival(float frameTime)
        {
            RadarPlayers = false;
            int playersAlive = 0;
            int botsAlive = 0;
            Span<bool> teamsAlive = stackalloc bool[4];
            int aliveTeamCount = 0;
            for (int i = 0; i < PlayerEntity.SlotCapacity; i++)
            {
                PlayerEntity player = _players.Items[i];
                if (player.LoadFlags.TestFlag(LoadFlags.Active)
                    && (player.Health > 0 || TeamDeaths[player.TeamIndex] <= PointGoal))
                {
                    Time[i] += frameTime;
                    if (player.IsBot)
                    {
                        botsAlive++;
                    }
                    else
                    {
                        playersAlive++;
                    }
                    if (Teams)
                    {
                        if ((uint)player.TeamIndex < (uint)TeamCount && !teamsAlive[player.TeamIndex])
                        {
                            teamsAlive[player.TeamIndex] = true;
                            aliveTeamCount++;
                        }
                    }
                }
            }
            if (Mods.Network.NetMatchEnd.MayEndOnScore
                && (playersAlive + botsAlive < 2 || Teams && aliveTeamCount < 2))
            {
                MatchTime = 0;
                for (int i = 0; i < PlayerEntity.SlotCapacity; i++)
                {
                    PlayerEntity player = _players.Items[i];
                    if (player.LoadFlags.TestFlag(LoadFlags.Active)
                        && (player.Health > 0 || TeamDeaths[player.TeamIndex] <= PointGoal))
                    {
                        Time[i] = -1; // MAX
                    }
                }
            }
            else if (playersAlive + botsAlive == 2 && _players.PlayerCount > 2)
            {
                RadarPlayers = true;
            }
        }

        public void ModeStateCapture(Scene scene)
        {
            EndIfPointGoalReached();
        }

        public void ModeStateBounty(Scene scene)
        {
            EndIfPointGoalReached();
        }

        public void ModeStateDefender(Scene scene)
        {
            if (!Mods.Network.NetMatchEnd.MayEndOnScore)
            {
                return;
            }
            for (int i = 0; i < PlayerEntity.SlotCapacity; i++)
            {
                PlayerEntity player = _players.Items[i];
                if (player.LoadFlags.TestFlag(LoadFlags.Active) && TeamTime[player.TeamIndex] >= TimeGoal)
                {
                    MatchTime = 0;
                    break;
                }
            }
        }

        public void ModeStateNodes(Scene scene)
        {
            EndIfPointGoalReached();
        }

        public void ModeStatePrimeHunter(Scene scene)
        {
            if (PrimeHunter == -1)
            {
                return;
            }
            PlayerEntity player = _players.Items[PrimeHunter];
            if (!player.LoadFlags.TestFlag(LoadFlags.Active))
            {
                PrimeHunter = -1;
                return;
            }
            if (scene.FrameCount % (10 * 2) == 0) // todo: FPS stuff
            {
                player.TakeDamage(1, DamageFlags.NoDmgInvuln, direction: null, source: null);
            }
            if (PrimeHunter != -1)
            {
                Time[PrimeHunter] += scene.FrameTime;
                if (Time[PrimeHunter] >= TimeGoal)
                {
                    MatchTime = 0;
                }
            }
        }

        private bool _whiteoutStarted = false;
        private bool _gameOverShown = false;
        public int QueuedOctolithMessageId { get; set; } = -1;

        public void UpdateFrame(Scene scene)
        {
            PromptType prompt = _players.Main.DialogPromptType;
            ConfirmState confirm = _players.Main.DialogConfirmState;

            void Quit()
            {
                scene.SetFade(FadeType.FadeOutBlack, length: 10 / 30f, overwrite: true, AfterFade.Exit);
                Music.Stop(fadeTime: 10 / 30f);
                Sfx.Instance.PlaySample((int)SfxId.QUIT_GAME, source: null, loop: false,
                    noUpdate: false, recency: -1, sourceOnly: false, cancellable: false);
            }

            if (prompt != PromptType.Any && confirm != ConfirmState.Okay)
            {
                Sfx.Instance.StopFreeSfxScripts();
                if (confirm == ConfirmState.Yes)
                {
                    if (prompt == PromptType.ShipHatch)
                    {
                        // yes to ship hatch (enter)
                        EnterShip(); // the game does this in the cockpit
                        Debug.Assert(scene.Room != null);
                        Movie movieId = scene.Room.RoomId switch
                        {
                            27 => Movie.AlinosTakeoff,
                            45 => Movie.CATakeoff,
                            65 => Movie.VDOTakeoff,
                            77 => Movie.ArcterraTakeoff,
                            _ => Movie.None
                        };
                        if (movieId != Movie.None && !Cheats.SkipPlanetIntros)
                        {
                            scene.StartMovie(movieId, FadeType.FadeOutInWhite, 20 / 30f,
                                FadeType.FadeOutBlack, 5 / 30f, afterMovieAction: AfterMovie.EndGame);
                        }
                        else
                        {
                            scene.SetFade(FadeType.FadeOutWhite, length: 20 / 30f, overwrite: true, AfterFade.EnterShip);
                        }
                        Music.Stop(fadeTime: 20 / 30f);
                        // todo: fade SFX
                        this.PausePrevented = true;
                        Sfx.Instance.PlaySample((int)SfxId.RETURN_TO_SHIP_YES, source: null, loop: false,
                            noUpdate: false, recency: -1, sourceOnly: false, cancellable: false);
                    }
                    else if (prompt == PromptType.GameOver)
                    {
                        // yes to game over (continue)
                        RestoreCleanSave();
                        Debug.Assert(scene.Room != null);
                        if (Cheats.ContinueFromCurrentRoom)
                        {
                            if (StorySave.CheckpointRoomId != scene.Room.RoomId)
                            {
                                StorySave.CheckpointEntityId = -1;
                            }
                            StorySave.CheckpointRoomId = scene.Room.RoomId;
                        }
                        else if (StorySave.CheckpointRoomId == -1)
                        {
                            StorySave.CheckpointEntityId = -1;
                            if (scene.AreaId == 0 || scene.AreaId == 1) // Alinos 1/2
                            {
                                StorySave.CheckpointRoomId = 27; // UNIT1_LAND
                            }
                            else if (scene.AreaId == 2 || scene.AreaId == 3) // CA 1/2
                            {
                                StorySave.CheckpointRoomId = 45; // UNIT2_LAND
                            }
                            else if (scene.AreaId == 4 || scene.AreaId == 5) // VDO 1/2
                            {
                                StorySave.CheckpointRoomId = 65; // UNIT3_LAND
                            }
                            else if (scene.AreaId == 6 || scene.AreaId == 7) // Arcterra 1/2
                            {
                                StorySave.CheckpointRoomId = 77; // UNIT4_LAND
                            }
                            else if (scene.AreaId == 8) // Oubliette
                            {
                                StorySave.CheckpointRoomId = 89; // Gorea_Land
                            }
                        }
                        if (StorySave.CheckpointRoomId == -1)
                        {
                            Quit();
                        }
                        // if CheckpointEntityId isn't set, we'll still spawn, just using the respawn point code path
                        TransitionRoomId = StorySave.CheckpointRoomId;
                        Sfx.Instance.PlaySample((int)SfxId.MENU_CONFIRM, source: null, loop: false,
                            noUpdate: false, recency: -1, sourceOnly: false, cancellable: false);
                        this.PausePrevented = true;
                        scene.SetFade(FadeType.FadeOutWhite, length: 10 / 30f, overwrite: true, AfterFade.LoadRoom);
                        UnpauseDialog();
                        _players.Main.RestartLongSfx(force: true);
                    }
                }
                else if (prompt == PromptType.GameOver)
                {
                    // no to game over (quit)
                    Quit();
                }
                else
                {
                    // no to ship hatch (resume)
                    _players.Main.RestartLongSfx();
                    Sfx.Instance.PlaySample((int)SfxId.RETURN_TO_SHIP_NO, source: null, loop: false,
                        noUpdate: false, recency: -1, sourceOnly: false, cancellable: false);
                    UnpauseDialog();
                }
                _players.Main.DialogPromptType = PromptType.Any;
                _players.Main.DialogConfirmState = ConfirmState.Okay;
            }
            if (!DialogPause)
            {
                if (_players.Main.Health > 0)
                {
                    for (int i = 0; i < scene.MessageQueue.Count; i++)
                    {
                        MessageInfo message = scene.MessageQueue[i];
                        if (message.Message == Message.ShipHatch && message.ExecuteFrame == scene.FrameCount)
                        {
                            Debug.Assert(scene.Room != null);
                            ResetEscapeState(updateSounds: false); // skdebug
                            _players.Main.DialogPromptType = PromptType.ShipHatch;
                            StorySave.CheckpointEntityId = message.Sender.Id;
                            StorySave.CheckpointRoomId = scene.Room.RoomId;
                            UpdateCleanSave(force: true);
                            // HUNTER GUNSHIP enter your ship?
                            _players.Main.ShowDialog(DialogType.YesNo, messageId: 1);
                            Sfx.Instance.StopFreeSfxScripts();
                            Sfx.Instance.PlayScript((int)SfxId.RETURN_TO_SHIP_SCR, source: null,
                                noUpdate: false, recency: -1, sourceOnly: false, cancellable: false);
                            break;
                        }
                    }
                    for (int i = 0; i < scene.MessageQueue.Count; i++)
                    {
                        MessageInfo message = scene.MessageQueue[i];
                        if (message.Message == Message.EscapeUpdate1 && message.ExecuteFrame == scene.FrameCount)
                        {
                            UpdateEscapeState((int)message.Param1 * 30, (int)message.Param2);
                        }
                    }
                    for (int i = 0; i < scene.MessageQueue.Count; i++)
                    {
                        MessageInfo message = scene.MessageQueue[i];
                        if (message.Message == Message.EscapeUpdate2 && message.ExecuteFrame == scene.FrameCount)
                        {
                            UpdateEscapeState((int)message.Param1, (int)message.Param2);
                        }
                    }
                    for (int i = 0; i < scene.MessageQueue.Count; i++)
                    {
                        MessageInfo message = scene.MessageQueue[i];
                        if (message.Message == Message.ShowPrompt && message.ExecuteFrame == scene.FrameCount)
                        {
                            int promptType = (int)message.Param2;
                            if (promptType == 0)
                            {
                                _players.Main.ShowDialog(DialogType.Okay, messageId: (int)message.Param1);
                            }
                            else if (promptType == 1)
                            {
                                _players.Main.ShowDialog(DialogType.YesNo, messageId: (int)message.Param1);
                            }
                        }
                    }
                }
                for (int i = 0; i < scene.MessageQueue.Count; i++)
                {
                    MessageInfo message = scene.MessageQueue[i];
                    if (message.Message == Message.ShowWarning && message.ExecuteFrame == scene.FrameCount)
                    {
                        int messageId = (int)message.Param1;
                        int duration = (int)message.Param2;
                        if (duration == 0)
                        {
                            duration = 15;
                        }
                        _players.Main.ShowDialog(DialogType.Overlay, messageId, param1: duration, param2: 1);
                    }
                }
                for (int i = 0; i < scene.MessageQueue.Count; i++)
                {
                    MessageInfo message = scene.MessageQueue[i];
                    if (message.Message == Message.ShowOverlay && message.ExecuteFrame == scene.FrameCount)
                    {
                        int messageId = (int)message.Param1;
                        int duration = (int)message.Param2;
                        _players.Main.ShowDialog(DialogType.Overlay, messageId, param1: duration, param2: 0);
                    }
                }
            }
            // start displaying dialog during the fade back in after the movie
            if (QueuedOctolithMessageId != -1 && scene.FadeType == FadeType.FadeInWhite && !scene.MoviePlaying)
            {
                // OCTOLITH ACQUIRED you obtained an OCTOLITH!
                _players.Main.ShowDialog(DialogType.Event, messageId: 7, param1: (int)EventType.Octolith);
                scene.SendMessage(Message.ShowPrompt, _players.Main, null, param1: QueuedOctolithMessageId, param2: 0, delay: 1);
                QueuedOctolithMessageId = -1;
            }
            float countdown = _players.Main.DeathCountdown;
            if (SinglePlayer && _players.Main.Health == 0 && countdown > 0)
            {
                if (countdown >= 145 / 30f)
                {
                    _whiteoutStarted = false;
                    _gameOverShown = false;
                    if (EscapeState == EscapeState.Escape)
                    {
                        // EMERGENCY security system activated.
                        _players.Main.ShowDialog(DialogType.Hud, messageId: 120, param1: 69, param2: 1);
                    }
                    else
                    {
                        // EMERGENCY POWER SUIT energy is depleted.
                        _players.Main.ShowDialog(DialogType.Hud, messageId: 116, param1: 45, param2: 1);
                    }
                }
                else if (countdown <= 1 / 30f && !_gameOverShown)
                {
                    _players.Main.CameraInfo.SetShake(0);
                    //ENERGY DEPLETED continue from last checkpoint?
                    _players.Main.DialogPromptType = PromptType.GameOver;
                    _players.Main.ShowDialog(DialogType.YesNo, messageId: 2);
                    ResetEscapeState(updateSounds: true); // the game does when reloading the room
                    _gameOverShown = true;
                }
                else if (countdown <= 50 / 30f && !_whiteoutStarted)
                {
                    _players.Main.BeginWhiteout();
                    _whiteoutStarted = true;
                }
            }
        }

        public void UpdateBossFlags(int areaId)
        {
            uint flags = (uint)StorySave.BossFlags;
            flags &= (uint)~(3 << (2 * areaId));
            flags |= (uint)(1 << (2 * areaId));
            StorySave.BossFlags = (BossFlags)flags;
        }

        private void EnterShip()
        {
            // update flags for the end of the escape sequence
            StorySave.TriggerState[2] &= 0x7F;
            if (StorySave.BossFlags.TestAny(BossFlags.Unit1B1Kill))
            {
                StorySave.BossFlags &= ~BossFlags.Unit1B1Kill;
                StorySave.BossFlags |= BossFlags.Unit1B1Done;
            }
            if (StorySave.BossFlags.TestAny(BossFlags.Unit1B2Kill))
            {
                StorySave.BossFlags &= ~BossFlags.Unit1B2Kill;
                StorySave.BossFlags |= BossFlags.Unit1B2Done;
            }
            if (StorySave.BossFlags.TestAny(BossFlags.Unit2B1Kill))
            {
                StorySave.BossFlags &= ~BossFlags.Unit2B1Kill;
                StorySave.BossFlags |= BossFlags.Unit2B1Done;
            }
            if (StorySave.BossFlags.TestAny(BossFlags.Unit2B2Kill))
            {
                StorySave.BossFlags &= ~BossFlags.Unit2B2Kill;
                StorySave.BossFlags |= BossFlags.Unit2B2Done;
            }
            if (StorySave.BossFlags.TestAny(BossFlags.Unit3B1Kill))
            {
                StorySave.BossFlags &= ~BossFlags.Unit3B1Kill;
                StorySave.BossFlags |= BossFlags.Unit3B1Done;
            }
            if (StorySave.BossFlags.TestAny(BossFlags.Unit3B2Kill))
            {
                StorySave.BossFlags &= ~BossFlags.Unit3B2Kill;
                StorySave.BossFlags |= BossFlags.Unit3B2Done;
            }
            if (StorySave.BossFlags.TestAny(BossFlags.Unit4B1Kill))
            {
                StorySave.BossFlags &= ~BossFlags.Unit4B1Kill;
                StorySave.BossFlags |= BossFlags.Unit4B1Done;
            }
            if (StorySave.BossFlags.TestAny(BossFlags.Unit4B2Kill))
            {
                StorySave.BossFlags &= ~BossFlags.Unit4B2Kill;
                StorySave.BossFlags |= BossFlags.Unit4B2Done;
            }
        }

        public void ResetEscapeState(bool updateSounds)
        {
            EscapeState = EscapeState.None;
            EscapeTimer = -1;
            EscapePaused = false;
            if (updateSounds)
            {
                UpdateEventSounds(timer: -1);
            }
        }

        private void UpdateEscapeState(int frames, int stateId)
        {
            var state = (EscapeState)stateId;
            float time = frames / 30f;
            if (state == EscapeState.None)
            {
                EscapeTimer = -1;
                UpdateEventSounds(timer: -1);
            }
            else if (time == 0)
            {
                EscapePaused = !EscapePaused;
            }
            else if (EscapeState != state || EscapeTimer == -1)
            {
                EscapeTimer = time;
                EscapePaused = false;
                if (state == EscapeState.Escape)
                {
                    Sfx.QueueStream(VoiceId.VOICE_EVACUATE);
                    Sfx.QueueStream(VoiceId.VOICE_EVACUATE, delay: 3);
                    Sfx.QueueStream(VoiceId.VOICE_EVACUATE, delay: 6);
                    Music.PlayMusic(MusicId.SEQ_OREGANO_M55);
                    Music.UpdateTempo(245, 0); // 95.7%
                    StorySave.TriggerState[2] |= 0x80;
                }
                else
                {
                    UpdateEventSounds(timer: -1);
                }
            }
            EscapeState = state;
        }

        private bool _playedTimedEventSfx = false;

        private void UpdateEventSounds(float timer)
        {
            Music.UpdateEventMusic(timer);
            if (timer > 165 / 30f)
            {
                _playedTimedEventSfx = false;
            }
            else if (timer >= 0 && !_playedTimedEventSfx)
            {
                _players.Main.PlayTimedSfx(SfxId.PUZZLE_TIMER1_SCR);
                _playedTimedEventSfx = true;
            }
            else if (timer < 0)
            {
                _players.Main.StopTimedSfx(SfxId.PUZZLE_TIMER1_SCR);
                _playedTimedEventSfx = false;
            }
        }

        public void UpdateState()
        {
            if (_players.PlayerCount == 0)
            {
                return;
            }
            IReadOnlyList<PlayerEntity> players = _players.Items;
            int[] prevTeamPoints = new int[PlayerEntity.SlotCapacity];
            int[] prevTeamDeaths = new int[PlayerEntity.SlotCapacity];
            for (int i = 0; i < PlayerEntity.SlotCapacity; i++)
            {
                prevTeamPoints[i] = TeamPoints[i];
                prevTeamDeaths[i] = TeamDeaths[i];
                TeamPoints[i] = 0;
                TeamDeaths[i] = 0;
                TeamKills[i] = 0;
                if (Mode == GameMode.Survival || Mode == GameMode.SurvivalTeams)
                {
                    TeamTime[i] = 0;
                }
            }
            for (int i = 0; i < PlayerEntity.SlotCapacity; i++)
            {
                PlayerEntity player = players[i];
                if (!player.LoadFlags.TestFlag(LoadFlags.Initial) || (uint)player.TeamIndex >= (uint)(Teams ? TeamCount : PlayerEntity.SlotCapacity))
                {
                    continue;
                }
                TeamPoints[player.TeamIndex] += Points[i];
                TeamDeaths[player.TeamIndex] += Deaths[i];
                TeamKills[player.TeamIndex] += Kills[i];
                if (Mode == GameMode.Survival || Mode == GameMode.SurvivalTeams)
                {
                    if (Time[i] == -1 || TeamTime[player.TeamIndex] != -1 && TeamTime[player.TeamIndex] < Time[i])
                    {
                        TeamTime[player.TeamIndex] = Time[i];
                    }
                }
                else if (Mode == GameMode.Defender || Mode == GameMode.DefenderTeams)
                {
                    Time[i] = TeamTime[player.TeamIndex];
                }
            }
            if (Mode == GameMode.Battle || Mode == GameMode.BattleTeams || Mode == GameMode.Capture || Mode == GameMode.Bounty
                || Mode == GameMode.BountyTeams || Mode == GameMode.Nodes || Mode == GameMode.NodesTeams)
            {
                int teamPoints = TeamPoints[_players.Main.TeamIndex];
                if (teamPoints != prevTeamPoints[_players.Main.TeamIndex] && teamPoints == PointGoal - 1)
                {
                    Sfx.QueueStream(VoiceId.VOICE_ONE_KILL_TO_WIN, delay: 1);
                }
            }
            else if (Mode == GameMode.Survival || Mode == GameMode.SurvivalTeams)
            {
                int opponents = 0;
                int lastTeam = -1;
                int opponentMask = 0;
                for (int i = 0; i < PlayerEntity.SlotCapacity; i++)
                {
                    PlayerEntity player = players[i];
                    if (!player.LoadFlags.TestAny(LoadFlags.Active))
                    {
                        continue;
                    }
                    if (player.Health > 0 || TeamDeaths[player.TeamIndex] <= PointGoal)
                    {
                        if (player.TeamIndex != _players.Main.TeamIndex
                            && (opponentMask & (1 << player.TeamIndex)) == 0)
                        {
                            opponentMask |= 1 << player.TeamIndex;
                            opponents++;
                            lastTeam = player.TeamIndex;
                        }
                    }
                    if (TeamDeaths[player.TeamIndex] > PointGoal && player.RespawnTimer == PlayerEntity.RespawnTime)
                    {
                        Sfx.QueueStream(VoiceId.VOICE_ELIMINATED);
                    }
                }
                if (_players.Main.LoadFlags.TestAny(LoadFlags.Active) && opponents == 1 && lastTeam != -1)
                {
                    int teamDeaths = TeamDeaths[lastTeam];
                    if (teamDeaths != prevTeamDeaths[lastTeam] && teamDeaths == PointGoal)
                    {
                        Sfx.QueueStream(VoiceId.VOICE_ONE_KILL_TO_WIN, delay: 1);
                    }
                }
            }
            UpdateStandings();
            // todo: update license info
        }

        private int ComparePlayers(int slot1, int slot2)
        {
            int points1 = Points[slot1];
            int points2 = Points[slot2];
            float time1 = Time[slot1];
            float time2 = Time[slot2];
            if (Mode == GameMode.Survival || Mode == GameMode.SurvivalTeams)
            {
                if (time1 == -1)
                {
                    time1 = Single.MaxValue;
                }
                if (time2 == -1)
                {
                    time2 = Single.MaxValue;
                }
            }
            int deaths1 = Deaths[slot1];
            int deaths2 = Deaths[slot2];
            int kills1 = Kills[slot1];
            int kills2 = Kills[slot2];
            if (Mode == GameMode.Battle || Mode == GameMode.BattleTeams)
            {
                if (points1 == points2 && deaths1 == deaths2)
                {
                    return 0;
                }
                if (points1 < points2 || points1 == points2 && deaths1 > deaths2)
                {
                    return -1;
                }
                return 1;
            }
            if (Mode == GameMode.Survival || Mode == GameMode.SurvivalTeams)
            {
                if (time1 == time2 && deaths1 == deaths2)
                {
                    return 0;
                }
                if (time1 < time2 || time1 == time2 && deaths1 > deaths2)
                {
                    return -1;
                }
                return 1;
            }
            if (Mode == GameMode.Defender || Mode == GameMode.DefenderTeams)
            {
                if (time1 == time2 && kills1 == kills2)
                {
                    return 0;
                }
                if (time1 < time2 || time1 == time2 && kills1 < kills2)
                {
                    return -1;
                }
                return 1;
            }
            if (Mode == GameMode.Capture || Mode == GameMode.Nodes || Mode == GameMode.NodesTeams
                || Mode == GameMode.Bounty || Mode == GameMode.BountyTeams)
            {
                if (points1 == points2 && kills1 == kills2)
                {
                    return 0;
                }
                if (points1 < points2 || points1 == points2 && kills1 < kills2)
                {
                    return -1;
                }
                return 1;
            }
            if (Mode == GameMode.PrimeHunter)
            {
                if (time1 == time2 && kills1 == kills2)
                {
                    return 0;
                }
                if (time1 < time2 || time1 == time2 && kills1 < kills2)
                {
                    return -1;
                }
                return 1;
            }
            return 0;
        }

        private int CompareTeams(int slot1, int slot2)
        {
            int points1 = TeamPoints[slot1];
            int points2 = TeamPoints[slot2];
            float time1 = TeamTime[slot1];
            float time2 = TeamTime[slot2];
            if (Mode == GameMode.Survival || Mode == GameMode.SurvivalTeams)
            {
                if (time1 == -1)
                {
                    time1 = Single.MaxValue;
                }
                if (time2 == -1)
                {
                    time2 = Single.MaxValue;
                }
            }
            int deaths1 = TeamDeaths[slot1];
            int deaths2 = TeamDeaths[slot2];
            int kills1 = TeamKills[slot1];
            int kills2 = TeamKills[slot2];
            if (Mode == GameMode.BattleTeams)
            {
                if (points1 == points2 && deaths1 == deaths2)
                {
                    return 0;
                }
                if (points1 < points2 || points1 == points2 && deaths1 > deaths2)
                {
                    return -1;
                }
                return 1;
            }
            if (Mode == GameMode.SurvivalTeams)
            {
                if (time1 == time2 && deaths1 == deaths2)
                {
                    return 0;
                }
                if (time1 < time2 || time1 == time2 && deaths1 > deaths2)
                {
                    return -1;
                }
                return 1;
            }
            if (Mode == GameMode.DefenderTeams)
            {
                if (time1 == time2 && kills1 == kills2)
                {
                    return 0;
                }
                if (time1 < time2 || time1 == time2 && kills1 < kills2)
                {
                    return -1;
                }
                return 1;
            }
            if (Mode == GameMode.Capture || Mode == GameMode.NodesTeams || Mode == GameMode.BountyTeams)
            {
                if (points1 == points2 && kills1 == kills2)
                {
                    return 0;
                }
                if (points1 < points2 || points1 == points2 && kills1 < kills2)
                {
                    return -1;
                }
                return 1;
            }
            return 0;
        }

        public void CompleteRandomEncounter(int roomId)
        {
            if (roomId >= 27 && roomId <= 92)
            {
                CompletedRandomEncounterRooms[roomId - 27] = true;
            }
        }

        private StorySave _cleanStorySave = null!;
        private StorySave? _storySave;
        // Asset-free state fixtures must not load logbook tables. A replica
        // creates its private save only if a room entity actually needs it.
        public StorySave StorySave { get => _storySave ??= new StorySave(); private set => _storySave = value; }

        public void UpdateCleanSave(bool force)
        {
            if (!force && EscapeTimer != -1 && EscapeState == EscapeState.Escape)
            {
                return;
            }
            StorySave.CopyTo(_cleanStorySave);
        }

        public void RestoreCleanSave()
        {
            // todo: save and restore more fields
            ushort prevFoundOctos = StorySave.FoundOctoliths;
            ushort prevCurOctos = StorySave.CurrentOctoliths;
            uint prevLostOctos = StorySave.LostOctoliths;
            byte[] prevAreaHunters = StorySave.AreaHunters.ToArray();
            _cleanStorySave.CopyTo(StorySave);
            int curCurCount = 0;
            int prevCurCount = 0;
            for (int i = 0; i < 8; i++)
            {
                if ((StorySave.CurrentOctoliths & (1 << i)) != 0)
                {
                    curCurCount++;
                }
                if ((prevCurOctos & (1 << i)) != 0)
                {
                    prevCurCount++;
                }
            }
            if (curCurCount > prevCurCount)
            {
                // octolith has been lost -- need to restore values from the dirty save
                StorySave.FoundOctoliths = prevFoundOctos;
                StorySave.CurrentOctoliths = prevCurOctos;
                StorySave.LostOctoliths = prevLostOctos;
            }
            prevAreaHunters.CopyTo(StorySave.AreaHunters, index: 0);
        }

        private const string _saveFolder = "Savedata";
        private readonly JsonSerializerOptions _jsonOpt = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            WriteIndented = true,
            Converters = { new ByteArrayConverter() }
        };

        private string GetSavePath(byte slot)
        {
            return Paths.Combine(_saveFolder, $"save{slot:000}.json");
        }

        private string GetSettingsPath()
        {
            return Paths.Combine(_saveFolder, $"settings.json");
        }

        public void LoadSave()
        {
            StorySave = ReadSave();
        }

        /// <summary>
        /// Begin a new game in the current slot.
        ///
        /// Nothing is written until the game itself saves, so choosing "new
        /// game" and then quitting leaves whatever was in the slot alone.
        /// </summary>
        public void StartNewSave()
        {
            StorySave = new StorySave();
        }

        public StorySave ReadSave()
        {
            StorySave? save = null;
            if (Menu.SaveSlot != 0)
            {
                string path = GetSavePath(Menu.SaveSlot);
                if (File.Exists(path))
                {
                    save = JsonSerializer.Deserialize<StorySave>(File.ReadAllText(path), _jsonOpt);
                }
            }
            return save ?? new StorySave();
        }

        /// <summary>True when that slot has a game in it.</summary>
        public bool SaveExists(byte slot)
        {
            return slot != 0 && File.Exists(GetSavePath(slot));
        }

        /// <summary>
        /// Read one slot without making it the current game.
        ///
        /// <see cref="ReadSave"/> reads whichever slot <see cref="Menu.SaveSlot"/>
        /// points at, which is the wrong question for a menu listing every
        /// slot at once: it would have to move the selection to read a slot
        /// and move it back, and a throw in between would leave it moved.
        /// Returns null when the slot is empty or the file cannot be read.
        /// </summary>
        public StorySave? PeekSave(byte slot)
        {
            if (!SaveExists(slot))
            {
                return null;
            }
            try
            {
                return JsonSerializer.Deserialize<StorySave>(
                    File.ReadAllText(GetSavePath(slot)), _jsonOpt);
            }
            catch (Exception)
            {
                // A save written by a different build, or half-written by a
                // crash, must not stop the menu that offers the other slots.
                return null;
            }
        }

        public void CommitSave()
        {
            if (Menu.SaveSlot == 0)
            {
                return;
            }
            if (!Directory.Exists(_saveFolder))
            {
                Directory.CreateDirectory(_saveFolder);
            }
            StorySave.Weapons &= 0xFF;
            if (StorySave.WeaponSlots[2] == (int)BeamType.OmegaCannon)
            {
                StorySave.WeaponSlots[2] = (int)BeamType.None;
            }
            for (int r = 91; r <= 92; r++)
            {
                for (int b = 0; b < 60; b++)
                {
                    StorySave.RoomState[r - 27][b] = 0;
                }
            }
            File.WriteAllText(GetSavePath(Menu.SaveSlot), JsonSerializer.Serialize(StorySave, _jsonOpt));
        }

        internal sealed class ByteArrayConverter : JsonConverter<byte[]>
        {
            public override byte[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                short[]? sByteArray = JsonSerializer.Deserialize<short[]>(ref reader, options);
                if (sByteArray == null)
                {
                    return null;
                }
                byte[] value = new byte[sByteArray.Length];
                for (int i = 0; i < sByteArray.Length; i++)
                {
                    value[i] = (byte)sByteArray[i];
                }
                return value;
            }

            public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
            {
                writer.WriteStartArray();
                foreach (byte val in value)
                {
                    writer.WriteNumberValue(val);
                }
                writer.WriteEndArray();
            }
        }

        private class SerializedSettings
        {
            // Bugfixes and Cheats stay at their code defaults -- there is no
            // UI to change them any more, and an old settings.json is not
            // allowed to override that. Features still persists, but only
            // the subset Commit()/Load() actually write -- see Features.cs.
            public IReadOnlyDictionary<string, string>? Features { get; set; }
            public MenuSettings? MenuSettings { get; set; }
        }

        public MenuSettings LoadSettings()
        {
            string path = GetSettingsPath();
            if (File.Exists(path))
            {
                SerializedSettings? settings = JsonSerializer.Deserialize<SerializedSettings>(File.ReadAllText(path), _jsonOpt);
                if (settings != null)
                {
                    if (settings.Features != null)
                    {
                        Features.Load(settings.Features);
                    }
                    if (settings.MenuSettings != null)
                    {
                        return settings.MenuSettings;
                    }
                }
            }
            return new MenuSettings();
        }

        public void CommitSettings(MenuSettings menuSettings)
        {
            // sktodo: commit menu options, including save slot
            if (!Directory.Exists(_saveFolder))
            {
                Directory.CreateDirectory(_saveFolder);
            }
            var settings = new SerializedSettings
            {
                Features = Features.Commit(),
                MenuSettings = menuSettings
            };
            File.WriteAllText(GetSettingsPath(), JsonSerializer.Serialize(settings, _jsonOpt));
        }

        public void Reset()
        {
            _cleanStorySave = new StorySave();
            LoadSave();
            CommitSave();
            UpdateCleanSave(force: true);
            MatchState = MatchState.InProgress;
            TransitionState = TransitionState.None;
            TransitionRoomId = -1;
            TransitionAltForm = false;
            ActivePlayers = 0;
            // The names too -- but only when there is no session holding
            // them. Everything else per-slot is cleared here and these were
            // not, so the roster of the last networked match survived into the
            // next one, and an offline match (which never writes a name at
            // all) drew that roster on its scoreboard: seven strangers against
            // eight bots. The guard is not caution, it is ordering: this runs
            // from the Scene constructor, and a client that joined a server
            // received its roster before the scene existed.
            bool keepNames = Mods.Network.NetSession.Active;
            for (int i = 0; i < PlayerEntity.SlotCapacity; i++)
            {
                if (!keepNames)
                {
                    Nicknames[i] = $"Player{i + 1}";
                }
                Stars[i] = 0;
                Standings[i] = 0;
                TeamStandings[i] = 0;
                ResultSlots[i] = 0;
                Points[i] = 0;
                TeamPoints[i] = 0;
                Kills[i] = 0;
                TeamKills[i] = 0;
                Deaths[i] = 0;
                TeamDeaths[i] = 0;
                Time[i] = 0;
                TeamTime[i] = 0;
                BeamDamageMax[i] = 0;
                BeamDamageDealt[i] = 0;
                DamageCount[i] = 0;
                AltDamageCount[i] = 0;
                Kills[i] = 0;
                Suicides[i] = 0;
                FriendlyKills[i] = 0;
                HeadshotKills[i] = 0;
                OctolithScores[i] = 0;
                OctolithDrops[i] = 0;
                OctolithStops[i] = 0;
                NodesCaptured[i] = 0;
                NodesLost[i] = 0;
                KillsAsPrime[i] = 0;
                PrimesKilled[i] = 0;
                for (int j = 0; j < 9; j++)
                {
                    BeamKills[i, j] = 0;
                }
            }
            PrimeHunter = -1;
            Teams = false;
            TeamCount = 2;
            FriendlyFire = false;
            PointGoal = 0;
            TimeGoal = 0;
            OctolithReset = false;
            RadarPlayers = false;
            AffinityWeapons = false;
            // Back to the cartridge's behaviour, like every other rule here
            // goes back to its own default: a match that has not said
            // otherwise is the game as the DS played it.
            ShadowFreeze = true;
            MatchTime = -1;
            PlayerEntity.Reset();
            CamSeqEntity.Current = null;
            CameraSequences.Current = null;
            MenuPause = false;
            DialogPause = false;
            _pausingDialog = false;
            _unpausingDialog = false;
            PausePrevented = false;
            EscapeState = EscapeState.None;
            EscapeTimer = -1;
            EscapePaused = false;
            QueuedOctolithMessageId = -1;
            QueuedOublietteUnlockMessage = false;
        }
    }

    public class StorySave
    {
        // use jagged arrays since 2D arrays can't be serialized out of the box
        public byte[][] RoomState { get; init; } // 66 x 60
        public byte[] VisitedRooms { get; init; } = new byte[9];
        public int[] VisitedConnectors { get; init; } = new int[9];
        public byte[] TriggerState { get; init; } = new byte[4];
        public byte[] Logbook { get; init; } = new byte[68];
        public byte[][] EnemyEncounters { get; init; } // 9 x 8
        public int ScanCount { get; set; }
        public int EquipmentCount { get; set; }
        public int CheckpointEntityId { get; set; } = -1;
        public int CheckpointRoomId { get; set; } = -1;
        public int Health { get; set; }
        public int HealthMax { get; set; }
        public int[] Ammo { get; init; } = new int[2];
        public int[] AmmoMax { get; init; } = new int[2];
        public int[] WeaponSlots { get; init; } = new int[3];
        public ushort Weapons { get; set; }
        public uint Artifacts { get; set; }
        public ushort FoundOctoliths { get; set; }
        public ushort CurrentOctoliths { get; set; }
        public uint LostOctoliths { get; set; } = UInt32.MaxValue;
        public ushort Areas { get; set; } = 0xC; // Celestial Archives 1 & 2
        public BossFlags BossFlags { get; set; } = BossFlags.None;
        public byte[] AreaHunters { get; init; } = new byte[4];
        public byte DefeatedHunters { get; set; }
        public SaveStats Stats { get; init; } = new SaveStats();

        public StorySave()
        {
            RoomState = new byte[66][];
            for (int i = 0; i < RoomState.Length; i++)
            {
                RoomState[i] = new byte[60];
            }
            EnemyEncounters = new byte[8][];
            for (int i = 0; i < EnemyEncounters.Length; i++)
            {
                EnemyEncounters[i] = new byte[8];
            }
            PlayerValues values = Metadata.PlayerValues[0];
            Health = HealthMax = values.EnergyTank - 1;
            Ammo[0] = AmmoMax[0] = 400;
            Ammo[1] = 0;
            AmmoMax[1] = 50;
            Weapons = (ushort)(WeaponUnlockBits.PowerBeam | WeaponUnlockBits.Missile);
            if (Cheats.StartWithAllUpgrades)
            {
                Health = HealthMax = 799;
                Ammo[0] = AmmoMax[0] = 4000;
                Ammo[1] = 950;
                AmmoMax[1] = 950;
                Weapons = 0xFF;
            }
            WeaponSlots[0] = (int)BeamType.PowerBeam;
            WeaponSlots[1] = (int)BeamType.Missile;
            WeaponSlots[2] = (int)BeamType.None;
            // todo: initialize more fields
            UpdateLogbook(0); // SCAN VISOR
            UpdateLogbook(1); // THERMAL POSITIONER
            UpdateLogbook(2); // ARM CANNON
            UpdateLogbook(3); // POWER BEAM
            UpdateLogbook(4); // MISSILE LAUNCHER
            UpdateLogbook(5); // MORPH BALL
            UpdateLogbook(6); // MORPH BALL BOMB
            UpdateLogbook(26); // JUMP BOOTS
            UpdateLogbook(28); // CHARGE SHOT
            if (Cheats.StartWithAllOctoliths)
            {
                FoundOctoliths = CurrentOctoliths = 0xFF;
            }
        }

        public int InitRoomState(int roomId, int entityId, bool active,
            int activeState = 3, int inactiveState = 1)
        {
            if (entityId == -1 || roomId < 27)
            {
                return 0;
            }
            if (roomId > 92 || entityId > 239) // skdebug
            {
                return 1;
            }
            roomId -= 27;
            activeState &= 3;
            inactiveState &= 3;
            (int byteIndex, int pairIndex) = Math.DivRem(entityId, 4);
            pairIndex *= 2;
            int pairMask = 3 << pairIndex;
            if ((RoomState[roomId][byteIndex] & pairMask) == 0)
            {
                RoomState[roomId][byteIndex] &= (byte)~pairMask;
                RoomState[roomId][byteIndex] |= (byte)((active ? activeState : inactiveState) << pairIndex);
            }
            return GetRoomState(roomId + 27, entityId);
        }

        public int GetRoomState(int roomId, int entityId)
        {
            if (entityId == -1 || roomId < 27 || roomId > 92)
            {
                return 0;
            }
            if (entityId > 239) // skdebug
            {
                return 1;
            }
            roomId -= 27;
            (int byteIndex, int pairIndex) = Math.DivRem(entityId, 4);
            pairIndex *= 2;
            return ((RoomState[roomId][byteIndex] >> pairIndex) & 3) - 1;
        }

        public void SetRoomState(int roomId, int entityId, int state)
        {
            if (entityId == -1 || roomId < 27 || roomId > 92 || entityId > 239)
            {
                return;
            }
            roomId -= 27;
            state &= 3;
            (int byteIndex, int pairIndex) = Math.DivRem(entityId, 4);
            pairIndex *= 2;
            int pairMask = 3 << pairIndex;
            RoomState[roomId][byteIndex] &= (byte)~pairMask;
            RoomState[roomId][byteIndex] |= (byte)(state << pairIndex);
            return;
        }

        public bool CheckVisitedRoom(int roomId)
        {
            if (roomId < 27 || roomId > 92)
            {
                return false;
            }
            roomId -= 27;
            (int byteIndex, int bitIndex) = Math.DivRem(roomId, 8);
            return (VisitedRooms[byteIndex] & (1 << bitIndex)) != 0;
        }

        public void SetVisitedRoom(int roomId)
        {
            if (roomId < 27 || roomId > 92)
            {
                return;
            }
            roomId -= 27;
            (int byteIndex, int bitIndex) = Math.DivRem(roomId, 8);
            VisitedRooms[byteIndex] |= (byte)(1 << bitIndex);
        }

        public bool CheckVisitedConnector(int connectorId, int areaId)
        {
            if (connectorId < 0 || connectorId > 63)
            {
                return false;
            }
            // there are two ints per planet (plus one for Oubliette, which doesn't have any connectors),
            // for 64 total connectors across both sub-areas, though the max number of connectors in any area is 15.
            if (connectorId >= 32)
            {
                return ((VisitedConnectors[(areaId & ~1) + 1] >> (connectorId - 32)) & 1) != 0;
            }
            return ((VisitedConnectors[areaId & ~1] >> connectorId) & 1) != 0;
        }

        public void SetVisitedConnector(int connectorId, int areaId)
        {
            if (connectorId < 0 || connectorId > 63)
            {
                return;
            }
            if (connectorId >= 32)
            {
                VisitedConnectors[(areaId & ~1) + 1] |= 1 << (connectorId - 32);
            }
            else
            {
                VisitedConnectors[areaId & ~1] |= 1 << connectorId;
            }
        }

        public bool CheckFoundOctolith(int areaId)
        {
            return (FoundOctoliths & (1 << areaId)) != 0;
        }

        public int CountFoundOctoliths()
        {
            int count = 0;
            for (int i = 0; i < 8; i++)
            {
                if (CheckFoundOctolith(i))
                {
                    count++;
                }
            }
            return count;
        }

        public void UpdateFoundOctolith(int areaId)
        {
            FoundOctoliths |= (ushort)(1 << areaId);
            CurrentOctoliths |= (ushort)(1 << areaId);
        }

        public bool CheckFoundArtifact(int artifactId, int modelId)
        {
            return (Artifacts & (1 << (artifactId + 3 * modelId))) != 0;
        }

        public int CountFoundArtifacts(int modelId)
        {
            int count = 0;
            for (int i = 0; i < 3; i++)
            {
                if (CheckFoundArtifact(i, modelId))
                {
                    count++;
                }
            }
            return count;
        }

        public void UpdateFoundArtifact(int artifactId, int modelId)
        {
            Artifacts |= (uint)(1 << (artifactId + 3 * modelId));
        }

        public int GetEnemyOctolithDrop(int hunter)
        {
            for (int i = 0; i < 8; i++)
            {
                if (((LostOctoliths >> (4 * i)) & 15) == hunter)
                {
                    return i;
                }
            }
            return 8;
        }

        public void UpdateLogbook(int scanId)
        {
            Debug.Assert(scanId >= 0 && scanId < 68 * 8);
            int index = scanId / 8;
            byte bit = (byte)(1 << (scanId % 8));
            if ((Logbook[index] & bit) == 0)
            {
                Logbook[index] |= bit;
                int category = Strings.GetScanEntryCategory(scanId);
                if (category < 3)
                {
                    // lore, bioform, object
                    ScanCount++;
                }
                else if (category == 3)
                {
                    // equipment
                    EquipmentCount++;
                }
            }
        }

        public bool CheckLogbook(int scanId)
        {
            Debug.Assert(scanId >= 0 && scanId < 68 * 8);
            int index = scanId / 8;
            byte bit = (byte)(1 << (scanId % 8));
            return (Logbook[index] & bit) != 0;
        }

        public int GetLogbookCount(bool unlockedOnly, params char[] categories)
        {
            IReadOnlyList<StringTableEntry> logbook = Strings.ReadStringTable(StringTables.ScanLog);
            int result = 0;
            for (int i = 0; i < logbook.Count; i++)
            {
                StringTableEntry entry = logbook[i];
                if (categories.Any(c => c == entry.Category) && (!unlockedOnly || CheckLogbook(i)))
                {
                    result++;
                }
            }
            return result;
        }

        public int GetMaxScanCount()
        {
            // 215 = 82 lore + 58 bioform + 75 object
            return GetLogbookCount(unlockedOnly: false, 'L', 'B', 'O');
        }

        public int GetCompletionPercentage()
        {
            int maxScans = GetMaxScanCount();
            if (maxScans == 0)
            {
                return 0;
            }
            int count = ScanCount;
            for (int i = 1; i < 8; i++)
            {
                if (i == 2)
                {
                    continue;
                }
                if ((Weapons & (1 << i)) != 0)
                {
                    count++;
                }
            }
            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    if ((Artifacts & (1 << (i * 3 + j))) != 0)
                    {
                        count++;
                    }
                }
            }
            for (int i = 0; i < 8; i++)
            {
                if ((FoundOctoliths & (1 << i)) != 0)
                {
                    count++;
                }
            }
            int etankCount = HealthMax / Metadata.PlayerValues[0].EnergyTank;
            int missileCount = (AmmoMax[1] - 50) / 100;
            int uaCount = (AmmoMax[0] - 400) / 300;
            count += etankCount + missileCount + uaCount;
            // 66 = 7 etanks + 12 missiles + 9 UA + 6 weapons + 24 artifacts + 8 Octoliths
            return 100 * count / (maxScans + 66);
        }

        public void CopyTo(StorySave other)
        {
            for (int i = 0; i < RoomState.Length; i++)
            {
                byte[] source = RoomState[i];
                Array.Copy(source, other.RoomState[i], source.Length);
            }
            for (int i = 0; i < EnemyEncounters.Length; i++)
            {
                byte[] source = EnemyEncounters[i];
                Array.Copy(source, other.EnemyEncounters[i], source.Length);
            }
            VisitedRooms.CopyTo(other.VisitedRooms, index: 0);
            VisitedConnectors.CopyTo(other.VisitedConnectors, index: 0);
            TriggerState.CopyTo(other.TriggerState, index: 0);
            Logbook.CopyTo(other.Logbook, index: 0);
            other.ScanCount = ScanCount;
            other.EquipmentCount = EquipmentCount;
            other.CheckpointEntityId = CheckpointEntityId;
            other.CheckpointRoomId = CheckpointRoomId;
            other.Health = Health;
            other.HealthMax = HealthMax;
            Ammo.CopyTo(other.Ammo, index: 0);
            AmmoMax.CopyTo(other.AmmoMax, index: 0);
            WeaponSlots.CopyTo(other.WeaponSlots, index: 0);
            other.Weapons = Weapons;
            other.Artifacts = Artifacts;
            other.Artifacts = Artifacts;
            other.FoundOctoliths = FoundOctoliths;
            other.CurrentOctoliths = CurrentOctoliths;
            other.LostOctoliths = LostOctoliths;
            other.Areas = Areas;
            other.BossFlags = BossFlags;
            AreaHunters.CopyTo(other.AreaHunters, index: 0);
            other.DefeatedHunters = DefeatedHunters;
        }

        public class SaveStats
        {
            public uint HunterKills { get; set; }
            public uint Deaths { get; set; }
            public uint EnemyHunterDeaths { get; set; }
            // todo: this should probably be stored globally and not deleted along with the save slot
            public uint EnemyKills { get; set; }
        }
    }
}
