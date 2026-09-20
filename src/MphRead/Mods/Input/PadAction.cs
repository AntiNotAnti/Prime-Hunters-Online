using System;
using System.Collections.Generic;

namespace MphRead.Mods.Input
{
    /// <summary>
    /// What a pad button does, as a thing a player can change.
    ///
    /// Not the <c>PlayerControls</c> properties themselves, and the difference
    /// is deliberate: several of those are one button on a pad and always were
    /// on the DS. <see cref="Shoot"/> drives both <c>Shoot</c> and
    /// <c>AltAttack</c>, <see cref="Jump"/> drives both <c>Jump</c> and
    /// <c>Boost</c>, and offering four rows for two buttons would let a player
    /// build a pad on which the ball cannot boost and nothing on screen says
    /// why.
    ///
    /// The weapon wheel shares the existing gameplay bind and slot resolver.
    /// </summary>
    public enum PadAction
    {
        /// <summary>The gun on foot and the alt form's attack in the ball.</summary>
        Shoot,
        Zoom,
        /// <summary>Jumping on foot, boosting in the ball: one button, as on the DS.</summary>
        Jump,
        Morph,
        Scan,
        ScanVisor,
        /// <summary>The DS's own pause button: map and status, scoreboard in a match.</summary>
        Scoreboard,
        NextWeapon,
        PrevWeapon,
        Missile,
        PowerBeam,
        /// <summary>
        /// The app's own menu. Not a <c>Keybind</c> like the rest -- it opens a
        /// window rather than doing something in the world -- so it is taken
        /// by whoever owns the window, through
        /// <see cref="GamepadInput.TakeMenuPress"/>.
        /// </summary>
        Menu,
        /// <summary>
        /// Open the chat line. Like <see cref="Menu"/> and unlike the rest,
        /// this opens something that then takes the keyboard, so it is taken
        /// through <see cref="GamepadInput.TakeChatPress"/> rather than held
        /// as a bind.
        /// </summary>
        Chat,
        WeaponWheel,
        VoltDriver, Battlehammer, Imperialist, Judicator, Magmaul, ShockCoil,
        OmegaCannon, AffinitySlot, LastWeapon
    }

}
