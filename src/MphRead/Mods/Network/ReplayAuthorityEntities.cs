using MphRead.Mods.Network;

namespace MphRead.Entities;

public partial class OctolithFlagEntity
{
    internal ReplayFlagState CaptureReplayAuthority() => new(Id, Position, ReplayActorRef.Capture(_carrier),
        ReplayActorRef.Capture(_lastCarrier), _atBase, _grounded, _resetTimer, _gravity);
    internal void ApplyReplayAuthority(ReplayFlagState value, ReplayReplicaState state)
    {
        if (_carrier != null && _carrier.OctolithFlag == this) _carrier.OctolithFlag = null;
        _carrier = value.Carrier.Resolve(_scene, state); _lastCarrier = value.LastCarrier.Resolve(_scene, state);
        if (_carrier != null) _carrier.OctolithFlag = this;
        Position = value.Position; _atBase = value.AtBase; _grounded = value.Grounded;
        _resetTimer = value.ResetTimer; _gravity = value.Gravity;
        ClosestNode = _carrier?.ClosestNode ?? (_atBase ? BaseClosestNode : null);
    }
}
public partial class NodeDefenseEntity
{
    internal ReplayNodeState CaptureReplayAuthority()
    {
        byte occupants = 0;
        for (int i = 0; i < _occupiedBy.Length; i++) if (_occupiedBy[i]) occupants |= (byte)(1 << i);
        return new(Id, (sbyte)_currentTeam, (sbyte)_occupyingTeam, occupants, ReplayActorRef.Capture(_capturedPlayer),
            _progress, _scoreTimer, _blinkTimer, _curRotation, _spinSpeed, _contested, _inProgress);
    }
    internal void ApplyReplayAuthority(ReplayNodeState value, ReplayReplicaState state)
    {
        _currentTeam = value.Team; _occupyingTeam = value.Occupying; _capturedPlayer = value.Capturer.Resolve(_scene, state);
        for (int i = 0; i < _occupiedBy.Length; i++) _occupiedBy[i] = (value.Occupants & 1 << i) != 0;
        _progress = value.Progress; _scoreTimer = value.ScoreTimer; _blinkTimer = value.BlinkTimer;
        _curRotation = value.Rotation; _spinSpeed = value.Spin; _contested = value.Contested; _inProgress = value.InProgress;
    }
}
