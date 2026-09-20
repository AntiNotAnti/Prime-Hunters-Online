using System;
using System.Collections.Immutable;
using System.Diagnostics;
using MphRead.Formats.Collision;
using MphRead.Formats.Culling;
using MphRead.Hud;
using MphRead.Text;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MphRead.Entities
{
    public partial class PlayerEntity
    {
        private int _pauseFrameCount = 0;
        private static int _drawPauseState = 0;
        private static float _navTextTimer = 0;
        private static bool _navLoading = false;
        // need to set and restore these because pausing is allowed while a dialog is open,
        // and if a dialog is open, the normal HUD update code won't run to set these as needed
        private int _pausedPrevBindingId1 = -1;
        private float _pausedPrevAlpha1 = 1;
        private int _pausedPrevMaskId = -1;
        private int _pausedPrevBindingId2 = -1;
        private float _pausedPrevAlpha2 = 1;
        private int _pausedPrevBindingId3 = -1;
        private int _pausedPrevBindingId4 = -1;
        private int _pausedPrevBindingId5 = -1;

        public void SetUpMenuPauseHud()
        {
            EndWeaponMenu();
            _navTextTimer = 0;
            _prevScrollingChars = 0;
            _drawPauseState = 1;
            if (GameState.InRoomTransition)
            {
                _navLoading = true;
            }
            else
            {
                SetUpMenuPauseMapNav();
                _navLoading = false;
            }
            _pausedPrevBindingId1 = _scene.Layer1Info.BindingId;
            _pausedPrevAlpha1 = _scene.Layer1Info.Alpha;
            _pausedPrevMaskId = _scene.Layer1Info.MaskId;
            _pausedPrevBindingId2 = _scene.Layer2Info.BindingId;
            _pausedPrevAlpha2 = _scene.Layer2Info.Alpha;
            _pausedPrevBindingId3 = _scene.Layer3Info.BindingId;
            _pausedPrevBindingId4 = _scene.Layer4Info.BindingId;
            _pausedPrevBindingId5 = _scene.Layer5Info.BindingId;
            for (int i = 0; i < 8; i++)
            {
                int start = (int)Rng.GetRandomInt1(20);
                int afterAnim = (int)Rng.GetRandomInt1(6);
                _mapOctolithInsts[i].SetAnimation(start, 35, 36, afterAnim, HudObjectLoopType.Offset);
            }
            _mapLostOctolithInst.SetAnimation(0, 19, 20, loop: true);
        }

        public void SetUpMenuPauseMapNav()
        {
            _navMapDrawNode = null;
            _navMapModelEnabled = false;
            _navDrawZoom = 0.75f;
            _navDrawRotX = MathHelper.RadiansToDegrees(MathF.Atan2(-_facingVector.X, -_facingVector.Z));
            _navDrawRotY = 28.125f;
            _navPanTimer = 0;
            _navCurRoomNodePos = Vector3.Zero;
            _navCurCenterNodePos = Vector3.Zero;
            _navInitRoomNodePos = Vector3.Zero;
            _navTargetPos = Vector3.Zero;
            _navPanOffset = Vector3.Zero;
            _pauseFrameCount = 0;
            string? roomName = NodeRef.RoomName;
            if (roomName != null)
            {
                int area = _scene.AreaId & ~1;
                for (int i = 0; i < 2; i++, area++)
                {
                    if (area >= 0 && area < _navMapModels.Length)
                    {
                        ModelInstance? model = _navMapModels[area];
                        if (model == null)
                        {
                            continue;
                        }
                        Node? roomNode = null;
                        for (int j = 0; j < model.Model.Nodes.Count; j++)
                        {
                            Node node = model.Model.Nodes[j];
                            if (node.ParentIndex == 0 && node.Name.Equals(roomName, StringComparison.InvariantCultureIgnoreCase))
                            {
                                roomNode = node;
                                break;
                            }
                        }
                        if (roomNode == null)
                        {
                            continue;
                        }
                        Node? centerNode = null;
                        for (int childId = roomNode.ChildIndex; childId != -1; childId = model.Model.Nodes[childId].NextIndex)
                        {
                            Node node = model.Model.Nodes[childId];
                            if (node.Name.StartsWith("cent"))
                            {
                                centerNode = node;
                                break;
                            }
                        }
                        if (centerNode == null)
                        {
                            centerNode = roomNode;
                        }
                        UpdateMapModelTransforms(model, area);
                        SetNavMapDrawNode(roomNode, centerNode);
                        _navMapModelEnabled = true;
                        _navInitRoomNodePos = _navCurRoomNodePos;
                    }
                }
                if (_navMapDrawNode != null)
                {
                    _navTargetPos = _navCurRoomNodePos;
                }
            }
        }

        private static readonly ImmutableArray<Vector3> _navDoorColors =
        [
            new Vector3(230, 230, 230) / 255f,
            new Vector3(255, 255,   0) / 255f,
            new Vector3(247, 148,  82) / 255f,
            new Vector3(  0, 255,   0) / 255f,
            new Vector3(255,   0,   0) / 255f,
            new Vector3(165,  74, 255) / 255f,
            new Vector3(255, 132,   0) / 255f,
            new Vector3(  0, 132, 255) / 255f,
            new Vector3(230, 230, 230) / 255f,
            new Vector3(165, 165, 165) / 255f
        ];

        private static readonly ImmutableArray<Vector3> _navMapNodeOffsets =
        [
            Vector3.Zero,
            new Vector3(-80.87378f, 22.282959f, 205.73096f),
            Vector3.Zero,
            new Vector3(0, 73, 0),
            Vector3.Zero,
            new Vector3(-136.7998f, -0.21191406f, 4.36499f),
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero
        ];

        private bool _navMapModelEnabled = false;
        private Node? _navMapDrawNode = null;
        private float _navDrawZoom = 0;
        private float _navDrawRotX = 0;
        private float _navDrawRotY = 0;
        private float _navPanTimer = 0;
        private Vector3 _navCurRoomNodePos = Vector3.Zero;
        private Vector3 _navCurCenterNodePos = Vector3.Zero;
        private Vector3 _navInitRoomNodePos = Vector3.Zero; // does not update when panning to another room
        private Vector3 _navTargetPos = Vector3.Zero; // center node pos + panning offset
        private Vector3 _navPanOffset = Vector3.Zero;

        private void SetNavMapDrawNode(Node roomNode, Node centerNode)
        {
            _navMapDrawNode = roomNode;
            _navCurRoomNodePos = roomNode.Animation.ExtractTranslation();
            _navCurCenterNodePos = centerNode.Animation.ExtractTranslation();
        }

        public void EndMenuPauseHud()
        {
            InitHudState();
            _scene.Layer1Info.BindingId = _pausedPrevBindingId1;
            _scene.Layer1Info.Alpha = _pausedPrevAlpha1;
            _scene.Layer1Info.MaskId = _pausedPrevMaskId;
            _scene.Layer2Info.BindingId = _pausedPrevBindingId2; // redundant
            _scene.Layer2Info.Alpha = _pausedPrevAlpha2;
            _scene.Layer3Info.BindingId = _pausedPrevBindingId3;
            _scene.Layer4Info.BindingId = _pausedPrevBindingId4;
            _scene.Layer5Info.BindingId = _pausedPrevBindingId5;
            if (GameState.DialogPause)
            {
                UpdateDialogs();
            }
            _navMapDrawNode = null;
            _navMapModelEnabled = false;
        }

        private static readonly ImmutableArray<(short X, short Y)> _mapIconPositions =
        [
            (15, 102), (15, 134), (15, 38), (15, 70), (241, 38), (241, 70), (241, 102), (241, 134)
        ];

        private static readonly ImmutableArray<(short X, short Y)> _mapDotOffsets =
        [
            (-10, -7), (10, -7), (0, 12)
        ];

        public void DrawPauseMenuBackground()
        {
            if (!_navMapModelEnabled || _drawPauseState != 1 || !_scene.NavMapRoomSymbols.HasValue || Controls.HudOverlay.IsDown)
            {
                return;
            }
            ImmutableArray<NavMapRoomSymbols> navMapRoomSymbols = _scene.NavMapRoomSymbols.Value;
            (Matrix4 viewMtx, Matrix4 orthoMtx) = GetPauseMapMatrices();
            int area = _scene.AreaId & ~1;
            for (int i = 0; i < 2; i++, area++)
            {
                if (area < 0 || area >= _navMapModels.Length)
                {
                    continue;
                }
                ModelInstance? model = _navMapModels[area];
                if (model == null)
                {
                    continue;
                }
                for (int j = 0; j < model.Model.Nodes.Count; j++)
                {
                    Node roomNode = model.Model.Nodes[j];
                    if (roomNode.ParentIndex != 0 || !CheckRoomVisited(roomNode))
                    {
                        continue;
                    }
                    for (int k = 0; k < navMapRoomSymbols.Length; k++)
                    {
                        NavMapRoomSymbols roomSymbols = navMapRoomSymbols[k];
                        if (!GameState.StorySave.CheckVisitedRoom(roomSymbols.Id)
                            || !String.Equals(roomSymbols.Name, roomNode.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        for (int l = 0; l < roomSymbols.Symbols.Length; l++)
                        {
                            NavMapEntitySymbol entitySymbol = roomSymbols.Symbols[l];
                            if (entitySymbol.Type != EntityType.Teleporter)
                            {
                                continue;
                            }
                            Vector3 teleporterPos = entitySymbol.Position + roomNode.Animation.Row3.Xyz.AddY(1);
                            if (Matrix.ProjectPosition(teleporterPos, viewMtx, orthoMtx, out Vector2 distPos) > 0)
                            {
                                var screenPos = new Vector2((distPos.X + 1) / 2, (1 - distPos.Y) / 2);
                                if (screenPos.X > 0 && screenPos.X < 1 && screenPos.Y > 0 && screenPos.Y < 1)
                                {
                                    _mapLegendOtherInst.PositionX = distPos.X - 1 / (256 / 8f);
                                    _mapLegendOtherInst.PositionY = distPos.Y - 1 / (192 / 8f);
                                    _mapLegendOtherInst.SetIndex(entitySymbol.SubType, _scene);
                                    _scene.DrawHudObject(_mapLegendOtherInst);
                                }
                            }
                        }
                    }
                }
            }
        }

        public void DrawPauseMenuForeground()
        {
            if (_navLoading)
            {
                StringTableEntry? entry = Strings.GetEntry('R', 997, StringTables.LocationNames); // topographical view initializing
                if (entry != null)
                {
                    _textSpacingY = 9;
                    Span<char> buffer = stackalloc char[128];
                    WrapText(entry.String1, 150, buffer);
                    // note: the game assumes the topo init message doesn't exceed 60 characters
                    int characters = (int)(_navTextTimer / (1 / 30f));
                    DrawText2D(128, 96, Align.PadCenter, 0, buffer, maxLength: characters);
                    if (characters > 0 && characters != _prevScrollingChars && characters <= entry.String1.Length)
                    {
                        _soundSource.StopFreeSfx(SfxId.LETTER_BLIP);
                        _soundSource.PlayFreeSfx(SfxId.LETTER_BLIP);
                        _prevScrollingChars = characters;
                    }
                    _textSpacingY = 0;
                }
            }
            else if (_drawPauseState == 1)
            {
                if (_scene.ProcessFrame)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        _mapOctolithInsts[i].ProcessAnimation(_scene);
                    }
                    _mapLostOctolithInst.ProcessAnimation(_scene);
                }
                int artifactIndex = 0;
                for (int i = 0; i < 8; i++)
                {
                    (short posX, short posY) = _mapIconPositions[i];
                    if ((GameState.StorySave.Areas & (1 << (i / 2 * 2))) != 0)
                    {
                        bool hasOctolith = (GameState.StorySave.CurrentOctoliths & (1 << i)) != 0;
                        uint lostHunter = (GameState.StorySave.LostOctoliths >> (i * 4)) & 15;
                        if (hasOctolith || lostHunter < 8)
                        {
                            HudObjectInstance octoInst = _mapOctolithInsts[i];
                            int offsetX = 0;
                            if (!hasOctolith)
                            {
                                HudObjectInstance portrait = _hunterInsts[lostHunter];
                                portrait.PositionX = (posX - 16) / 256f;
                                portrait.PositionY = (posY - 16) / 192f;
                                _scene.DrawHudObject(portrait);
                                octoInst = _mapLostOctolithInst;
                                offsetX = 6;
                            }
                            octoInst.PositionX = (posX + offsetX - (octoInst.Width / 2)) / 256f;
                            octoInst.PositionY = (posY - (octoInst.Height / 2)) / 192f;
                            _scene.DrawHudObject(octoInst);
                        }
                        else
                        {
                            _mapTeleporterInst.PositionX = (posX - _mapTeleporterInst.Width / 2) / 256f;
                            _mapTeleporterInst.PositionY = (posY - _mapTeleporterInst.Height / 2) / 192f;
                            int teleporterIndex = (GameState.StorySave.Artifacts & (7 << artifactIndex)) >> artifactIndex == 7 ? 1 : 0;
                            _mapTeleporterInst.SetIndex(teleporterIndex, _scene);
                            _scene.DrawHudObject(_mapTeleporterInst);
                            HudObjectInstance dotInst = _mapArtifactDotInsts[i];
                            for (int j = 0; j < 3; j++)
                            {
                                if ((GameState.StorySave.Artifacts & (1 << (artifactIndex + j))) != 0)
                                {
                                    (short offsetX, short offsetY) = _mapDotOffsets[j];
                                    dotInst.PositionX = (posX + offsetX - dotInst.Width / 2) / 256f;
                                    dotInst.PositionY = (posY + offsetY - dotInst.Height / 2) / 192f;
                                    _scene.DrawHudObject(dotInst);
                                }
                            }
                        }
                    }
                    artifactIndex += 3;
                }
                if (!_navMapModelEnabled)
                {
                    StringTableEntry? entry = Strings.GetEntry('R', 998, StringTables.LocationNames); // topographical view unavailable
                    if (entry != null)
                    {
                        _textSpacingY = 9;
                        Span<char> buffer = stackalloc char[256];
                        WrapText(entry.String1, 150, buffer);
                        // note: the game assumes the topo unavailable message doesn't exceed 200 characters
                        int characters = (int)(_navTextTimer / (1 / 30f));
                        DrawText2D(128, 96, Align.PadCenter, 0, buffer, maxLength: characters);
                        if (characters > 0 && characters != _prevScrollingChars && characters <= entry.String1.Length)
                        {
                            _soundSource.StopFreeSfx(SfxId.LETTER_BLIP);
                            _soundSource.PlayFreeSfx(SfxId.LETTER_BLIP);
                            _prevScrollingChars = characters;
                        }
                        _textSpacingY = 0;
                    }
                }
                else
                {
                    ReadOnlySpan<char> roomName = ReadOnlySpan<char>.Empty;
                    StringTableEntry? unknownEntry = Strings.GetEntry('R', 999, StringTables.LocationNames); // unknown location
                    if (unknownEntry != null)
                    {
                        roomName = unknownEntry.String1.AsSpan();
                    }
                    if (_navMapDrawNode != null)
                    {
                        for (int i = 1; i <= 35; i++)
                        {
                            int id = _scene.AreaId / 2 * 100 + i;
                            StringTableEntry? roomEntry = Strings.GetEntry('R', id, StringTables.LocationNames); // room_name\display_name
                            if (roomEntry == null)
                            {
                                break;
                            }
                            // note: display name is empty for connectors, while the room name is e.g. Con01, matching the name found in
                            // the connector door's entity data used for portals, rather than matching the usual room key (e.g. UNIT2_CZ).
                            if (MemoryExtensions.CompareTo(roomEntry.String1, _navMapDrawNode.Name, StringComparison.OrdinalIgnoreCase) == 0)
                            {
                                roomName = roomEntry.String2;
                                break;
                            }
                        }
                    }
                    if (roomName.Length > 0)
                    {
                        Debug.Assert(roomName.Length <= 200);
                        Span<char> buffer = stackalloc char[roomName.Length + 10];
                        _textSpacingY = 9;
                        int lines = WrapText(roomName, 175, buffer);
                        int characters = (int)(_navTextTimer / (1 / 30f));
                        int y = 173 - ((8 * lines) >> 1);
                        DrawText2D(128, y, Align.PadCenter, 0, buffer, maxLength: characters);
                        if ((roomName.Length > 1 || roomName.Length == 1 && roomName[0] != ' ')
                            && characters > 0 && characters != _prevScrollingChars && characters <= roomName.Length)
                        {
                            _soundSource.StopFreeSfx(SfxId.LETTER_BLIP);
                            _soundSource.PlayFreeSfx(SfxId.LETTER_BLIP);
                            _prevScrollingChars = characters;
                        }
                        _textSpacingY = 0;
                    }
                    if (Controls.HudOverlay.IsDown)
                    {
                        _mapLegendInfo[0].Unlocked = _availableWeapons[BeamType.Battlehammer];
                        _mapLegendInfo[1].Unlocked = _availableWeapons[BeamType.VoltDriver];
                        _mapLegendInfo[2].Unlocked = _availableWeapons[BeamType.ShockCoil];
                        _mapLegendInfo[3].Unlocked = _availableWeapons[BeamType.Imperialist];
                        _mapLegendInfo[4].Unlocked = _availableWeapons[BeamType.Judicator];
                        _mapLegendInfo[5].Unlocked = _availableWeapons[BeamType.Magmaul];
                        int column = 0;
                        float posY = 65;
                        Span<char> buffer = stackalloc char[128];
                        for (int i = 0; i < _mapLegendInfo.Length; i++)
                        {
                            MapLegendInfo info = _mapLegendInfo[i];
                            string text;
                            if (info.Group == 0)
                            {
                                if (info.Unlocked)
                                {
                                    text = Strings.GetMessage('B', info.MessageId, StringTables.HudMessagesSP); // weapon name
                                }
                                else
                                {
                                    text = Scene.Language == Language.Spanish ? "(?)" : "???";
                                }
                            }
                            else
                            {
                                text = Strings.GetMessage('M', info.MessageId, StringTables.HudMessagesSP); // any/missile/portal
                            }
                            float textX = column == 0 ? 114 : 143;
                            float objX = column == 0 ? 116 : 132;
                            info.HudObject.PositionX = (objX + info.OffsetX) / 256f;
                            info.HudObject.PositionY = (posY + info.OffsetY) / 192f;
                            info.HudObject.SetIndex(info.ObjectIndex, _scene);
                            _scene.DrawHudObject(info.HudObject);
                            _textSpacingY = 8;
                            buffer.Clear();
                            int lines = WrapText(text, 80, buffer);
                            DrawText2D(textX, posY + 1, column == 0 ? Align.Right : Align.Left, 0, buffer);
                            _textSpacingY = 0;
                            posY += 8 * lines + 3;
                            if (posY >= 129)
                            {
                                posY = 65;
                                column++;
                            }
                        }
                    }
                }
            }
            DrawPauseQuitInterface();
        }

        private void DrawPauseQuitInterface()
        {
            if (_drawPauseState == 1)
            {
                int posX = 26; // todo: invert for left-handed mode
                _mapQuitInst.PositionX = (posX - _mapQuitInst.Width / 2) / 256f;
                _mapQuitInst.PositionY = (173 - _mapQuitInst.Height / 2) / 192f;
                _scene.DrawHudObject(_mapQuitInst);
                string text = Strings.GetHudMessage(119);
                DrawText2D(posX, 181, Align.Center, 0, text);
            }
            else if (_drawPauseState == 2)
            {
                if (_scene.ProcessFrame)
                {
                    _dialogButtonInst.ProcessAnimation(_scene);
                }
                string text = Strings.GetHudMessage(122); // QUIT GAME are you sure?
                int characters = (int)(_navTextTimer / (1 / 30f));
                DrawText2D(128, 90, Align.PadCenter, 0, text, maxLength: characters);
                if (characters > 0 && characters != _prevScrollingChars && characters <= text.Length)
                {
                    _soundSource.StopFreeSfx(SfxId.LETTER_BLIP);
                    _soundSource.PlayFreeSfx(SfxId.LETTER_BLIP);
                    _prevScrollingChars = characters;
                }
                DrawDialogConfirmButtons(DialogType.YesNo);
            }
        }

        public void ProcessPauseMenu()
        {
            if (_navLoading)
            {
                if (_navTextTimer < 60 / 30f)
                {
                    _navTextTimer += _scene.FrameTime;
                }
                if (_navTextTimer >= 60 / 30f && !GameState.InRoomTransition)
                {
                    SetUpMenuPauseMapNav();
                    _navTextTimer = 0;
                    _prevScrollingChars = 0;
                    _navLoading = false;
                }
            }
            else if (_navTextTimer < 200 / 30f) // applies to topo unavailable, location name, quit game
            {
                _navTextTimer += _scene.FrameTime;
            }
            if (_pauseFrameCount > 0 && _pauseFrameCount % 2 == 0) // todo: FPS stuff
            {
                _navPlayerPosModel.UpdateAnimFrames();
            }
            _pauseFrameCount++;
            if (_scene.CameraMode == CameraMode.Player)
            {
                ProcessPauseMenuInput();
            }
        }

        private void ResetPauseQuitDisplay()
        {
            _navTextTimer = 0;
            _prevScrollingChars = 0;
            _dialogButtonInst.SetAnimation(start: 0, target: 2, frames: 3, afterAnim: 2);
        }

        private void ProcessPauseMenuInput()
        {
            if (GameState.PausePrevented)
            {
                return;
            }
            if (_drawPauseState == 1 && CheckButtonPressed(DialogButton.Quit))
            {
                _soundSource.PlayFreeSfx(SfxId.QUIT_GAME);
                _drawPauseState = 2;
                ResetPauseQuitDisplay();
                return;
            }
            if (_drawPauseState == 2)
            {
                if (CheckButtonPressed(DialogButton.Yes))
                {
                    GameState.PausePrevented = true;
                    _soundSource.PlayFreeSfx(SfxId.QUIT_GAME);
                    _soundSource.PlayFreeSfx(SfxId.RETURN_TO_SHIP_YES);
                    _drawPauseState = 0;
                    ResetPauseQuitDisplay();
                    Music.Stop(20 / 30f);
                    _scene.SetFade(FadeType.FadeOutBlack, 20 / 30f, overwrite: true, AfterFade.Exit);
                    return;
                }
                if (CheckButtonPressed(DialogButton.No))
                {
                    _soundSource.PlayFreeSfx(SfxId.RETURN_TO_SHIP_NO);
                    _drawPauseState = 1;
                    ResetPauseQuitDisplay();
                    return;
                }
            }
            if (_drawPauseState != 1)
            {
                return;
            }
            if (_isScrollingUp)
            {
                _navDrawZoom -= 0.0625f;
            }
            else if (_isScrollingDown)
            {
                _navDrawZoom += 0.0625f;
            }
            _navDrawZoom = Math.Clamp(_navDrawZoom, 0.4375f, 3);
            if (Controls.AimLeft.IsDown)
            {
                _navDrawRotX += 2.8125f / 2;
            }
            else if (Controls.AimRight.IsDown)
            {
                _navDrawRotX -= 2.8125f / 2;
            }
            else if (Input.MouseState?.IsButtonDown(MouseButton.Left) == true && Input.MouseDeltaX != 0)
            {
                _navDrawRotX += -Input.MouseDeltaX / 8f * 2.8125f;
            }
            if (_navDrawRotX >= 360)
            {
                _navDrawRotX -= 360;
            }
            else if (_navDrawRotX <= -360)
            {
                _navDrawRotX += 360;
            }
            if (Controls.AimUp.IsDown)
            {
                _navDrawRotY -= 2.8125f / 2;
            }
            else if (Controls.AimDown.IsDown)
            {
                _navDrawRotY += 2.8125f / 2;
            }
            else if (Input.MouseState?.IsButtonDown(MouseButton.Left) == true && Input.MouseDeltaY != 0)
            {
                _navDrawRotY += Input.MouseDeltaY / 8f * 2.8125f;
            }
            _navDrawRotY = Math.Clamp(_navDrawRotY, -71.41113f, 71.41113f);
            (Matrix4 viewMtx, Matrix4 orthoMtx) = GetPauseMapMatrices();
            float panDirX = 0;
            if (Controls.MoveLeft.IsDown)
            {
                panDirX = -1;
            }
            else if (Controls.MoveRight.IsDown)
            {
                panDirX = 1;
            }
            if (panDirX != 0)
            {
                _navPanOffset += viewMtx.Column0.Xyz * (4.6f / 2) * panDirX;
            }
            float panDirY = 0;
            if (Controls.MoveUp.IsDown)
            {
                panDirY = 1;
            }
            else if (Controls.MoveDown.IsDown)
            {
                panDirY = -1;
            }
            if (panDirY != 0)
            {
                _navPanOffset += viewMtx.Column1.Xyz * (4.6f / 2) * panDirY;
            }
            if (panDirX != 0 || panDirY != 0)
            {
                _navPanTimer = 0;
                float minDist = 512 * 512;
                Node? newRoomNode = null;
                Node? newCenterNode = null;
                int area = _scene.AreaId & ~1;
                for (int i = 0; i < 2; i++, area++)
                {
                    if (area >= 0 && area < _navMapModels.Length)
                    {
                        ModelInstance? model = _navMapModels[area];
                        if (model == null)
                        {
                            continue;
                        }
                        for (int j = 0; j < model.Model.Nodes.Count; j++)
                        {
                            Node roomNode = model.Model.Nodes[j];
                            if (roomNode.ParentIndex != 0 || !CheckRoomVisited(roomNode))
                            {
                                continue;
                            }
                            for (int k = roomNode.ChildIndex; k != -1;)
                            {
                                Node node = model.Model.Nodes[k];
                                Vector3 nodePos = node.Animation.Row3.Xyz;
                                if (node.Name.StartsWith("cent") && Matrix.ProjectPosition(nodePos, viewMtx, orthoMtx, out Vector2 distPos) > 0)
                                {
                                    var screenPos = new Vector2((distPos.X + 1) / 2, (1 - distPos.Y) / 2);
                                    // undo the coordinate transform so we get percentages from center
                                    float distX = distPos.X * 2 - 1;
                                    float distY = 1 - distPos.Y * 2;
                                    float dist = distX * distX + distY * distY;
                                    if (screenPos.X > 0 && screenPos.X < 1 && screenPos.Y > 0 && screenPos.Y < 1 && dist < minDist)
                                    {
                                        newRoomNode = roomNode;
                                        newCenterNode = node;
                                        minDist = dist;
                                    }
                                }
                                k = node.NextIndex;
                            }
                        }
                    }
                }
                if (newRoomNode != null && newRoomNode != _navMapDrawNode)
                {
                    Debug.Assert(newCenterNode != null);
                    SetNavMapDrawNode(newRoomNode, newCenterNode);
                    _navPanOffset = _navTargetPos - _navCurCenterNodePos;
                    _navTextTimer = 0;
                    _prevScrollingChars = 0;
                }
            }
            else if (_navPanOffset != Vector3.Zero && !Features.NoMapCentering)
            {
                if (_navPanTimer < 16 * 30)
                {
                    _navPanTimer += _scene.FrameTime;
                    if (_navPanTimer > 16 * 30)
                    {
                        _navPanTimer = 16 * 30;
                    }
                }
                float frames = _navPanTimer * 30f;
                float factor = 1 - 0.1f * (frames / 16); // max decay of 10% after 16 frames in-game
                float x = ExponentialDecay(factor, _navPanOffset.X);
                float y = ExponentialDecay(factor, _navPanOffset.Y);
                float z = ExponentialDecay(factor, _navPanOffset.Z);
                if (MathF.Abs(x) < 1 / 4096f)
                {
                    x = 0;
                }
                if (MathF.Abs(y) < 1 / 4096f)
                {
                    y = 0;
                }
                if (MathF.Abs(z) < 1 / 4096f)
                {
                    z = 0;
                }
                _navPanOffset = new Vector3(x, y, z);
            }
        }

        private (Vector3 CameraPosition, Vector3 CameraTarget) GetPauseMapLookVectors()
        {
            _navTargetPos = _navCurCenterNodePos + _navPanOffset;
            Vector3 cameraTarget = _navTargetPos.AddY(3.75f);
            (float sinX, float cosX) = MathF.SinCos(MathHelper.DegreesToRadians(_navDrawRotX));
            (float sinY, float cosY) = MathF.SinCos(MathHelper.DegreesToRadians(_navDrawRotY));
            Vector3 cameraPos = cameraTarget + new Vector3(sinX * cosY * 90, sinY * 90, cosX * cosY * 90);
            return (cameraPos, cameraTarget);
        }

        public (Matrix4 ViewMatrix, Matrix4 PerspectiveMatrix) GetPauseMapMatrices()
        {
            Matrix4 orthoMtx = Matrix4.CreateOrthographic(256 * _navDrawZoom, 192 / 256f * 256 * _navDrawZoom, -400, 400);
            (Vector3 cameraPos, Vector3 cameraTarget) = GetPauseMapLookVectors();
            Matrix4 viewMtx = Matrix4.LookAt(cameraPos, cameraTarget, Vector3.UnitY);
            return (viewMtx, orthoMtx);
        }

        private void UpdateMapModelTransforms(ModelInstance inst, int area)
        {
            Matrix4 transform = Matrix4.CreateScale(inst.Model.Scale.X);
            Vector3 offset = _navMapNodeOffsets[area];
            transform.Row3.Xyz += offset;
            UpdateTransforms(inst, transform, 0);
        }

        private bool CheckRoomVisited(Node node)
        {
            if (node.Name.StartsWith("Con") && node.Name.Length >= 5 && Int32.TryParse(node.Name.AsSpan(3, 2), out int id) && id >= 1)
            {
                return GameState.StorySave.CheckVisitedConnector(id - 1, _scene.AreaId);
            }
            for (int i = 27; i <= 92; i++)
            {
                RoomMetadata meta = Metadata.GetRoomById(i)!;
                if (String.Compare(node.Name, meta.Name, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return GameState.StorySave.CheckVisitedRoom(i);
                }
            }
            return false;
        }

        public void GetPauseMapRenderItems()
        {
            if (!_navMapModelEnabled || _drawPauseState != 1 || Controls.HudOverlay.IsDown)
            {
                return;
            }
            int area = _scene.AreaId & ~1;
            for (int i = 0; i < 2; i++, area++)
            {
                if (area >= _navMapModels.Length)
                {
                    break;
                }
                ModelInstance? inst = _navMapModels[area];
                if (inst == null)
                {
                    continue;
                }
                UpdateMapModelTransforms(inst, area);
                GetMapDrawItems(inst);
            }
            Matrix4 playerPosTransform = Matrix4.CreateScale(2) * GetTransformMatrix(_facingVector, _upVector);
            Vector3 roomOffset = Vector3.Zero;
            Debug.Assert(_scene.Room != null);
            for (int i = 0; i < _scene.Room.RoomCollision.Count; i++)
            {
                CollisionInstance collision = _scene.Room.RoomCollision[i];
                if (collision.ConnectorName != null && collision.ConnectorName == NodeRef.RoomName)
                {
                    roomOffset = collision.Translation;
                    break;
                }
            }
            // bugfix: the game scales the door Y position in Fan Room Alpha/Beta so that it lines up with the top of the room and the connector,
            // which have an exaggerated height to make the rooms fit in the combined map. however, the player indicator is unchanged, so when standing
            // in front of the upper door, it looks like you're only halfway up on the map model, and stepping inside the connector appears discontinuous.
            // fix by applying the same factor to the player position (only if inside the actual room, and not a connector).
            float posHeightFactor = 1;
            if (NodeRef.RoomName == "UNIT2_C2") // Fan Room Alpha
            {
                posHeightFactor = 2.05f;
            }
            else if (NodeRef.RoomName == "UNIT2_C3") // Fan Room Beta
            {
                posHeightFactor = 1.305f;
            }
            float x = _position.X + _navInitRoomNodePos.X - roomOffset.X;
            float y = _position.Y * posHeightFactor + _navInitRoomNodePos.Y - roomOffset.Y + 1;
            float z = _position.Z + _navInitRoomNodePos.Z - roomOffset.Z;
            playerPosTransform.Row3.Xyz = new Vector3(x, y, z);
            UpdateTransforms(_navPlayerPosModel, playerPosTransform, 0);
            GetDrawItems(_navPlayerPosModel, 0);
            if (_navMapDrawNode == null || _navMapDrawNode.Name.StartsWith("Con") || !_scene.NavMapRoomSymbols.HasValue)
            {
                // doors are only draw for a selected non-connector room
                return;
            }
            // hack(?) to make the door lighting appear as it does in-game where the light vectors are all zeroes
            (Vector3 cameraPos, Vector3 cameraTarget) = GetPauseMapLookVectors();
            Vector3 lightVec = (cameraTarget - cameraPos).Normalized();
            var lightInfo = new LightInfo(lightVec, Vector3.One, Vector3.Zero, Vector3.Zero);
            ImmutableArray<NavMapRoomSymbols> navMapRoomSymbols = _scene.NavMapRoomSymbols.Value;
            for (int i = 0; i < navMapRoomSymbols.Length; i++)
            {
                NavMapRoomSymbols roomSymbols = navMapRoomSymbols[i];
                if (!String.Equals(roomSymbols.Name, _navMapDrawNode.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                for (int j = 0; j < roomSymbols.Symbols.Length; j++)
                {
                    NavMapEntitySymbol entitySymbol = roomSymbols.Symbols[j];
                    if (entitySymbol.Type == EntityType.Door)
                    {
                        bool locked = entitySymbol.Locked;
                        if (entitySymbol.Id != -1)
                        {
                            locked = GameState.StorySave.GetRoomState(roomSymbols.Id, entitySymbol.Id) != 0;
                        }
                        int palette = 0;
                        if (locked)
                        {
                            palette = entitySymbol.SubType;
                            if (palette > 7)
                            {
                                palette = 9;
                            }
                        }
                        Vector3 doorPos = entitySymbol.Position + _navCurRoomNodePos;
                        Matrix4 doorTransform = GetTransformMatrix(entitySymbol.FacingVector, entitySymbol.UpVector, doorPos);
                        UpdateTransforms(_navDoorModel, doorTransform, 0);
                        _navDoorModel.Model.Materials[0].CurrentDiffuse = _navDoorColors[palette];
                        GetDrawItems(_navDoorModel, 0, lightInfo);
                    }
                }
            }
        }

        private void GetMapDrawItems(ModelInstance inst)
        {
            int polygonId = _scene.GetNextPolygonId();
            GetItems(inst, inst.Model.Nodes[0], polygonId);

            void GetItems(ModelInstance inst, Node node, int polygonId)
            {
                Model model = inst.Model;
                if (node.Enabled)
                {
                    Node? roomParent = null;
                    int parentIndex = node.ParentIndex;
                    while (parentIndex > 0)
                    {
                        roomParent = model.Nodes[parentIndex];
                        parentIndex = roomParent.ParentIndex;
                    }
                    if (roomParent != null && !CheckRoomVisited(roomParent))
                    {
                        return;
                    }
                    bool isSelected = _navMapDrawNode != null && roomParent == _navMapDrawNode;
                    int start = node.MeshId / 2;
                    for (int k = 0; k < node.MeshCount; k++)
                    {
                        Mesh mesh = model.Meshes[start + k];
                        if (!mesh.Visible)
                        {
                            continue;
                        }
                        // in-game, the overlap between walls and floors causes the outline of the floor to be lightened, like
                        // a wireframe, for non-selected rooms. not really sure if we'll bother recreating that at the moment.
                        Material material = model.Materials[mesh.MaterialId];
                        material.Wireframe = 0;
                        material.Lighting = 1;
                        float alpha = isSelected ? 20 / 31f : 4 / 31f;
                        Vector3 emission = Vector3.Zero;
                        Vector3 lightColor = isSelected
                                ? new Vector3(247, 153, 52) / 255f
                                : new Vector3(247, 123, 22) / 255f;
                        var lightInfo = new LightInfo(new Vector3(0, 0, -0), lightColor, Vector3.Zero, lightColor);
                        _scene.AddRenderItem(material, polygonId, alpha, emission, lightInfo, Matrix4.Identity,
                            node.Animation, mesh.ListId, model.NodeMatrixIds.Count, model.MatrixStackValues, null,
                            null, SelectionType.None, node.BillboardMode);
                        if (!isSelected)
                        {
                            _scene.AddRenderItem(material, polygonId, alpha, emission, lightInfo, Matrix4.Identity,
                                node.Animation, mesh.ListId, model.NodeMatrixIds.Count, model.MatrixStackValues, null,
                                null, SelectionType.None, node.BillboardMode);
                        }
                        else if (material.Name.Length > 0 && material.Name[0] != 'T')
                        {
                            // the game gets the desired color using emission color only with wireframe + no lighting
                            // we can't use emission the same way if lighting is turned off, so we set the same color as diffuse
                            material.Wireframe = 1;
                            material.Lighting = 0;
                            emission = new Vector3(247, 123, 22) / 255f;
                            Vector3 prevDiffuse = material.CurrentDiffuse;
                            material.CurrentDiffuse = emission;
                            _scene.AddRenderItem(material, polygonId, 1, emission, lightInfo, Matrix4.Identity,
                                node.Animation, mesh.ListId, model.NodeMatrixIds.Count, model.MatrixStackValues, null,
                                null, SelectionType.None, node.BillboardMode);
                            material.CurrentDiffuse = prevDiffuse;
                        }
                    }
                    if (node.ChildIndex != -1)
                    {
                        GetItems(inst, model.Nodes[node.ChildIndex], polygonId);
                    }
                }
                if (node.NextIndex != -1)
                {
                    GetItems(inst, model.Nodes[node.NextIndex], polygonId);
                }
            }
        }

        private ImmutableArray<MapLegendInfo> _mapLegendInfo = ImmutableArray<MapLegendInfo>.Empty;

        private class MapLegendInfo
        {
            public bool Unlocked { get; set; }
            public int Group { get; }
            public int MessageId { get; }
            public int OffsetX { get; }
            public int OffsetY { get; }
            public HudObjectInstance HudObject { get; }
            public int ObjectIndex { get; }

            public MapLegendInfo(bool unlocked, int messageId, int offsetX, int offsetY,
                HudObjectInstance hudObject, int objectIndex)
            {
                Unlocked = unlocked;
                Group = unlocked ? 1 : 0;
                MessageId = messageId;
                OffsetX = offsetX;
                OffsetY = offsetY;
                HudObject = hudObject;
                ObjectIndex = objectIndex;
            }
        }
    }
}
