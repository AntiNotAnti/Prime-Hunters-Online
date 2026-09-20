using MphRead.Mods.Input;
namespace MphRead.Entities
{
    public partial class PlayerEntity
    {
        private void ModControllerFeedback(GamepadFeedback feedback)
        {
            if (IsMainPlayer && !IsBot && !Mods.SpectatorMode.IsSpectating
                && feedback is GamepadFeedback.Fire or GamepadFeedback.ChargedShot)
                Mods.Input.AimAssist.AimAssistTelemetry.Shot(CurrentWeapon);
            if (IsMainPlayer && !IsBot && !Mods.SpectatorMode.IsSpectating
                && GamepadContexts.Current == GamepadContext.Gameplay)
                GamepadHaptics.Play(feedback);
        }
        private int ModControllerWeaponSelection()
        {
            var stick = GamepadInput.AimStick;
            int slot = WeaponSelectionDirection.ControllerSlot(stick.X, stick.Y);
            return ModResolveWeaponSlot(slot);
        }
        private int ModResolveWeaponSlot(int slot)
        {
            if (slot < 0 || slot >= _weaponSelectInsts.Length) return -1;
            var beam = (BeamType)_weaponSelectInsts[slot].CurrentFrame;
            if (!_availableWeapons[beam]) return -1;
            WeaponSelection = beam;
            return slot;
        }
    }
}
