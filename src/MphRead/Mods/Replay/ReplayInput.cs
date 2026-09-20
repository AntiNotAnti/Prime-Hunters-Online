using MphRead.Mods.Input;
using MphRead.Mods.Network;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MphRead.Mods.Replay
{
    public static class ReplayInput
    {
        public static bool HandleKey(Keys key)
        {
            if (!DemoPlayback.IsActive || PauseMenu.Open) return false;
            switch (key)
            {
                case Keys.Space: ReplayController.TogglePause(); break;
                case Keys.Period: ReplayController.StepForward(); break;
                case Keys.Comma: ReplayController.Seek(ReplayController.CurrentFrame > 0 ? ReplayController.CurrentFrame - 1 : 0, false); break;
                case Keys.LeftBracket: ReplayController.ChangeRate(-1); break;
                case Keys.RightBracket: ReplayController.ChangeRate(1); break;
                case Keys.Home: ReplayController.Restart(); break;
                case Keys.Left: ReplayController.Seek(ReplayController.CurrentFrame > 300 ? ReplayController.CurrentFrame - 300 : 0); break;
                case Keys.Right: ReplayController.Seek((uint)System.Math.Min((ulong)ReplayController.CurrentFrame + 300, ReplayController.DurationFrames)); break;
                case Keys.F: ReplayCamera.ToggleFree(); break;
                case Keys.C: ReplayCamera.SetMode(ReplayCamera.Mode == ReplayCameraMode.Chase ? ReplayCameraMode.FirstPerson : ReplayCameraMode.Chase); break;
                case Keys.O: ReplayCamera.SetMode(ReplayCameraMode.Orbit); break;
                case Keys.B: ReplayCamera.Bookmark(); break;
                case Keys.N: ReplayCamera.RestoreBookmark(); break;
                case >= Keys.D1 and <= Keys.D8: SpectatorMode.Watch(key - Keys.D1); break;
                default: return false;
            }
            ReplayController.NoteInput();
            return true;
        }
        public static void PollGamepad()
        {
            if (!DemoPlayback.IsActive || PauseMenu.Open) return;
            if (GamepadInput.TakePress(GamepadButtons.A)) ReplayController.TogglePause();
            if (GamepadInput.TakePress(GamepadButtons.X)) ReplayController.StepForward();
            if (GamepadInput.TakePress(GamepadButtons.Y)) ReplayCamera.ToggleFree();
            if (GamepadInput.TakePress(GamepadButtons.DpadDown)) ReplayController.ChangeRate(-1);
            if (GamepadInput.TakePress(GamepadButtons.DpadUp)) ReplayController.ChangeRate(1);
            if (GamepadInput.TakePress(GamepadButtons.RightBumper)) SpectatorMode.CycleNext();
            if (GamepadInput.TakePress(GamepadButtons.LeftBumper)) SpectatorMode.CyclePrevious();
        }
    }
}
