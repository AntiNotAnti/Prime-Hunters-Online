using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// The pair at the foot of every screen: leaving on the left, and what the
    /// screen is for on the right.
    ///
    /// <para>
    /// One pair on every screen that asks anything, always in the same two
    /// places, so that leaving and committing stop being things to look for.
    /// The screens used to answer this five different ways -- "Back",
    /// "Launch", "Connect", "Start", the window's own close button -- which is
    /// five words for two actions.
    /// </para>
    ///
    /// <para>
    /// It <b>is</b> a <see cref="DeckButton"/> now, rather than a second
    /// control that drew the same slab, the same edge and the same spring in
    /// its own code. Two implementations of one object is how the tick came to
    /// have a four-point edge while the tabs above it had three, and why a
    /// change to the press had to be made twice. What is left here is the part
    /// that is actually this control's: which face a role wears, which key does
    /// the same thing, and how a role's label is cased.
    /// </para>
    ///
    /// <para>
    /// The metrics are the reference's, per role: <c>.back</c> is a 1.05em
    /// label on a four-point edge, <c>.go</c> a 1.4em label on a six-point
    /// one. They are different sizes on purpose -- the commit is the biggest
    /// thing on the foot and the way out is not.
    /// </para>
    /// </summary>
    internal sealed class UiMark : Decorator
    {
        public enum Shape
        {
            Cancel,
            Accept,
            /// <summary>A plus: make a new one of something. Neither yes nor no.</summary>
            Add,
            /// <summary>An arrow into a tray: fetch something that is missing.</summary>
            Fetch
        }

        public static readonly StyledProperty<string> LabelProperty =
            AvaloniaProperty.Register<UiMark, string>(nameof(Label), "");

        public string Label
        {
            get => GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public event EventHandler? Click;

        private readonly Shape _shape;
        private readonly DeckButton _button;

        /// <summary>
        /// Two roles, two colours. Leaving is brass; everything that does what
        /// the screen is for is the one blue, whether it is a tick or a plus.
        /// Three colours across three marks made a foot that looked like three
        /// unrelated offers rather than one choice and a way out.
        /// </summary>
        private static Deck.Face FaceFor(Shape shape) => shape switch
        {
            Shape.Cancel => Deck.Face.Brass,
            Shape.Accept => Deck.Face.Blue,
            _ => Deck.Face.Slate
        };

        /// <summary>
        /// The key that does the same thing. Only the two that have one: a
        /// keycap invented for "install the server files" would be a lie in a
        /// chip.
        /// </summary>
        private static string KeyFor(Shape shape) => shape switch
        {
            Shape.Cancel => "ESC",
            Shape.Accept => "⏎",
            _ => ""
        };

        public UiMark(Shape shape, string label)
        {
            _shape = shape;
            bool accept = shape == Shape.Accept;
            _button = new DeckButton(Case(label), FaceFor(shape),
                sizeEms: accept ? 1.4 : 1.05,
                padXEms: accept ? 1.4 : 1.0,
                padYEms: accept ? 0.4 : 0.5,
                lip: accept ? 6 : 4)
            {
                KeyCap = KeyFor(shape)
            };
            _button.Click += (_, _) =>
            {
                if (IsEnabled)
                {
                    Click?.Invoke(this, EventArgs.Empty);
                }
            };
            Child = _button;
            SetCurrentValue(LabelProperty, label);
        }

        /// <summary>
        /// The commit shouts and the way out does not.
        ///
        /// Upper case on the tick, sentence case on the cross: the reference
        /// writes "START" and "Back", and the difference is the whole of how a
        /// foot says which of the two is the act.
        /// </summary>
        private string Case(string label)
        {
            if (label.Length == 0)
            {
                return label;
            }
            return _shape == Shape.Accept
                ? label.ToUpperInvariant()
                : char.ToUpperInvariant(label[0]) + label[1..];
        }

        /// <summary>The bob, for the one button on a screen that wants looking at.</summary>
        public bool Idle
        {
            get => _button.Idle;
            set => _button.Idle = value;
        }

        public bool Focus() => _button.Focus();

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == LabelProperty)
            {
                _button.Text = Case(Label);
            }
            else if (change.Property == IsEnabledProperty)
            {
                _button.IsEnabled = IsEnabled;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                e.Handled = true;
                if (IsEnabled)
                {
                    Click?.Invoke(this, EventArgs.Empty);
                }
                return;
            }
            base.OnKeyDown(e);
        }
    }
}
