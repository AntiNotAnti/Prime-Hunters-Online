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
            bool handled = true;
            if (Hit(key, InputSettings.ReplayPlayPauseKey)) ReplayController.TogglePause();
            else if (Hit(key, InputSettings.ReplayStepForwardKey)) ReplayController.StepForward();
            else if (Hit(key, InputSettings.ReplayStepBackKey))
                ReplayController.Seek(ReplayController.CurrentFrame > 0
                    ? ReplayController.CurrentFrame - 1 : 0, false);
            else if (Hit(key, InputSettings.ReplaySlowerKey)) ReplayController.ChangeRate(-1);
            else if (Hit(key, InputSettings.ReplayFasterKey)) ReplayController.ChangeRate(1);
            else if (Hit(key, InputSettings.ReplayRestartKey)) ReplayController.Restart();
            else if (Hit(key, InputSettings.ReplaySeekBackKey))
                ReplayController.Seek(ReplayController.CurrentFrame > 300
                    ? ReplayController.CurrentFrame - 300 : 0);
            else if (Hit(key, InputSettings.ReplaySeekForwardKey))
                ReplayController.Seek((uint)System.Math.Min(
                    (ulong)ReplayController.CurrentFrame + 300,
                    ReplayController.DurationFrames));
            else
            {
                switch (key)
                {
                    case Keys.F: ReplayCamera.ToggleFree(); break;
                    case Keys.C: ReplayCamera.SetMode(ReplayCamera.Mode == ReplayCameraMode.Chase
                        ? ReplayCameraMode.FirstPerson : ReplayCameraMode.Chase); break;
                    case Keys.O: ReplayCamera.SetMode(ReplayCameraMode.Orbit); break;
                    case Keys.B: ReplayCamera.Bookmark(); break;
                    case Keys.N: ReplayCamera.RestoreBookmark(); break;
                    case >= Keys.D1 and <= Keys.D8: SpectatorMode.Watch(key - Keys.D1); break;
                    default: handled = false; break;
                }
            }
            if (!handled) return false;
            ReplayController.NoteInput();
            return true;
        }

        private static bool Hit(Keys key, Keys binding)
            => binding != Keys.Unknown && key == binding;

        public static void PollGamepad()
        {
            if (!DemoPlayback.IsActive || PauseMenu.Open) return;
            if (GamepadInput.TakeActionPress(PadAction.ReplayPlayPause))
                ReplayController.TogglePause();
            if (GamepadInput.TakeActionPress(PadAction.ReplayStep))
                ReplayController.StepForward();
            if (GamepadInput.TakeActionPress(PadAction.ReplayCameraMode))
                ReplayCamera.ToggleFree();
            if (GamepadInput.TakeActionPress(PadAction.ReplaySeekBack))
                ReplayController.Seek(ReplayController.CurrentFrame > 300
                    ? ReplayController.CurrentFrame - 300 : 0);
            if (GamepadInput.TakeActionPress(PadAction.ReplaySeekForward))
                ReplayController.Seek((uint)System.Math.Min(
                    (ulong)ReplayController.CurrentFrame + 300,
                    ReplayController.DurationFrames));
            if (GamepadInput.TakeActionPress(PadAction.ReplaySlower))
                ReplayController.ChangeRate(-1);
            if (GamepadInput.TakeActionPress(PadAction.ReplayFaster))
                ReplayController.ChangeRate(1);
            if (GamepadInput.TakeActionPress(PadAction.ReplayNextPlayer))
                SpectatorMode.CycleNext();
            if (GamepadInput.TakeActionPress(PadAction.ReplayPrevPlayer))
                SpectatorMode.CyclePrevious();
        }
    }
}
