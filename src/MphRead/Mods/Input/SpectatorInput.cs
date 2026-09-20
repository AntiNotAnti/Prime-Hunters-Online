using System;
namespace MphRead.Mods.Input
{
    public readonly record struct SpectatorInput(bool NextPlayer, bool PreviousPlayer, bool ToggleView,
        bool Scoreboard, bool OpenMenu, float MoveX, float MoveY, float LookX, float LookY, float Ascend, float Descend)
    {
        public static SpectatorInput ReadController(bool replay = false)
        {
            if (!GamepadContexts.Focused || GamepadContexts.Current != GamepadContext.Gameplay) return default;
            var pad = GamepadInput.State;
            var move = GamepadAnalog.ApplyRadialDeadZone(pad.LeftX, pad.LeftY, GamepadOptions.LeftInner, GamepadOptions.LeftOuter);
            return new(GamepadInput.TakePress(GamepadButtons.RightBumper), GamepadInput.TakePress(GamepadButtons.LeftBumper),
                GamepadInput.TakePress(GamepadButtons.Y), pad.Down(GamepadButtons.Back), GamepadInput.TakePress(GamepadButtons.Start),
                move.X, move.Y, GamepadInput.AimDeltaX, GamepadInput.AimDeltaY,
                pad.RightTrigger, pad.LeftTrigger);
        }
        public void ApplyView(bool replay = false)
        {
            if (NextPlayer) SpectatorMode.CycleNext();
            if (PreviousPlayer) SpectatorMode.CyclePrevious();
            if (ToggleView) SpectatorMode.ToggleView();
        }
    }
}
