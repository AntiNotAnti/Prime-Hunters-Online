using System;
namespace MphRead.Mods.Input
{
    public readonly record struct SpectatorInput(bool NextPlayer, bool PreviousPlayer, bool ToggleView,
        bool Scoreboard, bool OpenMenu, float MoveX, float MoveY, float LookX, float LookY, float Ascend, float Descend)
    {
        internal static (float X, float Y) CameraLook(float aimX, float aimY)
            // Gameplay aim uses a negated horizontal convention because UpdateAimX
            // consumes turn in player space. The roam camera's positive yaw turns
            // right, so convert exactly once at the spectator boundary.
            => (-aimX, aimY);

        public static SpectatorInput ReadController(bool replay = false)
        {
            if (!GamepadContexts.Focused || GamepadContexts.Current != GamepadContext.Gameplay) return default;
            var pad = GamepadInput.State;
            var move = GamepadAnalog.ApplyRadialDeadZone(pad.LeftX, pad.LeftY, GamepadOptions.LeftInner, GamepadOptions.LeftOuter);
            var look = CameraLook(GamepadInput.AimDeltaX, GamepadInput.AimDeltaY);
            return new(GamepadInput.TakePress(GamepadButtons.RightBumper), GamepadInput.TakePress(GamepadButtons.LeftBumper),
                GamepadInput.TakePress(GamepadButtons.Y), pad.Down(GamepadButtons.Back), GamepadInput.TakePress(GamepadButtons.Start),
                move.X, move.Y, look.X, look.Y,
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
