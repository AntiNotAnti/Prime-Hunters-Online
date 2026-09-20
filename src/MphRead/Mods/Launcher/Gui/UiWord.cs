using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// One word of a menu, and nothing else: no bar, no box, no fill. What
    /// tells you where you are is the colour and, on the front screen, the
    /// size.
    ///
    /// The front screen drew these as a <see cref="Border"/> round a
    /// <see cref="TextBlock"/> that brightened under the pointer, which looked
    /// right and could not be driven from a keyboard -- there was nothing to
    /// focus and nothing that answered Enter. This is the same drawing with
    /// the three things a menu needs: focus, Enter, and a press that survives
    /// a finger, which on a touchscreen hovers nothing and so has left the row
    /// by the time it lets go.
    /// </summary>
    internal sealed class UiWord : Control
    {
        public static readonly StyledProperty<string> TextProperty =
            AvaloniaProperty.Register<UiWord, string>(nameof(Text), "");

        public static readonly StyledProperty<bool> SelectedProperty =
            AvaloniaProperty.Register<UiWord, bool>(nameof(Selected));

        public string Text
        {
            get => GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        /// <summary>The entry whose page is open, in a strip of them.</summary>
        public bool Selected
        {
            get => GetValue(SelectedProperty);
            set => SetValue(SelectedProperty, value);
        }

        public event EventHandler? Click;

        private readonly double _size;
        private readonly FontFamily _font;
        private readonly Color _colour;
        private readonly Tap _tap = new();

        static UiWord()
        {
            AffectsRender<UiWord>(TextProperty, SelectedProperty, IsEnabledProperty);
            AffectsMeasure<UiWord>(TextProperty);
        }

        public UiWord(string text, double size = UiLayout.WordSize,
            FontFamily? font = null, Color? colour = null)
        {
            Text = text;
            _size = size;
            _font = font ?? GuiTheme.Display;
            _colour = colour ?? GuiTheme.Text;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        private FormattedText Label(IBrush brush)
        {
            return new FormattedText(Text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(_font, FontStyle.Normal, FontWeight.Normal),
                _size, brush);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            FormattedText text = Label(GuiTheme.TextBrush);
            // Its own size in both directions: a word in a horizontal strip
            // that measures to nothing is drawn on top of its neighbours, and
            // a bare Control measures to nothing.
            return new Size(Math.Min(text.Width + 4, availableSize.Width),
                text.Height + 2);
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            InvalidateVisual();
            base.OnPointerEntered(e);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            _tap.Cancel();
            InvalidateVisual();
            base.OnPointerExited(e);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            _tap.Press(e, this);
            Focus();
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
            base.OnPointerPressed(e);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            // A finger that has set off across the screen is scrolling
            // whatever this sits in, and is no longer pressing this. See Tap.
            _tap.Moved(e, this);
            base.OnPointerMoved(e);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            // Tap decides: the press has to have landed here, stayed within a
            // finger's slop, and let go inside. Where the release landed, not
            // IsPointerOver -- a finger hovers nothing, so the pointer has
            // already left by the time it lets go.
            bool tapped = _tap.Release(e, this);
            InvalidateVisual();
            if (ReferenceEquals(e.Pointer.Captured, this))
            {
                e.Pointer.Capture(null);
            }
            base.OnPointerReleased(e);
            if (tapped && IsEnabled)
            {
                Click?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            // The scroll gesture above has taken the pointer.
            _tap.Cancel();
            InvalidateVisual();
            base.OnPointerCaptureLost(e);
        }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            InvalidateVisual();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            InvalidateVisual();
            base.OnLostFocus(e);
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

        public override void Render(DrawingContext context)
        {
            // Avalonia hit-tests what was drawn, not the bounds: without this
            // the word only answers the pointer over its own glyphs.
            context.FillRectangle(Brushes.Transparent,
                new Rect(0, 0, Bounds.Width, Bounds.Height));
            bool lit = (IsPointerOver || IsFocused) && IsEnabled;
            // The accent, for every word, on the frame the pointer arrives.
            //
            // Two wrong answers came before it. The word's own colour blended
            // towards white was the first: the text colour is already all but
            // white, so Settings and Quit answered the pointer with a change
            // nobody could see, while Play -- which starts amber -- was the
            // only word that visibly reacted. Gliding to the accent over 140
            // ms was the second, and it was reported straight back as the
            // button taking a fifth of a second to notice the pointer, which
            // is exactly what an eased 140 ms ramp is. A menu word is not an
            // animation; it is a state, and the state is "the pointer is
            // here". So: no ramp, no movement, and the one thing that changes
            // is the colour.
            Color colour = !IsEnabled ? GuiTheme.TextDim
                : lit ? GuiTheme.Shade(GuiTheme.Accent, 0.2)
                : Selected ? GuiTheme.Accent : _colour;
            context.DrawText(Label(new SolidColorBrush(colour)), new Point(0, 0));
        }
    }
}
