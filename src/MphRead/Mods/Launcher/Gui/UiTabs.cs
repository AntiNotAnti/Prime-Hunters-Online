using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// The strip that says which of a screen's few faces is up: an arrow, the
    /// names with a dot between them, an arrow.
    ///
    /// It is what turned seven screens into one. Choosing a server, a map, a
    /// save slot and a recording were four separate cards with four layouts,
    /// and they are four answers to the same question -- what are we playing.
    /// A strip over one list is that question asked once.
    ///
    /// The arrows are real controls rather than decoration, because a phone
    /// has no left and right arrow keys and the strip is the only way to
    /// change face there.
    /// </summary>
    internal sealed class UiTabs : StackPanel
    {
        /// <summary>Raised after <see cref="Index"/> has already moved.</summary>
        public event EventHandler? Changed;

        private readonly List<DeckButton> _tabs = new();
        private int _index;

        public int Index
        {
            get => _index;
            set
            {
                int clamped = _tabs.Count == 0 ? 0 : Math.Clamp(value, 0, _tabs.Count - 1);
                if (clamped == _index)
                {
                    return;
                }
                _index = clamped;
                Mark();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public UiTabs(IReadOnlyList<string> names, int index = 0)
        {
            // Faces, not words with dots between them. A strip of words needs
            // the dots and the two arrows to read as a strip at all; a row of
            // objects reads as one without either, and the one that is up says
            // so by being a different colour rather than by being brighter.
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
            HorizontalAlignment = HorizontalAlignment.Center;
            _index = names.Count == 0 ? 0 : Math.Clamp(index, 0, names.Count - 1);

            for (int i = 0; i < names.Count; i++)
            {
                // The reference's `.tab`: `font-size: 1.05em` of the frame,
                // `padding: .38em .8em` of its own, and the `.btn` default
                // six-point edge. Not a smaller menu entry -- the same object
                // at a different density, which is why all three numbers move
                // together.
                var tab = new DeckButton(names[i], Deck.Face.Slate,
                    sizeEms: 1.05, padXEms: 0.8, padYEms: 0.38, lip: 6);
                int target = i;
                tab.Click += (_, _) => Index = target;
                _tabs.Add(tab);
                Children.Add(tab);
            }
            Mark();
        }

        /// <summary>
        /// The left and right keys, wherever they were pressed on the screen.
        /// Offered to the host rather than handled here: the strip is rarely
        /// what holds the keyboard -- the list under it is.
        /// </summary>
        public bool HandleKey(Key key)
        {
            if (key == Key.Left)
            {
                Step(-1);
                return true;
            }
            if (key == Key.Right)
            {
                Step(1);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Put the keyboard on the name that is up. A <see cref="StackPanel"/>
        /// cannot take focus itself, so a host that asked the strip to take it
        /// would silently focus nothing -- and the first Tab would then land
        /// on whatever the tree happened to offer first.
        /// </summary>
        public void FocusSelected()
        {
            if (_index >= 0 && _index < _tabs.Count)
            {
                _tabs[_index].Focus();
            }
        }

        /// <summary>
        /// The strip's own gap, `.4em` of the frame. A StackPanel's Spacing is
        /// a number rather than a binding, so it is set from the em here --
        /// once, and only when the em has moved.
        /// </summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            double gap = Math.Round(Deck.GetEm(this) * 0.4);
            if (Math.Abs(gap - Spacing) > 0.01)
            {
                Spacing = gap;
            }
            return base.MeasureOverride(availableSize);
        }

        /// <summary>Wrapping: the faces are few and running off the end of them is nobody's intent.</summary>
        private void Step(int direction)
        {
            if (_tabs.Count == 0)
            {
                return;
            }
            _index = (_index + direction + _tabs.Count) % _tabs.Count;
            Mark();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void Mark()
        {
            for (int i = 0; i < _tabs.Count; i++)
            {
                _tabs[i].Wear(i == _index ? Deck.Face.Rust : Deck.Face.Slate,
                    selected: i == _index);
            }
        }

    }
}
