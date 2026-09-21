using MphRead.Formats;

namespace MphRead.Entities
{
    public partial class PlayerEntity
    {
        /// <summary>
        /// Read this frame's pointer movement as a possible boost, and if it is
        /// one, ask for the boost the touch head's swipe asks for. In ordinary
        /// mouse mode the deltas come from GLFW; in desktop Stylus Mode the same
        /// fields are fed by PointerDevice, so the DS-style pen flick follows the
        /// exact same boost path instead of growing a second gesture implementation.
        ///
        /// A partial rather than a dozen lines inside <c>ProcessAlt</c>: the
        /// gesture wants the player's own <c>Input</c> deltas and its flags,
        /// both private, and the alternative is widening four of them. What
        /// upstream carries is the one call.
        ///
        /// The gates are all "is the pointer currently saying something else":
        /// the weapon wheel is answered by dragging, either manual boost input
        /// being held is a charge the player is building on purpose and a flick
        /// would cut it short at a charge they did not choose, and a camera
        /// sequence or a frame-advance step is not a frame anybody moved a
        /// mouse in. There is no gate for the form, the hunter or the
        /// ability, because the one call site is already inside all three.
        ///
        /// Every player runs <c>ProcessAlt</c> -- bots, and the puppets
        /// standing in for other people -- and exactly one of them has a
        /// mouse, so the others leave before they can clear the history the
        /// main player is building. See <see cref="Mods.Input.MouseFlick"/>.
        /// </summary>
        private void ModCheckMouseFlick(bool buttonBoostDown)
        {
            if (!IsMainPlayer || IsBot)
            {
                return;
            }
            if (!Controls.MouseAim || buttonBoostDown
                || Flags1.TestFlag(PlayerFlags1.NoAimInput)
                || Flags1.TestFlag(PlayerFlags1.WeaponMenuOpen)
                || Mods.SpectatorMode.IsSpectating
                || _scene.FrameAdvance || _scene.FrameAdvanceLastFrame
                || CameraSequence.Current?.Flags.TestFlag(CamSeqFlags.BlockInput) == true)
            {
                Mods.Input.MouseFlick.Reset();
                return;
            }
            if (Mods.Input.MouseFlick.Check(Input.MouseDeltaX, Input.MouseDeltaY,
                _scene.FrameCount, out float dirX, out float dirY))
            {
                SwipeBoostRequested = true;
                SwipeBoostX = dirX;
                SwipeBoostY = dirY;
            }
        }
    }
}
