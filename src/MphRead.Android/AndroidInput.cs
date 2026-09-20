using System;
using System.Collections.Generic;
using System.Reflection;
using MphRead.Entities;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MphRead.Droid
{
    /// <summary>
    /// A keyboard and a mouse that no one is holding.
    ///
    /// The engine's input path is one method -- <c>PlayerEntity.ProcessAllInput</c>
    /// -- which reads a <see cref="KeyboardState"/> and a <see cref="MouseState"/>
    /// once a frame and turns them into the <c>Keybind</c> flags the player
    /// code actually reads. The cheapest correct way to play with a touchscreen
    /// is therefore not to fork that method: it is to hand the scene a keyboard
    /// and a mouse of our own and press the keys the player has bound.
    ///
    /// That buys three things for free. Rebinding works -- a thumb on SHOOT
    /// presses whatever <c>Controls.Shoot</c> says, key or mouse button. Mouse
    /// sensitivity and inverted aim work, because aiming moves a pointer and
    /// the engine reads the same delta it always did. And the weapon wheel
    /// works, because it is a *touchscreen* mechanic on the DS: it reads the
    /// pointer's absolute position, so while the menu is held open the pointer
    /// is the finger rather than a delta.
    ///
    /// OpenTK does not let anyone else build those two types -- the
    /// constructors and the setters are internal -- so they are reached by
    /// reflection, once, into delegates. Nothing here runs per vertex; it is a
    /// handful of calls a frame.
    /// </summary>
    internal sealed class AndroidInput
    {
        public KeyboardState Keyboard { get; }
        public MouseState Mouse { get; }

        private readonly Action<KeyboardState, Keys, bool> _setKey;
        private readonly Action<MouseState, Vector2> _setPosition;
        private readonly Action<MouseState, MouseButton, bool> _setButton;

        private Vector2 _pointer;

        // What this frame's actions have asked for, and what the last frame
        // left held. See Apply.
        private readonly HashSet<Keys> _keysDown = new HashSet<Keys>();
        private readonly HashSet<MouseButton> _buttonsDown = new HashSet<MouseButton>();
        private readonly HashSet<Keys> _keysHeld = new HashSet<Keys>();
        private readonly HashSet<MouseButton> _buttonsHeld = new HashSet<MouseButton>();

        public AndroidInput()
        {
            Type keyboardType = typeof(KeyboardState);
            Type mouseType = typeof(MouseState);
            Keyboard = (KeyboardState)Activator.CreateInstance(keyboardType, nonPublic: true)!;
            Mouse = (MouseState)Activator.CreateInstance(mouseType, nonPublic: true)!;
            _setKey = Bind<Action<KeyboardState, Keys, bool>>(
                keyboardType.GetMethod("SetKeyState",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
                "KeyboardState.SetKeyState");
            _setPosition = Bind<Action<MouseState, Vector2>>(
                mouseType.GetProperty("Position",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.SetMethod,
                "MouseState.Position setter");
            _setButton = Bind<Action<MouseState, MouseButton, bool>>(
                mouseType.GetProperty("Item",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.SetMethod,
                "MouseState button setter");
        }

        private static T Bind<T>(MethodInfo? method, string what) where T : Delegate
        {
            if (method == null)
            {
                throw new ProgramException(
                    $"{what} is not where this build of OpenTK keeps it; touch input cannot be delivered.");
            }
            return (T)method.CreateDelegate(typeof(T));
        }

        /// <summary>Where the pointer is, in window pixels.</summary>
        public Vector2 Pointer => _pointer;

        public void SetKey(Keys key, bool down)
        {
            if (key != Keys.Unknown)
            {
                _setKey(Keyboard, key, down);
            }
        }

        public void SetButton(MouseButton button, bool down)
        {
            _setButton(Mouse, button, down);
        }

        /// <summary>
        /// Press or release whatever the player has this action bound to.
        ///
        /// **Held down wins over let go, for the frame.** Two actions can share
        /// one bind, and the game's own defaults do it: <c>shoot</c> and
        /// <c>altAttack</c> are both <c>MouseButton.Left</c>, because the DS
        /// had one fire button and the alt form's attack is what it does while
        /// you are a ball. Applying each action in turn therefore had the last
        /// one applied decide the shared button's state -- so a thumb on FIRE
        /// set Left down and the very next line, ALT not being held, set it
        /// straight back up. FIRE did nothing at all and ALT fired, which is
        /// exactly how it was reported.
        ///
        /// So a frame's presses are accumulated rather than written straight
        /// through, and <see cref="CommitFrame"/> writes the union. Anything
        /// no action asked for this frame is released. Nothing here depends on
        /// the *order* actions are applied in any more, which is what made the
        /// bug possible.
        /// </summary>
        public void Apply(Keybind bind, bool down)
        {
            if (bind.Type == ButtonType.Key)
            {
                if (bind.Key != Keys.Unknown && down)
                {
                    _keysDown.Add(bind.Key);
                }
            }
            else if (bind.Type == ButtonType.Mouse)
            {
                if (down)
                {
                    _buttonsDown.Add(bind.MouseButton);
                }
            }
            // Scroll binds have no touch equivalent and are left alone.
        }

        /// <summary>
        /// A key that is not one of the player's binds.
        ///
        /// For the spectator's free camera, which is the room viewer's Roam
        /// camera: <c>Scene.OnKeyHeld</c> drives it by reading W/A/S/D, Space
        /// and V off the keyboard directly rather than through a
        /// <see cref="Keybind"/>, so there is no bind to apply and the key
        /// itself has to be pressed. Through the same accumulator as
        /// everything else, so <see cref="CommitFrame"/> releases it when the
        /// thumb comes off.
        /// </summary>
        public void ApplyKey(Keys key, bool down)
        {
            if (key != Keys.Unknown && down)
            {
                _keysDown.Add(key);
            }
        }

        /// <summary>
        /// A press that is not one of the player's binds -- the dialog box's
        /// OK button, which the DS read off its touch screen. Goes through the
        /// same accumulator so <see cref="CommitFrame"/> does not release it.
        /// </summary>
        public void ApplyButton(MouseButton button, bool down)
        {
            if (down)
            {
                _buttonsDown.Add(button);
            }
        }

        /// <summary>
        /// Start collecting a frame's presses. Everything held at the end of
        /// the last frame is remembered, so <see cref="CommitFrame"/> knows
        /// what to release.
        /// </summary>
        public void BeginFrame()
        {
            _keysDown.Clear();
            _buttonsDown.Clear();
        }

        /// <summary>Write the frame's union of presses, and release the rest.</summary>
        public void CommitFrame()
        {
            foreach (Keys key in _keysHeld)
            {
                if (!_keysDown.Contains(key))
                {
                    SetKey(key, down: false);
                }
            }
            foreach (MouseButton button in _buttonsHeld)
            {
                if (!_buttonsDown.Contains(button))
                {
                    SetButton(button, down: false);
                }
            }
            foreach (Keys key in _keysDown)
            {
                SetKey(key, down: true);
            }
            foreach (MouseButton button in _buttonsDown)
            {
                SetButton(button, down: true);
            }
            _keysHeld.Clear();
            _keysHeld.UnionWith(_keysDown);
            _buttonsHeld.Clear();
            _buttonsHeld.UnionWith(_buttonsDown);
        }

        /// <summary>Aiming: move the pointer, which is what the engine reads.</summary>
        public void MovePointer(float deltaX, float deltaY)
        {
            if (deltaX == 0 && deltaY == 0)
            {
                return;
            }
            _pointer.X += deltaX;
            _pointer.Y += deltaY;
            _setPosition(Mouse, _pointer);
        }

        /// <summary>
        /// Put the pointer somewhere exactly, for the parts of the game that
        /// were a touchscreen on the DS and read a position rather than a
        /// movement -- the weapon wheel and the map.
        /// </summary>
        public void PlacePointer(float x, float y)
        {
            _pointer = new Vector2(x, y);
            _setPosition(Mouse, _pointer);
        }

        /// <summary>
        /// Everything up, for a match that is starting or a view that lost
        /// focus. Goes straight to the state rather than through
        /// <see cref="Apply"/>, which only ever records presses now.
        /// </summary>
        public void ReleaseAll()
        {
            BeginFrame();
            CommitFrame();
            PlayerControls? controls = PlayerEntity.Main?.Controls;
            if (controls == null)
            {
                return;
            }
            for (int i = 0; i < controls.All.Length; i++)
            {
                Keybind bind = controls.All[i];
                if (bind.Type == ButtonType.Key)
                {
                    SetKey(bind.Key, down: false);
                }
                else if (bind.Type == ButtonType.Mouse)
                {
                    SetButton(bind.MouseButton, down: false);
                }
            }
        }
    }
}
