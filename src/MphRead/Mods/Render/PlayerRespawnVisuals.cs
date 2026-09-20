using System;
using MphRead.Formats;
using MphRead.Hud;

namespace MphRead.Entities
{
    public partial class PlayerEntity
    {
        // Main-player spawn only. Keep this CPU-only: spawning also runs in
        // simulation, and render diagnostics sample GL on the render thread.
        private void ResetRespawnVisualState()
        {
            HudEndDisrupted();
            // The inactive state can retain the cooldown from natural recovery;
            // HudEndDisrupted handles active effects but leaves that timer alone.
            _hudDisruptedTimer = 0;
            EndWhiteout();
            _whiteoutAmount = 0;
            _whiteoutTime = 0;
            Array.Clear(HudWhiteoutTable);

            Array.Clear(_damageIndicatorTimers);
            _deathCountdown = 0;
            _hudWeaponMenuOpen = false;
            _hudPreviousWeaponSelection = -1;
            WeaponSelection = CurrentWeapon;
            Mods.Input.WeaponWheel.Close();
            _hudZoom = false;
            _sniperReticle = false;

            // Match Spawn's camera-reset condition. Scripted single-player
            // cameras retain their authored shake and transition timing.
            if (!GameState.SinglePlayer || CameraSequence.Current == null)
            {
                CameraInfo.Shake = 0;
            }

            if (HudReady)
            {
                _damageIndicator.Active = false;
                for (int i = 0; i < _damageIndicatorNodes.Length; i++)
                {
                    _damageIndicatorNodes[i].Enabled = false;
                }
            }

            // Every normal UpdateHud clears all five bindings, then restores
            // the visible helmet/visor layers using configured opacity. Clear
            // cached ice, masks and dialog layers here as well for early exits.
            ResetRespawnHudLayer(_scene.Layer1Info);
            ResetRespawnHudLayer(_scene.Layer2Info);
            ResetRespawnHudLayer(_scene.Layer3Info);
            ResetRespawnHudLayer(_scene.Layer4Info);
            ResetRespawnHudLayer(_scene.Layer5Info);

            // Spawn already resets ice timers/visibility, damage flash timing,
            // model alpha, HUD offsets, reticle animation and player flags.
            // Keep scene fades: their completion can trigger room/movie changes.
            // Match messages, scoreboard input and persistent effects stay owned
            // by their existing gameplay paths.
        }

        private static void ResetRespawnHudLayer(LayerInfo layer)
        {
            layer.BindingId = -1;
            layer.MaskId = -1;
            layer.Alpha = 1;
            // Layout is not transient: the menu-pause path restores bindings
            // without recomputing scale, so retain each layer's dimensions.
            layer.ShiftX = 0;
            layer.ShiftY = 0;
        }
    }
}
