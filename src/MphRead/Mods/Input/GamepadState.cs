using System;

namespace MphRead.Mods.Input
{
    /// <summary>
    /// The buttons an Xbox-shaped pad has, as flags.
    ///
    /// Xbox-shaped because that is what both platforms hand over: GLFW remaps
    /// every pad it recognises onto this layout through SDL's controller
    /// database, and Android's own <c>KEYCODE_BUTTON_*</c> constants are the
    /// same fifteen in the same places. A DualShock's cross arrives as
    /// <see cref="A"/> from both, and nothing above this file has to know
    /// which pad it is talking to.
    /// </summary>
    [Flags]
    public enum GamepadButtons
    {
        None = 0,
        A = 1 << 0,
        B = 1 << 1,
        X = 1 << 2,
        Y = 1 << 3,
        LeftBumper = 1 << 4,
        RightBumper = 1 << 5,
        Back = 1 << 6,
        Start = 1 << 7,
        LeftThumb = 1 << 8,
        RightThumb = 1 << 9,
        DpadUp = 1 << 10,
        DpadRight = 1 << 11,
        DpadDown = 1 << 12,
        DpadLeft = 1 << 13,
        /// <summary>
        /// Both triggers, as buttons. They are analogue on every pad worth
        /// having and every action here is on or off, so the threshold is
        /// applied once, where the pad is read, rather than at each use.
        /// </summary>
        LeftTrigger = 1 << 14,
        RightTrigger = 1 << 15
    }

    /// <summary>
    /// One pad, one frame: two sticks, two triggers and the buttons.
    ///
    /// Deliberately free of any OpenTK or Android type. The desktop fills it
    /// by polling GLFW and Android by adding up the events its window
    /// receives, and everything that decides what a press *means* reads this
    /// and nothing else -- which is what lets the mapping be one file instead
    /// of two that drift.
    ///
    /// Sticks are -1..1 with y positive **up**, which is the opposite of what
    /// both sources report and is corrected as they are read: a stick pushed
    /// forward should aim up, and a sign convention that has to be remembered
    /// at each use gets remembered wrongly at one of them.
    /// </summary>
    public struct GamepadState
    {
        public bool Connected;
        public float LeftX;
        public float LeftY;
        public float RightX;
        public float RightY;
        public float LeftTrigger;
        public float RightTrigger;
        public GamepadButtons Buttons;

        /// <summary>What the pad is called, for the settings screen to show.</summary>
        public string Name;

        public readonly bool Down(GamepadButtons button) => (Buttons & button) != 0;
    }
}
