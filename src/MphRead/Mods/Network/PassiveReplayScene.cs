using System;
using System.IO;
using MphRead.Entities;
using MphRead.Mods.Input;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    /// <summary>A scene and reader with a common lifetime, separate from the foreground host.</summary>
    internal sealed class PassiveReplayScene : IDisposable
    {
        public ReplayPlaybackSession Session { get; }
        public ReplayReplicaState State { get; }
        public Scene Scene { get; }
        private bool _disposed;
        public PassiveReplayScene(string path, Vector2i size)
        {
            var host = new PassiveReplaySessionHost();
            State = host.State;
            Session = new ReplayPlaybackSession(host);
            if (!Session.Join(path)) { Session.Dispose(); throw new InvalidDataException(Session.LastError); }
            MatchStatePacket match = State.Match ?? throw new InvalidDataException("Replay has no room.");
            Scene = new Scene(size, SyntheticInput.CreateKeyboard(), SyntheticInput.CreateMouse(), _ => { }, () => { },
                new ReplaySceneServices(Session, State));
            try
            {
                Scene.GameState.Mode = (GameMode)match.Mode;
                if (Scene.GameState.SinglePlayer) throw new InvalidDataException("Passive reconstruction requires a recorded multiplayer world.");
                for (int slot = 0; slot < PlayerEntity.SlotCapacity; slot++)
                {
                    var occupant = State.Occupant(slot);
                    Scene.AddPlayer(occupant.Generation == 0 ? Hunter.Samus : occupant.Hunter, occupant.Color);
                    Scene.Players.Items[slot].IsBot = false;
                }
                Scene.AddRoom(match.RoomKey, (GameMode)match.Mode,
                    playerCount: Scene.Services.NetworkWorldProfile?.EntityLayerPlayers ?? match.PlayerCount);
                Scene.OnLoad();
            }
            catch { Dispose(); throw; }
        }
        public bool Step()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Session.AtEnd) return false;
            Session.PumpFrame();
            if (Session.LastResult != ReplayOpenResult.Success) throw new InvalidDataException(Session.LastError);
            Scene.StepReplica();
            Session.Transport.AfterFrame();
            return true;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Scene.DoCleanup();
            Scene.UnloadGl();
            Session.Dispose();
        }
    }
}
